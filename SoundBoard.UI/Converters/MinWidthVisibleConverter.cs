using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace SoundBoard.UI.Converters;

/// <summary>
/// Returns true when the bound double (typically a window/control width) is
/// at least the converter parameter. Used to hide UI elements responsively
/// when the window shrinks below a threshold.
/// </summary>
public class MinWidthVisibleConverter : IValueConverter
{
    public static readonly MinWidthVisibleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double width = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0
        };
        double threshold = 0;
        if (parameter is string s) double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold);
        else if (parameter is double pd) threshold = pd;
        return width >= threshold;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
