using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoard.Core.Services;

namespace SoundBoard.UI.ViewModels;

/// <summary>
/// One row in the Settings window's installed-plugins list. Reads metadata
/// from <see cref="IPluginMetadata"/> and tracks whether the user has the
/// plugin enabled (separate from whether it's currently loaded — toggling
/// enable takes effect after the next restart).
/// </summary>
public partial class PluginItemViewModel : ObservableObject
{
    private readonly IPluginMetadata _metadata;

    public string Id => _metadata.Id;
    public string Name => _metadata.Name;
    public string Version => _metadata.Version;
    public string Author => _metadata.Author;
    public string Description => _metadata.Description;
    public bool IsLoaded => _metadata.IsLoaded;
    public bool LoadFailed => _metadata.LoadFailed;
    public string? ErrorMessage => _metadata.ErrorMessage;

    [ObservableProperty]
    private bool _isEnabledInSettings;

    public PluginItemViewModel(IPluginMetadata metadata, bool isEnabled)
    {
        _metadata = metadata;
        _isEnabledInSettings = isEnabled;
    }
}
