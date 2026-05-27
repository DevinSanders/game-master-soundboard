using System;

namespace SoundBoard.Core.Models;

/// <summary>
/// A single audio file in the library with its playback defaults. Presets and
/// playlist items reference Track by id and may override most of its playback
/// settings without mutating the row.
/// </summary>
public class Track
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;

    /// <summary>RPG Awesome icon class name (e.g. <c>ra-sword</c>) or null.</summary>
    public string? Icon { get; set; }
    
    // Per-track customization

    /// <summary>Which audio bus this track mixes into. References
    /// <see cref="Bus.Id"/>; default is <see cref="BuiltInBusIds.DefaultForNewTracks"/>
    /// (the Music bus). Per-spawn the value can be overridden by
    /// <see cref="Preset.BusIdOverride"/> or
    /// <see cref="ShortcutButton.BusIdOverride"/>; the track's own
    /// BusId is the fallback when neither override applies.</summary>
    public int BusId { get; set; } = BuiltInBusIds.DefaultForNewTracks;

    public float Volume { get; set; } = 1.0f;
    public TimeSpan? StartPoint { get; set; }
    public TimeSpan? EndPoint { get; set; }
    public TimeSpan FadeInDuration { get; set; } = TimeSpan.Zero;
    public TimeSpan FadeOutDuration { get; set; } = TimeSpan.Zero;
    public bool IsLooping { get; set; }

    /// <summary>Silence applied at the start of playback and re-applied on
    /// every loop iteration. Originally introduced as a loop-only delay;
    /// generalized so presets can stagger track start times.</summary>
    public TimeSpan StartDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Full audio duration of the file, cached after the first time
    /// it's read. Null = not yet measured. Lets the library list show
    /// per-track length without opening every file on each load.</summary>
    public TimeSpan? FileDuration { get; set; }

    /// <summary>The duration the user will actually hear when this track
    /// plays: from <see cref="StartPoint"/> to <see cref="EndPoint"/>, or
    /// the full file length when those are null. Returns null while
    /// <see cref="FileDuration"/> is still uncached and no end-point clip is
    /// set, in which case the UI should show "—".</summary>
    public TimeSpan? PlayableLength
    {
        get
        {
            var start = StartPoint ?? TimeSpan.Zero;
            var end = EndPoint ?? FileDuration;
            if (!end.HasValue) return null;
            var len = end.Value - start;
            return len > TimeSpan.Zero ? len : TimeSpan.Zero;
        }
    }
}
