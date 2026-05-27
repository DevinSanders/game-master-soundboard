using SoundBoard.PluginApi;

namespace SoundBoard.Core.Plugins;

/// <summary>
/// Host-side concrete <see cref="IPluginContext"/>. Constructed once per
/// plugin during load and passed to <see cref="IPlugin.Initialize"/>.
///
/// <para><b>Registry timing.</b> <see cref="CodecRegistry"/> is settable
/// (via <see cref="SetCodecRegistry"/>) so the host can build the
/// registry AFTER every codec plugin has registered itself, then
/// retro-fit each context with the completed snapshot. This way a
/// transport plugin like <c>codec.webstream</c> sees the full list of
/// installed codecs at <c>CreateStream</c> time even though its
/// <c>Initialize</c> ran before the registry snapshot was built.</para>
/// </summary>
public class PluginContext : IPluginContext
{
    public IWindowService? WindowService { get; init; }
    public string PluginDataPath { get; init; }

    /// <summary>The codec registry. Set by <see cref="SetCodecRegistry"/>
    /// once the host has finished loading every plugin; null before
    /// then (which is also why the interface declares it as nullable —
    /// plugins must null-check).</summary>
    public IAudioCodecRegistry? CodecRegistry { get; private set; }

    /// <summary>The sidechain registry. Set by <see cref="SetSidechainRegistry"/>
    /// when the host audio engine is ready (after bus mixers are created
    /// but before the first plugin's <c>CreateEffect</c> runs). Null in
    /// headless / CLI hosts that don't bring up the audio pipeline.</summary>
    public ISidechainRegistry? Sidechain { get; private set; }

    public PluginContext(IWindowService? windowService, string pluginDataPath)
    {
        WindowService = windowService;
        PluginDataPath = pluginDataPath;
    }

    /// <summary>Host-only mutator. Called by <see cref="Services.PluginService"/>
    /// after <c>DiscoverAndLoad</c> finishes to inject the completed
    /// snapshot into every plugin context that was built earlier in the
    /// pass. Not exposed on the public interface — plugins can read the
    /// registry but not replace it.</summary>
    internal void SetCodecRegistry(IAudioCodecRegistry registry)
    {
        CodecRegistry = registry;
    }

    /// <summary>Host-only mutator for the sidechain registry. Plugins
    /// observe live changes via <see cref="ISidechainRegistry.SourcesChanged"/>;
    /// the registry reference itself is stable for the app's lifetime.</summary>
    internal void SetSidechainRegistry(ISidechainRegistry registry)
    {
        Sidechain = registry;
    }
}
