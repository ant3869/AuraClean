using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace AuraClean.Services;

/// <summary>
/// Toggles between Dark and Light themes at runtime.
/// Modifies existing brush colors in-place so StaticResource bindings update automatically.
/// </summary>
public static class ThemeService
{
    private static bool _isLightTheme;

    public static bool IsLightTheme => _isLightTheme;

    // Dark theme colors (original Obsidian Aurora)
    private static readonly Dictionary<string, Color> DarkColors = new()
    {
        ["AuraBgColor"]              = (Color)ColorConverter.ConvertFromString("#080810"),
        ["AuraSurfaceColor"]         = (Color)ColorConverter.ConvertFromString("#0F0F1E"),
        ["AuraSurfaceLightColor"]    = (Color)ColorConverter.ConvertFromString("#171730"),
        ["AuraSurfaceElevatedColor"] = (Color)ColorConverter.ConvertFromString("#1F1F42"),
        ["AuraTextBrightColor"]      = (Color)ColorConverter.ConvertFromString("#F0F0FF"),
        ["AuraTextColor"]            = (Color)ColorConverter.ConvertFromString("#C8C8E0"),
        ["AuraTextDimColor"]         = (Color)ColorConverter.ConvertFromString("#7B7BA0"),
        ["AuraTextMutedColor"]       = (Color)ColorConverter.ConvertFromString("#3D3D5C"),
        ["AuraBorderColor"]          = (Color)ColorConverter.ConvertFromString("#222244"),
        ["AuraBorderSubtleColor"]    = (Color)ColorConverter.ConvertFromString("#1A1A36"),
    };

    // Light theme colors (Aurora Light)
    private static readonly Dictionary<string, Color> LightColors = new()
    {
        ["AuraBgColor"]              = (Color)ColorConverter.ConvertFromString("#F5F5FA"),
        ["AuraSurfaceColor"]         = (Color)ColorConverter.ConvertFromString("#FFFFFF"),
        ["AuraSurfaceLightColor"]    = (Color)ColorConverter.ConvertFromString("#EDEDF5"),
        ["AuraSurfaceElevatedColor"] = (Color)ColorConverter.ConvertFromString("#E0E0F0"),
        ["AuraTextBrightColor"]      = (Color)ColorConverter.ConvertFromString("#1A1A2E"),
        ["AuraTextColor"]            = (Color)ColorConverter.ConvertFromString("#2E2E4A"),
        ["AuraTextDimColor"]         = (Color)ColorConverter.ConvertFromString("#6B6B8A"),
        ["AuraTextMutedColor"]       = (Color)ColorConverter.ConvertFromString("#A0A0BA"),
        ["AuraBorderColor"]          = (Color)ColorConverter.ConvertFromString("#D0D0E0"),
        ["AuraBorderSubtleColor"]    = (Color)ColorConverter.ConvertFromString("#E0E0EC"),
    };

    // Mapping from Color resource keys to their SolidColorBrush resource keys
    private static readonly Dictionary<string, string> ColorToBrushMap = new()
    {
        ["AuraBgColor"]              = "AuraBackground",
        ["AuraSurfaceColor"]         = "AuraSurface",
        ["AuraSurfaceLightColor"]    = "AuraSurfaceLight",
        ["AuraSurfaceElevatedColor"] = "AuraSurfaceElevated",
        ["AuraTextBrightColor"]      = "AuraTextBright",
        ["AuraTextColor"]            = "AuraTextPrimary",
        ["AuraTextDimColor"]         = "AuraTextSecondary",
        ["AuraTextMutedColor"]       = "AuraTextMuted",
        ["AuraBorderColor"]          = "AuraBorder",
        ["AuraBorderSubtleColor"]    = "AuraBorderSubtle",
    };

    /// <summary>
    /// Applies the specified theme, swapping brush colors in-place.
    /// </summary>
    public static void ApplyTheme(bool light)
    {
        _isLightTheme = light;
        var res = Application.Current.Resources;
        var palette = light ? LightColors : DarkColors;

        foreach (var (colorKey, color) in palette)
        {
            // Update the Color resource
            res[colorKey] = color;

            // Update the corresponding SolidColorBrush in-place so existing bindings update
            if (ColorToBrushMap.TryGetValue(colorKey, out var brushKey) &&
                res[brushKey] is SolidColorBrush brush)
            {
                if (brush.IsFrozen)
                {
                    // Replace frozen brushes with new mutable ones
                    res[brushKey] = new SolidColorBrush(color);
                }
                else
                {
                    brush.Color = color;
                }
            }
        }

        // Update the sidebar gradient brush
        if (res["AuraSidebarGradient"] is LinearGradientBrush sidebarBrush)
        {
            if (sidebarBrush.IsFrozen)
            {
                var newBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1)
                };
                if (light)
                {
                    newBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#F0F0F8"), 0));
                    newBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#E8E8F0"), 1));
                }
                else
                {
                    newBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#0C0C1A"), 0));
                    newBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#08080F"), 1));
                }
                res["AuraSidebarGradient"] = newBrush;
            }
            else
            {
                if (light)
                {
                    sidebarBrush.GradientStops[0].Color = (Color)ColorConverter.ConvertFromString("#F0F0F8");
                    sidebarBrush.GradientStops[1].Color = (Color)ColorConverter.ConvertFromString("#E8E8F0");
                }
                else
                {
                    sidebarBrush.GradientStops[0].Color = (Color)ColorConverter.ConvertFromString("#0C0C1A");
                    sidebarBrush.GradientStops[1].Color = (Color)ColorConverter.ConvertFromString("#08080F");
                }
            }
        }

        // Switch Material Design base theme
        var paletteHelper = new PaletteHelper();
        var theme = paletteHelper.GetTheme();
        theme.SetBaseTheme(light ? BaseTheme.Light : BaseTheme.Dark);
        paletteHelper.SetTheme(theme);
    }

    public static void ToggleTheme() => ApplyTheme(!_isLightTheme);
}
