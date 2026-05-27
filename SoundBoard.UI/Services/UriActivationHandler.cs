using SoundBoard.Core.Services;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using SoundBoard.Core.Activation;
using SoundBoard.Core.Data;
using SoundBoard.Core.Logging;
using SoundBoard.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace SoundBoard.UI.Services;

/// <summary>
/// Resolves <c>gmsound://</c> URIs against the database and dispatches them
/// through <see cref="IAudioPlaybackEngine"/>. Invoked from
/// <c>SoundBoard.Desktop.Program</c> at startup and on second-instance
/// activations forwarded via the named pipe.
///
/// All engine calls are marshalled to the UI thread because we may be invoked
/// from the named-pipe listener's background thread.
/// </summary>
public class UriActivationHandler
{
    private readonly IAudioPlaybackEngine _engine;
    private readonly ISoundBoardDbContextFactory _dbFactory;

    /// <summary>URIs observed at launch (Program.cs) before the DI container
    /// is built. App.OnFrameworkInitializationCompleted drains this once
    /// services are ready.</summary>
    public static ConcurrentQueue<string> PendingUris { get; } = new();

    public UriActivationHandler(IAudioPlaybackEngine engine, ISoundBoardDbContextFactory dbFactory)
    {
        _engine = engine;
        _dbFactory = dbFactory;
    }

    /// <summary>Pull every queued URI through <see cref="Handle"/>.</summary>
    public void DrainPending()
    {
        while (PendingUris.TryDequeue(out var pending))
            Handle(pending);
    }

    /// <summary>Parse + execute a single URI string. Returns true if it was a
    /// recognized gmsound URI (regardless of whether the lookup succeeded).</summary>
    public bool Handle(string uriText)
    {
        if (!SoundboardUri.TryParse(uriText, out var uri))
            return false;

        Dispatcher.UIThread.Post(() => Execute(uri));
        return true;
    }

