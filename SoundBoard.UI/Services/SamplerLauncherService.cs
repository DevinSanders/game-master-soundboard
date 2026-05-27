using SoundBoard.Core.Models;
using SoundBoard.Core.Services;
using SoundBoard.UI.ViewModels;

namespace SoundBoard.UI.Services;

/// <summary>
/// One-call façade for opening the sampler chain editor for any owner.
/// Used by the playing-mixer-card FX buttons and by the engine when it
/// hands callbacks to spawned VMs. Wraps the per-owner key/title
/// conventions so call sites can stay terse.
/// </summary>
public interface ISamplerLauncherService
{
    void Open(SamplerOwnerType ownerType, int? ownerId, string displayName);
}

/// <inheritdoc cref="ISamplerLauncherService"/>
public sealed class SamplerLauncherService : ISamplerLauncherService
{
    private readonly ISamplerChainService _chain;
    private readonly IPluginService _plugins;
    private readonly IWindowManagerService _windows;

    public SamplerLauncherService(ISamplerChainService chain, IPluginService plugins, IWindowManagerService windows)
    {
        _chain = chain;
        _plugins = plugins;
        _windows = windows;
    }

    public void Open(SamplerOwnerType ownerType, int? ownerId, string displayName)
    {
        var key = ownerType switch
        {
            SamplerOwnerType.Master   => "sampler-editor-master",
            SamplerOwnerType.Shortcut => $"sampler-editor-shortcut-{ownerId}",
            SamplerOwnerType.Preset   => $"sampler-editor-preset-{ownerId}",
            SamplerOwnerType.Playlist => $"sampler-editor-playlist-{ownerId}",
            SamplerOwnerType.Bus      => $"sampler-editor-bus-{ownerId}",
            _                         => $"sampler-editor-{ownerType}-{ownerId}",
        };
        var label = string.IsNullOrWhiteSpace(displayName) ? ownerType.ToString() : displayName;
        var vm = new SamplerEditorViewModel(_chain, _plugins, ownerType, ownerId, label);
        _windows.ShowWindow(vm, key, $"FX Chain — {label}", 760, 600);
    }
}
