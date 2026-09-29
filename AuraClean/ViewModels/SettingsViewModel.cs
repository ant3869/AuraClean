using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AuraClean.ViewModels;

/// <summary>One checkable scheduled-cleanup category (D2).</summary>
public partial class SchedulableCategory : ObservableObject
{
    public JunkType Type { get; }
    public string DisplayName { get; }

    [ObservableProperty] private bool _isSelected = true;

    public SchedulableCategory(JunkType type, string displayName)
    {
        Type = type;
        DisplayName = displayName;
    }
}

/// <summary>
/// ViewModel for the Settings page.
/// Provides two-way binding to all AppSettings properties with save/reset support.
/// </summary>
public partial class SettingsViewModel : ObservableObject, IExperienceModeAware
{
    // ── General ──
    [ObservableProperty] private bool _isAdvancedMode;
    [ObservableProperty] private bool _createRestorePointBeforeClean;
    [ObservableProperty] private bool _dryRunMode;
    [ObservableProperty] private bool _showConfirmationDialogs;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private ThemeMode _selectedThemeMode;

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

    // ── Scheduled Cleanup ──
    [ObservableProperty] private bool _scheduledCleanupEnabled;
    [ObservableProperty] private string _scheduledCleanupFrequency;
    [ObservableProperty] private string _scheduledCleanupTime;
    [ObservableProperty] private int _scheduledCleanupDayOfWeek;
    [ObservableProperty] private int _scheduledCleanupDayIndex;

    // ── D1 exclusions + D2 scheduled categories ──
    public ObservableCollection<string> CleanerExcludedPaths { get; } = [];
    public ObservableCollection<SchedulableCategory> ScheduledCategories { get; } = [];
    public bool HasExcludedPaths => CleanerExcludedPaths.Count > 0;

    // ── UI State ──
    [ObservableProperty] private string _statusMessage = "Settings loaded.";
    [ObservableProperty] private bool _hasUnsavedChanges;
    [ObservableProperty] private string _settingsPath = string.Empty;

    private bool _suppressModeChangeTracking;
    private bool _suppressThemePreview;
    private bool _suppressListTracking;

    public string[] ShredAlgorithms { get; } =
        ["QuickZero", "Random", "DoD3Pass", "Enhanced7Pass"];

    public long[] LargeFileSizePresets { get; } = [50, 100, 250, 500, 1024];

    public int[] RetentionDayPresets { get; } = [7, 14, 30, 60, 90];

    public int[] HistoryLimitPresets { get; } = [100, 250, 500, 1000, 2000];

    public string[] ScheduleFrequencies { get; } = ["Daily", "Weekly", "Monthly"];

    public string[] DayOfWeekNames { get; } =
        ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    public SettingsViewModel()
    {
        DefaultShredAlgorithm = "DoD3Pass";
        ScheduledCleanupFrequency = "Weekly";
        ScheduledCleanupTime = "03:00";

        foreach (var type in CleanupModePolicy.GetNormalModeDefaults())
        {
            var item = new SchedulableCategory(type, new JunkItem { Type = type }.Category);
            item.PropertyChanged += OnScheduledCategoryChanged;
            ScheduledCategories.Add(item);
        }
        CleanerExcludedPaths.CollectionChanged += OnExcludedPathsChanged;

        LoadFromDisk();

        // The sidebar toggle can change the theme too; keep the radio group in sync.
        ThemeService.ThemeChanged += (_, _) => SyncThemeMode(ThemeService.Mode);
    }

    private void SyncThemeMode(ThemeMode mode)
    {
        _suppressThemePreview = true;
        SelectedThemeMode = mode;
        _suppressThemePreview = false;
    }

