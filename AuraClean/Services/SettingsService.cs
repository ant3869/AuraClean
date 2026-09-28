using AuraClean.Helpers;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuraClean.Services;

/// <summary>
/// Manages application settings with JSON persistence in %LocalAppData%\AuraClean\Settings.
/// Thread-safe singleton pattern for consistent settings access across the application.
/// </summary>
public static class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AuraClean", "Settings");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    private static readonly object _lock = new();
    private static AppSettings? _cached;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Loads settings from disk, or returns cached copy if already loaded.
    /// Falls back to defaults on any read error. Out-of-range values are clamped.
    /// </summary>
    public static AppSettings Load()
    {
        lock (_lock)
        {
            if (_cached != null)
                return _cached;

            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    _cached = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                    _cached.Normalize();
                    DiagnosticLogger.Info("SettingsService", $"Loaded settings from {SettingsFile}");
                    return _cached;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("SettingsService", "Failed to load settings, using defaults", ex);
                PreserveCorruptSettingsFile();
            }

            _cached = new AppSettings();
            return _cached;
        }
    }

    /// <summary>
    /// Persists the settings to disk atomically and applies OS-level side effects.
    /// Returns false when the file could not be written.
    /// </summary>
    public static bool Save(AppSettings settings) => Save(settings, applySideEffects: true);

    /// <summary>
    /// Persists the settings; <paramref name="applySideEffects"/> = false skips the OS-level
    /// registration work (used for UI-only preferences such as the theme).
    /// </summary>
    public static bool Save(AppSettings settings, bool applySideEffects)
    {
        bool saved;
        bool launchAtStartup;

        lock (_lock)
        {
            settings.Normalize();
            _cached = settings;
            launchAtStartup = settings.LaunchAtStartup;

            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                var tempFile = SettingsFile + ".tmp";
                File.WriteAllText(tempFile, json);
                File.Move(tempFile, SettingsFile, overwrite: true);
                DiagnosticLogger.Info("SettingsService", "Settings saved successfully");
                saved = true;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("SettingsService", "Failed to save settings", ex);
                saved = false;
            }
        }

        // Side effects run outside the lock: they may be slow (Task Scheduler COM calls).
        if (applySideEffects)
            LaunchAtLogonService.Apply(launchAtStartup);
        return saved;
    }

    /// <summary>
    /// Resets all settings to defaults and saves.
    /// </summary>
    public static AppSettings ResetToDefaults()
    {
        var defaults = new AppSettings();
        Save(defaults);
        return defaults;
    }

    /// <summary>
    /// Returns the full path to the settings directory (for display in UI).
    /// </summary>
    public static string GetSettingsDirectory() => SettingsDir;

    /// <summary>
    /// Invalidates cached settings so next Load() re-reads from disk.
    /// </summary>
    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cached = null;
        }
    }

    private static void PreserveCorruptSettingsFile()
    {
        try
        {
            if (File.Exists(SettingsFile))
                File.Copy(SettingsFile, SettingsFile + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("SettingsService", "Could not back up unreadable settings file", ex);
        }
    }
}

/// <summary>
/// Application settings model. All properties have safe defaults.
/// </summary>
public class AppSettings
{
    public static readonly string[] ScheduleFrequencies = ["Daily", "Weekly", "Monthly"];
    public static readonly string[] ShredAlgorithms = ["QuickZero", "Random", "DoD3Pass", "Enhanced7Pass"];

    // ── General ──
    public ExperienceMode ExperienceMode { get; set; } = ExperienceMode.Normal;
    public bool CreateRestorePointBeforeClean { get; set; } = true;
    public bool DryRunMode { get; set; } = false;
    public bool ShowConfirmationDialogs { get; set; } = true;
    public bool MinimizeToTray { get; set; } = false;
    public bool LaunchAtStartup { get; set; } = false;
    public ThemeMode Theme { get; set; } = ThemeModes.Default;

    /// <summary>
    /// Pre-1.6 theme flag. Read only to migrate old settings files into <see cref="Theme"/>;
    /// never written back (false is the default and is omitted).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsLightTheme { get; set; }

    // ── Cleaner ──
    public bool CleanTempFiles { get; set; } = true;
    public bool CleanWindowsUpdate { get; set; } = true;
    public bool CleanPrefetch { get; set; } = true;
    public bool CleanCrashDumps { get; set; } = true;
    public bool CleanRecycleBin { get; set; } = true;
    public bool CleanBrowserCache { get; set; } = true;
    public bool CleanThumbnailCache { get; set; } = true;
    public bool CleanWindowsLogs { get; set; } = true;
    public bool RunHeuristicScan { get; set; } = false;
    public int AbandonedFileDaysThreshold { get; set; } = 180;

