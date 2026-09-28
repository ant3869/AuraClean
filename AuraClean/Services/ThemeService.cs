using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AuraClean.Helpers;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace AuraClean.Services;

/// <summary>
/// Applies the OpenEval-derived palette at runtime and keeps it in sync with the user's
/// Auto / Light / Dark preference. In Auto the app follows the Windows app theme live.
/// Brushes are updated in place so both StaticResource and DynamicResource consumers repaint.
/// </summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private static bool _initialized;
    private static bool _hasApplied;

    /// <summary>The persisted preference.</summary>
    public static ThemeMode Mode { get; private set; } = ThemeModes.Default;

    /// <summary>The theme currently on screen (Auto resolved against Windows).</summary>
    public static bool IsLightTheme { get; private set; }

    /// <summary>Raised on the UI thread after the preference or the on-screen theme changes.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>
    /// Applies <paramref name="mode"/> and starts following Windows theme changes. Call once at
    /// startup, before the first window is created, so the first paint is already correct.
    /// </summary>
    public static void Initialize(ThemeMode mode)
    {
        Mode = ThemeModes.Normalize(mode);

        if (!_initialized)
        {
            _initialized = true;
            ApplyReducedMotion();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded));

            if (Application.Current is { } app)
                app.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }

        Apply();
    }

    /// <summary>Changes the preference, applies it immediately and (by default) saves it.</summary>
    public static void SetMode(ThemeMode mode, bool persist = true)
    {
        Mode = ThemeModes.Normalize(mode);
        if (persist)
            Persist(Mode);
        Apply();
    }

    /// <summary>Advances Auto → Light → Dark → Auto and saves the result.</summary>
    public static ThemeMode Cycle()
    {
        SetMode(ThemeModes.Next(Mode));
        return Mode;
    }

    /// <summary>Reads the Windows "app mode" setting; dark when it cannot be read.</summary>
    public static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightThemeValue) is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            DiagnosticLogger.Warn("ThemeService", "Could not read the Windows app theme", ex);
            return false;
        }
    }

    private static void Persist(ThemeMode mode)
    {
        var settings = SettingsService.Load();
        if (settings.Theme == mode)
            return;

        settings.Theme = mode;
        settings.LastModified = DateTime.Now;
        if (!SettingsService.Save(settings, applySideEffects: false))
            DiagnosticLogger.Warn("ThemeService", "Theme preference could not be saved");
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || Mode != ThemeMode.Auto)
            return;

        // "General" fires for many unrelated settings; only repaint when the resolved theme flips.
        if (ThemeModes.ResolveIsLight(Mode, SystemPrefersLight()) != IsLightTheme)
            Apply();
    }

    private static void Apply()
    {
        var app = Application.Current;
        if (app == null)
            return;

        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(Apply);
            return;
        }

        var light = ThemeModes.ResolveIsLight(Mode, SystemPrefersLight());
        var palette = ThemePalette.Build(light);

        try
        {
            if (!_hasApplied || light != IsLightTheme)
            {
                // MaterialDesign writes its own brushes into the application resources, so it goes
                // first and the palette's overrides of those keys win.
                ApplyMaterialDesignTheme(light, palette);
                ApplyResources(app.Resources, palette);
                _hasApplied = true;
            }

            IsLightTheme = light;
            foreach (Window window in app.Windows)
                ApplyTitleBar(window);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThemeService", $"Failed to apply the {(light ? "light" : "dark")} theme", ex);
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void ApplyMaterialDesignTheme(bool light, ThemeResourceSet palette)
    {
        var helper = new PaletteHelper();
        var theme = helper.GetTheme();
        theme.SetBaseTheme(light ? BaseTheme.Light : BaseTheme.Dark);
        theme.SetPrimaryColor(ToColor(palette.Colors["AuraAccentColor"]));
        theme.SetSecondaryColor(ToColor(palette.Colors["AuraAccentSoftColor"]));
        helper.SetTheme(theme);
    }

    private static void ApplyResources(ResourceDictionary resources, ThemeResourceSet palette)
    {
        foreach (var (key, value) in palette.Colors)
            resources[key] = ToColor(value);

        foreach (var (key, value) in palette.Brushes)
        {
            var color = ToColor(value);

            // MaterialDesign animates unfrozen brushes it finds under its own keys when its theme
            // changes; an animation would override the palette, so those keys always get a fresh
            // frozen brush. AuraClean's own brushes are updated in place so StaticResource
            // consumers repaint too.
            if (!key.StartsWith("MaterialDesign", StringComparison.Ordinal) &&
                resources[key] is SolidColorBrush { IsFrozen: false } brush)
            {
                brush.Color = color;
                continue;
            }

            var replacement = new SolidColorBrush(color);
            replacement.Freeze();
            resources[key] = replacement;
        }

        foreach (var (key, stops) in palette.Gradients)
        {
            if (resources[key] is GradientBrush { IsFrozen: false } gradient && gradient.GradientStops.Count == stops.Length)
            {
                for (int i = 0; i < stops.Length; i++)
                    gradient.GradientStops[i].Color = ToColor(stops[i]);
            }
            else if (resources[key] is GradientBrush existing && existing.GradientStops.Count == stops.Length)
            {
                var copy = existing.Clone();
                for (int i = 0; i < stops.Length; i++)
                    copy.GradientStops[i].Color = ToColor(stops[i]);
                resources[key] = copy;
            }
        }
    }

    /// <summary>
    /// Honors Windows' "Show animations" setting: every shared motion duration collapses to zero so
    /// control transitions become instant. Runs before any style is instantiated.
    /// </summary>
    private static void ApplyReducedMotion()
    {
        if (SystemParameters.ClientAreaAnimation || Application.Current is not { } app)
            return;

        foreach (var key in new[] { "AuraMotionFast", "AuraMotionControl", "AuraMotionPanel" })
        {
            foreach (var dictionary in app.Resources.MergedDictionaries)
            {
                if (dictionary.Contains(key))
                    dictionary[key] = new Duration(TimeSpan.Zero);
            }
        }
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
            ApplyTitleBar(window);
    }

    private static Color ToColor(Rgba value) => Color.FromArgb(value.A, value.R, value.G, value.B);

    // ── Native title bar ──

    private const int DwmUseImmersiveDarkModeLegacy = 19;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Matches the OS-drawn caption to the theme: dark mode flag on Windows 10+, and on Windows 11
    /// the caption itself takes the shell background and foreground colors.
    /// </summary>
    private static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            var palette = ThemePalette.Build(IsLightTheme);
            int dark = IsLightTheme ? 0 : 1;
            if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeLegacy, ref dark, sizeof(int));

            // Unsupported before Windows 11 (returns an error HRESULT, which is fine to ignore).
            int caption = ToColorRef(palette.Brushes["AuraShellBackground"]);
            int text = ToColorRef(palette.Brushes["AuraTextBright"]);
            DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmTextColor, ref text, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            DiagnosticLogger.Warn("ThemeService", "Title bar theming is unavailable on this system", ex);
        }
    }

    private static int ToColorRef(Rgba value) => value.R | (value.G << 8) | (value.B << 16);
}
