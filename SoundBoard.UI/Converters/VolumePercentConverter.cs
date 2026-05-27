using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace SoundBoard.UI.Converters;

/// <summary>
/// Formats a 0.0–2.0 volume multiplier as a percentage string (e.g. 0.85 → "85%",
/// 1.0 → "100%", 2.0 → "200%"). Used in volume slider readouts throughout the app.
/// </summary>
public sealed class VolumePercentConverter : IValueConverter
{
    public static readonly VolumePercentConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "0%";
        double v = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => System.Convert.ToDouble(value, CultureInfo.InvariantCulture),
        };
        return $"{Math.Round(v * 100)}%";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
