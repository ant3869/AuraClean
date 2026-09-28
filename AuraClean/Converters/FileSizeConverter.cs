using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AuraClean.Converters;

/// <summary>
/// Converts a byte count to a human-readable file size string (e.g., "2.3 GB").
/// </summary>
public class FileSizeConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        long bytes;
        try
        {
            bytes = value is IConvertible convertible
                ? convertible.ToInt64(CultureInfo.InvariantCulture)
                : 0;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            bytes = 0;
        }

        if (bytes <= 0) return "0 B";

        int unitIndex = 0;
        double size = bytes;
        while (size >= 1024 && unitIndex < Units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{size:F0} {Units[unitIndex]}" : $"{size:F1} {Units[unitIndex]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a boolean to a visibility value. True = Visible, False = Collapsed.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool boolValue = value is true;
        if (parameter is string p && p.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
            boolValue = !boolValue;
        return boolValue ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Inverts a boolean value.
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>
/// Inverse of BoolToVisibilityConverter. True = Collapsed, False = Visible.
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts an int to Visibility. 0 = Collapsed, any other value = Visible.
/// </summary>
public class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int i) return i > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        return System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a health score (0-100) to the theme's status tone for the gauge:
/// red below 40, amber below 70, green otherwise. Returns the shared theme brush so the
/// gauge repaints when the theme changes.
/// </summary>
public class HealthScoreColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ThemeBrushes.Get(value is int score ? ThemeBrushes.ForScore(score) : "AuraTextMuted");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Looks up theme brushes; the returned instances are updated in place on theme change.</summary>
public static class ThemeBrushes
{
    public static System.Windows.Media.Brush Get(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as System.Windows.Media.Brush
        ?? System.Windows.Media.Brushes.Transparent;

    /// <summary>Status tone for a 0-100 score: err below 40, warn below 70, ok otherwise.</summary>
    public static string ForScore(int score) => score switch
    {
        < 40 => "AuraErr",
        < 70 => "AuraWarn",
        _ => "AuraOk",
    };
}

/// <summary>
/// Converts a treemap color index (0-9) to a color brush for the nested rectangles.
/// Uses a palette of distinct, visually pleasing colors.
/// </summary>
public class TreemapColorConverter : IValueConverter
{
    private static readonly System.Windows.Media.Color[] Palette =
    [
        System.Windows.Media.Color.FromRgb(0x7C, 0x5C, 0xFC), // Violet
        System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xC3), // Cyan
        System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x8A), // Coral
        System.Windows.Media.Color.FromRgb(0xFF, 0xB7, 0x4D), // Amber
        System.Windows.Media.Color.FromRgb(0x64, 0xB5, 0xF6), // Blue
        System.Windows.Media.Color.FromRgb(0x5B, 0xF0, 0xD7), // Mint
        System.Windows.Media.Color.FromRgb(0xE0, 0x73, 0xAD), // Pink
        System.Windows.Media.Color.FromRgb(0xFF, 0xD5, 0x4F), // Yellow
        System.Windows.Media.Color.FromRgb(0x4D, 0xD0, 0xE1), // Teal
        System.Windows.Media.Color.FromRgb(0xA0, 0x88, 0xC0), // Lavender
    ];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int index = value is int i ? i % Palette.Length : 0;
        return new System.Windows.Media.SolidColorBrush(Palette[index]);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a score (0-100) to a percentage width for score bars.
/// Parameter is the max width as a double string.
/// </summary>
public class ScoreToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IConvertible || !double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double score))
            score = 0;
        double maxWidth = 200;
        if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double mw))
            maxWidth = mw;
        return Math.Max(4, score / 100.0 * maxWidth);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a theme brush key (e.g. "AuraOk") or a "#RRGGBB" string to a brush.
/// Keys resolve to the shared theme brushes, so bound elements follow theme changes.
/// </summary>
public class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
            return ThemeBrushes.Get("AuraAccentSoft");

        if (!text.StartsWith('#'))
            return ThemeBrushes.Get(text);

        if (text.Length == 7 &&
            uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            return brush;
        }

        return ThemeBrushes.Get("AuraAccentSoft");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Rotates a section chevron: expanded (true) = 0°, collapsed (false) = -90°.
/// </summary>
public class ExpandedChevronAngleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? 0.0 : -90.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Converts a score 0-100 into the arc sweep angle (0-360) for the circular gauge.
/// </summary>
public class ScoreToAngleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IConvertible || !double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double score))
            score = 0;
        return score / 100.0 * 360.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Trend arrow tone: up = ok, down = err, flat = dim.</summary>
public class TrendArrowColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ThemeBrushes.Get((value as string) switch
        {
            "↑" => "AuraOk",
            "↓" => "AuraErr",
            _ => "AuraTextMuted",
        });

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Ambient glow behind the health score. The design system uses flat surfaces with no glow,
/// so this is transparent in every state; kept so existing bindings stay valid.
/// </summary>
public class HealthGlowBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Media.Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Two-way bridge between an enum property and a group of RadioButtons:
/// IsChecked is true when the value equals ConverterParameter; checking a button writes that value back.
/// </summary>
public class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value != null && parameter is string name &&
        string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true || parameter is not string name)
            return Binding.DoNothing;

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return enumType.IsEnum && Enum.TryParse(enumType, name, ignoreCase: true, out var result)
            ? result!
            : Binding.DoNothing;
    }
}
