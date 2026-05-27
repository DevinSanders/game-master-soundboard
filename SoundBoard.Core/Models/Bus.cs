namespace SoundBoard.Core.Models;

/// <summary>
/// One named audio bus. Tracks route to exactly one bus (configured per
/// Track, optionally overridden per Preset or per Shortcut). The bus is
/// where audio actually mixes — each <see cref="Bus"/> corresponds to a
/// dedicated <c>MixingSampleProvider</c> in the audio engine. After all
/// buses mix internally, the host sums them into the master output where
/// the post-master FX chain runs.
///
/// <para><b>Why buses exist.</b> Sidechain effects (the canonical example
/// being a ducker that pushes music down while SFX is playing) need to
/// detect on one signal and apply gain to a different signal. Without
/// buses the host has only one mix, so a ducker either self-ducks (every
/// loud sample triggers its own gain reduction) or has nothing useful to
/// detect. Buses split the mix into independent streams that sidechain
/// plugins can listen across.</para>
///
/// <para><b>Built-in buses.</b> The first launch seeds three buses
/// (<c>Music</c>, <c>Ambient</c>, <c>SFX</c>) with
/// <see cref="IsBuiltIn"/> = true. Built-ins can be renamed but cannot
/// be deleted — deleting them would orphan every <see cref="Track.BusId"/>
/// pointing at them, and the audio engine assumes at least one bus
/// exists. Users can freely add additional buses for finer-grained
/// routing.</para>
/// </summary>
public class Bus
{
    public int Id { get; set; }

    /// <summary>Display name (shown in the bus dropdowns and the bus FX
    /// chain editor). Free-form. Can be edited; the lineage is the
    /// <see cref="Id"/>, not the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Sort order in dropdowns and the bus FX chain UI. Lower
    /// values come first. The seeded order is Music (0), Ambient (10),
    /// SFX (20); leaving gaps lets users insert custom buses between
    /// them without renumbering.</summary>
    public int Order { get; set; }

    /// <summary>Optional accent colour for UI hints (e.g. tint the bus
    /// dropdown badge). Free-form hex string or null. v1 doesn't render
    /// this anywhere; reserved for future per-bus mixer cards.</summary>
    public string? Color { get; set; }

    /// <summary>When true, this bus was seeded by the host and cannot
    /// be deleted from the Buses management UI. Built-in buses are still
    /// renameable. Custom user-added buses have <see cref="IsBuiltIn"/>
    /// = false and can be freely deleted (tracks pointing at the deleted
    /// bus get reassigned to the first surviving bus by ordinal).</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Per-bus linear gain. 1.0 = unity. Applied after the bus's
    /// FX chain runs but before the bus signal feeds into the master
    /// combine. Persisted so the GM's level balance survives app
    /// restarts. The Bus Mixer page hosts the slider; the audio thread
    /// reads it through <see cref="Audio.BusMixer.Volume"/>'s
    /// Volatile.Int32 bit-pattern so updates are torn-write-safe across
    /// CPUs.</summary>
    public float Volume { get; set; } = 1.0f;
}

/// <summary>Stable IDs for the built-in buses. Persisted in the DB so the
/// audio engine and the migration code can hard-code the "default bus
/// for new tracks" without a lookup. Keep these in lockstep with the
/// seed inserts in <c>SchemaMigrations</c> — the version-1001 migration
/// hard-codes these values.</summary>
public static class BuiltInBusIds
{
    public const int Music = 1;
    public const int Ambient = 2;
    public const int Sfx = 3;

    /// <summary>The bus every new Track gets unless the user changes it.
    /// Picked Music because for soundboard use the most common track
    /// type is background music; sound effects are the loud-but-rare
    /// special case that the user opts INTO the SFX bus for.</summary>
    public const int DefaultForNewTracks = Music;
}
