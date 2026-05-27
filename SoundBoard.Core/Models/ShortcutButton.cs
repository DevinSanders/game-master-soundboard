namespace SoundBoard.Core.Models;

/// <summary>
/// A single button on a <see cref="ShortcutPage"/>. Targets exactly one of
/// Track, Preset, or Playlist via the corresponding *Id property; clicking
/// toggles play/pause for that target.
/// </summary>
public class ShortcutButton
{
    public int Id { get; set; }
    public int ShortcutPageId { get; set; }
    public ShortcutPage? ShortcutPage { get; set; }
    
    public string? Label { get; set; }
    public string? ImagePath { get; set; }

    /// <summary>RPG Awesome icon class name (e.g. <c>ra-sword</c>). When set,
    /// takes precedence over <see cref="ImagePath"/> in the button render.</summary>
    public string? Icon { get; set; }
    
    public int Row { get; set; }
    public int Column { get; set; }
    
    public int? TrackId { get; set; }
    public Track? Track { get; set; }

    public int? PresetId { get; set; }
    public Preset? Preset { get; set; }

    public int? PlaylistId { get; set; }
    public Playlist? Playlist { get; set; }

    /// <summary>Optional bus override for Track shortcuts. When set and
    /// this button targets a Track, the track plays through the named
    /// bus regardless of the track's own <see cref="Track.BusId"/>.
    /// Ignored for Preset and Playlist shortcuts — those have their own
    /// routing rules (Preset uses <see cref="Preset.BusIdOverride"/>;
    /// Playlists never override per the design spec, each playlist item
    /// plays on its track's own bus).</summary>
    public int? BusIdOverride { get; set; }
}
