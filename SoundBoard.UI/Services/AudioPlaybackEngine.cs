using NAudio.Wave;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Data;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using SoundBoard.PluginApi;
using SoundBoard.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;

namespace SoundBoard.UI.Services;

/// <inheritdoc cref="IAudioPlaybackEngine"/>
public class AudioPlaybackEngine : IAudioPlaybackEngine, IDisposable
{
    private readonly MasterMixer _masterMixer;
    private readonly LocalAudioPlayer _localPlayer;
    private readonly ISoundBoardDbContextFactory _dbContextFactory;
    private readonly ISamplerChainService _samplerChain;
    private readonly IPluginService _pluginService;
    private readonly ISamplerLauncherService _samplerLauncher;
    private readonly DispatcherTimer _telemetryTimer;

    public ObservableCollection<IActiveMixerItem> ActiveItems { get; } = new();
    public ObservableCollection<PlayingTrackViewModel> ActiveTracks { get; } = new();

    // ── Playlist state ───────────────────────────────────────────────────────

    private sealed class PlaylistSession
    {
        public Playlist Playlist = null!;
        public List<PlaylistItem> Items = new();
        /// <summary>Sequential cursor: index of the next item to play if
        /// <see cref="Random"/> is false. Ignored in random mode.</summary>
        public int NextIndex;
        /// <summary>Index of the currently-playing item. Set by
        /// <c>AdvancePlaylist</c> each time it spawns. -1 before the first
        /// spawn. Used by Skip / Stop / cursor-bookkeeping paths that
        /// shouldn't assume sequential order.</summary>
        public int CurrentIndex = -1;
        public bool Random;
        public bool Cancelled;
        /// <summary>Force the next <c>AdvancePlaylist</c> call to spawn even
        /// when Autoplay is off. Set by Skip Forward / Backward so a user
        /// gesture doesn't get swallowed by the autoplay-stop branch.</summary>
        public bool ForceNextAdvance;
        /// <summary>The currently-playing child mixer item (a track or preset
        /// VM). Used by the telemetry tick to find the running provider when
        /// deciding whether to trigger a crossfade.</summary>
        public IActiveMixerItem? CurrentSpawned;
        /// <summary>True once the crossfade-into-next trigger has fired for
        /// the current item. Prevents the telemetry loop from re-spawning
        /// repeatedly between the trigger moment and the current item's
        /// natural completion.</summary>
        public bool CrossfadeTriggered;
        /// <summary>Mixer card representing this running playlist. Stays in
        /// <c>ActiveItems</c> across all the playlist's child spawns; gets
        /// removed when the playlist ends naturally or is stopped.</summary>
        public PlayingPlaylistViewModel? ViewModel;

        /// <summary>Id of the soundboard shortcut that started this playlist,
        /// if any. Propagated to each child spawn so the shortcut's sampler
        /// chain layers on every item the playlist plays.</summary>
        public int? FromShortcutId;
    }

    private readonly Dictionary<int, PlaylistSession> _activePlaylists = new();

    /// <summary>Per-playlist cursor remembered across separate Play calls so
    /// autoplay-off sequential playback can pick up where it left off. Keyed
    /// by playlist id; the value is the index of the most recently spawned
    /// item. In-memory only — resets on app restart.</summary>
    private readonly Dictionary<int, int> _playlistCursors = new();

    public AudioPlaybackEngine(MasterMixer masterMixer, LocalAudioPlayer localPlayer, ISoundBoardDbContextFactory dbContextFactory, ISamplerChainService samplerChain, IPluginService pluginService, ISamplerLauncherService samplerLauncher)
    {
        _masterMixer = masterMixer;
        _localPlayer = localPlayer;
        _dbContextFactory = dbContextFactory;
        _samplerChain = samplerChain;
        _pluginService = pluginService;
        _samplerLauncher = samplerLauncher;

        _localPlayer.Play();

        ActiveItems.CollectionChanged += SyncActiveTracksProjection;

        _telemetryTimer = new DispatcherTimer { Interval = UiConstants.TelemetryTick };
        _telemetryTimer.Tick += TelemetryTimer_Tick;
        _telemetryTimer.Start();
    }

