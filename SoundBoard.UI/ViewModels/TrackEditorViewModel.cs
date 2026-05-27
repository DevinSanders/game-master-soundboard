using SoundBoard.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using SoundBoard.UI.Services;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Backs the Track Editor window — edits the library defaults for one
/// <see cref="Track"/> (volume, fade in/out, start delay, range, looping,
/// icon, tags). Also hosts the static-waveform preview, the "trim silence
/// now" action, and any <see cref="Plugins.IUIExtensionPlugin"/> controls
/// placed in the "track editor" slot.
///
/// Every editable field auto-persists through <see cref="Persistence"/> —
/// the editor has no Save button. Slider drags suspend the debounce until
/// the user releases the thumb; non-slider edits (textbox, toggle) flush
/// on the debounce timer or when the window closes.
/// </summary>
public partial class TrackEditorViewModel : ViewModelBase, IDisposable
{
    private readonly ISoundBoardDbContextFactory _dbFactory;
    private readonly IAudioPlaybackEngine _playbackEngine;
    private readonly SoundBoard.Core.Services.IPluginService _pluginService;

    /// <summary>Debounced save coordinator. The view wires its sliders to
    /// this so drags don't write to SQLite per pixel of motion.</summary>
    public EditPersistence Persistence { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<object> PluginControls { get; } = new();

    [ObservableProperty]
    private Track? _track;

    [ObservableProperty]
    private double _totalDurationSeconds;

    // ── Track field shims (POCO needs observable + persisting wrappers) ──

    public string TrackName
    {
        get => Track?.Name ?? "";
        set
        {
            if (Track == null || Track.Name == value) return;
            Track.Name = value;
            ScheduleSave();
            OnPropertyChanged();
        }
    }

    public string TrackTags
    {
        get => Track?.Tags ?? "";
        set
        {
            if (Track == null || Track.Tags == value) return;
            Track.Tags = value;
            ScheduleSave();
            OnPropertyChanged();
        }
    }

    public double TrackVolume
    {
        get => Track?.Volume ?? 1.0;
        set
        {
            if (Track == null || System.Math.Abs(Track.Volume - value) < 0.0001) return;
            Track.Volume = (float)value;
            ScheduleSave();
            OnPropertyChanged();
        }
    }

    public bool TrackIsLooping
    {
        get => Track?.IsLooping ?? false;
        set
        {
            if (Track == null || Track.IsLooping == value) return;
            Track.IsLooping = value;
            ScheduleSave();
            OnPropertyChanged();
        }
    }

    // ── Bus routing ─────────────────────────────────────────────────
    //
    // The bus dropdown reads from <see cref="AvailableBuses"/> (a one-shot
    // snapshot of the Buses table at editor-open time) and writes through
    // to <see cref="Track.BusId"/>. Selected-bus lookup goes by id rather
    // than reference so dropdown re-population on focus-loss doesn't drop
    // the selection.

    /// <summary>Snapshot of every configured bus, in <see cref="Bus.Order"/>.
    /// Populated on Track-load. Doesn't observe live changes — the user
    /// would have to reopen the editor after editing the Buses page.
    /// Acceptable v1 trade-off; the Buses page is rare-use UI.</summary>
    public System.Collections.ObjectModel.ObservableCollection<Bus> AvailableBuses { get; } = new();

    /// <summary>Selected bus id. Two-way bound to the dropdown via
    /// <c>SelectedValue</c> + <c>SelectedValuePath="Id"</c>.</summary>
    public int TrackBusId
    {
        get => Track?.BusId ?? BuiltInBusIds.DefaultForNewTracks;
        set
        {
            if (Track == null || Track.BusId == value) return;
            Track.BusId = value;
            ScheduleSave();
            OnPropertyChanged();
        }
    }

    private void LoadAvailableBuses()
    {
        AvailableBuses.Clear();
        using var db = _dbFactory.CreateDbContext();
        foreach (var bus in db.Buses.OrderBy(b => b.Order).ThenBy(b => b.Id))
            AvailableBuses.Add(bus);
    }

    // ── Fade Properties ───────────────────────────────────────

    public double FadeInSeconds
    {
        get => Track?.FadeInDuration.TotalSeconds ?? 0;
        set
        {
            if (Track != null)
            {
                Track.FadeInDuration = TimeSpan.FromSeconds(value);
                ScheduleSave();
                OnPropertyChanged(nameof(FadeInSeconds));
            }
        }
    }

    public double FadeOutSeconds
    {
        get => Track?.FadeOutDuration.TotalSeconds ?? 0;
        set
        {
            if (Track != null)
            {
                Track.FadeOutDuration = TimeSpan.FromSeconds(value);
                ScheduleSave();
                OnPropertyChanged(nameof(FadeOutSeconds));
            }
        }
    }

    public double StartDelaySeconds
    {
        get => Track?.StartDelay.TotalSeconds ?? 0;
        set
        {
            if (Track != null)
            {
                Track.StartDelay = TimeSpan.FromSeconds(value);
                ScheduleSave();
                OnPropertyChanged(nameof(StartDelaySeconds));
            }
        }
    }

    // ── Start / End Point Properties ──────────────────────────

    public double StartPointSeconds
    {
        get => Track?.StartPoint?.TotalSeconds ?? 0;
        set
        {
            if (Track != null)
            {
                Track.StartPoint = value > 0 ? TimeSpan.FromSeconds(value) : null;
                ScheduleSave();
                OnPropertyChanged(nameof(StartPointSeconds));
                OnPropertyChanged(nameof(StartPointDisplay));
                OnPropertyChanged(nameof(PlayableLengthDisplay));
            }
        }
    }

    public double EndPointSeconds
    {
        get => Track?.EndPoint?.TotalSeconds ?? TotalDurationSeconds;
        set
        {
            if (Track != null)
            {
                // If it's near the end, we can keep it as null (play to end) or just save it
                Track.EndPoint = (value > 0 && value < TotalDurationSeconds - 0.1) ? TimeSpan.FromSeconds(value) : null;
                ScheduleSave();
                OnPropertyChanged(nameof(EndPointSeconds));
                OnPropertyChanged(nameof(EndPointDisplay));
                OnPropertyChanged(nameof(PlayableLengthDisplay));
            }
        }
    }

    /// <summary>Formatted display of the start point. Routed through
    /// <see cref="SoundBoard.UI.Converters.DurationDisplayConverter"/> so
    /// the tenths-of-seconds precision matches every other duration
    /// display in the app (the shared TimeSpan-formatting rule).</summary>
    public string StartPointDisplay =>
        SoundBoard.UI.Converters.DurationDisplayConverter.Format(TimeSpan.FromSeconds(StartPointSeconds));

    /// <summary>Formatted display of the end point.</summary>
    public string EndPointDisplay =>
        SoundBoard.UI.Converters.DurationDisplayConverter.Format(
            TimeSpan.FromSeconds(EndPointSeconds > 0 ? EndPointSeconds : TotalDurationSeconds));

    /// <summary>Compact display of the playable length — the portion
    /// between StartPoint and EndPoint that the user will actually hear.
    /// Updates live as the range sliders move; routed through the shared
    /// formatter so the precision (1 decimal of seconds) stays in lockstep
    /// with the rest of the app.</summary>
    public string PlayableLengthDisplay
    {
        get
        {
            var end = EndPointSeconds > 0 ? EndPointSeconds : TotalDurationSeconds;
            var len = end - StartPointSeconds;
            if (!double.IsFinite(len) || len < 0) len = 0;
            return SoundBoard.UI.Converters.DurationDisplayConverter.Format(TimeSpan.FromSeconds(len));
        }
    }

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Status text shown next to the "Trim silence now" button.
    /// Updates while the scan runs and reports the result.</summary>
    [ObservableProperty]
    private string _trimStatus = string.Empty;

    /// <summary>Observable shim over <see cref="Track.Icon"/> — Track is a POCO
    /// so direct bindings don't refresh on icon changes. Editor preview binds
    /// to this property instead.</summary>
    public string? TrackIcon
    {
        get => Track?.Icon;
        set
        {
            if (Track != null && Track.Icon != value)
            {
                Track.Icon = value;
                ScheduleSave();
                OnPropertyChanged();
            }
        }
    }

    public void SetIcon(string? icon)
    {
        TrackIcon = icon;
    }

    public TrackEditorViewModel(ISoundBoardDbContextFactory dbFactory, IAudioPlaybackEngine playbackEngine, SoundBoard.Core.Services.IPluginService pluginService)
    {
        _dbFactory = dbFactory;
        _playbackEngine = playbackEngine;
        _pluginService = pluginService;

        LoadPluginControls();
    }

    /// <summary>Flush any pending debounced writes and tear down the
    /// <see cref="EditPersistence"/> timer. Called by
    /// <see cref="WindowManagerService"/> on window close and on
    /// swap-replace. Without this, the helper's <see cref="DispatcherTimer"/>
    /// would keep the VM (and its captured <see cref="Track"/>, plugin
    /// controls, and DB factory) rooted via the dispatcher's timer list
    /// — every Track editor open would leak one VM.</summary>
    public void Dispose()
    {
        try { Persistence.Dispose(); }
        catch (Exception ex) { Log.Warn("TrackEditorVM", $"Persistence.Dispose threw: {ex.Message}"); }
    }

    private void LoadPluginControls()
    {
        // Sampler plugins no longer surface global UI here — per-instance UIs
        // live behind the sampler editor for each owner (shortcut / preset /
        // playlist / master). This list only hosts non-DSP UI extensions.
        foreach (var control in _pluginService.GetExtensionControls(SoundBoard.PluginApi.UIPlacement.TrackEditor))
            PluginControls.Add(control);
    }

    partial void OnTrackChanged(Track? value)
    {
        if (value == null)
        {
            TotalDurationSeconds = 300;
            NotifyAll();
            return;
        }

        LoadAvailableBuses();
        _ = LoadTrackInfoAsync(value);
    }

    private async Task LoadTrackInfoAsync(Track track)
    {
        IsBusy = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(track.FilePath) && File.Exists(track.FilePath))
            {
                await Task.Run(() =>
                {
                    var reader = AudioFileReaderCrossPlatform.Create(track.FilePath);
                    try
                    {
                        TotalDurationSeconds = reader.TotalTime.TotalSeconds;
                    }
                    finally
                    {
                        if (reader is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }
                });
            }
            else
            {
                TotalDurationSeconds = 300;
            }
        }
        catch
        {
            TotalDurationSeconds = 300;
        }
        finally
        {
            IsBusy = false;
            NotifyAll();
        }
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(TrackName));
        OnPropertyChanged(nameof(TrackTags));
        OnPropertyChanged(nameof(TrackVolume));
        OnPropertyChanged(nameof(TrackIsLooping));
        OnPropertyChanged(nameof(FadeInSeconds));
        OnPropertyChanged(nameof(FadeOutSeconds));
        OnPropertyChanged(nameof(StartDelaySeconds));
        OnPropertyChanged(nameof(TrackIcon));
        OnPropertyChanged(nameof(TrackBusId));
        OnPropertyChanged(nameof(StartPointSeconds));
        OnPropertyChanged(nameof(EndPointSeconds));
        OnPropertyChanged(nameof(StartPointDisplay));
        OnPropertyChanged(nameof(EndPointDisplay));
        OnPropertyChanged(nameof(PlayableLengthDisplay));
    }

    /// <summary>Schedule a debounced save for the current Track. Single
    /// key per track-id so a hundred slider ticks collapse to one DB write.
    /// The closure reads the in-memory model at flush time so it always
    /// persists the latest values.</summary>
    private void ScheduleSave()
    {
        if (Track == null) return;
        var id = Track.Id;
        Persistence.Schedule($"Track:{id}", () =>
        {
            if (Track == null) return;
            bool displayChanged = false;
            _dbFactory.EditorSave<Core.Models.Track>(id, tracked =>
            {
                // Detect a user-visible change so we can notify other open
                // windows (Library list, Playlist editor rows, Preset cards)
                // that they should refresh. Volume / fade / loop / start-end
                // changes don't surface anywhere else and would otherwise
                // trigger a wave of reloads on every slider tick.
                displayChanged =
                    tracked.Name != Track.Name ||
                    tracked.Icon != Track.Icon ||
                    tracked.Tags != Track.Tags;

                tracked.Name = Track.Name;
                tracked.Tags = Track.Tags;
                tracked.Icon = Track.Icon;
                tracked.Volume = Track.Volume;
                tracked.IsLooping = Track.IsLooping;
                tracked.FadeInDuration = Track.FadeInDuration;
                tracked.FadeOutDuration = Track.FadeOutDuration;
                tracked.StartDelay = Track.StartDelay;
                tracked.StartPoint = Track.StartPoint;
                tracked.EndPoint = Track.EndPoint;
                tracked.BusId = Track.BusId;
            });

            if (displayChanged)
            {
                // LibraryRefreshedMessage is the existing fan-out vehicle
                // — Library, Playlists, Presets all have handlers that
                // reload on receipt. A targeted TrackMutated message
                // would be cheaper but requires every consumer to grow
                // a new subscription. Phase 4 refactor target.
                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default
                    .Send(new Messages.LibraryRefreshedMessage());
            }
        });
    }

    [RelayCommand]
    private void ResetStartPoint()
    {
        StartPointSeconds = 0;
    }

    [RelayCommand]
    private void ResetEndPoint()
    {
        EndPointSeconds = TotalDurationSeconds;
    }

    [RelayCommand]
    private void Preview()
    {
        if (Track != null)
        {
            // Flush pending edits so the engine reads the latest settings.
            Persistence.Flush();
            _playbackEngine.TogglePlayPause(Track);
        }
    }

    /// <summary>Scan the track's audio for leading + trailing silence and
    /// write the suggested non-silent range into <see cref="Track.StartPoint"/>
    /// / <see cref="Track.EndPoint"/>. Runs on a background thread because
    /// the scan touches every sample in the file; the user can still adjust
    /// the points manually afterward.
    ///
    /// Robustness: every exit path resets <see cref="IsBusy"/> so the wait
    /// cursor and disabled button can never stick. Background work runs on a
    /// detached thread (not <see cref="Task.Run"/>) so a stalled scan can't
    /// also block the thread pool; if the scan never finishes the UI is
    /// still recoverable by closing/reopening the editor.</summary>
    [RelayCommand]
    private async Task TrimSilence()
    {
        if (Track == null || string.IsNullOrWhiteSpace(Track.FilePath) || !File.Exists(Track.FilePath))
        {
            TrimStatus = "No audio file to scan.";
            return;
        }

        var trackPath = Track.FilePath;
        var trackName = Track.Name;
        Log.Info("TrimSilence", $"Starting scan of '{trackName}' ({trackPath})");

        IsBusy = true;
        TrimStatus = "Scanning…";

        var tcs = new TaskCompletionSource<(TimeSpan start, TimeSpan end)>();

        // Share the reader between the scan thread and the watchdog so a
        // timeout can dispose it — that's the only way to unstick NVorbis
        // when it's hung inside ReadSamples on a corrupt frame.
        ISeekableSampleProvider? sharedReader = null;

        var scanThread = new Thread(() =>
        {
            ISeekableSampleProvider? localReader = null;
            try
            {
                localReader = AudioFileReaderCrossPlatform.Create(trackPath);
                System.Threading.Volatile.Write(ref sharedReader, localReader);
                SilenceTrimmer.TrimSilence(localReader, out var s, out var e);
                tcs.TrySetResult((s, e));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                if (localReader is IDisposable d)
                {
                    try { d.Dispose(); } catch { /* already disposed by watchdog */ }
                }
            }
        }) { IsBackground = true, Name = "SilenceTrimmer" };
        scanThread.Start();

        // Hard timeout so a buggy decoder (we've seen NVorbis chew on
        // specific .ogg files indefinitely) can't leave the editor's UI
        // disabled forever. On timeout we dispose the reader from this
        // thread — disposing while NVorbis is mid-read makes the next
        // sample access throw, which unwinds the scan thread cleanly.
        var timeout = Task.Delay(TimeSpan.FromSeconds(30));
        try
        {
            var winner = await Task.WhenAny(tcs.Task, timeout).ConfigureAwait(true);
            if (winner == timeout)
            {
                TrimStatus = "Scan timed out after 30s; track left unchanged.";
                Log.Warn("TrimSilence", $"Scan of '{trackName}' exceeded 30s timeout — disposing reader to unstick the decoder.");
                var stuckReader = System.Threading.Volatile.Read(ref sharedReader);
                if (stuckReader is IDisposable d)
                {
                    try { d.Dispose(); }
                    catch (Exception ex) { Log.Warn("TrimSilence", $"Reader dispose threw: {ex.Message}"); }
                }
                return;
            }
            var (start, end) = await tcs.Task.ConfigureAwait(true);

            // Sanity check the scanner's output before writing into TimeSpan
            // setters — a NaN/Infinity would throw OverflowException and bomb
            // the entire trim flow.
            double startSec = start.TotalSeconds;
            double endSec   = end.TotalSeconds;
            if (!double.IsFinite(startSec) || !double.IsFinite(endSec) || endSec <= startSec)
            {
                TrimStatus = "Scan produced an invalid range; track left unchanged.";
                Log.Warn("TrimSilence", $"Invalid scan result for '{trackName}': start={startSec}s end={endSec}s");
                return;
            }

            // Push the change through the bindable accessors so the
            // sliders refresh and the debounced save fires.
            StartPointSeconds = startSec;
            EndPointSeconds   = endSec;
            var startFmt = SoundBoard.UI.Converters.DurationDisplayConverter.Format(start);
            var endFmt   = SoundBoard.UI.Converters.DurationDisplayConverter.Format(end);
            TrimStatus = $"Trimmed to {startFmt}–{endFmt}.";
            Log.Info("TrimSilence", $"Scan complete for '{trackName}': {startFmt}–{endFmt}");
        }
        catch (Exception ex)
        {
            TrimStatus = $"Scan failed: {ex.Message}";
            Log.Error("TrimSilence", $"Scan failed for '{trackName}'", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
