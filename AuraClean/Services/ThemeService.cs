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

    // Dark theme colors (Storm Dark)
    private static readonly Dictionary<string, Color> DarkColors = new()
    {
        ["AuraBgColor"]              = (Color)ColorConverter.ConvertFromString("#06080C"),
        ["AuraSurfaceColor"]         = (Color)ColorConverter.ConvertFromString("#0C1016"),
        ["AuraSurfaceLightColor"]    = (Color)ColorConverter.ConvertFromString("#141A22"),
        ["AuraSurfaceElevatedColor"] = (Color)ColorConverter.ConvertFromString("#1A2230"),
        ["AuraTextBrightColor"]      = (Color)ColorConverter.ConvertFromString("#E8ECF0"),
        ["AuraTextColor"]            = (Color)ColorConverter.ConvertFromString("#B0B8C4"),
        ["AuraTextDimColor"]         = (Color)ColorConverter.ConvertFromString("#6B7A8A"),
        ["AuraTextMutedColor"]       = (Color)ColorConverter.ConvertFromString("#3A4450"),
        ["AuraBorderColor"]          = (Color)ColorConverter.ConvertFromString("#1E2832"),
        ["AuraBorderSubtleColor"]    = (Color)ColorConverter.ConvertFromString("#161E26"),
    };

    // Light theme colors (Storm Light)
    private static readonly Dictionary<string, Color> LightColors = new()
    {
        ["AuraBgColor"]              = (Color)ColorConverter.ConvertFromString("#F2F4F6"),
        ["AuraSurfaceColor"]         = (Color)ColorConverter.ConvertFromString("#FAFBFC"),
        ["AuraSurfaceLightColor"]    = (Color)ColorConverter.ConvertFromString("#E8ECF0"),
        ["AuraSurfaceElevatedColor"] = (Color)ColorConverter.ConvertFromString("#DDE2E8"),
        ["AuraTextBrightColor"]      = (Color)ColorConverter.ConvertFromString("#141A22"),
        ["AuraTextColor"]            = (Color)ColorConverter.ConvertFromString("#2A3440"),
        ["AuraTextDimColor"]         = (Color)ColorConverter.ConvertFromString("#5A6878"),
        ["AuraTextMutedColor"]       = (Color)ColorConverter.ConvertFromString("#94A0AE"),
        ["AuraBorderColor"]          = (Color)ColorConverter.ConvertFromString("#CAD0D8"),
        ["AuraBorderSubtleColor"]    = (Color)ColorConverter.ConvertFromString("#DDE2E8"),
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
                    newBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#E8ECF0"), 0));
                    newBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#DDE2E8"), 1));
                }
                else
                {
                    newBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#0A0E14"), 0));
                    newBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#060810"), 1));
                }
                res["AuraSidebarGradient"] = newBrush;
            }
            else
            {
                if (light)
                {
                    sidebarBrush.GradientStops[0].Color = (Color)ColorConverter.ConvertFromString("#E8ECF0");
                    sidebarBrush.GradientStops[1].Color = (Color)ColorConverter.ConvertFromString("#DDE2E8");
                }
                else
                {
                    sidebarBrush.GradientStops[0].Color = (Color)ColorConverter.ConvertFromString("#0A0E14");
                    sidebarBrush.GradientStops[1].Color = (Color)ColorConverter.ConvertFromString("#060810");
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
