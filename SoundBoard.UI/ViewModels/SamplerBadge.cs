namespace SoundBoard.UI.ViewModels;

/// <summary>
/// Lightweight descriptor of an attached sampler — what shows up as a chip
/// on a mixer card. Built by the engine from the ephemeral sampler chain
/// at spawn time; carries the plugin's display name (for the chip label)
/// and id (in case future versions want a click-to-open-editor flow).
/// </summary>
public sealed record SamplerBadge(string PluginName, string PluginId);
