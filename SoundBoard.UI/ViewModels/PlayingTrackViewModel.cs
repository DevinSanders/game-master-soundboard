using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Models;
using System;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// A single playing-track card on the mixer. Wraps a
/// <see cref="TrackSampleProvider"/> and exposes its volume, loop, pause,
/// and position state as bindable properties. <c>IsPlaylistOwned</c> hides
/// the card while a playlist is driving this track — the playlist card
/// becomes the single point of control in that case.
///
/// <para>Shared Volume / IsPaused / sampler-badge / FX-Chain plumbing lives
/// in <see cref="PlayingItemViewModelBase"/>; this class just adds the
/// track-specific bits (loop toggle, scrub position, IAudioSamples).</para>
/// </summary>
public partial class PlayingTrackViewModel : PlayingItemViewModelBase, IAudioSamples
{
    private readonly TrackSampleProvider _provider;

    public Track Track { get; }

    public override string Name => Track.Name;

    [ObservableProperty]
    private bool _isLooping;

    [ObservableProperty]
    private double _positionSeconds;

    /// <summary>True when this track is running as part of a playlist. The
    /// mixer hides individual cards for playlist-owned items so the playlist
    /// card is the single point of control.</summary>
    [ObservableProperty]
    private bool _isPlaylistOwned;

    /// <summary>Effective fade-out duration for this running instance. Seeded
    /// from the Track row, but the playlist engine overrides it when a
    /// playlist-wide fade-out is configured. Stop() reads from here, not
    /// from <see cref="Track"/>, so the override applies even on user-driven
    /// stops.</summary>
    public TimeSpan FadeOutDuration { get; set; }

    public double TotalSeconds => _provider.TotalTime.TotalSeconds;

    /// <summary>True when the underlying codec source supports random-access
    /// seeking. False for live web streams. The mixer card binds the scrub
    /// slider's <c>IsVisible</c> and the loop toggle's <c>IsEnabled</c> to
    /// this so the user can't try to scrub something that doesn't support it.</summary>
    public bool IsSeekable => _provider.IsSeekable;

    /// <summary>The length of the audible portion of one play — EndPoint
    /// minus StartPoint. This is what the mixer's position slider runs
    /// over, NOT the full file length, since clipping the range should
    /// shrink the slider accordingly.</summary>
    public double EffectiveDurationSeconds =>
        (_provider.EndPoint - _provider.StartPoint).TotalSeconds;

    /// <summary>Position within the audible portion (0 .. <see cref="EffectiveDurationSeconds"/>).
    /// Tracks <see cref="PositionSeconds"/> minus the provider's StartPoint
    /// so the slider sits at 0 when playback first starts even if StartPoint
    /// is mid-file. Two-way binding: scrubbing this seeks the provider to
    /// <c>StartPoint + value</c>.</summary>
    public double RelativePositionSeconds
    {
        get
        {
            var rel = PositionSeconds - _provider.StartPoint.TotalSeconds;
            if (rel < 0) rel = 0;
            return rel;
        }
        set
        {
            var absolute = _provider.StartPoint.TotalSeconds + Math.Max(0, value);
            if (Math.Abs(_provider.Position.TotalSeconds - absolute) > 0.1)
            {
                _provider.Position = TimeSpan.FromSeconds(absolute);
                PositionSeconds = absolute;
            }
        }
    }

    /// <summary>How long one full play of this track takes, accounting for
    /// the per-track StartDelay, StartPoint, and EndPoint. Returns
    /// <see cref="TimeSpan.MaxValue"/> when the track loops (no natural end).
    /// Used by the playlist engine to schedule crossfades.</summary>
    public TimeSpan EffectivePlayDuration =>
        _provider.IsLooping
            ? TimeSpan.MaxValue
            : _provider.StartDelay + (_provider.EndPoint - _provider.StartPoint);

    /// <summary>Audio seconds left before the track's natural end (EndPoint
    /// minus absolute position). Doesn't include StartDelay — by the time
    /// playback is in the audible portion, the delay is already consumed.
    /// <see cref="double.PositiveInfinity"/> while looping. Used by the
    /// playlist crossfade detector to schedule the next item.</summary>
    public double RemainingSeconds
    {
        get
        {
            if (_provider.IsLooping) return double.PositiveInfinity;
            var remaining = _provider.EndPoint.TotalSeconds - _provider.Position.TotalSeconds;
            return remaining > 0 ? remaining : 0;
        }
    }

    public string TimeDisplay => $"{FormatSeconds(RelativePositionSeconds)} / {FormatSeconds(EffectiveDurationSeconds)}";

    private static string FormatSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = System.TimeSpan.FromSeconds(seconds);
        return SoundBoard.UI.Converters.DurationDisplayConverter.Format(t);
    }

    public event EventHandler<float[]>? AudioDataAvailable;

    public PlayingTrackViewModel(Track track, TrackSampleProvider provider)
    {
        Track = track;
        _provider = provider;
        Volume = provider.Volume;
        IsPaused = provider.IsPaused;
        _isLooping = provider.IsLooping;
        FadeOutDuration = track.FadeOutDuration;

        _provider.AudioDataAvailable += (s, data) => AudioDataAvailable?.Invoke(this, data);
    }

    protected override void OnIsPausedChangedCore(bool value)
    {
        _provider.IsPaused = value;
    }

    protected override void OnVolumeChangedCore(double value)
    {
        _provider.Volume = (float)value;
    }

    partial void OnIsLoopingChanged(bool value)
    {
        _provider.IsLooping = value;
    }

    public void UpdatePositionFromProvider()
    {
        // 0.1s threshold matches the tenths digit shown in TimeDisplay so
        // the readout actually animates smoothly. Anything smaller is
        // floating-point noise from the buffer-aligned position; anything
        // bigger would leave the tenths digit visibly stuck.
        if (Math.Abs(PositionSeconds - _provider.Position.TotalSeconds) > 0.1)
        {
            PositionSeconds = _provider.Position.TotalSeconds;
        }
    }

    public override void Stop()
    {
        _provider.Stop(FadeOutDuration);
    }

    partial void OnPositionSecondsChanged(double value)
    {
        // If the UI updates the position (user scrub), update the provider
        if (Math.Abs(_provider.Position.TotalSeconds - value) > 0.5)
        {
            _provider.Position = TimeSpan.FromSeconds(value);
        }
        OnPropertyChanged(nameof(TimeDisplay));
        OnPropertyChanged(nameof(RelativePositionSeconds));
    }
}