    // ── File Shredder ──
    public string DefaultShredAlgorithm { get; set; } = "DoD3Pass";

    // ── Large File Finder ──
    public long DefaultLargeFileSizeMb { get; set; } = 100;

    // ── Duplicate Finder ──
    public long DefaultMinDuplicateSizeMb { get; set; } = 1;

    // ── Quarantine ──
    public int QuarantineRetentionDays { get; set; } = 30;
    public bool AutoPurgeExpiredQuarantine { get; set; } = true;

    // ── History ──
    public int MaxHistoryEntries { get; set; } = 500;
    public bool LogCleanupOperations { get; set; } = true;

    // ── Scheduled Cleanup ──
    public bool ScheduledCleanupEnabled { get; set; } = false;
    public string ScheduledCleanupFrequency { get; set; } = "Weekly";  // Daily, Weekly, Monthly
    public string ScheduledCleanupTime { get; set; } = "03:00";       // 24h format
    public int ScheduledCleanupDayOfWeek { get; set; } = 1;           // 1=Mon ... 7=Sun (for Weekly)

    // ── Onboarding ──
    public bool HasCompletedOnboarding { get; set; } = false;

    // ── Metadata ──
    public DateTime LastModified { get; set; } = DateTime.Now;

    /// <summary>
    /// Clamps every numeric/enumerated setting into its valid range so hand-edited or
    /// corrupted settings files can never drive a service with nonsensical values.
    /// </summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(ExperienceMode))
            ExperienceMode = ExperienceMode.Normal;

        if (IsLightTheme)
        {
            Theme = ThemeMode.Light;
            IsLightTheme = false;
        }
        Theme = ThemeModes.Normalize(Theme);

        AbandonedFileDaysThreshold = Math.Clamp(AbandonedFileDaysThreshold, 30, 3650);
        DefaultLargeFileSizeMb = Math.Clamp(DefaultLargeFileSizeMb, 1, 1024 * 1024);
        DefaultMinDuplicateSizeMb = Math.Clamp(DefaultMinDuplicateSizeMb, 0, 1024 * 1024);
        QuarantineRetentionDays = Math.Clamp(QuarantineRetentionDays, 1, 3650);
        MaxHistoryEntries = Math.Clamp(MaxHistoryEntries, 10, 100_000);
        ScheduledCleanupDayOfWeek = Math.Clamp(ScheduledCleanupDayOfWeek, 1, 7);

        if (!ShredAlgorithms.Contains(DefaultShredAlgorithm, StringComparer.OrdinalIgnoreCase))
            DefaultShredAlgorithm = "DoD3Pass";

        var frequency = ScheduleFrequencies.FirstOrDefault(f =>
            f.Equals(ScheduledCleanupFrequency?.Trim(), StringComparison.OrdinalIgnoreCase));
        ScheduledCleanupFrequency = frequency ?? "Weekly";

        ScheduledCleanupTime = TryParseScheduleTime(ScheduledCleanupTime, out var time)
            ? time.ToString(@"hh\:mm", CultureInfo.InvariantCulture)
            : "03:00";
    }

    /// <summary>Parses a 24-hour "HH:mm" (or "H:mm") time of day.</summary>
    public static bool TryParseScheduleTime(string? value, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!TimeSpan.TryParseExact(value.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var parsed))
            return false;

        if (parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1))
            return false;

        time = parsed;
        return true;
    }
}

public enum ExperienceMode
{
    Normal,
    Advanced
}

public static class ExperienceModeService
{
    public static event Action<bool>? ModeChanged;

    public static ExperienceMode GetMode() => SettingsService.Load().ExperienceMode;

    public static bool IsAdvancedMode() => GetMode() == ExperienceMode.Advanced;

    public static void SaveMode(bool isAdvancedMode)
    {
        var settings = SettingsService.Load();
        settings.ExperienceMode = isAdvancedMode ? ExperienceMode.Advanced : ExperienceMode.Normal;
        settings.LastModified = DateTime.Now;
        SettingsService.Save(settings);
        ModeChanged?.Invoke(isAdvancedMode);
    }

    /// <summary>
    /// Broadcasts the currently persisted mode to every listener (used after settings are
    /// reset or reloaded from disk without going through <see cref="SaveMode"/>).
    /// </summary>
    public static void NotifyModeChanged() => ModeChanged?.Invoke(IsAdvancedMode());
}
