using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoard.Core.Audio;
using SoundBoard.Core.Models;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// One row in the running-preset card's flyout — a single child track inside
/// the currently-playing preset. Shows the track's name, icon, and length;
/// the loop toggle writes through live to the underlying provider so the
/// user can flip a child's loop state mid-playback without re-spawning.
/// </summary>
public partial class PresetChildEntry : ViewModelBase
{
    private readonly TrackSampleProvider _provider;

    public PresetTrack Entry { get; }

    public string Name => Entry.Track?.Name ?? "(missing track)";
    public string? Icon => Entry.Track?.Icon;

    /// <summary>"m:ss" length using the preset's effective StartPoint/EndPoint
    /// overrides. Mirrors PresetTrackCardViewModel.LengthDisplay so the
    /// numbers match between the editor and the running flyout.</summary>
    public string LengthDisplay
    {
        get
        {
            var start = Entry.EffectiveStartPoint ?? System.TimeSpan.Zero;
            var end = Entry.EffectiveEndPoint ?? Entry.Track?.FileDuration;
            if (!end.HasValue) return "—";
            var len = end.Value - start;
            if (len < System.TimeSpan.Zero) len = System.TimeSpan.Zero;
            return SoundBoard.UI.Converters.DurationDisplayConverter.Format(len);
        }
    }

    /// <summary>Live loop flag for this child. Writes through to the running
    /// provider so the change takes effect immediately.</summary>
    public bool IsLooping
    {
        get => _provider.IsLooping;
        set
        {
            if (_provider.IsLooping == value) return;
            _provider.IsLooping = value;
            OnPropertyChanged();
        }
    }

    public PresetChildEntry(PresetTrack entry, TrackSampleProvider provider)
    {
        Entry = entry;
        _provider = provider;
    }
}
