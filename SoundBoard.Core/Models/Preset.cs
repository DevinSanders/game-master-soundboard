using System;
using System.Collections.Generic;

namespace SoundBoard.Core.Models;

/// <summary>
/// A named "scene" that plays several tracks together as a unit. Each member
/// is a <see cref="PresetTrack"/> that references a library Track and may
/// override its playback settings for this preset only.
/// </summary>
public class Preset
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Icon { get; set; }

    /// <summary>Optional bus override. When set, every track this preset
    /// spawns is routed through the named bus regardless of the track's
    /// own <see cref="Track.BusId"/>. When null, each track plays on its
    /// own bus. Use case: a "Combat ambience" preset wants all of its
    /// tracks on the Ambient bus even if the user normally routes those
    /// individual tracks somewhere else.</summary>
    public int? BusIdOverride { get; set; }

    public ICollection<PresetTrack> Tracks { get; set; } = new List<PresetTrack>();
}

/// <summary>
/// One entry inside a preset. Points to a source <see cref="Track"/> (the
/// audio file + library defaults) and optionally overrides any playback
/// setting for this entry only. The same Track may appear in a preset
/// multiple times with different overrides.
///
/// Null override = inherit the Track's value. Non-null override = use this
/// instead. The <see cref="EffectiveX"/> helpers resolve the inheritance.
/// </summary>
public class PresetTrack
{
    public int Id { get; set; }
    public int PresetId { get; set; }
    public Preset? Preset { get; set; }

    public int TrackId { get; set; }
    public Track? Track { get; set; }

    public int Order { get; set; }

    public float? OverrideVolume { get; set; }
    public TimeSpan? OverrideStartPoint { get; set; }
    public TimeSpan? OverrideEndPoint { get; set; }
    public TimeSpan? OverrideFadeIn { get; set; }
    public TimeSpan? OverrideFadeOut { get; set; }
    public TimeSpan? OverrideStartDelay { get; set; }
    public bool? OverrideIsLooping { get; set; }

    public float EffectiveVolume => OverrideVolume ?? Track?.Volume ?? 1.0f;
    public TimeSpan? EffectiveStartPoint => OverrideStartPoint ?? Track?.StartPoint;
    public TimeSpan? EffectiveEndPoint => OverrideEndPoint ?? Track?.EndPoint;
    public TimeSpan EffectiveFadeIn => OverrideFadeIn ?? Track?.FadeInDuration ?? TimeSpan.Zero;
    public TimeSpan EffectiveFadeOut => OverrideFadeOut ?? Track?.FadeOutDuration ?? TimeSpan.Zero;
    public TimeSpan EffectiveStartDelay => OverrideStartDelay ?? Track?.StartDelay ?? TimeSpan.Zero;
    public bool EffectiveIsLooping => OverrideIsLooping ?? Track?.IsLooping ?? false;
}
