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
        if (value is not long bytes) return "0 B";
        if (bytes == 0) return "0 B";

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
/// Converts a health score (0-100) to a color brush for the gauge.
/// Red (0-40) → Orange (40-70) → Green (70-100).
/// </summary>
public class HealthScoreColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int score) return new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E));

        var color = score switch
        {
            < 40 => System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x8A),   // Coral/Warning
            < 70 => System.Windows.Media.Color.FromRgb(0xFF, 0xB7, 0x4D),   // Amber
            _ => System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xC3)       // Cyan/Success
        };

        return new System.Windows.Media.SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
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
/// Converts a hex color string like "#FF6B8A" to a SolidColorBrush.
/// </summary>
public class HexToBrushConverter : IValueConverter
{
    private static readonly System.Windows.Media.SolidColorBrush FallbackBrush =
        new(System.Windows.Media.Color.FromRgb(0x7C, 0x5C, 0xFC));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && hex.StartsWith('#') && hex.Length == 7)
        {
            try
            {
                byte r = System.Convert.ToByte(hex.Substring(1, 2), 16);
                byte g = System.Convert.ToByte(hex.Substring(3, 2), 16);
                byte b = System.Convert.ToByte(hex.Substring(5, 2), 16);
                return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            }
            catch (FormatException) { }
        }
        return FallbackBrush;
    }

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

public class TrendArrowColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var arrow = value as string ?? string.Empty;
        return arrow switch
        {
            "↑" => new SolidColorBrush(Color.FromRgb(0x4F, 0xD4, 0xA0)), // AuraSuccess / Mint
            "↓" => new SolidColorBrush(Color.FromRgb(0xE8, 0x60, 0x70)), // AuraWarning / Coral
            _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x65, 0x80)),   // Muted
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class HealthGlowBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var glowColor = value is int score
            ? score switch
            {
                < 40 => Color.FromArgb(0x14, 0xFF, 0x6B, 0x8A),  // Coral glow
                < 70 => Color.FromArgb(0x14, 0xFF, 0xB7, 0x4D),  // Amber glow
                _ => Color.FromArgb(0x14, 0x5C, 0xA8, 0x8A)      // Teal glow
            }
            : Color.FromArgb(0x14, 0x9B, 0x88, 0xFF);            // Default violet

        var fadeColor = Color.FromArgb((byte)(glowColor.A / 3), glowColor.R, glowColor.G, glowColor.B);

        var brush = new RadialGradientBrush
        {
            Center = new System.Windows.Point(0.3, 0.5),
            RadiusX = 0.5,
            RadiusY = 1.2,
            GradientStops =
            {
                new GradientStop(glowColor, 0),
                new GradientStop(fadeColor, 0.6),
                new GradientStop(Colors.Transparent, 1)
            }
        };
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