    /// <summary>
    /// Loads settings from SettingsService and maps to ViewModel properties.
    /// </summary>
    private void LoadFromDisk()
    {
        var s = SettingsService.Load();

        _suppressModeChangeTracking = true;
        IsAdvancedMode = s.ExperienceMode == ExperienceMode.Advanced;
        _suppressModeChangeTracking = false;
        CreateRestorePointBeforeClean = s.CreateRestorePointBeforeClean;
        DryRunMode = s.DryRunMode;
        ShowConfirmationDialogs = s.ShowConfirmationDialogs;
        MinimizeToTray = s.MinimizeToTray;
        LaunchAtStartup = s.LaunchAtStartup;
        SyncThemeMode(s.Theme);

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

        ScheduledCleanupEnabled = s.ScheduledCleanupEnabled;
        ScheduledCleanupFrequency = s.ScheduledCleanupFrequency;
        ScheduledCleanupTime = s.ScheduledCleanupTime;
        ScheduledCleanupDayOfWeek = s.ScheduledCleanupDayOfWeek;
        ScheduledCleanupDayIndex = Math.Clamp(s.ScheduledCleanupDayOfWeek, 1, 7) - 1;

        _suppressListTracking = true;
        try
        {
            CleanerExcludedPaths.Clear();
            foreach (var path in s.CleanerExcludedPaths ?? [])
            {
                if (!string.IsNullOrWhiteSpace(path))
                    CleanerExcludedPaths.Add(path.Trim());
            }

            // Empty stored list = defaults = every low-risk category checked.
            var configured = new HashSet<string>(
                s.ScheduledCleanupCategories ?? [], StringComparer.OrdinalIgnoreCase);
            bool useDefaults = configured.Count == 0;
            foreach (var category in ScheduledCategories)
                category.IsSelected = useDefaults || configured.Contains(category.Type.ToString());
        }
        finally
        {
            _suppressListTracking = false;
        }
        OnPropertyChanged(nameof(HasExcludedPaths));

        SettingsPath = SettingsService.GetSettingsDirectory();
        HasUnsavedChanges = false;
        StatusMessage = "Settings loaded.";
    }

    /// <summary>
    /// Maps ViewModel properties back to AppSettings and persists.
    /// </summary>
    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (ScheduledCleanupEnabled && !AppSettings.TryParseScheduleTime(ScheduledCleanupTime, out _))
        {
            StatusMessage = $"'{ScheduledCleanupTime}' isn't a valid time. Use 24-hour HH:mm, for example 03:00.";
            return;
        }

        var previous = SettingsService.Load();
        var s = new AppSettings
        {
            ExperienceMode = IsAdvancedMode ? ExperienceMode.Advanced : ExperienceMode.Normal,
            CreateRestorePointBeforeClean = CreateRestorePointBeforeClean,
            DryRunMode = DryRunMode,
            ShowConfirmationDialogs = ShowConfirmationDialogs,
            MinimizeToTray = MinimizeToTray,
            LaunchAtStartup = LaunchAtStartup,
            Theme = ThemeService.Mode,

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

            ScheduledCleanupEnabled = ScheduledCleanupEnabled,
            ScheduledCleanupFrequency = ScheduledCleanupFrequency,
            ScheduledCleanupTime = ScheduledCleanupTime,
            ScheduledCleanupDayOfWeek = ScheduledCleanupDayIndex + 1,

            // All categories checked = defaults (stored as an empty list).
            ScheduledCleanupCategories = ScheduledCategories.All(c => c.IsSelected)
                ? []
                : ScheduledCategories.Where(c => c.IsSelected).Select(c => c.Type.ToString()).ToList(),
            CleanerExcludedPaths = CleanerExcludedPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),

            // Not edited on this page — carry over so saving never re-triggers onboarding.
            HasCompletedOnboarding = previous.HasCompletedOnboarding,

