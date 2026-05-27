namespace SoundBoard.PluginApi;

/// <summary>
/// Base interface every plugin and theme must implement. A plugin DLL must
/// expose exactly one concrete type implementing <see cref="IPlugin"/>; the
/// host instantiates it via the parameterless constructor and calls
/// <see cref="Initialize"/> once at startup.
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// Stable, globally-unique identifier (reverse-DNS style is conventional,
    /// e.g. <c>"com.acme.equalizer"</c>). The host uses this to remember
    /// enable/select state across launches, so it must not change between
    /// versions of the same plugin.
    /// </summary>
    string Id { get; }

    /// <summary>Display name shown in the settings UI.</summary>
    string Name { get; }

    /// <summary>Plugin version string. Free-form; conventionally semver.</summary>
    string Version { get; }

    /// <summary>Plugin author for attribution in the settings UI.</summary>
    string Author { get; }

    /// <summary>Short description shown alongside the name.</summary>
    string Description { get; }

    /// <summary>
    /// Called once during host startup, after the plugin is instantiated and
    /// before any marker-interface hooks (codec registration, theme apply,
    /// etc.) fire. Use this to read config from
    /// <see cref="IPluginContext.PluginDataPath"/>.
    /// </summary>
    void Initialize(IPluginContext context);

    /// <summary>
    /// Called on host shutdown. Release file handles, flush state, etc.
    /// </summary>
    void Shutdown();
}
