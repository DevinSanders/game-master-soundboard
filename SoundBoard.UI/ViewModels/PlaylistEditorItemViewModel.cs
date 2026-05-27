using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoard.Core.Models;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Per-row wrapper for one <see cref="PlaylistItem"/> shown in the playlist
/// editor. <see cref="PlaylistItem"/> is a POCO and doesn't notify property
/// changes, so the loop-override cycle button and any other live-mutating
/// field would leave the UI stale. This wrapper exposes the override as an
/// <c>[ObservableProperty]</c> that writes through to the underlying model
/// and raises <c>PropertyChanged</c> for the bindings.
/// </summary>
public partial class PlaylistEditorItemViewModel : ViewModelBase
{
    public PlaylistItem Model { get; }

    public PlaylistEditorItemViewModel(PlaylistItem model)
    {
        Model = model;
        _overrideIsLooping = model.OverrideIsLooping;
    }

    // ── Pass-through properties used by the row template ──
    public int Id => Model.Id;
    public int? TrackId => Model.TrackId;
    public Track? Track => Model.Track;
    public int? PresetId => Model.PresetId;
    public Preset? Preset => Model.Preset;
    public int Order { get => Model.Order; set => Model.Order = value; }

    /// <summary>"m:ss" length for this item. For a track entry it's the
    /// track's playable length (StartPoint→EndPoint). For a preset entry it's
    /// the max of its children's playable lengths — a rough estimate of how
    /// long the preset will run. Returns "—" if the underlying durations
    /// haven't been cached yet.</summary>
    public string LengthDisplay
    {
        get
        {
            System.TimeSpan? len = null;
            if (Track != null) len = Track.PlayableLength;
            else if (Preset != null)
            {
                System.TimeSpan max = System.TimeSpan.Zero;
                bool any = false;
                foreach (var pt in Preset.Tracks)
                {
                    var t = pt.Track?.PlayableLength;
                    if (!t.HasValue) continue;
                    if (t.Value > max) max = t.Value;
                    any = true;
                }
                if (any) len = max;
            }
            return len.HasValue
                ? SoundBoard.UI.Converters.DurationDisplayConverter.Format(len.Value)
                : "—";
        }
    }

    /// <summary>Three-state loop override that drives the per-row 🔁 button.
    /// Setter writes through to the model and raises change notification so
    /// the button label/color refresh without rebuilding the row.</summary>
    [ObservableProperty]
    private bool? _overrideIsLooping;

    partial void OnOverrideIsLoopingChanged(bool? value)
    {
        Model.OverrideIsLooping = value;
    }
}
