using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace SoundBoard.UI.Converters;

/// <summary>
/// Renders a bound expand/collapse bool as the appropriate Unicode arrow
/// glyph — "▾" when expanded, "▸" when collapsed. Used by the Library
/// view's facet headers and anywhere else a single-character chevron
/// indicates expand state.
/// </summary>
public class ChevronConverter : IValueConverter
{
    public static readonly ChevronConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "▾" : "▸";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
