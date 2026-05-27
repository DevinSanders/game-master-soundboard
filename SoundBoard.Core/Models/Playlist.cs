using System;
using System.Collections.Generic;

namespace SoundBoard.Core.Models;

/// <summary>
/// An ordered queue of tracks and presets that play sequentially. Items
/// auto-advance when each one finishes. Playlist-wide fades override the
/// per-item fade defaults when non-zero.
/// </summary>
public class Playlist
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Icon { get; set; }

    /// <summary>Playlist-wide fade-in applied to every item that starts under
    /// this playlist. Zero disables the override — items fall back to their
    /// own track/preset fade-in.</summary>
    public TimeSpan FadeInDuration { get; set; } = TimeSpan.Zero;

    /// <summary>Playlist-wide fade-out applied to every item when it ends or
    /// is advanced past under this playlist. Zero disables the override.</summary>
    public TimeSpan FadeOutDuration { get; set; } = TimeSpan.Zero;

    /// <summary>True (default) = auto-advance to the next item when each
    /// finishes. False = each Play call fires exactly one item and stops;
    /// the next Play picks the next track in order (or another random pick
    /// if <see cref="Random"/> is also set).</summary>
    public bool Autoplay { get; set; } = true;

    /// <summary>True = pick items in random order instead of by
    /// <see cref="PlaylistItem.Order"/>. Combined with <see cref="Autoplay"/>
    /// off, makes the playlist behave like a randomized soundbank: each
    /// Play yields a different random item.</summary>
    public bool Random { get; set; } = false;

    public ICollection<PlaylistItem> Items { get; set; } = new List<PlaylistItem>();
}

/// <summary>
/// One entry in a <see cref="Playlist"/>. Exactly one of <see cref="TrackId"/>
/// or <see cref="PresetId"/> is expected to be set. <see cref="OverrideIsLooping"/>
/// lets a single playlist entry force loop behavior different from the
/// underlying library row without modifying it.
/// </summary>
public class PlaylistItem
{
    public int Id { get; set; }
    public int PlaylistId { get; set; }
    public Playlist? Playlist { get; set; }

    public int Order { get; set; }

    // Can either point to a Track or a Preset
    public int? TrackId { get; set; }
    public Track? Track { get; set; }

    public int? PresetId { get; set; }
    public Preset? Preset { get; set; }

    /// <summary>Per-item loop override that applies only when this item plays
    /// inside this playlist. Null means inherit from the underlying track or
    /// preset; a non-null value forces that loop state for this entry without
    /// touching the library row.</summary>
    public bool? OverrideIsLooping { get; set; }
}
