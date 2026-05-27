namespace SoundBoard.PluginApi;

/// <summary>
/// Contributes a UI panel to one of the host's known insertion points
/// (the <see cref="UIPlacement"/>). Use this for visual extensions that
/// don't fit one of the audio-specific plugin contracts.
/// </summary>
public interface IUIExtensionPlugin : IPlugin
{
    /// <summary>Insertion point where the host should mount this UI.</summary>
    UIPlacement Placement { get; }

    /// <summary>
    /// Return an Avalonia control (or a ViewModel the host can resolve to
    /// one) to host at the requested placement.
    /// </summary>
    object CreateControl();
}