    private void Execute(SoundboardUri uri)
    {
        try
        {
            if (uri.StopPlaying) _engine.StopAll();

            switch (uri.Action)
            {
                case SoundboardUriAction.StopAll:
                    _engine.StopAll();
                    return;

                case SoundboardUriAction.Play:
                    PlayItem(uri);
                    return;

                case SoundboardUriAction.Stop:
                    StopItem(uri);
                    return;

                case SoundboardUriAction.Toggle:
                    ToggleItem(uri);
                    return;

                case SoundboardUriAction.Next:
                case SoundboardUriAction.Previous:
                    SkipPlaylist(uri);
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Error("URI", $"Activation failed for {uri}", ex);
        }
    }

    private void SkipPlaylist(SoundboardUri uri)
    {
        if (uri.ItemType != SoundboardUriItemType.Playlist || uri.ItemId is null)
        {
            Log.Warn("URI", $"{uri.Action} only valid on playlists");
            return;
        }
        using var db = _dbFactory.CreateDbContext();
        if (db.Playlists.AsNoTracking().FirstOrDefault(pl => pl.Id == uri.ItemId) is not { } pl) return;

        if (uri.Action == SoundboardUriAction.Next) _engine.SkipPlaylistForward(pl);
        else                                        _engine.SkipPlaylistBackward(pl);
    }

    private void PlayItem(SoundboardUri uri)
    {
        if (uri.ItemType is null || uri.ItemId is null) return;
        using var db = _dbFactory.CreateDbContext();

        switch (uri.ItemType)
        {
            case SoundboardUriItemType.Track:
            {
                var track = db.Tracks.AsNoTracking().FirstOrDefault(t => t.Id == uri.ItemId);
                if (track == null) { LogMissing("track", uri.ItemId.Value); return; }

                // Per-activation overrides — clone so we don't mutate the
                // library row. Engine reads these directly.
                if (uri.Volume.HasValue)    track.Volume = uri.Volume.Value;
                if (uri.Loop.HasValue)      track.IsLooping = uri.Loop.Value;
                if (uri.FadeIn.HasValue)    track.FadeInDuration = uri.FadeIn.Value;
                if (uri.FadeOut.HasValue)   track.FadeOutDuration = uri.FadeOut.Value;
                if (uri.StartDelay.HasValue) track.StartDelay = uri.StartDelay.Value;

                _engine.PlayTrack(track);
                break;
            }
            case SoundboardUriItemType.Preset:
            {
                var preset = db.Presets
                    .Include(p => p.Tracks)
                        .ThenInclude(t => t.Track)
                    .AsNoTracking()
                    .FirstOrDefault(p => p.Id == uri.ItemId);
                if (preset == null) { LogMissing("preset", uri.ItemId.Value); return; }
                _engine.PlayPreset(preset);
                break;
            }
            case SoundboardUriItemType.Playlist:
            {
                var playlist = db.Playlists
                    .Include(p => p.Items)
                        .ThenInclude(i => i.Track)
                    .Include(p => p.Items)
                        .ThenInclude(i => i.Preset)
                            .ThenInclude(pr => pr!.Tracks)
                                .ThenInclude(pt => pt.Track)
                    .AsNoTracking()
                    .FirstOrDefault(p => p.Id == uri.ItemId);
                if (playlist == null) { LogMissing("playlist", uri.ItemId.Value); return; }
                _engine.PlayPlaylist(playlist);
                break;
            }
        }
    }

    private void StopItem(SoundboardUri uri)
    {
        if (uri.ItemType is null || uri.ItemId is null) return;
        using var db = _dbFactory.CreateDbContext();

        switch (uri.ItemType)
        {
            case SoundboardUriItemType.Track:
                if (db.Tracks.AsNoTracking().FirstOrDefault(t => t.Id == uri.ItemId) is { } t)
                    _engine.StopTrack(t);
                break;
            case SoundboardUriItemType.Preset:
                if (db.Presets.AsNoTracking().FirstOrDefault(p => p.Id == uri.ItemId) is { } p)
                    _engine.StopPreset(p);
                break;
            case SoundboardUriItemType.Playlist:
                if (db.Playlists.AsNoTracking().FirstOrDefault(pl => pl.Id == uri.ItemId) is { } pl)
                    _engine.StopPlaylist(pl);
                break;
        }
    }

    private void ToggleItem(SoundboardUri uri)
    {
        if (uri.ItemType is null || uri.ItemId is null) return;
        using var db = _dbFactory.CreateDbContext();

        switch (uri.ItemType)
        {
            case SoundboardUriItemType.Track:
                if (db.Tracks.AsNoTracking().FirstOrDefault(t => t.Id == uri.ItemId) is { } t)
                    _engine.TogglePlayPause(t);
                break;
            case SoundboardUriItemType.Preset:
                if (db.Presets
                        .Include(p => p.Tracks).ThenInclude(pt => pt.Track)
                        .AsNoTracking()
                        .FirstOrDefault(p => p.Id == uri.ItemId) is { } p)
                    _engine.TogglePlayPausePreset(p);
                break;
            case SoundboardUriItemType.Playlist:
                // Playlists don't expose a real toggle in v1; map to Stop if
                // running, Play if idle.
                if (db.Playlists.AsNoTracking().FirstOrDefault(pl => pl.Id == uri.ItemId) is { } pl)
                {
                    if (_engine.IsPlaylistRunning(pl)) _engine.StopPlaylist(pl);
                    else
                    {
                        // Reload with includes for actual playback.
                        var hydrated = db.Playlists
                            .Include(x => x.Items).ThenInclude(i => i.Track)
                            .Include(x => x.Items).ThenInclude(i => i.Preset)
                                .ThenInclude(pr => pr!.Tracks).ThenInclude(pt => pt.Track)
                            .AsNoTracking()
                            .First(x => x.Id == pl.Id);
                        _engine.PlayPlaylist(hydrated);
                    }
                }
                break;
        }
    }

    private static void LogMissing(string kind, int id) =>
        Log.Warn("URI", $"{kind} #{id} not found in library — activation ignored");
}
