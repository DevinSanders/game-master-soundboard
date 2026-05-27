namespace SoundBoard.Core.Models;

/// <summary>
/// One configured DSP sampler bound to a target (master bus, shortcut,
/// preset, or playlist). The host materialises an
/// <c>ISamplerInstance</c> per row at startup, calls
/// <c>DeserializeConfig(ConfigJson)</c>, and hooks it into the audio
/// chain for the appropriate target.
///
/// Polymorphic relationship: <see cref="OwnerType"/> + <see cref="OwnerId"/>
/// together identify the owning entity. <c>OwnerId</c> is null when
/// <c>OwnerType == Master</c>.
/// </summary>
public class SamplerAttachment
{
    public int Id { get; set; }

    /// <summary>Plugin <c>Id</c> (e.g. <c>"com.example.reverb"</c>) of the
    /// sampler. Looked up against the loaded plugin list at attach time.
    /// If the plugin isn't installed, the row is kept (so the user can
    /// reinstall and recover) and skipped in the chain.</summary>
    public string PluginId { get; set; } = "";

    /// <summary>JSON blob produced by the instance's <c>SerializeConfig()</c>.
    /// Round-tripped through <c>DeserializeConfig</c> when the host wires
    /// the instance up at startup.</summary>
    public string ConfigJson { get; set; } = "";

    /// <summary>Target type — see <see cref="SamplerOwnerType"/>.</summary>
    public SamplerOwnerType OwnerType { get; set; }

    /// <summary>Foreign-key id of the owning entity. Null for
    /// <see cref="SamplerOwnerType.Master"/> rows. EF Core does not
    /// constrain this with a hard FK because <see cref="OwnerType"/>
    /// switches which table it points at; the application enforces
    /// referential integrity on add/remove.</summary>
    public int? OwnerId { get; set; }

    /// <summary>Position in the owner's chain. Lower values run first
    /// (source → order 0 → order 1 → … → output).</summary>
    public int Order { get; set; }

    /// <summary>When true, the host skips this attachment when building
    /// the chain. Used by the editor UI for A/B comparison without
    /// destroying the configuration.</summary>
    public bool IsBypassed { get; set; }
}

/// <summary>Which kind of entity owns a <see cref="SamplerAttachment"/>.</summary>
public enum SamplerOwnerType
{
    /// <summary>Master bus — affects the post-bus-combine output. Runs
    /// after every per-bus FX chain. <c>OwnerId</c> is null.</summary>
    Master = 0,
    /// <summary>Single soundboard button. <c>OwnerId</c> = <c>ShortcutButton.Id</c>.</summary>
    Shortcut = 1,
    /// <summary>Preset (all of its tracks). <c>OwnerId</c> = <c>Preset.Id</c>.</summary>
    Preset = 2,
    /// <summary>Playlist (all of its items). <c>OwnerId</c> = <c>Playlist.Id</c>.</summary>
    Playlist = 3,
    /// <summary>One specific audio bus. <c>OwnerId</c> = <c>Bus.Id</c>.
    /// Effects run between the bus's MixingSampleProvider and the
    /// combine into the master output. Sidechain sources can subscribe
    /// to the post-bus-FX signal of any bus.</summary>
    Bus = 4,
}
