using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the Settings page.
/// Provides two-way binding to all AppSettings properties with save/reset support.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    // ── General ──
    [ObservableProperty] private bool _createRestorePointBeforeClean;
    [ObservableProperty] private bool _dryRunMode;
    [ObservableProperty] private bool _showConfirmationDialogs;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _launchAtStartup;

    // ── Cleaner ──
    [ObservableProperty] private bool _cleanTempFiles;
    [ObservableProperty] private bool _cleanWindowsUpdate;
    [ObservableProperty] private bool _cleanPrefetch;
    [ObservableProperty] private bool _cleanCrashDumps;
    [ObservableProperty] private bool _cleanRecycleBin;
    [ObservableProperty] private bool _cleanBrowserCache;
    [ObservableProperty] private bool _cleanThumbnailCache;
    [ObservableProperty] private bool _cleanWindowsLogs;
    [ObservableProperty] private bool _runHeuristicScan;
    [ObservableProperty] private int _abandonedFileDaysThreshold;

    // ── Tools ──
    [ObservableProperty] private string _defaultShredAlgorithm;
    [ObservableProperty] private long _defaultLargeFileSizeMb;
    [ObservableProperty] private long _defaultMinDuplicateSizeMb;

    // ── Quarantine ──
    [ObservableProperty] private int _quarantineRetentionDays;
    [ObservableProperty] private bool _autoPurgeExpiredQuarantine;

    // ── History ──
    [ObservableProperty] private int _maxHistoryEntries;
    [ObservableProperty] private bool _logCleanupOperations;

    // ── UI State ──
    [ObservableProperty] private string _statusMessage = "Settings loaded.";
    [ObservableProperty] private bool _hasUnsavedChanges;
    [ObservableProperty] private string _settingsPath = string.Empty;

    public string[] ShredAlgorithms { get; } =
        ["QuickZero", "Random", "DoD3Pass", "Enhanced7Pass"];

    public long[] LargeFileSizePresets { get; } = [50, 100, 250, 500, 1024];

    public int[] RetentionDayPresets { get; } = [7, 14, 30, 60, 90];

    public int[] HistoryLimitPresets { get; } = [100, 250, 500, 1000, 2000];

    public SettingsViewModel()
    {
        DefaultShredAlgorithm = "DoD3Pass"; // safe default before Load
        LoadFromDisk();
    }

    /// <summary>
    /// Loads settings from SettingsService and maps to ViewModel properties.
    /// </summary>
    private void LoadFromDisk()
    {
        var s = SettingsService.Load();

        CreateRestorePointBeforeClean = s.CreateRestorePointBeforeClean;
        DryRunMode = s.DryRunMode;
        ShowConfirmationDialogs = s.ShowConfirmationDialogs;
        MinimizeToTray = s.MinimizeToTray;
        LaunchAtStartup = s.LaunchAtStartup;

        CleanTempFiles = s.CleanTempFiles;
        CleanWindowsUpdate = s.CleanWindowsUpdate;
        CleanPrefetch = s.CleanPrefetch;
        CleanCrashDumps = s.CleanCrashDumps;
        CleanRecycleBin = s.CleanRecycleBin;
        CleanBrowserCache = s.CleanBrowserCache;
        CleanThumbnailCache = s.CleanThumbnailCache;
        CleanWindowsLogs = s.CleanWindowsLogs;
        RunHeuristicScan = s.RunHeuristicScan;
        AbandonedFileDaysThreshold = s.AbandonedFileDaysThreshold;

        DefaultShredAlgorithm = s.DefaultShredAlgorithm;
        DefaultLargeFileSizeMb = s.DefaultLargeFileSizeMb;
        DefaultMinDuplicateSizeMb = s.DefaultMinDuplicateSizeMb;

        QuarantineRetentionDays = s.QuarantineRetentionDays;
        AutoPurgeExpiredQuarantine = s.AutoPurgeExpiredQuarantine;

        MaxHistoryEntries = s.MaxHistoryEntries;
        LogCleanupOperations = s.LogCleanupOperations;

        SettingsPath = SettingsService.GetSettingsDirectory();
        HasUnsavedChanges = false;
        StatusMessage = "Settings loaded.";
    }

    /// <summary>
    /// Maps ViewModel properties back to AppSettings and persists.
    /// </summary>
    [RelayCommand]
    private void SaveSettings()
    {
        var s = new AppSettings
        {
            CreateRestorePointBeforeClean = CreateRestorePointBeforeClean,
            DryRunMode = DryRunMode,
            ShowConfirmationDialogs = ShowConfirmationDialogs,
            MinimizeToTray = MinimizeToTray,
            LaunchAtStartup = LaunchAtStartup,

            CleanTempFiles = CleanTempFiles,
            CleanWindowsUpdate = CleanWindowsUpdate,
            CleanPrefetch = CleanPrefetch,
            CleanCrashDumps = CleanCrashDumps,
            CleanRecycleBin = CleanRecycleBin,
            CleanBrowserCache = CleanBrowserCache,
            CleanThumbnailCache = CleanThumbnailCache,
            CleanWindowsLogs = CleanWindowsLogs,
            RunHeuristicScan = RunHeuristicScan,
            AbandonedFileDaysThreshold = AbandonedFileDaysThreshold,

            DefaultShredAlgorithm = DefaultShredAlgorithm,
            DefaultLargeFileSizeMb = DefaultLargeFileSizeMb,
            DefaultMinDuplicateSizeMb = DefaultMinDuplicateSizeMb,

            QuarantineRetentionDays = QuarantineRetentionDays,
            AutoPurgeExpiredQuarantine = AutoPurgeExpiredQuarantine,

            MaxHistoryEntries = MaxHistoryEntries,
            LogCleanupOperations = LogCleanupOperations,

            LastModified = DateTime.Now
        };

        SettingsService.Save(s);
        HasUnsavedChanges = false;
        StatusMessage = $"Settings saved at {DateTime.Now:HH:mm:ss}.";
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        SettingsService.ResetToDefaults();
        LoadFromDisk();
        HasUnsavedChanges = false;
        StatusMessage = "Settings reset to defaults.";
    }

    [RelayCommand]
    private void ReloadSettings()
    {
        SettingsService.InvalidateCache();
        LoadFromDisk();
        StatusMessage = "Settings reloaded from disk.";
    }

    // ── Track changes for the "unsaved" indicator ──

    partial void OnCreateRestorePointBeforeCleanChanged(bool value) => HasUnsavedChanges = true;
    partial void OnDryRunModeChanged(bool value) => HasUnsavedChanges = true;
    partial void OnShowConfirmationDialogsChanged(bool value) => HasUnsavedChanges = true;
    partial void OnMinimizeToTrayChanged(bool value) => HasUnsavedChanges = true;
    partial void OnLaunchAtStartupChanged(bool value) => HasUnsavedChanges = true;
    partial void OnCleanTempFilesChanged(bool value) => HasUnsavedChanges = true;
    partial void OnCleanWindowsUpdateChanged(bool value) => HasUnsavedChanges = true;
    partial void OnCleanPrefetchChanged(bool value) => HasUnsavedChanges = true;
    partial void OnCleanCrashDumpsChanged(bool value) => HasUnsavedChanges = true;
    partial void OnCleanRecycleBinChanged(bool value) => HasUnsavedChanges = true;
    partial void OnCleanBrowserCacheChanged(bool value) => HasUnsavedChanges = true;
    partial void OnCleanThumbnailCacheChanged(bool value) => HasUnsavedChanges = true;
    partial void OnCleanWindowsLogsChanged(bool value) => HasUnsavedChanges = true;
    partial void OnRunHeuristicScanChanged(bool value) => HasUnsavedChanges = true;
    partial void OnAbandonedFileDaysThresholdChanged(int value) => HasUnsavedChanges = true;
    partial void OnDefaultShredAlgorithmChanged(string value) => HasUnsavedChanges = true;
    partial void OnDefaultLargeFileSizeMbChanged(long value) => HasUnsavedChanges = true;
    partial void OnDefaultMinDuplicateSizeMbChanged(long value) => HasUnsavedChanges = true;
    partial void OnQuarantineRetentionDaysChanged(int value) => HasUnsavedChanges = true;
    partial void OnAutoPurgeExpiredQuarantineChanged(bool value) => HasUnsavedChanges = true;
    partial void OnMaxHistoryEntriesChanged(int value) => HasUnsavedChanges = true;
    partial void OnLogCleanupOperationsChanged(bool value) => HasUnsavedChanges = true;
}
