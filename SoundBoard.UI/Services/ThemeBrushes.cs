using Avalonia;
using Avalonia.Media;

namespace SoundBoard.UI.Services;

/// <summary>
/// Resolves runtime theme brushes for code-behind that can't easily bind
/// XAML resources (e.g. drop-zone borders set imperatively in pointer
/// event handlers). Centralised so theme keys live in one place — pre-fix,
/// every drop-target view defined its own hex literal for the highlight
/// color, drifting from the active <c>PrimaryAccent</c> whenever a theme
/// plugin swapped the accent.
/// </summary>
public static class ThemeBrushes
{
    /// <summary>The drop-zone highlight brush (semi-transparent accent).
    /// Returned as the live resource so a future palette swap can update
    /// it without a recompile.</summary>
    public static IBrush DropZoneHighlight =>
        Resolve("DropZoneHighlight") ?? new SolidColorBrush(Color.FromArgb(180, 0x25, 0x63, 0xEB));

    /// <summary>Safe lookup for a theme-keyed <see cref="IBrush"/>.
    /// Returns null if the key isn't found or the resolved value isn't a
    /// brush — caller is expected to fall back to a literal. Use this
    /// instead of casting <c>Application.Current.FindResource(key)</c>:
    /// the latter returns <see cref="AvaloniaProperty.UnsetValue"/> on a
    /// miss, and a hard cast to <see cref="IBrush"/> on that sentinel
    /// throws <see cref="System.InvalidCastException"/> at runtime.</summary>
    public static IBrush? Resolve(string key)
    {
        var app = Application.Current;
        if (app?.Resources is null) return null;
        if (app.Resources.TryGetResource(key, app.ActualThemeVariant, out var found) && found is IBrush brush)
            return brush;
        return null;
    }
}
