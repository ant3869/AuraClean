using AuraClean.Helpers;
using Microsoft.Win32;
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
    /// Falls back to defaults on any read error.
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
                    DiagnosticLogger.Info("SettingsService", $"Loaded settings from {SettingsFile}");
                    return _cached;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("SettingsService", "Failed to load settings, using defaults", ex);
            }

            _cached = new AppSettings();
            return _cached;
        }
    }

    /// <summary>
    /// Persists the current settings to disk.
    /// </summary>
    public static void Save(AppSettings settings)
    {
        lock (_lock)
        {
            _cached = settings;

            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(SettingsFile, json);
                DiagnosticLogger.Info("SettingsService", "Settings saved successfully");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("SettingsService", "Failed to save settings", ex);
            }

            // Apply side-effects
            ApplyLaunchAtStartup(settings.LaunchAtStartup);
        }
    }

    /// <summary>
    /// Writes or removes the AuraClean entry from the Windows Registry Run key
    /// so the app auto-starts (or stops auto-starting) with Windows.
    /// </summary>
    private static void ApplyLaunchAtStartup(bool enable)
    {
        const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "AuraClean";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue(valueName, $"\"{exePath}\" --minimized");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("SettingsService", "Failed to apply LaunchAtStartup", ex);
        }
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
}

/// <summary>
/// Application settings model. All properties have safe defaults.
/// </summary>
public class AppSettings
{
    // ── General ──
    public bool CreateRestorePointBeforeClean { get; set; } = true;
    public bool DryRunMode { get; set; } = false;
    public bool ShowConfirmationDialogs { get; set; } = true;
    public bool MinimizeToTray { get; set; } = false;
    public bool LaunchAtStartup { get; set; } = false;
    public bool IsLightTheme { get; set; } = false;

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

    // ── Metadata ──
    public DateTime LastModified { get; set; } = DateTime.Now;
}
