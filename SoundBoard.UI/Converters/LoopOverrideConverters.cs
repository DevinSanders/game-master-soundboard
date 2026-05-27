using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace SoundBoard.UI.Converters;

/// <summary>
/// Renders a <c>bool?</c> loop-override value as a short label:
///   null  → "inherit"
///   true  → "force on"
///   false → "force off"
/// Used by the playlist editor's per-item loop button so the current state
/// is visible without hovering for a tooltip.
/// </summary>
public class LoopOverrideLabelConverter : IValueConverter
{
    public static readonly LoopOverrideLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            null => "inherit",
            true => "force on",
            false => "force off",
            _ => "inherit",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Colors the loop-override label so users can pick out at a glance which
/// items are overridden: a forced-state value uses the accent / warning
/// color; inherit stays in the secondary-text color.
///
/// <para>Pulls live theme brushes via <see cref="Services.ThemeBrushes"/>'s
/// pattern — the keys <c>LoopInheritForeground</c>,
/// <c>LoopForceOnForeground</c>, and <c>LoopForceOffForeground</c> live in
/// <c>App.axaml</c>'s ThemeDictionaries and follow the active palette.</para>
/// </summary>
public class LoopOverrideBrushConverter : IValueConverter
{
    public static readonly LoopOverrideBrushConverter Instance = new();

    // Fallbacks used if Application.Current isn't available (rare — only
    // during unit tests of the converter, today).
    private static readonly IBrush InheritFallback  = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0xA0));
    private static readonly IBrush ForceOnFallback  = new SolidColorBrush(Color.FromRgb(0x55, 0xFF, 0x55));
    private static readonly IBrush ForceOffFallback = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            true  => Resolve("LoopForceOnForeground",  ForceOnFallback),
            false => Resolve("LoopForceOffForeground", ForceOffFallback),
            _     => Resolve("LoopInheritForeground",  InheritFallback),
        };

    private static IBrush Resolve(string key, IBrush fallback)
    {
        var app = Avalonia.Application.Current;
        if (app?.Resources is null) return fallback;
        if (app.Resources.TryGetResource(key, app.ActualThemeVariant, out var found) && found is IBrush b)
            return b;
        return fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