    private void SyncActiveTracksProjection(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (var v in e.NewItems.OfType<PlayingTrackViewModel>())
                if (!ActiveTracks.Contains(v)) ActiveTracks.Add(v);

        if (e.OldItems != null)
            foreach (var v in e.OldItems.OfType<PlayingTrackViewModel>())
                ActiveTracks.Remove(v);

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ActiveTracks.Clear();
            foreach (var v in ActiveItems.OfType<PlayingTrackViewModel>()) ActiveTracks.Add(v);
        }
    }

    private void TelemetryTimer_Tick(object? sender, EventArgs e)
    {
        foreach (var item in ActiveItems.OfType<PlayingTrackViewModel>())
            item.UpdatePositionFromProvider();

        CheckPlaylistCrossfades();
    }

    /// <summary>For every active playlist, if the current item is a non-looping
    /// track and the playlist has any fade-in/fade-out set, watch for the
    /// remaining-time threshold and pre-spawn the next item so its fade-in
    /// overlaps the current item's fade-out. Driven off the existing 100 ms
    /// telemetry tick — naturally pauses when the user pauses (provider
    /// position freezes).
    ///
    /// We deliberately only support crossfading TRACK items right now. A
    /// preset's "natural duration" is undefined when any child loops, and the
    /// existing onCompleted-then-advance path handles preset transitions
    /// fine without crossfade.</summary>
    private void CheckPlaylistCrossfades()
    {
        // Snapshot — AdvancePlaylist can remove from _activePlaylists when
        // the sequential queue runs out, which would otherwise blow up the
        // enumeration mid-tick.
        var snapshot = _activePlaylists.Values.ToList();
        foreach (var session in snapshot)
        {
            if (session.Cancelled || session.CrossfadeTriggered) continue;
            if (!session.Playlist.Autoplay) continue; // no auto-advance → no crossfade
            if (session.CurrentSpawned is not PlayingTrackViewModel tvm) continue;
            if (tvm.IsLooping) continue;

            var fadeIn  = session.Playlist.FadeInDuration;
            var fadeOut = session.Playlist.FadeOutDuration;
            var window  = fadeIn > fadeOut ? fadeIn : fadeOut;
            if (window <= TimeSpan.Zero) continue;

            // RemainingSeconds is EndPoint - absolute position, which is
            // what the crossfade needs (the previous "duration - position"
            // form mis-computed remaining for any track with StartPoint > 0
            // since PositionSeconds is absolute, not relative).
            var remainingSec = tvm.RemainingSeconds;
            if (double.IsPositiveInfinity(remainingSec)) continue;
            if (remainingSec > window.TotalSeconds) continue;

            // Crossfade window reached. Start the current item's fade-out
            // (its FadeOutDuration was seeded with the playlist override) and
            // immediately spawn the next item, which begins with its own
            // fade-in. The two overlap during the remaining `window` seconds.
            session.CrossfadeTriggered = true;
            tvm.Stop();
            AdvancePlaylist(session);
        }
    }

    // ── Track playback ───────────────────────────────────────────────────────

    public void PlayTrack(Track track) => PlayTrackInternal(track, fromShortcutId: null);

    public void PlayTrack(Track track, int fromShortcutId) => PlayTrackInternal(track, fromShortcutId);

    private void PlayTrackInternal(Track track, int? fromShortcutId)
    {
        // Ignore playlist-owned instances: the same Track may be running as
        // a hidden child of a playlist, and a Play-this-track click should
        // spawn an independent instance rather than steal the playlist's.
        var existing = FindStandaloneTrack(track.Id);
        if (existing != null) { existing.IsPaused = false; return; }

        // A library-track Track has no per-target chain. The only owner a
        // shortcut-of-track activation produces is the shortcut itself.
        var owners = fromShortcutId.HasValue
            ? new[] { new SamplerOwnerRef(SamplerOwnerType.Shortcut, fromShortcutId.Value) }
            : Array.Empty<SamplerOwnerRef>();
        SpawnTrack(track, onCompleted: null, owners: owners);
    }

    private PlayingTrackViewModel? FindStandaloneTrack(int trackId) =>
        ActiveItems.OfType<PlayingTrackViewModel>()
                   .FirstOrDefault(t => t.Track.Id == trackId && !t.IsPlaylistOwned);

    private PlayingPresetViewModel? FindStandalonePreset(int presetId) =>
        ActiveItems.OfType<PlayingPresetViewModel>()
                   .FirstOrDefault(p => p.Preset.Id == presetId && !p.IsPlaylistOwned);

    /// <summary>One sampler-attachment owner the engine should layer onto
    /// a track playback. Multiple owners can stack — e.g. a shortcut for
    /// a preset stacks (Preset, presetId) + (Shortcut, shortcutId).</summary>
    private readonly record struct SamplerOwnerRef(SamplerOwnerType Type, int? Id);

    private PlayingTrackViewModel? SpawnTrack(Track track, Action? onCompleted,
                                              TimeSpan? fadeInOverride = null,
                                              TimeSpan? fadeOutOverride = null,
                                              params SamplerOwnerRef[] owners)
    {
        try
        {
            var trackProvider = BuildTrackProvider(track, out var mixerInput, out var ephemeralSamplers, out var busId, owners);

            // The card's "🎛 FX" button opens the chain editor for the
            // shortcut that activated this track (Track shortcuts are the
            // only Track-level chain owner per the routing rule). A direct
            // PlayTrack from the library has no editable chain so the
            // button stays hidden via HasSamplerEditor.
            var shortcutOwner = owners.FirstOrDefault(o => o.Type == SamplerOwnerType.Shortcut);
            Action? openEditor = shortcutOwner.Type == SamplerOwnerType.Shortcut
                ? () => _samplerLauncher.Open(SamplerOwnerType.Shortcut, shortcutOwner.Id, $"Shortcut: {track.Name}")
                : null;

            var viewModel = new PlayingTrackViewModel(track, trackProvider)
            {
                AttachedSamplers = BuildBadges(owners),
                OpenSamplerEditorAction = openEditor,
            };

            // Apply fade overrides BEFORE Play() — fade-in is consumed by Play
            // and only fires once; fade-out is consumed by Stop, but storing
            // it on the VM up front means user-driven stops also honor it.
            if (fadeOutOverride.HasValue) viewModel.FadeOutDuration = fadeOutOverride.Value;
            var effectiveFadeIn = fadeInOverride ?? track.FadeInDuration;

            trackProvider.OnPlaybackStopped = () =>
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _masterMixer.RemoveMixerInput(mixerInput);
                    ActiveItems.Remove(viewModel);
                    // Defer disposal: the audio thread may still be inside
                    // Read holding a reference through the (now-removed)
                    // mixer input. MasterMixer drains pending disposals
                    // after one Read cycle elapses.
                    _masterMixer.DeferDispose(trackProvider);
                    foreach (var s in ephemeralSamplers)
                    {
                        try { _samplerChain.UnregisterEphemeral(s); } catch { }
                        _masterMixer.DeferDispose(s);
                    }
                    onCompleted?.Invoke();
                });
            };

            // Prime the provider BEFORE the mixer can read from it — otherwise
            // the audio thread may race and pull samples from position 0 with no
            // gap configured, undermining StartDelay and any custom StartPoint.
            trackProvider.Play(effectiveFadeIn);
            ActiveItems.Add(viewModel);
            _masterMixer.AddMixerInput(mixerInput, busId);

            // Diagnostic log: when a track silently fails to play (no audio,
            // no error), we need to know whether the spawn path even ran and
            // what the routing looked like. The track-provider WaveFormat
            // here is post-resample, so anything other than the master's
            // 48 kHz / 2 ch is a routing bug. Bus + master volumes are
            // logged too because a stale 0.0 value silences everything
            // without any other surface symptom.
            var masterVol = _masterMixer.LocalVolume;
            var busVol = _masterMixer.GetBus(busId)?.Volume ?? float.NaN;
            Log.Info("Playback",
                $"Spawned track #{track.Id} '{track.Name}' on bus {busId} " +
                $"(format={trackProvider.WaveFormat.SampleRate}Hz/{trackProvider.WaveFormat.Channels}ch, " +
                $"trackVol={trackProvider.Volume:0.00}, busVol={busVol:0.00}, masterVol={masterVol:0.00}).");

            return viewModel;
        }
        catch (Exception ex)
        {
            Log.Error("Playback", $"Error playing track {track.Name}", ex);
            onCompleted?.Invoke(); // don't strand a playlist if one item fails
            return null;
        }
    }

    public void StopTrack(Track track)
    {
        var existing = FindStandaloneTrack(track.Id);
        if (existing != null)
        {
            existing.IsPaused = false;
            existing.Stop();
        }
    }

    public void TogglePlayPause(Track track) => TogglePlayPauseInternal(track, fromShortcutId: null);

    /// <summary>Toggle variant that layers the shortcut's sampler chain on
    /// first-play. If the track is already playing, just toggles pause —
    /// the chain was set at spawn time.</summary>
    public void TogglePlayPause(Track track, int fromShortcutId) => TogglePlayPauseInternal(track, fromShortcutId);

    private void TogglePlayPauseInternal(Track track, int? fromShortcutId)
    {
        var existing = FindStandaloneTrack(track.Id);
        if (existing != null) existing.IsPaused = !existing.IsPaused;
        else PlayTrackInternal(track, fromShortcutId);
    }

    public bool IsTrackPlaying(Track track) =>
        FindStandaloneTrack(track.Id) is { IsPaused: false };

    public bool IsTrackPaused(Track track) =>
        FindStandaloneTrack(track.Id) is { IsPaused: true };

    // ── Preset playback ──────────────────────────────────────────────────────

    public void PlayPreset(Preset preset) => PlayPresetInternal(preset, fromShortcutId: null);

    public void PlayPreset(Preset preset, int fromShortcutId) => PlayPresetInternal(preset, fromShortcutId);

    private void PlayPresetInternal(Preset preset, int? fromShortcutId)
    {
        // Skip playlist-owned hidden instances — a direct Play should spawn
        // a fresh independent card on the mixer rather than steal the one
        // the playlist is driving (which the user can't see).
        var existing = FindStandalonePreset(preset.Id);
        if (existing != null) { existing.IsPaused = false; return; }
        SpawnPreset(preset, onCompleted: null, fromShortcutId: fromShortcutId);
    }

    private PlayingPresetViewModel? SpawnPreset(Preset preset, Action? onCompleted,
                                                TimeSpan? fadeInOverride = null,
                                                TimeSpan? fadeOutOverride = null,
                                                int? fromShortcutId = null)
    {
        // Owner stack: the preset's chain first, then the shortcut's chain
        // (if any) layered on top. Each child track gets a fresh ephemeral
        // instance per owner so multi-track presets don't share state.
        var owners = fromShortcutId.HasValue
            ? new[] { new SamplerOwnerRef(SamplerOwnerType.Preset, preset.Id),
                      new SamplerOwnerRef(SamplerOwnerType.Shortcut, fromShortcutId.Value) }
            : new[] { new SamplerOwnerRef(SamplerOwnerType.Preset, preset.Id) };

        var presetVm = new PlayingPresetViewModel(preset)
        {
            AttachedSamplers = BuildBadges(owners),
            OpenSamplerEditorAction = () => _samplerLauncher.Open(SamplerOwnerType.Preset, preset.Id, preset.Name),
        };

        foreach (var pt in preset.Tracks.OrderBy(t => t.Order))
        {
            var srcTrack = pt.Track;
            if (srcTrack == null || string.IsNullOrEmpty(srcTrack.FilePath)) continue;

            try
            {
                // The playlist-level overrides win over per-child preset fades
                // because the user is asking for a uniform fade across the
                // whole playlist run; otherwise inherit from the PresetTrack.
                var effectiveFadeIn  = fadeInOverride  ?? pt.EffectiveFadeIn;
                var effectiveFadeOut = fadeOutOverride ?? pt.EffectiveFadeOut;

                var effective = new Track
                {
                    Id = srcTrack.Id,
                    Name = srcTrack.Name,
                    FilePath = srcTrack.FilePath,
                    // Copy BusId so the bus resolver inside BuildTrackProvider
                    // sees the source track's bus when the preset doesn't set
                    // a BusIdOverride. Without this the synthesised effective
                    // track silently defaulted to the model default
                    // (BuiltInBusIds.Music), silently overriding any per-track
                    // bus the user had configured.
                    BusId = srcTrack.BusId,
                    Volume = pt.EffectiveVolume,
                    StartPoint = pt.EffectiveStartPoint,
                    EndPoint = pt.EffectiveEndPoint,
                    FadeInDuration = effectiveFadeIn,
                    FadeOutDuration = effectiveFadeOut,
                    StartDelay = pt.EffectiveStartDelay,
                    IsLooping = pt.EffectiveIsLooping
                };

                var trackProvider = BuildTrackProvider(effective, out var mixerInput, out var ephemeralSamplers,
                                                        out var busId,
                                                        owners);
                trackProvider.Volume = effective.Volume;

                var child = new PlayingPresetViewModel.Child
                {
                    PresetTrackId = pt.Id,
                    Provider = trackProvider,
                    BaseVolume = effective.Volume,
                    FadeOutDuration = effective.FadeOutDuration,
                    RemoveFromMixer = () =>
                    {
                        _masterMixer.RemoveMixerInput(mixerInput);
                        // Defer to MasterMixer's drainer so the audio
                        // thread can finish whatever Read cycle still
                        // references trackProvider before it's torn down.
                        _masterMixer.DeferDispose(trackProvider);
                        foreach (var s in ephemeralSamplers)
                        {
                            try { _samplerChain.UnregisterEphemeral(s); } catch { }
                            _masterMixer.DeferDispose(s);
                        }
                    }
                };

                trackProvider.OnPlaybackStopped = () =>
                    Dispatcher.UIThread.InvokeAsync(() => presetVm.NotifyChildStopped(child));

                // Play() first so the source is seeked and gap is set before
                // the mixer can pull samples (see comment in SpawnTrack).
                trackProvider.Play(effective.FadeInDuration);
                presetVm.AddChild(child, pt);
                _masterMixer.AddMixerInput(mixerInput, busId);
            }
            catch (Exception ex)
            {
                Log.Error("Playback", "Error spawning preset child track", ex);
            }
        }

        presetVm.AllStoppedCallback = () => Dispatcher.UIThread.InvokeAsync(() =>
        {
            ActiveItems.Remove(presetVm);
            onCompleted?.Invoke();
        });

        ActiveItems.Add(presetVm);
        return presetVm;
    }

    public void StopPreset(Preset preset)
    {
        var existing = FindStandalonePreset(preset.Id);
        existing?.Stop();
    }

    public void TogglePlayPausePreset(Preset preset) => TogglePlayPausePresetInternal(preset, fromShortcutId: null);

    public void TogglePlayPausePreset(Preset preset, int fromShortcutId) => TogglePlayPausePresetInternal(preset, fromShortcutId);

    private void TogglePlayPausePresetInternal(Preset preset, int? fromShortcutId)
    {
        var existing = FindStandalonePreset(preset.Id);
        if (existing != null) existing.IsPaused = !existing.IsPaused;
        else PlayPresetInternal(preset, fromShortcutId);
    }

    public bool IsPresetPlaying(Preset preset) =>
        FindStandalonePreset(preset.Id) is { IsPaused: false };

    public bool IsPresetPaused(Preset preset) =>
        FindStandalonePreset(preset.Id) is { IsPaused: true };

    // ── Playlist playback (sequential auto-advance) ──────────────────────────

    public void PlayPlaylist(Playlist playlist) => PlayPlaylistInternal(playlist, fromShortcutId: null);

    public void PlayPlaylist(Playlist playlist, int fromShortcutId) => PlayPlaylistInternal(playlist, fromShortcutId);

    private void PlayPlaylistInternal(Playlist playlist, int? fromShortcutId)
    {
        if (_activePlaylists.ContainsKey(playlist.Id)) return;

        // Re-query items directly from the DB rather than trusting the caller's
        // navigation collection. EF's double-Include on Playlist.Items
        // (Items.Track + Items.Preset paths) can occasionally leave
        // duplicate references in the navigation when AsSplitQuery isn't
        // used — that surfaced as "each track plays twice". A direct query
        // on PlaylistItems gives one row per PK regardless.
        List<PlaylistItem> freshItems;
        using (var db = _dbContextFactory.CreateDbContext())
        {
            freshItems = db.PlaylistItems
                .AsNoTracking()
                .Where(i => i.PlaylistId == playlist.Id)
                .Include(i => i.Track)
                .Include(i => i.Preset)
                    .ThenInclude(pr => pr!.Tracks)
                        .ThenInclude(pt => pt.Track)
                .OrderBy(i => i.Order)
                .ToList();
        }

        if (freshItems.Count == 0)
        {
            Log.Info("Playlist", $"'{playlist.Name}' has no items; nothing to play.");
            return;
        }

        Log.Info("Playlist", $"Starting '{playlist.Name}' with {freshItems.Count} item(s) " +
                              $"(autoplay={playlist.Autoplay}, random={playlist.Random})");

        // Pick the starting index. Random honors the no-repeat rule by avoiding
        // whatever played last; sequential resumes after the cursor.
        int startIndex = 0;
        _playlistCursors.TryGetValue(playlist.Id, out var lastPlayed);
        if (playlist.Random)
        {
            startIndex = PickRandomIndex(freshItems.Count,
                _playlistCursors.ContainsKey(playlist.Id) ? lastPlayed : null);
        }
        else if (_playlistCursors.ContainsKey(playlist.Id))
        {
            startIndex = (lastPlayed + 1) % freshItems.Count;
        }

        var session = new PlaylistSession
        {
            Playlist = playlist,
            Items = freshItems,
            NextIndex = startIndex,
            Random = playlist.Random,
            FromShortcutId = fromShortcutId,
        };

        // Mixer card lives in ActiveItems for the playlist's whole lifetime.
        // The child track/preset cards are hidden via IsPlaylistOwned so the
        // user controls the playlist from this one card. The VM owns a
        // detached, per-session collection of item entries — loop overrides
        // and ad-hoc reorders happen there without writing back to the DB.
        var owners = fromShortcutId.HasValue
            ? new[] { new SamplerOwnerRef(SamplerOwnerType.Playlist, playlist.Id),
                      new SamplerOwnerRef(SamplerOwnerType.Shortcut, fromShortcutId.Value) }
            : new[] { new SamplerOwnerRef(SamplerOwnerType.Playlist, playlist.Id) };
        session.ViewModel = new PlayingPlaylistViewModel(playlist, this, freshItems)
        {
            AttachedSamplers = BuildBadges(owners),
            OpenSamplerEditorAction = () => _samplerLauncher.Open(SamplerOwnerType.Playlist, playlist.Id, playlist.Name),
        };
        ActiveItems.Add(session.ViewModel);

        _activePlaylists[playlist.Id] = session;
        AdvancePlaylist(session);
    }

    /// <summary>Pick a random index in [0,count). If <paramref name="exclude"/>
    /// is set and there's more than one item, the result is guaranteed not
    /// to equal it — prevents back-to-back repeats in random mode.</summary>
    private static int PickRandomIndex(int count, int? exclude)
    {
        if (count <= 0) return 0;
        if (count == 1) return 0;
        var rng = System.Random.Shared;
        int pick;
        do { pick = rng.Next(count); } while (exclude.HasValue && pick == exclude.Value);
        return pick;
    }

    public void StopPlaylist(Playlist playlist)
    {
        if (!_activePlaylists.TryGetValue(playlist.Id, out var session)) return;
        session.Cancelled = true;
        _activePlaylists.Remove(playlist.Id);

        // Drop the playlist's mixer card immediately — the user asked to stop,
        // they shouldn't see it linger while the child fades out.
        if (session.ViewModel != null) ActiveItems.Remove(session.ViewModel);

        // Stop the currently-playing child DIRECTLY via the session's
        // tracked reference. We can't go through StopTrack / StopPreset
        // here because those lookups filter out playlist-owned items (the
        // child's card is hidden from the mixer while owned), so the
        // standalone lookup returns null and the child keeps playing.
        // The child's own OnPlaybackStopped fade-out cleanup still runs
        // because Stop() drives the same path a natural end takes.
        session.CurrentSpawned?.Stop();
    }

    public bool IsPlaylistRunning(Playlist playlist) => _activePlaylists.ContainsKey(playlist.Id);

    public bool IsPlaylistPaused(Playlist playlist)
        => _activePlaylists.TryGetValue(playlist.Id, out var session)
           && session.ViewModel != null
           && session.ViewModel.IsPaused;

    public void TogglePlayPausePlaylist(Playlist playlist) => TogglePlayPausePlaylistInternal(playlist, fromShortcutId: null);

    public void TogglePlayPausePlaylist(Playlist playlist, int fromShortcutId) => TogglePlayPausePlaylistInternal(playlist, fromShortcutId);

    private void TogglePlayPausePlaylistInternal(Playlist playlist, int? fromShortcutId)
    {
        if (_activePlaylists.TryGetValue(playlist.Id, out var session) && session.ViewModel != null)
        {
            // Setting IsPaused on the playlist VM cascades to the current
            // child via PlayingPlaylistViewModel.OnIsPausedChanged.
            session.ViewModel.IsPaused = !session.ViewModel.IsPaused;
        }
        else
        {
            PlayPlaylistInternal(playlist, fromShortcutId);
        }
    }

    public void SkipPlaylistForward(Playlist playlist)
    {
        if (!_activePlaylists.TryGetValue(playlist.Id, out var session)) return;
        // ForceNextAdvance ensures the onCompleted → AdvancePlaylist path
        // actually spawns the next item even when Autoplay is off.
        session.ForceNextAdvance = true;
        StopCurrentSessionItem(session);
    }

    public void SkipPlaylistBackward(Playlist playlist)
    {
        if (!_activePlaylists.TryGetValue(playlist.Id, out var session)) return;

        if (session.Random)
        {
            // No meaningful "back" in random mode — re-roll a new pick.
            session.ForceNextAdvance = true;
            StopCurrentSessionItem(session);
            return;
        }

        int currentIdx = session.CurrentIndex;
        if (currentIdx <= 0) return; // already at the first item, nothing to rewind to.
        int targetIdx = currentIdx - 1;

        // Order matters: resolve the currently-playing item FIRST, THEN set
        // NextIndex to the rewound position so the onCompleted →
        // AdvancePlaylist call lands on it.
        var current = session.Items[currentIdx];
        session.NextIndex = targetIdx;
        session.CurrentIndex = -1; // AdvancePlaylist will set the new value
        session.ForceNextAdvance = true;
        if (current.Track != null) StopTrack(current.Track);
        else if (current.Preset != null) StopPreset(current.Preset);
    }

    private void StopCurrentSessionItem(PlaylistSession session)
    {
        int idx = session.CurrentIndex;
        if (idx < 0 || idx >= session.Items.Count) return;
        var current = session.Items[idx];
        if (current.Track != null) StopTrack(current.Track);
        else if (current.Preset != null) StopPreset(current.Preset);
    }

    public void StopAll()
    {
        // Cancel every running playlist first so its advance-callback doesn't
        // try to chain into the next item after we tear everything down — AND
        // drop the playlist's mixer card here while we still know which one
        // belongs to which session. Doing this in the broader Stop loop below
        // wouldn't work: PlayingPlaylistViewModel.Stop() routes through
        // StopPlaylist(), which would early-return because _activePlaylists
        // was already cleared, leaving the card orphaned in ActiveItems with
        // no way to control or remove it.
        foreach (var session in _activePlaylists.Values)
        {
            session.Cancelled = true;
            if (session.ViewModel != null) ActiveItems.Remove(session.ViewModel);
        }
        _activePlaylists.Clear();

        // Snapshot — Stop() triggers async removal from ActiveItems via the
        // mixer's stopped callbacks, which would mutate the collection underneath us.
        // Playlist child tracks/presets are still here; their OnPlaybackStopped
        // callbacks will route through AdvancePlaylist which sees Cancelled
        // and exits cleanly.
        foreach (var item in ActiveItems.ToList()) item.Stop();
    }

    private void AdvancePlaylist(PlaylistSession session)
    {
        if (session.Cancelled) return;

        // Autoplay-off: after the first item finishes, stop the session unless
        // a Skip button forced this advance.
        bool force = session.ForceNextAdvance;
        session.ForceNextAdvance = false;
        if (session.CurrentIndex >= 0 && !session.Playlist.Autoplay && !force)
        {
            _playlistCursors[session.Playlist.Id] = session.CurrentIndex;
            _activePlaylists.Remove(session.Playlist.Id);
            if (session.ViewModel != null) ActiveItems.Remove(session.ViewModel);
            return;
        }

        // Pick the next index. Random mode re-rolls (avoiding the last
        // played); sequential mode uses NextIndex and increments.
        int currentIndex;
        if (session.Random)
        {
            int? exclude = session.CurrentIndex >= 0 ? session.CurrentIndex : null;
            currentIndex = PickRandomIndex(session.Items.Count, exclude);
            // Keep NextIndex in lockstep so any downstream code reading it
            // (Skip-backward bails out anyway) doesn't see a stale value.
            session.NextIndex = currentIndex + 1;
        }
        else
        {
            if (session.NextIndex >= session.Items.Count)
            {
                _activePlaylists.Remove(session.Playlist.Id);
                // Natural end — drop the playlist card from the mixer.
                if (session.ViewModel != null) ActiveItems.Remove(session.ViewModel);
                return;
            }
            currentIndex = session.NextIndex++;
        }

        session.CurrentIndex = currentIndex;
        session.CrossfadeTriggered = false;
        _playlistCursors[session.Playlist.Id] = currentIndex;
        var item = session.Items[currentIndex];

        // Per-session loop override: the VM's entry for this slot may have
        // had its IsLooping flipped via the playlist card's flyout. Honor it
        // when spawning rather than reading directly off the source track.
        bool? loopOverride = session.ViewModel?.GetLoopOverrideAt(currentIndex);

        // Playlist-wide fades apply to every item that starts under the
        // playlist. Zero on the playlist means "no override — fall back to
        // the track/preset's own fades".
        TimeSpan? fadeInOverride  = session.Playlist.FadeInDuration  > TimeSpan.Zero
                                       ? session.Playlist.FadeInDuration  : null;
        TimeSpan? fadeOutOverride = session.Playlist.FadeOutDuration > TimeSpan.Zero
                                       ? session.Playlist.FadeOutDuration : null;

        // Snapshot this spawn's index so the onCompleted callback can detect
        // whether the session already advanced past it (e.g. via a telemetry-
        // driven crossfade pre-spawn). Without this guard, the current item's
        // natural-end callback would fire AdvancePlaylist a second time and
        // skip an item.
        int spawnIndex = currentIndex;
        Action onItemCompleted = () =>
        {
            if (session.Cancelled) return;
            if (session.CurrentIndex != spawnIndex) return; // already advanced
            AdvancePlaylist(session);
        };

        IActiveMixerItem? spawned = null;
        if (item.Track != null)
        {
            // Stack: (Playlist) then optional (Shortcut) layered on top so
            // a shortcut that drives a playlist also applies its own samplers
            // to each child track the playlist plays.
            var childOwners = session.FromShortcutId.HasValue
                ? new[] { new SamplerOwnerRef(SamplerOwnerType.Playlist, session.Playlist.Id),
                          new SamplerOwnerRef(SamplerOwnerType.Shortcut, session.FromShortcutId.Value) }
                : new[] { new SamplerOwnerRef(SamplerOwnerType.Playlist, session.Playlist.Id) };
            spawned = SpawnTrack(item.Track,
                onCompleted: onItemCompleted,
                fadeInOverride: fadeInOverride,
                fadeOutOverride: fadeOutOverride,
                owners: childOwners);
            if (spawned is PlayingTrackViewModel tvm && loopOverride.HasValue)
                tvm.IsLooping = loopOverride.Value; // cascades to provider
        }
        else if (item.Preset != null)
        {
            spawned = SpawnPreset(item.Preset,
                onCompleted: onItemCompleted,
                fadeInOverride: fadeInOverride,
                fadeOutOverride: fadeOutOverride,
                fromShortcutId: session.FromShortcutId);
            if (spawned is PlayingPresetViewModel pvm && loopOverride.HasValue)
                pvm.SetAllChildrenLoop(loopOverride.Value);
        }
        else
        {
            // Empty item — skip immediately.
            AdvancePlaylist(session);
            return;
        }

        // Mark the spawned child as playlist-owned so the mixer hides its
        // individual card.
        if (spawned is PlayingTrackViewModel tvm2) tvm2.IsPlaylistOwned = true;
        else if (spawned is PlayingPresetViewModel pvm2) pvm2.IsPlaylistOwned = true;

        session.CurrentSpawned = spawned;

        var currentName = item.Track?.Name ?? item.Preset?.Name ?? "(empty)";
        string? nextName = null;
        if (session.NextIndex < session.Items.Count)
        {
            var nextItem = session.Items[session.NextIndex];
            nextName = nextItem.Track?.Name ?? nextItem.Preset?.Name;
        }
        session.ViewModel?.NotifyAdvanced(spawned, currentName, nextName, currentIndex, session.Items.Count);
    }

    public void SetPlaylistItemLoop(Playlist playlist, int itemIndex, bool isLooping)
    {
        if (!_activePlaylists.TryGetValue(playlist.Id, out var session)) return;
        if (itemIndex < 0 || itemIndex >= session.Items.Count) return;

        // Push the new value down to the entry so AdvancePlaylist sees it on
        // future spawns. If this is the CURRENTLY playing item, also apply
        // live to the running provider so the change takes effect immediately.
        session.ViewModel?.SetEntryLoop(itemIndex, isLooping);

        int currentIdx = session.CurrentIndex;
        if (itemIndex != currentIdx) return;

        // Live-apply to whatever's currently playing for this slot.
        var current = session.Items[currentIdx];
        if (current.Track != null)
        {
            var live = ActiveItems.OfType<PlayingTrackViewModel>()
                .FirstOrDefault(t => t.IsPlaylistOwned && t.Track.Id == current.Track.Id);
            if (live != null) live.IsLooping = isLooping;
        }
        else if (current.Preset != null)
        {
            var live = ActiveItems.OfType<PlayingPresetViewModel>()
                .FirstOrDefault(p => p.IsPlaylistOwned && p.Preset.Id == current.Preset.Id);
            live?.SetAllChildrenLoop(isLooping);
        }
    }

    public void MovePlaylistItem(Playlist playlist, int fromIndex, int toIndex)
    {
        if (!_activePlaylists.TryGetValue(playlist.Id, out var session)) return;
        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= session.Items.Count) return;
        if (toIndex   < 0 || toIndex   >= session.Items.Count) return;

        // Only upcoming items (index >= NextIndex) may move — past history is
        // immutable, and we never reorder across the played/upcoming boundary.
        if (fromIndex < session.NextIndex) return;
        if (toIndex   < session.NextIndex) return;

        var item = session.Items[fromIndex];
        session.Items.RemoveAt(fromIndex);
        session.Items.Insert(toIndex, item);

        session.ViewModel?.NotifyItemsReordered(fromIndex, toIndex);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private TrackSampleProvider BuildTrackProvider(Track track, out ISampleProvider mixerInput, out IReadOnlyList<ISamplerInstance> attachedSamplers,
                                                   params SamplerOwnerRef[] owners)
    {
        return BuildTrackProvider(track, out mixerInput, out attachedSamplers, out _, owners);
    }

    /// <summary>Build the per-track playback chain and report which audio
    /// bus the resulting provider should be routed to. Bus resolution
    /// follows the precedence chain:
    /// <list type="number">
    ///   <item>Shortcut <see cref="ShortcutButton.BusIdOverride"/> (when a
    ///   Track-spawn was initiated by a shortcut and the shortcut sets
    ///   one — Preset and Playlist shortcuts NEVER override the bus per
    ///   the design spec, those defer to their target's own routing).</item>
    ///   <item>Preset <see cref="Preset.BusIdOverride"/> (when this track
    ///   is being spawned as a preset child and the preset sets one).</item>
    ///   <item>The track's own <see cref="Track.BusId"/> (the fallback).</item>
    /// </list>
    /// Playlists deliberately do NOT have a bus override field — per the
    /// design, each playlist item plays on its track's own bus.</summary>
    private TrackSampleProvider BuildTrackProvider(Track track, out ISampleProvider mixerInput,
                                                   out IReadOnlyList<ISamplerInstance> attachedSamplers,
                                                   out int busId,
                                                   params SamplerOwnerRef[] owners)
    {
        var seekable = AudioFileReaderCrossPlatform.Create(track.FilePath);

        var trackProvider = new TrackSampleProvider(seekable)
        {
            Volume = 1.0f,
            StartPoint = track.StartPoint ?? TimeSpan.Zero,
            EndPoint = track.EndPoint ?? seekable.TotalTime,
            IsLooping = track.IsLooping,
            StartDelay = track.StartDelay
        };

        ISampleProvider chain = trackProvider;
        if (trackProvider.WaveFormat.SampleRate != _masterMixer.WaveFormat.SampleRate)
            chain = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(trackProvider, _masterMixer.WaveFormat.SampleRate);

        if (chain.WaveFormat.Channels != _masterMixer.WaveFormat.Channels
            && _masterMixer.WaveFormat.Channels == 2 && chain.WaveFormat.Channels == 1)
        {
            chain = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(chain);
        }

        // Apply per-owner samplers AFTER format conversion so each instance
        // sees the mixer-format buffer. Owners are stacked in order — a
        // shortcut for a preset gets (Preset, presetId), then (Shortcut,
        // shortcutId) so the shortcut's chain wraps the preset's chain.
        // Master-tier samplers are applied at the mixer bus, not here, so
        // the engine never passes a Master owner here.
        var combined = new List<ISamplerInstance>();
        foreach (var owner in owners)
        {
            if (owner.Type == SamplerOwnerType.Master) continue; // defensive
            combined.AddRange(_samplerChain.BuildEphemeralChain(owner.Type, owner.Id));
        }
        attachedSamplers = combined;

        foreach (var sampler in attachedSamplers)
        {
            try
            {
                var next = sampler.CreateEffect(chain);
                if (next != null) chain = next;
            }
            catch (Exception ex)
            {
                Log.Error("Sampler", $"Plugin instance threw from CreateEffect — skipping in chain.", ex);
            }
        }

        mixerInput = chain;
        busId = ResolveBusId(track, owners);
        return trackProvider;
    }

    /// <summary>Resolve which bus the spawned track should route to. See
    /// the precedence rules in <see cref="BuildTrackProvider"/>'s xmldoc.
    /// Reads the override fields fresh from the DB so a recently-edited
    /// preset or shortcut takes effect on the next play without needing
    /// to re-launch the app. Falls back to <see cref="Track.BusId"/> on
    /// any miss (deleted row, null override, unknown owner type).</summary>
    private int ResolveBusId(Track track, SamplerOwnerRef[] owners)
    {
        // Shortcut override wins ONLY for Track-targeting shortcuts — the
        // ShortcutButtonViewModel.Click path passes the shortcut owner for
        // a Track click but routes through SpawnPreset / SpawnPlaylist for
        // those target types (which never set BusIdOverride per the spec).
        // We can use the same heuristic here: if the owner stack includes
        // a Shortcut AND nothing else above it (no Preset/Playlist owner
        // intercepting), check the shortcut's BusIdOverride.
        var shortcutOwner = owners.FirstOrDefault(o => o.Type == SamplerOwnerType.Shortcut);
        bool hasPresetOrPlaylistAbove = owners.Any(o =>
            o.Type == SamplerOwnerType.Preset || o.Type == SamplerOwnerType.Playlist);

        using var db = _dbContextFactory.CreateDbContext();

        if (shortcutOwner.Type == SamplerOwnerType.Shortcut && !hasPresetOrPlaylistAbove && shortcutOwner.Id.HasValue)
        {
            var btn = db.ShortcutButtons.AsNoTracking().FirstOrDefault(b => b.Id == shortcutOwner.Id.Value);
            if (btn?.BusIdOverride is int sBus) return sBus;
        }

        var presetOwner = owners.FirstOrDefault(o => o.Type == SamplerOwnerType.Preset);
        if (presetOwner.Type == SamplerOwnerType.Preset && presetOwner.Id.HasValue)
        {
            var preset = db.Presets.AsNoTracking().FirstOrDefault(p => p.Id == presetOwner.Id.Value);
            if (preset?.BusIdOverride is int pBus) return pBus;
        }

        return track.BusId;
    }

    /// <summary>Backwards-compatible overload — called by existing sites
    /// that don't yet know about per-owner samplers (master playback).</summary>
    private TrackSampleProvider BuildTrackProvider(Track track, out ISampleProvider mixerInput)
    {
        return BuildTrackProvider(track, out mixerInput, out _);
    }

    /// <summary>Resolve human-readable plugin names for the combined chain
    /// of samplers attached to the given owners. Used by the mixer card
    /// to render small chips ("Reverb", "Mute (Sample)") under the title.
    /// Reads attachment rows fresh — same source of truth as the audio
    /// path's BuildEphemeralChain — so a row whose plugin isn't loaded
    /// shows up as "(missing plugin)". Owners are walked in stack order
    /// so chips display in the same order the chain runs.</summary>
    private IReadOnlyList<SamplerBadge> BuildBadges(params SamplerOwnerRef[] owners)
    {
        var badges = new List<SamplerBadge>();
        foreach (var owner in owners)
        {
            if (owner.Type == SamplerOwnerType.Master) continue;
            var rows = _samplerChain.GetAttachments(owner.Type, owner.Id).Where(r => !r.IsBypassed).ToList();
            foreach (var row in rows)
            {
                var plugin = _pluginService.LoadedPlugins.OfType<IAudioSamplerPlugin>().FirstOrDefault(p => p.Id == row.PluginId);
                var label = plugin?.Name ?? "(missing plugin)";
                badges.Add(new SamplerBadge(label, row.PluginId));
            }
        }
        return badges;
    }

    public void Dispose()
    {
        _telemetryTimer.Stop();
    }
}
