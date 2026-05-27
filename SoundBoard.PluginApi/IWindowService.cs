namespace SoundBoard.PluginApi;

/// <summary>
/// Lets a plugin request a new host window for a UI it owns.
/// </summary>
public interface IWindowService
{
    /// <summary>
    /// Opens a new window containing <paramref name="content"/>. The host
    /// resolves a view for the content (typically by matching ViewModel type
    /// to view), so <paramref name="content"/> may be either a control or a
    /// ViewModel known to the host.
    /// </summary>
    /// <param name="content">Avalonia <c>Control</c> instance, or a
    /// ViewModel the host can resolve to one.</param>
    /// <param name="title">Window title shown in the OS title bar.</param>
    /// <param name="width">Optional preferred width (logical pixels).</param>
    /// <param name="height">Optional preferred height (logical pixels).</param>
    void ShowWindow(object content, string title, double? width = null, double? height = null);
}
