using SoundBoard.Core.Models;
using SoundBoard.UI.ViewModels;
using System.Collections.ObjectModel;

namespace SoundBoard.UI.Services;

/// <summary>
/// The UI-side facade over the audio pipeline. Owns the collection of
/// currently-playing items, builds the per-track/per-preset audio graph and
/// hands it to the <see cref="Core.Audio.MasterMixer"/>, and drives the
/// sequential auto-advance for playlists. Singleton — every VM uses the
/// same instance.
/// </summary>
public interface IAudioPlaybackEngine
{
    /// <summary>
    /// Every card currently shown in the mixer. Mix of
    /// <see cref="PlayingTrackViewModel"/> and <see cref="PlayingPresetViewModel"/>
    /// (and any future <see cref="IActiveMixerItem"/>). UI templates dispatch on type.
    /// </summary>
    ObservableCollection<IActiveMixerItem> ActiveItems { get; }

    /// <summary>Convenience filtered view used by older bindings (Now Playing, etc).</summary>
    ObservableCollection<PlayingTrackViewModel> ActiveTracks { get; }

    void PlayTrack(Track track);

    /// <summary>Same as <see cref="PlayTrack(Track)"/> but layers the named
    /// shortcut's sampler chain on top of the playback. Used when activation
    /// comes from a soundboard button so the shortcut's own samplers apply
    /// to whatever the target is.</summary>
    void PlayTrack(Track track, int fromShortcutId);

    void StopTrack(Track track);
    void TogglePlayPause(Track track);
    /// <summary>Toggle variant that layers the shortcut's sampler chain on first-play.</summary>
    void TogglePlayPause(Track track, int fromShortcutId);
    bool IsTrackPlaying(Track track);
    bool IsTrackPaused(Track track);

    void PlayPreset(Preset preset);
    /// <summary>Preset playback with a shortcut's sampler chain layered on top.</summary>
    void PlayPreset(Preset preset, int fromShortcutId);
    void StopPreset(Preset preset);
    void TogglePlayPausePreset(Preset preset);
    /// <summary>Toggle variant that layers the shortcut's sampler chain on first-play.</summary>
    void TogglePlayPausePreset(Preset preset, int fromShortcutId);
    bool IsPresetPlaying(Preset preset);
    bool IsPresetPaused(Preset preset);

    /// <summary>Start sequential playback of a playlist. Each item becomes a
    /// normal mixer card while playing; on natural end (or user stop) the
    /// engine auto-advances to the next item. No-op if already running.</summary>
    void PlayPlaylist(Playlist playlist);
    /// <summary>Playlist playback with a shortcut's sampler chain layered on top.</summary>
    void PlayPlaylist(Playlist playlist, int fromShortcutId);

    /// <summary>Cancel a running playlist and stop its current item.</summary>
    void StopPlaylist(Playlist playlist);

    bool IsPlaylistRunning(Playlist playlist);

    /// <summary>True when a running playlist is currently paused (its active
    /// child item has been paused).</summary>
    bool IsPlaylistPaused(Playlist playlist);

    /// <summary>Toggle pause/resume on the playlist if it's running; start it
    /// from the top if it isn't. Same semantics as the per-track and per-preset
    /// toggle helpers — used by soundboard shortcut buttons.</summary>
    void TogglePlayPausePlaylist(Playlist playlist);
    /// <summary>Toggle variant that layers the shortcut's sampler chain on first-play.</summary>
    void TogglePlayPausePlaylist(Playlist playlist, int fromShortcutId);

    /// <summary>Skip the currently playing item in the playlist; the natural
    /// stop callback will auto-advance to the next item.</summary>
    void SkipPlaylistForward(Playlist playlist);

    /// <summary>Rewind one slot and skip the current item so the previous item plays.</summary>
    void SkipPlaylistBackward(Playlist playlist);

    /// <summary>Override the loop behavior of one item in a running playlist's
    /// queue. If the targeted item is currently playing, the override is also
    /// applied live to the running provider (track loop flag flips, or every
    /// child of a preset is forced to the given value). Future items use the
    /// override when the engine advances to them. Does not persist.</summary>
    void SetPlaylistItemLoop(Playlist playlist, int itemIndex, bool isLooping);

    /// <summary>Reorder one item within a running playlist's queue. Only
    /// upcoming items (index >= NextIndex) may move, and only among
    /// themselves — played history and the current item stay put. Does not
    /// persist.</summary>
    void MovePlaylistItem(Playlist playlist, int fromIndex, int toIndex);

    /// <summary>Fade out everything currently in the mixer and cancel every
    /// running playlist. Used by URI activations that pass <c>stopPlaying=true</c>.</summary>
    void StopAll();
}
