using System.Globalization;

namespace AuraClean.Helpers;

/// <summary>
/// Theme preference. <see cref="Auto"/> follows the Windows app theme live;
/// <see cref="Light"/> and <see cref="Dark"/> are fixed.
/// </summary>
public enum ThemeMode
{
    Auto,
    Light,
    Dark,
}

/// <summary>
/// Pure theme-preference rules (no WPF dependency so they can be unit-tested on any OS).
/// Cycle order matches the design system: Auto → Light → Dark → Auto.
/// </summary>
public static class ThemeModes
{
    public const ThemeMode Default = ThemeMode.Dark;

    public static ThemeMode Normalize(ThemeMode mode) => Enum.IsDefined(mode) ? mode : Default;

    public static bool ResolveIsLight(ThemeMode mode, bool systemPrefersLight) => Normalize(mode) switch
    {
        ThemeMode.Auto => systemPrefersLight,
        ThemeMode.Light => true,
        _ => false,
    };

    public static ThemeMode Next(ThemeMode mode) => Normalize(mode) switch
    {
        ThemeMode.Auto => ThemeMode.Light,
        ThemeMode.Light => ThemeMode.Dark,
        _ => ThemeMode.Auto,
    };

    public static string Label(ThemeMode mode) => Normalize(mode) switch
    {
        ThemeMode.Auto => "Auto (system)",
        ThemeMode.Light => "Light",
        _ => "Dark",
    };
}

