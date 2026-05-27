using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// A single "card" in the mixer that represents a Preset playing as a unit.
/// Internally it owns N <see cref="TrackSampleProvider"/>s (one per
/// <see cref="PresetTrack"/>), each already wired into the master mixer.
/// The card's master Volume scales every child provider; Stop fades them
/// all out at once.
///
/// The card removes itself from the mixer's ActiveItems collection when its
/// last surviving child stops naturally — see <see cref="AllStoppedCallback"/>.
/// </summary>
public partial class PlayingPresetViewModel : PlayingItemViewModelBase, IAudioSamples
{
    public Preset Preset { get; }

    public override string Name => Preset.Name;

    /// <summary>True when this preset is running as part of a playlist. The
    /// mixer hides individual cards for playlist-owned items so the playlist
    /// card is the single point of control.</summary>
    [ObservableProperty]
    private bool _isPlaylistOwned;

    /// <summary>One internal child track. We keep its base volume separate from
    /// the card's master so master changes can be reapplied multiplicatively.
    /// <see cref="PresetTrackId"/> ties this back to the source PresetTrack
    /// row so the preset editor can find + live-update the right child when
    /// the user drags a slider while playback is in progress.</summary>
    internal sealed class Child
    {
        public int PresetTrackId;
        public TrackSampleProvider Provider = null!;
        public float BaseVolume;
        public TimeSpan FadeOutDuration;
        public Action? RemoveFromMixer;
        public bool Stopped;
    }

    private readonly List<Child> _children = new();
    private bool _allStoppedFired;

    /// <summary>Per-child entries shown in the mixer card's flyout. One row
    /// per PresetTrack that's actually playing (children whose source file
    /// failed to load are skipped). Bound to the flyout's ItemsControl.</summary>
    public ObservableCollection<PresetChildEntry> Children { get; } = new();

    /// <summary>Invoked once after every child has finished and been removed
    /// from the master mixer. The engine uses this to drop the preset card
    /// from <c>ActiveItems</c>.</summary>
    public Action? AllStoppedCallback { get; set; }

    public PlayingPresetViewModel(Preset preset)
    {
        Preset = preset;
    }

    /// <summary>Aggregated audio stream for the visualizer — every child
    /// provider's AudioDataAvailable funnels through here, so the preset
    /// card's RMS bars reflect the combined output.</summary>
    public event EventHandler<float[]>? AudioDataAvailable;

    internal void AddChild(Child child, PresetTrack entry)
    {
        _children.Add(child);
        Children.Add(new PresetChildEntry(entry, child.Provider));
        child.Provider.AudioDataAvailable += (s, data) => AudioDataAvailable?.Invoke(this, data);
    }

    protected override void OnIsPausedChangedCore(bool value)
    {
        foreach (var c in _children) c.Provider.IsPaused = value;
    }

    protected override void OnVolumeChangedCore(double value)
    {
        // Master scales each child's base volume.
        foreach (var c in _children) c.Provider.Volume = c.BaseVolume * (float)value;
    }

    /// <summary>Called by the engine when one of our children fires
    /// OnPlaybackStopped — bookkeeping so we know when the card should disappear.</summary>
    internal void NotifyChildStopped(Child child)
    {
        if (child.Stopped) return;
        child.Stopped = true;
        child.RemoveFromMixer?.Invoke();

        if (_allStoppedFired) return;
        foreach (var c in _children) if (!c.Stopped) return;
        _allStoppedFired = true;
        AllStoppedCallback?.Invoke();
    }

    /// <summary>Live-edit hook from the preset editor — push a card's current
    /// override values into the running child provider so the user can hear
    /// volume / loop / start-delay changes immediately. FadeIn isn't pushed
    /// (only fires at start, which has already happened); FadeOut is stored
    /// so it's used on the next Stop call.</summary>
    public void ApplyLiveSettings(int presetTrackId, float baseVolume, bool isLooping, TimeSpan startDelay, TimeSpan fadeOut)
    {
        var child = _children.FirstOrDefault(c => c.PresetTrackId == presetTrackId);
        if (child == null) return;
        child.BaseVolume = baseVolume;
        child.Provider.Volume = baseVolume * (float)Volume;
        child.Provider.IsLooping = isLooping;
        child.Provider.StartDelay = startDelay;
        child.FadeOutDuration = fadeOut;
    }

    public override void Stop()
    {
        // Each child fades out for its own configured duration; the engine's
        // per-child OnPlaybackStopped will route through NotifyChildStopped
        // and the card auto-removes after the last one settles.
        foreach (var c in _children)
        {
            if (c.Stopped) continue;
            c.Provider.IsPaused = false;
            c.Provider.Stop(c.FadeOutDuration);
        }
    }

    /// <summary>Force every internal child's loop flag. Used by the playlist
    /// session when the user overrides the playlist-level loop state on a
    /// preset entry — turning loop off means "play this preset through once
    /// so the playlist can advance" even if some child tracks were marked
    /// looping at the library level.</summary>
    public void SetAllChildrenLoop(bool isLooping)
    {
        foreach (var c in _children) c.Provider.IsLooping = isLooping;
    }

    /// <summary>Force every internal child's fade-out duration. Used by the
    /// playlist engine to apply a playlist-wide fade-out override to this
    /// preset card. Fade-in is intentionally NOT settable post-spawn — fade-in
    /// only fires once at Play() time, which has already happened.</summary>
    public void SetAllChildrenFadeOut(TimeSpan fadeOut)
    {
        foreach (var c in _children) c.FadeOutDuration = fadeOut;
    }
}
