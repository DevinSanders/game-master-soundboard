using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoard.PluginApi;
using SoundBoard.UI.ViewModels;

namespace SoundBoard.UI.Services;

/// <summary>
/// Opens secondary windows on behalf of view models without leaking
/// Avalonia types into the VMs themselves. Dedupes by view-model type so
/// asking to show a window that's already open just activates the existing
/// one instead of spawning a duplicate. Also implements the plugin-facing
/// <see cref="IWindowService"/> so plugin UI extensions can use the same
/// surface.
/// </summary>
public interface IWindowManagerService : IWindowService
{
    /// <summary>Open (or focus) a window hosting the given view model.</summary>
    void ShowWindow(ViewModelBase viewModel, string title, int width = 800, int height = 600);

    /// <summary>Open (or focus) a window using an explicit string key for
    /// dedupe — needed when the same VM type may be popped out multiple
    /// times targeting different underlying items (e.g. one shortcut page
    /// per window).</summary>
    void ShowWindow(ViewModelBase viewModel, string key, string title, int width, int height);

    /// <summary>Close the window currently hosting <paramref name="viewModel"/>, if any.</summary>
    void CloseWindow(ViewModelBase viewModel);
}
