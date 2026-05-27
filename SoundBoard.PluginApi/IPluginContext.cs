namespace SoundBoard.PluginApi;

/// <summary>
/// Host services handed to a plugin at <see cref="IPlugin.Initialize"/>.
/// </summary>
public interface IPluginContext
{
    /// <summary>
    /// Window service for surfacing plugin UIs. May be <c>null</c> in
    /// headless / CLI hosts; null-check before use.
    /// </summary>
    IWindowService? WindowService { get; }

    /// <summary>
    /// A private writable directory unique to this plugin (under the host's
    /// app-data area). Use it for config files, caches, and any persistent
    /// state. Created by the host before <see cref="IPlugin.Initialize"/>
    /// is called.
    /// </summary>
    string PluginDataPath { get; }

    /// <summary>
    /// Read-only snapshot of the codec plugins the host has loaded. Used
    /// by transport plugins (URL streamers, archive readers, …) to
    /// dispatch decoded bytes to a format-specific codec without bundling
    /// every decoder library. See <see cref="IAudioCodecRegistry"/> for
    /// the boundaries and safety contract.
    ///
    /// <para>The registry is a one-shot snapshot built once after every
    /// codec plugin has finished loading and registered into the host.
    /// Plugins receive it via this property at
    /// <see cref="IPlugin.Initialize"/> time, but the snapshot is
    /// constructed AFTER all plugins finish initializing — so reading
    /// the registry inside <see cref="IPlugin.Initialize"/> may return
    /// an empty list. Query it later (lazily, at <c>CreateStream</c>
    /// time) for the complete set.</para>
    ///
    /// <para>May be <c>null</c> in headless / CLI hosts that don't run
    /// the codec pipeline. Null-check before use.</para>
    /// </summary>
    IAudioCodecRegistry? CodecRegistry { get; }

    /// <summary>
    /// Sidechain registry — exposes one <see cref="ISidechainSource"/>
    /// per audio bus so plugins (the canonical example being a ducker)
    /// can subscribe to one bus's post-bus-FX signal as a detection
    /// trigger and apply gain reduction to a different bus the same
    /// plugin is attached to.
    ///
    /// <para>The registry is live — buses added or removed via Settings
    /// → Buses fire <see cref="ISidechainRegistry.SourcesChanged"/> so
    /// source-picker UIs can refresh. Plugins persisting a source id
    /// should fall back to "(none)" if
    /// <see cref="ISidechainRegistry.GetSourceById"/> returns null at
    /// reattach time (the bus may have been deleted between launches).</para>
    ///
    /// <para>May be <c>null</c> in headless / CLI hosts that don't run
    /// the bus pipeline. Null-check before use.</para>
    /// </summary>
    ISidechainRegistry? Sidechain { get; }
}
