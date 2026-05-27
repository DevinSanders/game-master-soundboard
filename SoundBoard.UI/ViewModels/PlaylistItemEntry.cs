using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoard.Core.Models;

namespace SoundBoard.UI.ViewModels;

/// <summary>Where a <see cref="PlaylistItemEntry"/> sits in the run: not
/// yet played, playing right now, or already played in this session.</summary>
public enum PlaylistItemStatus
{
    Pending,
    Current,
    Played,
}

/// <summary>
/// One row in the playlist card's expanded flyout — a single track or preset
/// entry shown with its name, loop toggle, and Move Up/Down controls. The
/// entries mirror <see cref="PlaylistSession.Items"/> on the engine side;
/// the playlist VM keeps the two in sync.
///
/// State on this entry is per-session — changes never persist back to the
/// underlying <see cref="Track"/> / <see cref="Preset"/> rows.
/// </summary>
public partial class PlaylistItemEntry : ViewModelBase
{
    /// <summary>Position in the session's playlist. Updated when items are
    /// reordered so commands can address entries by index.</summary>
    [ObservableProperty]
    private int _index;

    public PlaylistItem Item { get; }
    public string Name { get; }
    public string? Icon { get; }
    public bool IsPreset { get; }

    /// <summary>Mirrors the engine-side loop state for this run. Initially
    /// inherits from the source track / preset; toggled by the user via the
    /// flyout, and consumed by <c>AdvancePlaylist</c> when spawning future
    /// items (or applied live to the current child if this is the current
    /// item).</summary>
    [ObservableProperty]
    private bool _isLooping;

    [ObservableProperty]
    private PlaylistItemStatus _status = PlaylistItemStatus.Pending;

    public PlaylistItemEntry(PlaylistItem item, int index)
    {
        Item = item;
        Index = index;

        // Inherited loop state: the source track's IsLooping, or "any child
        // loops" for a preset. Used as the fallback when no per-item override
        // is set.
        bool inheritedLoop = false;
        if (item.Track != null)
        {
            Name = item.Track.Name;
            Icon = item.Track.Icon;
            IsPreset = false;
            inheritedLoop = item.Track.IsLooping;
        }
        else if (item.Preset != null)
        {
            Name = item.Preset.Name;
            Icon = item.Preset.Icon;
            IsPreset = true;
            // A preset is "looping" from the playlist's perspective if any of
            // its children loop — that's what would block advance.
            foreach (var pt in item.Preset.Tracks)
            {
                if (pt.EffectiveIsLooping) { inheritedLoop = true; break; }
            }
        }
        else
        {
            Name = "(empty)";
            Icon = null;
            IsPreset = false;
        }

        // The persisted per-item override (set in the playlist editor) wins
        // over the inherited value. Null means "inherit from library".
        _isLooping = item.OverrideIsLooping ?? inheritedLoop;
    }

    public bool IsCurrent => Status == PlaylistItemStatus.Current;
    public bool IsPlayed => Status == PlaylistItemStatus.Played;
    public bool IsPending => Status == PlaylistItemStatus.Pending;
    public bool CanModify => Status != PlaylistItemStatus.Played;
    public double RowOpacity => Status == PlaylistItemStatus.Played ? 0.45 : 1.0;

    /// <summary>Short marker shown next to the row — "NOW" for the playing
    /// item, blank otherwise. Played items rely on the opacity dim, no extra
    /// text needed.</summary>
    public string StatusLabel => Status switch
    {
        PlaylistItemStatus.Current => "NOW",
        _ => string.Empty,
    };

    /// <summary>1-based position for display ("1.", "2." …). Bound directly
    /// in XAML to avoid an index-+1 converter.</summary>
    public string PositionDisplay => $"{Index + 1}.";

    /// <summary>"m:ss" length of this entry. For tracks: the playable length
    /// (StartPoint→EndPoint, or the full file). For presets: the longest
    /// child track's playable length. Returns "—" when not yet known.</summary>
    public string LengthDisplay
    {
        get
        {
            System.TimeSpan? len = null;
            if (Item.Track != null) len = Item.Track.PlayableLength;
            else if (Item.Preset != null)
            {
                System.TimeSpan max = System.TimeSpan.Zero;
                bool any = false;
                foreach (var pt in Item.Preset.Tracks)
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

    partial void OnStatusChanged(PlaylistItemStatus value)
    {
        OnPropertyChanged(nameof(IsCurrent));
        OnPropertyChanged(nameof(IsPlayed));
        OnPropertyChanged(nameof(IsPending));
        OnPropertyChanged(nameof(CanModify));
        OnPropertyChanged(nameof(RowOpacity));
        OnPropertyChanged(nameof(StatusLabel));
    }

    partial void OnIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PositionDisplay));
    }
}