            LastModified = DateTime.Now
        };

        var saved = await Task.Run(() => SettingsService.Save(s));
        if (!saved)
        {
            StatusMessage = "Settings could not be written to disk. Check that the settings folder is writable.";
            return;
        }

        // Reflect any values that were clamped into range during save.
        LoadFromDisk();

        string scheduleMessage;
        try
        {
            var (ok, message) = await ScheduledCleanupService.ApplyScheduleAsync();
            scheduleMessage = ok ? string.Empty : $" Scheduled cleanup: {message}";
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("SettingsVM", "Applying the cleanup schedule failed", ex);
            scheduleMessage = " The cleanup schedule could not be updated.";
        }

        HasUnsavedChanges = false;
        StatusMessage = $"Settings saved at {DateTime.Now:HH:mm:ss}.{scheduleMessage}";
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        if (!SafetyPromptService.ConfirmDestructiveAction(
                "Reset every setting to its default value? This also switches back to Normal mode.",
                "Reset settings"))
            return;

        var onboardingDone = SettingsService.Load().HasCompletedOnboarding;
        var defaults = new AppSettings { HasCompletedOnboarding = onboardingDone };
        SettingsService.Save(defaults);

        LoadFromDisk();
        ThemeService.SetMode(SelectedThemeMode, persist: false);
        ExperienceModeService.NotifyModeChanged();
        _ = ScheduledCleanupService.ApplyScheduleAsync();

        HasUnsavedChanges = false;
        StatusMessage = "Settings reset to defaults.";
    }

    [RelayCommand]
    private void ReloadSettings()
    {
        SettingsService.InvalidateCache();
        LoadFromDisk();
        ThemeService.SetMode(SelectedThemeMode, persist: false);
        ExperienceModeService.NotifyModeChanged();
        StatusMessage = "Settings reloaded from disk.";
    }

    // ── D1 exclusion list ──

    [RelayCommand]
    private void AddExcludedFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select files to exclude from cleaning",
            Filter = "All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            int added = 0;
            foreach (var path in dialog.FileNames)
                added += TryAddExcludedPath(path) ? 1 : 0;
            StatusMessage = added > 0
                ? $"Added {added} exclusion(s). Save to apply."
                : "Those files are already excluded.";
        }
    }

    [RelayCommand]
    private void AddExcludedFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a folder to exclude (covers everything beneath it)",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            StatusMessage = TryAddExcludedPath(dialog.FolderName)
                ? $"Excluded '{dialog.FolderName}'. Save to apply."
                : "That folder is already excluded.";
        }
    }

    [RelayCommand]
    private void RemoveExcludedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (CleanerExcludedPaths.Remove(path))
            StatusMessage = $"Removed '{path}'. Save to apply.";
    }

    private bool TryAddExcludedPath(string path)
    {
        var trimmed = path.Trim();
        if (CleanerExcludedPaths.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            return false;
        CleanerExcludedPaths.Add(trimmed);
        return true;
    }

    private void OnExcludedPathsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasExcludedPaths));
        if (!_suppressListTracking)
            HasUnsavedChanges = true;
    }

    private void OnScheduledCategoryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressListTracking && e.PropertyName == nameof(SchedulableCategory.IsSelected))
            HasUnsavedChanges = true;
    }

    // ── Track changes for the "unsaved" indicator ──

    partial void OnCreateRestorePointBeforeCleanChanged(bool value) => HasUnsavedChanges = true;
    partial void OnIsAdvancedModeChanged(bool value)
    {
        if (_suppressModeChangeTracking)
            return;

        ExperienceModeService.SaveMode(value);
        HasUnsavedChanges = true;
    }
    partial void OnDryRunModeChanged(bool value) => HasUnsavedChanges = true;
    partial void OnShowConfirmationDialogsChanged(bool value) => HasUnsavedChanges = true;
    partial void OnMinimizeToTrayChanged(bool value) => HasUnsavedChanges = true;
    partial void OnLaunchAtStartupChanged(bool value) => HasUnsavedChanges = true;
    partial void OnSelectedThemeModeChanged(ThemeMode value)
    {
        if (_suppressThemePreview)
            return;

        // Appearance applies and saves immediately, like the sidebar toggle; it is not part of
        // the "unsaved changes" set.
        ThemeService.SetMode(value);
    }
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
    partial void OnScheduledCleanupEnabledChanged(bool value) => HasUnsavedChanges = true;
    partial void OnScheduledCleanupFrequencyChanged(string value) => HasUnsavedChanges = true;
    partial void OnScheduledCleanupTimeChanged(string value) => HasUnsavedChanges = true;
    partial void OnScheduledCleanupDayOfWeekChanged(int value) => HasUnsavedChanges = true;
    partial void OnScheduledCleanupDayIndexChanged(int value)
    {
        ScheduledCleanupDayOfWeek = value + 1;
        HasUnsavedChanges = true;
    }

    public void SetExperienceMode(bool isAdvancedMode)
    {
        _suppressModeChangeTracking = true;
        IsAdvancedMode = isAdvancedMode;
        _suppressModeChangeTracking = false;
    }
}
