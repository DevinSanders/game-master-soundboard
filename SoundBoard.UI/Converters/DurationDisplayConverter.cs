using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace SoundBoard.UI.Converters;

/// <summary>
/// Formats a <see cref="TimeSpan"/> or nullable TimeSpan as a compact length
/// string: "m:ss" for under an hour, "h:mm:ss" beyond. Null or a non-finite
/// value renders as the placeholder ("—").
/// </summary>
public sealed class DurationDisplayConverter : IValueConverter
{
    public static readonly DurationDisplayConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Boxing a Nullable<TimeSpan> with a value lands here as a boxed
        // TimeSpan; a null nullable arrives as null. One pattern check
        // handles both.
        if (value is TimeSpan ts) return Format(ts);
        return "—";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        // One decimal of seconds throughout the app so cuts shorter than
        // a second still read meaningfully — e.g. a 0.8s SFX vs. a 0s typo.
        int tenths = (t.Milliseconds / 100);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}.{tenths}"
            : $"{t.Minutes}:{t.Seconds:D2}.{tenths}";
    }
}
