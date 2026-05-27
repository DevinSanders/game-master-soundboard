using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundBoard.Core.Models;
using SoundBoard.UI.Services;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Mixer card representing a running playlist. The child tracks/presets that
/// the playlist drives are still added to <see cref="IAudioPlaybackEngine.ActiveItems"/>
/// for audio plumbing, but they're marked <c>IsPlaylistOwned</c> so the mixer
/// hides their individual cards — this one is the single point of control.
///
/// The card exposes a per-session view of the playlist queue
/// (<see cref="SessionItems"/>) so the user can flip loop on items or
/// reorder upcoming items without writing those changes back to the DB.
/// </summary>
public partial class PlayingPlaylistViewModel : PlayingItemViewModelBase
{
    private readonly IAudioPlaybackEngine _playbackEngine;

    public Playlist Playlist { get; }

    public override string Name => Playlist.Name;

    /// <summary>Track or Preset currently playing in this playlist, used to
    /// route pause/resume from the playlist card down to the actual audio
    /// item without exposing it as its own card in the mixer.</summary>
    private IActiveMixerItem? _currentChild;

    /// <summary>Index of the currently-playing item within
    /// <see cref="SessionItems"/>. -1 before the first advance.</summary>
    private int _currentIndex = -1;

    /// <summary>Per-session view of every entry in the playlist queue. Order
    /// reflects the run order (possibly reordered via the flyout). Each
    /// entry's IsLooping reflects any user override and is consulted by the
    /// engine when advancing.</summary>
    public ObservableCollection<PlaylistItemEntry> SessionItems { get; }

    [ObservableProperty]
    private string _currentItemDisplay = "Starting…";

    [ObservableProperty]
    private string _nextItemDisplay = string.Empty;

    [ObservableProperty]
    private bool _hasNextItem;

    [ObservableProperty]
    private bool _hasPreviousItem;

    [ObservableProperty]
    private string _positionDisplay = string.Empty;

    /// <summary>Playlist-wide volume scaling isn't wired through to children
    /// yet — the slider is omitted from the card for now. Volume / IsPaused
    /// shims live on <see cref="PlayingItemViewModelBase"/>.</summary>

    public PlayingPlaylistViewModel(Playlist playlist, IAudioPlaybackEngine playbackEngine, List<PlaylistItem> sessionItems)
    {
        Playlist = playlist;
        _playbackEngine = playbackEngine;

        SessionItems = new ObservableCollection<PlaylistItemEntry>();
        for (int i = 0; i < sessionItems.Count; i++)
        {
            SessionItems.Add(new PlaylistItemEntry(sessionItems[i], i));
        }
    }

    /// <summary>Engine notifies us when the playlist advances. <paramref name="currentChild"/>
    /// is the newly-spawned ActiveItems entry (track or preset) that's now playing.</summary>
    public void NotifyAdvanced(IActiveMixerItem? currentChild, string currentName, string? nextName, int currentIndex, int totalItems)
    {
        _currentChild = currentChild;
        _currentIndex = currentIndex;

        CurrentItemDisplay = currentName;
        NextItemDisplay = nextName ?? "(end of playlist)";
        HasNextItem = !string.IsNullOrEmpty(nextName);
        HasPreviousItem = currentIndex > 0;
        PositionDisplay = totalItems > 0 ? $"{currentIndex + 1} of {totalItems}" : string.Empty;

        // Roll forward the per-entry statuses so the flyout's row colors stay
        // aligned with what's been played, what's playing, and what's queued.
        for (int i = 0; i < SessionItems.Count; i++)
        {
            if (i < currentIndex) SessionItems[i].Status = PlaylistItemStatus.Played;
            else if (i == currentIndex) SessionItems[i].Status = PlaylistItemStatus.Current;
            else SessionItems[i].Status = PlaylistItemStatus.Pending;
        }

        // A fresh advance always resumes the underlying child, so reset our
        // own paused flag to match. The base raises PlayPauseText via its
        // OnIsPausedChanged partial.
        if (currentChild != null && currentChild.IsPaused) currentChild.IsPaused = false;
        IsPaused = false;
    }

    /// <summary>Engine accessor — returns the per-session loop override for
    /// the entry at the given index, or null if the index is out of range.
    /// AdvancePlaylist consults this before spawning.</summary>
    internal bool? GetLoopOverrideAt(int index)
    {
        if (index < 0 || index >= SessionItems.Count) return null;
        return SessionItems[index].IsLooping;
    }

    /// <summary>Engine setter — used when the user toggles loop via the flyout.
    /// Updates the entry so future advances see the new value; the engine
    /// separately handles live-applying to the running provider if the entry
    /// is the current one.</summary>
    internal void SetEntryLoop(int index, bool isLooping)
    {
        if (index < 0 || index >= SessionItems.Count) return;
        SessionItems[index].IsLooping = isLooping;
    }

    /// <summary>Engine callback when items are reordered via
    /// <see cref="IAudioPlaybackEngine.MovePlaylistItem"/>. Mirror the swap in
    /// our observable collection so the flyout updates, and renumber the
    /// remaining entries' Index property.</summary>
    internal void NotifyItemsReordered(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= SessionItems.Count) return;
        if (toIndex   < 0 || toIndex   >= SessionItems.Count) return;
        SessionItems.Move(fromIndex, toIndex);
        for (int i = 0; i < SessionItems.Count; i++) SessionItems[i].Index = i;
    }

    protected override void OnIsPausedChangedCore(bool value)
    {
        // Route the pause down to whatever's actually playing right now.
        if (_currentChild != null) _currentChild.IsPaused = value;
    }

    [RelayCommand]
    private void SkipForward() => _playbackEngine.SkipPlaylistForward(Playlist);

    [RelayCommand]
    private void SkipBackward() => _playbackEngine.SkipPlaylistBackward(Playlist);

    /// <summary>IActiveMixerItem.Stop — fades the current child out and cancels
    /// the playlist. The engine drops this card from ActiveItems once cleanup
    /// completes.</summary>
    public override void Stop() => _playbackEngine.StopPlaylist(Playlist);

    [RelayCommand]
    private void StopPlaylist() => Stop();

    /// <summary>Toggles loop on a session entry. Routes through the engine so
    /// the override is applied uniformly (and, for the current item, takes
    /// effect on the running provider immediately).</summary>
    [RelayCommand]
    private void ToggleEntryLoop(PlaylistItemEntry? entry)
    {
        if (entry == null) return;
        _playbackEngine.SetPlaylistItemLoop(Playlist, entry.Index, !entry.IsLooping);
    }

    [RelayCommand]
    private void MoveEntryUp(PlaylistItemEntry? entry)
    {
        if (entry == null) return;
        _playbackEngine.MovePlaylistItem(Playlist, entry.Index, entry.Index - 1);
    }

    [RelayCommand]
    private void MoveEntryDown(PlaylistItemEntry? entry)
    {
        if (entry == null) return;
        _playbackEngine.MovePlaylistItem(Playlist, entry.Index, entry.Index + 1);
    }
}