/// <summary>An sRGB color with 8-bit alpha, independent of any UI framework.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Rgba Transparent = new(0, 0, 0, 0);

    /// <summary>Parses <c>#RRGGBB</c> or <c>#AARRGGBB</c>.</summary>
    public static Rgba Parse(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        var digits = hex.Trim().TrimStart('#');
        if (digits.Length is not (6 or 8) ||
            !uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"'{hex}' is not a #RRGGBB or #AARRGGBB color.");
        }

        return digits.Length == 6
            ? new Rgba((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : new Rgba((byte)(value >> 16), (byte)(value >> 8), (byte)value, (byte)(value >> 24));
    }

    public override string ToString() => A == 255
        ? $"#{R:X2}{G:X2}{B:X2}"
        : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// CSS <c>color-mix(in srgb, a p%, b)</c> with premultiplied alpha, the blend the design system
/// uses for every derived surface instead of raw opacity.
/// </summary>
public static class ColorMix
{
    public static Rgba Mix(Rgba a, double percentA, Rgba b)
    {
        var p = Math.Clamp(percentA / 100.0, 0, 1);
        double alphaA = a.A / 255.0 * p;
        double alphaB = b.A / 255.0 * (1 - p);
        var alpha = alphaA + alphaB;
        if (alpha <= 0)
            return Rgba.Transparent;

        return new Rgba(
            Channel(a.R, b.R, alphaA, alphaB, alpha),
            Channel(a.G, b.G, alphaA, alphaB, alpha),
            Channel(a.B, b.B, alphaA, alphaB, alpha),
            (byte)Math.Round(alpha * 255));
    }

    /// <summary><c>color-mix(in srgb, color p%, transparent)</c>: the same color at p% alpha.</summary>
    public static Rgba Fade(Rgba color, double percent) => Mix(color, percent, Rgba.Transparent);

    private static byte Channel(byte a, byte b, double weightA, double weightB, double alpha) =>
        (byte)Math.Round(Math.Clamp((a * weightA + b * weightB) / alpha, 0, 255));
}

/// <summary>Base tokens of one theme (OpenEval <c>tokens.css</c>).</summary>
public sealed record ThemeTokens(
    Rgba Bg, Rgba BgSubtle, Rgba BgElev,
    Rgba Bd, Rgba BdSubtle,
    Rgba Fg, Rgba FgMuted, Rgba FgDim,
    Rgba Accent, Rgba AccentSoft,
    Rgba Info, Rgba Ok, Rgba Warn, Rgba Err,
    Rgba Shadow)
{
    public static readonly ThemeTokens Dark = new(
        Bg: Rgba.Parse("#0A0A0B"), BgSubtle: Rgba.Parse("#111113"), BgElev: Rgba.Parse("#16161A"),
        Bd: Rgba.Parse("#26262B"), BdSubtle: Rgba.Parse("#1D1D22"),
        Fg: Rgba.Parse("#E8E8EA"), FgMuted: Rgba.Parse("#8B8B94"), FgDim: Rgba.Parse("#82828C"),
        Accent: Rgba.Parse("#7C5CFF"), AccentSoft: Rgba.Parse("#A78BFF"),
        Info: Rgba.Parse("#64C8C0"), Ok: Rgba.Parse("#3FB950"), Warn: Rgba.Parse("#D29922"), Err: Rgba.Parse("#F85149"),
        Shadow: new Rgba(0, 0, 0, 46));

    public static readonly ThemeTokens Light = new(
        Bg: Rgba.Parse("#FFFFFF"), BgSubtle: Rgba.Parse("#F6F6F8"), BgElev: Rgba.Parse("#ECECF0"),
        Bd: Rgba.Parse("#D1D1D6"), BdSubtle: Rgba.Parse("#E0E0E5"),
        Fg: Rgba.Parse("#1A1A1F"), FgMuted: Rgba.Parse("#5C5C66"), FgDim: Rgba.Parse("#63636D"),
        Accent: Rgba.Parse("#7C5CFF"), AccentSoft: Rgba.Parse("#6344E6"),
        Info: Rgba.Parse("#096C68"), Ok: Rgba.Parse("#1A7F37"), Warn: Rgba.Parse("#855800"), Err: Rgba.Parse("#CF222E"),
        Shadow: new Rgba(0, 0, 0, 20));
}

/// <summary>Every resource value one theme needs, keyed by resource key.</summary>
public sealed record ThemeResourceSet(
    IReadOnlyDictionary<string, Rgba> Colors,
    IReadOnlyDictionary<string, Rgba> Brushes,
    IReadOnlyDictionary<string, Rgba[]> Gradients);

/// <summary>
/// Expands <see cref="ThemeTokens"/> into AuraClean's resource keys: the design system's derived
/// surfaces (card, shell, hover, active, tone tints) plus the legacy keys existing views consume,
/// and MaterialDesign's own brushes so its controls match.
/// </summary>
public static class ThemePalette
{
    public static ThemeResourceSet Build(bool light) => Build(light ? ThemeTokens.Light : ThemeTokens.Dark);

    public static ThemeResourceSet Build(ThemeTokens t)
    {
        static Rgba M(Rgba a, double p, Rgba b) => ColorMix.Mix(a, p, b);

        var cardBg = M(t.BgSubtle, 94, t.Bg);
        var cardBd = M(t.Bd, 88, t.FgDim);
        var shellBg = M(t.BgSubtle, 92, t.Bg);
        var hoverBg = M(t.BgElev, 82, t.BgSubtle);
        var hoverBd = M(t.Bd, 80, t.FgDim);
        var activeBg = M(t.Accent, 14, t.BgSubtle);
        var activeBd = M(t.Accent, 24, t.Bd);
        var rowHover = M(t.BgElev, 66, t.BgSubtle);

        Rgba Tint(Rgba tone) => M(tone, 10, t.BgSubtle);
        Rgba Line(Rgba tone) => M(tone, 38, t.Bd);
        Rgba Strong(Rgba tone) => M(tone, 14, t.BgSubtle);

        var colors = new Dictionary<string, Rgba>
        {
            ["AuraBgColor"] = t.Bg,
            ["AuraSurfaceColor"] = cardBg,
            ["AuraSurfaceLightColor"] = t.BgElev,
            ["AuraSurfaceElevatedColor"] = t.BdSubtle,
            ["AuraVioletColor"] = t.Accent,
            ["AuraVioletDarkColor"] = t.Accent,
            ["AuraVioletLightColor"] = t.AccentSoft,
            ["AuraAccentColor"] = t.Accent,
            ["AuraAccentSoftColor"] = t.AccentSoft,
            ["AuraCyanColor"] = t.Info,
            ["AuraBlueColor"] = t.Info,
            ["AuraMintColor"] = t.Ok,
            ["AuraAmberColor"] = t.Warn,
            ["AuraCoralColor"] = t.Err,
            ["AuraCriticalColor"] = t.Err,
            ["AuraTextBrightColor"] = t.Fg,
            ["AuraTextColor"] = t.Fg,
            ["AuraTextDimColor"] = t.FgMuted,
            ["AuraTextMutedColor"] = t.FgDim,
            ["AuraBorderColor"] = t.Bd,
            ["AuraBorderSubtleColor"] = t.BdSubtle,
            ["AuraShadowColor"] = t.Shadow,
        };

        var brushes = new Dictionary<string, Rgba>
        {
            // Surfaces
            ["AuraBackground"] = t.Bg,
            ["AuraBgSubtle"] = t.BgSubtle,
            ["AuraBgElev"] = t.BgElev,
            ["AuraSurface"] = cardBg,
            ["AuraSurfaceLight"] = t.BgElev,
            ["AuraSurfaceElevated"] = t.BdSubtle,
            ["AuraCardBackground"] = cardBg,
            ["AuraCardBorder"] = cardBd,
            ["AuraCardHoverBorder"] = M(t.Accent, 42, t.Bd),
            ["AuraShellBackground"] = shellBg,
            ["AuraHoverBackground"] = hoverBg,
            ["AuraHoverBorder"] = hoverBd,
            ["AuraActiveBackground"] = activeBg,
            ["AuraActiveBorder"] = activeBd,
            ["AuraInputBackground"] = t.Bg,
            ["AuraFocusRing"] = ColorMix.Fade(t.Accent, 12),
            ["AuraSelection"] = M(t.Accent, 28, t.Bg),
            ["AuraRowHover"] = rowHover,
            ["AuraRowDivider"] = ColorMix.Fade(t.Bd, 68),
            ["AuraTableHeader"] = M(t.BgSubtle, 96, t.Bg),
            ["AuraStatBackground"] = ColorMix.Fade(t.Bg, 54),
            ["AuraStatBorder"] = ColorMix.Fade(t.Bd, 72),
            ["AuraPageIconBackground"] = M(t.Accent, 10, t.BgSubtle),
            ["AuraPageIconBorder"] = M(t.Accent, 24, t.BdSubtle),
            ["AuraLinkBorder"] = M(t.Accent, 40, t.Bd),
            ["AuraSelectedBackground"] = M(t.Accent, 6, t.Bg),
            ["AuraSegmentBorder"] = M(t.Bd, 86, t.FgDim),
            ["AuraSegmentHoverBorder"] = M(t.Bd, 82, t.FgDim),
            ["AuraSegmentHoverBackground"] = M(t.BgElev, 78, t.BgSubtle),
            ["AuraSegmentActiveBorder"] = M(t.Accent, 48, t.Bd),
            ["AuraSegmentActiveBackground"] = M(t.Accent, 11, t.BgSubtle),
            ["AuraWarnBannerBackground"] = M(t.Warn, 8, t.BgSubtle),
            ["AuraWarnBannerBorder"] = M(t.Warn, 42, t.BdSubtle),
            ["AuraErrBannerBackground"] = M(t.Err, 8, t.BgSubtle),
            ["AuraErrBannerBorder"] = M(t.Err, 42, t.BdSubtle),
            ["AuraPrimaryHoverOverlay"] = ColorMix.Fade(t.Bg, 10),
            ["AuraScrollThumb"] = t.Bd,
            ["AuraScrollThumbHover"] = t.FgDim,

            // Text
            ["AuraTextBright"] = t.Fg,
            ["AuraTextPrimary"] = t.Fg,
            ["AuraTextSecondary"] = t.FgMuted,
            ["AuraTextMuted"] = t.FgDim,

            // Borders
            ["AuraBorder"] = t.Bd,
            ["AuraBorderSubtle"] = t.BdSubtle,

            // Accent: fills use Accent, text and icons use AccentSoft (contrast-safe in both themes)
            ["AuraAccent"] = t.Accent,
            ["AuraAccentSoft"] = t.AccentSoft,
            ["AuraAccentPurple"] = t.AccentSoft,
            ["AuraAccentLight"] = t.AccentSoft,

            // Semantic tones
            ["AuraOk"] = t.Ok,
            ["AuraWarn"] = t.Warn,
            ["AuraErr"] = t.Err,
            ["AuraInfo"] = t.Info,
            ["AuraAccentTeal"] = t.Info,
            ["AuraSuccess"] = t.Ok,
            ["AuraAmber"] = t.Warn,
            ["AuraWarning"] = t.Err,
            ["AuraCritical"] = t.Err,

            // Status pill tints (fill) and lines (border)
            ["AuraAccentTint"] = M(t.Accent, 10, t.BgSubtle),
            ["AuraAccentLine"] = M(t.Accent, 38, t.Bd),
            ["AuraOkTint"] = Tint(t.Ok),
            ["AuraOkLine"] = Line(t.Ok),
            ["AuraWarnTint"] = Tint(t.Warn),
            ["AuraWarnLine"] = Line(t.Warn),
            ["AuraErrTint"] = Tint(t.Err),
            ["AuraErrLine"] = Line(t.Err),
            ["AuraInfoTint"] = Tint(t.Info),
            ["AuraInfoLine"] = Line(t.Info),

            // Legacy overlay, badge and severity keys used by existing views
            ["AuraAccentPurpleSemi"] = M(t.Accent, 10, t.BgSubtle),
            ["AuraAccentTealSemi"] = Tint(t.Info),
            ["AuraAmberSemi"] = Tint(t.Warn),
            ["AuraCoralSemi"] = Tint(t.Err),
            ["AuraIconBadgeBg"] = M(t.Accent, 10, t.BgSubtle),
            ["AuraNeutralBadge"] = t.BgElev,
            ["AuraOverlayLight"] = hoverBg,
            ["AuraOverlaySubtle"] = ColorMix.Fade(t.BgElev, 66),
            ["AuraOverlayFaint"] = ColorMix.Fade(t.BgElev, 58),
            ["AuraTrackBg"] = t.BgElev,
            ["AuraSuccessBadge"] = Tint(t.Ok),
            ["AuraSuccessTint"] = Tint(t.Ok),
            ["AuraWarningBadge"] = Tint(t.Warn),
            ["AuraCriticalBadge"] = Tint(t.Err),
            ["AuraInfoBadge"] = Tint(t.Info),
            ["AuraAccentPurpleBadge"] = M(t.Accent, 10, t.BgSubtle),
            ["AuraAccentTealBadge"] = Tint(t.Info),
            ["AuraAmberBadge"] = Tint(t.Warn),
            ["AuraBlueBadge"] = Tint(t.Info),
            ["AuraCriticalSeverity"] = Strong(t.Err),
            ["AuraCoralSeverity"] = Strong(t.Err),
            ["AuraAmberSeverity"] = Strong(t.Warn),
            ["AuraSuccessSeverity"] = Strong(t.Ok),

            // MaterialDesign controls (ComboBox, TextBox, ListView, tooltips, cards)
            ["MaterialDesign.Brush.Background"] = t.Bg,
            ["MaterialDesignPaper"] = t.Bg,
            ["MaterialDesign.Brush.Foreground"] = t.Fg,
            ["MaterialDesignBody"] = t.Fg,
            ["MaterialDesign.Brush.ForegroundLight"] = t.FgMuted,
            ["MaterialDesignBodyLight"] = t.FgMuted,
            ["MaterialDesign.Brush.Card.Background"] = cardBg,
            ["MaterialDesignCardBackground"] = cardBg,
            ["MaterialDesign.Brush.Card.Border"] = cardBd,
            ["MaterialDesign.Brush.ToolTip.Background"] = t.BgElev,
            ["MaterialDesignToolTipBackground"] = t.BgElev,
            ["MaterialDesign.Brush.Separator.Background"] = t.BdSubtle,
            ["MaterialDesignDivider"] = t.BdSubtle,
            ["MaterialDesign.Brush.TextBox.Border"] = t.Bd,
            ["MaterialDesignTextBoxBorder"] = t.Bd,
            ["MaterialDesign.Brush.TextBox.OutlineBorder"] = t.Bd,
            ["MaterialDesign.Brush.TextBox.OutlineInactiveBorder"] = t.Bd,
            ["MaterialDesign.Brush.TextBox.HoverBorder"] = hoverBd,
            ["MaterialDesign.Brush.TextBox.HoverBackground"] = hoverBg,
            ["MaterialDesign.Brush.TextBox.FilledBackground"] = t.Bg,
            ["MaterialDesign.Brush.ComboBox.Border"] = t.Bd,
            ["MaterialDesign.Brush.ComboBox.OutlineBorder"] = t.Bd,
            ["MaterialDesign.Brush.ComboBox.OutlineInactiveBorder"] = t.Bd,
            ["MaterialDesign.Brush.ComboBox.HoverBorder"] = hoverBd,
            ["MaterialDesign.Brush.ComboBox.HoverBackground"] = hoverBg,
            ["MaterialDesign.Brush.ComboBox.FilledBackground"] = t.Bg,
            ["MaterialDesign.Brush.ComboBox.Popup.DarkBackground"] = t.BgSubtle,
            ["MaterialDesign.Brush.ComboBox.Popup.LightBackground"] = t.BgSubtle,
            ["MaterialDesign.Brush.ComboBox.Popup.DarkForeground"] = t.Fg,
            ["MaterialDesign.Brush.ComboBox.Popup.LightForeground"] = t.Fg,
            ["MaterialDesign.Brush.ListView.Hover"] = rowHover,
            ["MaterialDesign.Brush.ListView.Selected"] = activeBg,
            ["MaterialDesign.Brush.ListView.Separator"] = ColorMix.Fade(t.Bd, 68),
            ["MaterialDesign.Brush.ListBoxItem.Selected"] = activeBg,
            ["MaterialDesign.Brush.DataGrid.RowHoverBackground"] = rowHover,
            ["MaterialDesign.Brush.DataGrid.Selected"] = activeBg,
            ["MaterialDesign.Brush.DataGrid.Border"] = t.BdSubtle,
            ["MaterialDesign.Brush.DataGrid.ColumnHeaderForeground"] = t.FgDim,
            ["MaterialDesign.Brush.CheckBox.Off"] = t.FgDim,
            ["MaterialDesign.Brush.CheckBox.UncheckedBorder"] = t.Bd,
            ["MaterialDesign.Brush.ToggleButton.Switch.TrackOffBackground"] = t.Bd,
            ["MaterialDesign.Brush.ScrollBar.Foreground"] = t.Bd,
            ["MaterialDesign.Brush.Chip.Background"] = t.BgElev,
            ["MaterialDesign.Brush.Chip.OutlineBorder"] = t.Bd,
            ["MaterialDesign.Brush.ValidationError"] = t.Err,
            ["MaterialDesignValidationErrorBrush"] = t.Err,
        };

        var gradients = new Dictionary<string, Rgba[]>
        {
            ["AuraGradientAccent"] = [t.Accent, t.AccentSoft],
            ["AuraGradientVertical"] = [t.Accent, t.AccentSoft],
            ["AuraGradientHorizontal"] = [t.Accent, t.AccentSoft],
            ["AuraGradientWarm"] = [t.Err, t.Warn],
            ["AuraSidebarGradient"] = [shellBg, shellBg],
            ["AuraGradientScoreCard"] = [cardBg, cardBg, cardBg],
            ["AuraGradientLogo"] = [t.Fg, t.AccentSoft],
            ["AuraGlowViolet"] = [Rgba.Transparent, Rgba.Transparent, Rgba.Transparent],
            ["AuraGlowHero"] = [Rgba.Transparent, Rgba.Transparent, Rgba.Transparent],
        };

        return new ThemeResourceSet(colors, brushes, gradients);
    }
}
