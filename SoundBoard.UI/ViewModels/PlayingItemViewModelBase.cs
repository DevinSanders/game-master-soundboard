using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Shared base for every mixer card type — <see cref="PlayingTrackViewModel"/>,
/// <see cref="PlayingPresetViewModel"/>, <see cref="PlayingPlaylistViewModel"/>.
/// Owns the cross-card surface that the Mixer view binds to: master Volume
/// slider, IsPaused toggle, sampler-badge bar, FX Chain button. Derived VMs
/// add their type-specific provider plumbing (TrackSampleProvider, child list,
/// session items, …) and override the protected hooks to wire Volume / IsPaused
/// changes into their concrete audio path.
///
/// <para><b>Hook pattern.</b> CommunityToolkit's <c>[ObservableProperty]</c>
/// source-generates a <c>partial void OnXChanged</c> on the declaring class,
/// which derived classes can't extend. So the base implements those partials
/// once and calls <see cref="OnIsPausedChangedCore"/> / <see cref="OnVolumeChangedCore"/>
/// — both <c>virtual</c> no-ops by default — to give derived VMs a clean
/// override point without giving up the generator's per-property change
/// notifications.</para>
/// </summary>
public abstract partial class PlayingItemViewModelBase : ViewModelBase, IActiveMixerItem
{
    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private double _volume = 1.0;

    public abstract string Name { get; }

    public string PlayPauseText => IsPaused ? "Resume" : "Pause";

    public int VolumePercent => (int)Math.Round(Volume * 100);

    /// <summary>Sampler plugins currently attached to this playback. Rendered
    /// as badges on the card so the GM can see at a glance what DSP is
    /// active. Set by the engine at spawn time; empty for plays with no
    /// attached chain (direct PlayTrack from the library).</summary>
    public IReadOnlyList<SamplerBadge> AttachedSamplers { get; init; } = Array.Empty<SamplerBadge>();

    /// <summary>True when at least one sampler is attached — drives the
    /// visibility of the badges container in the card template.</summary>
    public bool HasAttachedSamplers => AttachedSamplers.Count > 0;

    /// <summary>Open the sampler chain editor for whichever owner this
    /// playback inherits its chain from. Null when there's no editable
    /// chain (e.g. direct PlayTrack — Tracks don't own chains). Set by
    /// the engine at spawn time.</summary>
    public Action? OpenSamplerEditorAction { get; init; }

    public bool HasSamplerEditor => OpenSamplerEditorAction != null;

    [RelayCommand]
    private void OpenSamplerEditor() => OpenSamplerEditorAction?.Invoke();

    partial void OnIsPausedChanged(bool value)
    {
        OnIsPausedChangedCore(value);
        OnPropertyChanged(nameof(PlayPauseText));
    }

    partial void OnVolumeChanged(double value)
    {
        OnVolumeChangedCore(value);
        OnPropertyChanged(nameof(VolumePercent));
    }

    /// <summary>Override to wire IsPaused into the concrete audio path
    /// (TrackSampleProvider, child list, current child item, …). Default
    /// no-op so derived VMs that don't need it can ignore it.</summary>
    protected virtual void OnIsPausedChangedCore(bool value) { }

    /// <summary>Override to wire Volume into the concrete audio path.
    /// Default no-op.</summary>
    protected virtual void OnVolumeChangedCore(double value) { }

    /// <summary><see cref="IActiveMixerItem.Stop"/> — fade the card's
    /// playback out and remove it from <c>ActiveItems</c>. Concrete impl
    /// per card type.</summary>
    public abstract void Stop();
}
