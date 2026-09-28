using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Management;
using System.Reflection;
using System.Security.Principal;

namespace AuraClean.ViewModels;

/// <summary>
/// The root ViewModel that orchestrates navigation, system health scoring,
/// and top-level commands (Analyze, Clean).
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private string _currentViewName = "Dashboard";
    [ObservableProperty] private int _systemHealthScore = 85;
    [ObservableProperty] private string _healthLabel = "Good";
    [ObservableProperty] private string _scoreTrendArrow = string.Empty;
    [ObservableProperty] private string _scoreTrendTooltip = string.Empty;
    [ObservableProperty] private bool _hasScoreTrend;
    private readonly int? _previousHealthScore;
    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private string _statusBarText = "AuraClean — Ready";
    [ObservableProperty] private DateTime _lastCleanedDate;
    [ObservableProperty] private bool _isHealthCheckRunning;
    [ObservableProperty] private string _healthCheckProgress = string.Empty;
    [ObservableProperty] private int _healthCheckStep;
    [ObservableProperty] private int _healthCheckTotalSteps = 4;
    [ObservableProperty] private string _healthCheckSummary = string.Empty;
    [ObservableProperty] private bool _isAdvancedMode;
    private bool _isApplyingExternalModeChange;

    /// <summary>Every view name the shell knows how to display.</summary>
    public static readonly IReadOnlySet<string> KnownViews = new HashSet<string>(StringComparer.Ordinal)
    {
        "Dashboard", "Uninstaller", "Cleaner", "Memory", "Browser", "StorageMap", "Monitor",
        "Startup", "Duplicates", "Shredder", "LargeFiles", "SystemInfo", "Settings", "History",
        "Quarantine", "ThreatScanner", "SoftwareUpdater", "DiskOptimizer", "FileRecovery",
        "EmptyFolders", "AppInstaller"
    };

    public string AppVersion { get; } = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0"}";

    public string ExperienceModeLabel => IsAdvancedMode ? "Advanced" : "Normal";

    public string ExperienceModeDescription => IsAdvancedMode
        ? "Full controls and expert actions are visible."
        : "Only low-risk cleanup and plain-language choices are shown.";

    public string ExperienceModeToggleLabel => IsAdvancedMode
        ? "Switch to Normal Mode"
        : "Switch to Advanced Mode";

    /// <summary>Current theme preference, drives the sidebar toggle's icon.</summary>
    public ThemeMode ThemeMode => ThemeService.Mode;

    /// <summary>Tooltip and accessible name for the sidebar theme toggle.</summary>
    public string ThemeToggleLabel =>
        $"Theme: {ThemeModes.Label(ThemeService.Mode)}. Switch to {ThemeModes.Label(ThemeModes.Next(ThemeService.Mode))}";

    // System info properties for Dashboard
    [ObservableProperty] private string _osName = string.Empty;
    [ObservableProperty] private string _cpuName = string.Empty;
    [ObservableProperty] private string _totalRam = string.Empty;
    [ObservableProperty] private string _systemUptime = string.Empty;
    [ObservableProperty] private string _machineName = Environment.MachineName;
    [ObservableProperty] private string _userName = Environment.UserName;

    public string LastCleanedDisplay =>
        LastCleanedDate == default ? "Never" : LastCleanedDate.ToString("MMM dd, yyyy");

    partial void OnLastCleanedDateChanged(DateTime value) =>
        OnPropertyChanged(nameof(LastCleanedDisplay));

    partial void OnIsAdvancedModeChanged(bool value)
    {
        if (!_isApplyingExternalModeChange)
            ExperienceModeService.SaveMode(value);

        ApplyExperienceModeToChildren(value);
        OnPropertyChanged(nameof(ExperienceModeLabel));
        OnPropertyChanged(nameof(ExperienceModeDescription));
        OnPropertyChanged(nameof(ExperienceModeToggleLabel));
        StatusBarText = value
            ? "Advanced mode enabled — expert controls are visible."
            : "Normal mode enabled — only safer defaults are shown.";
    }

    private void OnExperienceModeChanged(bool isAdvancedMode)
    {
        if (IsAdvancedMode == isAdvancedMode)
            return;

        _isApplyingExternalModeChange = true;
        try { IsAdvancedMode = isAdvancedMode; }
        finally { _isApplyingExternalModeChange = false; }
    }

    // Child ViewModels
    public UninstallerViewModel Uninstaller { get; } = new();
    public CleanerViewModel Cleaner { get; } = new();
    public MemoryViewModel Memory { get; } = new();
    public InstallMonitorViewModel InstallMonitor { get; } = new();
    public BrowserCleanerViewModel BrowserCleaner { get; } = new();
    public DiskAnalyzerViewModel DiskAnalyzer { get; } = new();
    public StartupManagerViewModel StartupManager { get; } = new();
    public DuplicateFinderViewModel DuplicateFinder { get; } = new();
    public FileShredderViewModel FileShredder { get; } = new();
    public LargeFileFinderViewModel LargeFileFinder { get; } = new();
    public SystemInfoViewModel SystemInfo { get; } = new();
    public SettingsViewModel Settings { get; } = new();
    public CleanupHistoryViewModel CleanupHistory { get; } = new();
    public QuarantineViewModel Quarantine { get; } = new();
    public ThreatScannerViewModel ThreatScanner { get; } = new();
    public SoftwareUpdaterViewModel SoftwareUpdater { get; } = new();
    public DiskOptimizerViewModel DiskOptimizer { get; } = new();
    public FileRecoveryViewModel FileRecovery { get; } = new();
    public EmptyFolderFinderViewModel EmptyFolderFinder { get; } = new();
    public AppInstallerViewModel AppInstaller { get; } = new();
    public OnboardingViewModel Onboarding { get; } = new();

    // Context menu
    [ObservableProperty] private bool _isContextMenuInstalled;
    [ObservableProperty] private string _contextMenuStatus = string.Empty;

    public string ContextMenuToggleLabel => IsContextMenuInstalled ? "Remove" : "Install";

    partial void OnIsContextMenuInstalledChanged(bool value) =>
        OnPropertyChanged(nameof(ContextMenuToggleLabel));

    // Navigation section collapse state
    [ObservableProperty] private bool _isCleanupExpanded = true;
    [ObservableProperty] private bool _isAnalyzeExpanded = true;
    [ObservableProperty] private bool _isOptimizeExpanded = true;
    [ObservableProperty] private bool _isUtilitiesExpanded = false;

    /// <summary>
    /// True while any feature is scanning, cleaning, installing, or otherwise working.
    /// Used to warn before the window is closed mid-operation.
    /// </summary>
    public bool IsAnyOperationRunning =>
        IsHealthCheckRunning ||
        Cleaner.IsBusy || Uninstaller.IsBusy || BrowserCleaner.IsBusy || Memory.IsBusy ||
        ThreatScanner.IsScanning || ThreatScanner.IsQuarantining ||
        FileShredder.IsBusy || DuplicateFinder.IsBusy || LargeFileFinder.IsBusy ||
        EmptyFolderFinder.IsBusy || Quarantine.IsBusy || StartupManager.IsBusy ||
        DiskOptimizer.IsBusy || AppInstaller.IsBusy || SoftwareUpdater.IsBusy ||
        FileRecovery.IsBusy || InstallMonitor.IsBusy;

    public MainViewModel()
    {
        using (var identity = WindowsIdentity.GetCurrent())
            IsAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        IsContextMenuInstalled = ContextMenuService.IsContextMenuInstalled();
        ContextMenuStatus = IsContextMenuInstalled ? "Installed" : "Not installed";

        _isApplyingExternalModeChange = true;
        try { IsAdvancedMode = ExperienceModeService.IsAdvancedMode(); }
        finally { _isApplyingExternalModeChange = false; }
        ApplyExperienceModeToChildren(IsAdvancedMode);
        ExperienceModeService.ModeChanged += OnExperienceModeChanged;
        ThemeService.ThemeChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ThemeMode));
            OnPropertyChanged(nameof(ThemeToggleLabel));
        };
        StatusBarText = "AuraClean — Ready";

        Cleaner.CleanupCompleted += OnCleanupCompleted;

        LastCleanedDate = LastCleanedStore.Load() ?? default;
        _previousHealthScore = LastCleanedStore.LoadHealthScore();

        UpdateHealthScore();
        _ = LoadSystemInfoAsync();
    }

    /// <summary>
    /// Background housekeeping run once after the main window is shown.
    /// </summary>
    public async Task RunStartupMaintenanceAsync()
    {
        try
        {
            if (!SettingsService.Load().AutoPurgeExpiredQuarantine)
                return;

            int purged = await QuarantineService.PurgeExpiredAsync();
            if (purged > 0)
            {
                CleanupHistoryService.Record(CleanupOperationType.QuarantinePurge, purged, 0,
                    $"Automatically purged {purged} expired quarantine item(s)");
                WeakReferenceMessenger.Default.Send(QuarantineChangedMessage.Instance);
                StatusBarText = $"Purged {purged} expired quarantine item(s).";
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("MainViewModel", "Startup maintenance failed", ex);
        }
    }

    /// <summary>
    /// Loads system information (OS, CPU, RAM, uptime) on a background thread.
    /// </summary>
    private async Task LoadSystemInfoAsync()
    {
        try
        {
            var (os, cpu, ram, uptime) = await Task.Run(() =>
            {
                string osName = $"Windows {Environment.OSVersion.Version}";
                try
                {
                    using var mos = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem");
                    using var results = mos.Get();
                    foreach (var obj in results)
                    {
                        using (obj)
                            osName = obj["Caption"]?.ToString()?.Trim() ?? osName;
                        break;
                    }
                }
                catch (Exception ex) { DiagnosticLogger.Warn("MainViewModel", "WMI OS query failed", ex); }

                string cpuName = $"{Environment.ProcessorCount} logical cores";
                try
                {
                    using var mos = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                    using var results = mos.Get();
                    foreach (var obj in results)
                    {
                        using (obj)
                            cpuName = obj["Name"]?.ToString()?.Trim() ?? cpuName;
                        break;
                    }
                }
                catch (Exception ex) { DiagnosticLogger.Warn("MainViewModel", "WMI CPU query failed", ex); }

                string totalRam;
                try
                {
                    var snapshot = MemoryManagerService.GetMemorySnapshot();
                    totalRam = snapshot.TotalPhysicalBytes > 0
                        ? FormatHelper.FormatBytes(snapshot.TotalPhysicalBytes)
                        : "N/A";
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn("MainViewModel", "Memory query failed", ex);
                    totalRam = "N/A";
                }

                var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
                var uptimeText = up.Days > 0
                    ? $"{up.Days}d {up.Hours}h {up.Minutes}m"
                    : $"{up.Hours}h {up.Minutes}m";

                return (osName, cpuName, totalRam, uptimeText);
            });

            OsName = os;
            CpuName = cpu;
            TotalRam = ram;
            SystemUptime = uptime;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("MainViewModel", "LoadSystemInfoAsync failed", ex);
        }
    }

    partial void OnSystemHealthScoreChanged(int value)
    {
        HealthLabel = value switch
        {
            >= 80 => "Excellent",
            >= 60 => "Good",
            >= 40 => "Fair",
            _ => "Poor"
        };

        if (_previousHealthScore.HasValue)
        {
            int delta = value - _previousHealthScore.Value;
            (ScoreTrendArrow, ScoreTrendTooltip) = delta switch
            {
                > 0 => ("↑", $"Up {delta} point{(delta != 1 ? "s" : "")} since last session"),
                < 0 => ("↓", $"Down {-delta} point{(delta != -1 ? "s" : "")} since last session"),
                _ => ("—", "Unchanged since last session")
            };
            HasScoreTrend = true;
        }
        else
        {
            HasScoreTrend = false;
        }

        LastCleanedStore.SaveHealthScore(value);
    }

    [RelayCommand]
    private void NavigateTo(string? viewName)
    {
        if (string.IsNullOrWhiteSpace(viewName) || !KnownViews.Contains(viewName))
            return;

        CurrentViewName = viewName;
    }

    [RelayCommand]
    private void ToggleCleanup() => IsCleanupExpanded = !IsCleanupExpanded;

    [RelayCommand]
    private void ToggleAnalyze() => IsAnalyzeExpanded = !IsAnalyzeExpanded;

    [RelayCommand]
    private void ToggleOptimize() => IsOptimizeExpanded = !IsOptimizeExpanded;

    [RelayCommand]
    private void ToggleUtilities() => IsUtilitiesExpanded = !IsUtilitiesExpanded;

    [RelayCommand]
    private void ToggleExperienceMode() => IsAdvancedMode = !IsAdvancedMode;

    [RelayCommand]
    private void CycleTheme()
    {
        var mode = ThemeService.Cycle();
        StatusBarText = $"Theme: {ThemeModes.Label(mode)}";
    }

    [RelayCommand]
    private async Task QuickAnalyzeAsync()
    {
        if (Cleaner.IsBusy)
        {
            StatusBarText = "The System Cleaner is already working — please wait for it to finish.";
            return;
        }

        StatusBarText = "Running quick analysis...";
        await Cleaner.AnalyzeCommand.ExecuteAsync(null);
        UpdateHealthScore();
        StatusBarText = Cleaner.HasResults
            ? $"Analysis complete — {Cleaner.FormattedTotalSize} can be cleaned. Open System Cleaner to review."
            : "Analysis complete — nothing to clean.";
    }

    [RelayCommand]
    private async Task QuickCleanAsync()
    {
        if (Cleaner.IsBusy)
        {
            StatusBarText = "The System Cleaner is already working — please wait for it to finish.";
            return;
        }

        if (!Cleaner.HasResults)
        {
            StatusBarText = "Analyzing before cleanup...";
            await Cleaner.AnalyzeCommand.ExecuteAsync(null);
            if (!Cleaner.HasResults)
            {
                UpdateHealthScore();
                StatusBarText = "Nothing to clean — your system is already tidy.";
                return;
            }
        }

        StatusBarText = "Running cleanup...";
        await Cleaner.CleanSelectedCommand.ExecuteAsync(null);
        StatusBarText = Cleaner.StatusMessage;
    }

    private void OnCleanupCompleted(object? sender, CleanupCompletedEventArgs e)
    {
        if (!e.WasDryRun && e.ItemsCleaned > 0)
        {
            LastCleanedDate = DateTime.Now;
            LastCleanedStore.Save(LastCleanedDate);
        }

        UpdateHealthScore();
    }

    [RelayCommand]
    private async Task RunHealthCheckAsync()
    {
        if (IsHealthCheckRunning) return;

        IsHealthCheckRunning = true;
        HealthCheckStep = 0;
        HealthCheckSummary = string.Empty;
        StatusBarText = "Running comprehensive health check...";

        int score = 100;
        var issues = new List<string>();

        try
        {
            // Step 1: Junk Analysis
            HealthCheckStep = 1;
            HealthCheckProgress = "Step 1/4 — Scanning for system junk...";
            try
            {
                if (!Cleaner.IsBusy)
                    await Cleaner.AnalyzeCommand.ExecuteAsync(null);

                if (Cleaner.TotalJunkSize > 0)
                {
                    int junkPenalty = (int)Math.Min(30, Cleaner.TotalJunkSize / (100.0 * 1024 * 1024) * 5);
                    score -= junkPenalty;
                    issues.Add($"Junk: {Cleaner.FormattedTotalSize} found");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("HealthCheck", "Step 1 (Junk Analysis) failed", ex);
            }

            // Step 2: Threat Scan (always a Quick scan; the user's chosen mode is restored afterwards)
            HealthCheckStep = 2;
            HealthCheckProgress = "Step 2/4 — Scanning for threats...";
            try
            {
                if (!ThreatScanner.IsScanning && !ThreatScanner.IsQuarantining)
                {
                    var previousMode = ThreatScanner.SelectedScanMode;
                    ThreatScanner.SelectedScanMode = ScanMode.Quick;
                    try { await ThreatScanner.StartScanCommand.ExecuteAsync(null); }
                    finally { ThreatScanner.SelectedScanMode = previousMode; }
                }

                int criticalThreats = ThreatScanner.CriticalCount + ThreatScanner.HighCount;
                int mediumThreats = ThreatScanner.MediumCount;

                if (criticalThreats > 0)
                {
                    score -= Math.Min(30, criticalThreats * 15);
                    issues.Add($"Threats: {criticalThreats} critical/high");
                }
                if (mediumThreats > 0)
                {
                    score -= Math.Min(10, mediumThreats * 3);
                    issues.Add($"Threats: {mediumThreats} medium");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("HealthCheck", "Step 2 (Threat Scan) failed", ex);
            }

            // Step 3: Startup Analysis
            HealthCheckStep = 3;
            HealthCheckProgress = "Step 3/4 — Analyzing startup items...";
            try
            {
                var startupEntries = await StartupManagerService.GetStartupEntriesAsync();
                int highImpactStartup = startupEntries.Count(e => e.IsEnabled &&
                    e.Impact == StartupManagerService.StartupImpact.High);

                if (highImpactStartup > 5)
                {
                    score -= Math.Min(15, (highImpactStartup - 5) * 3);
                    issues.Add($"Startup: {highImpactStartup} high-impact items");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("HealthCheck", "Step 3 (Startup Analysis) failed", ex);
            }

            // Step 4: Browser Privacy
            HealthCheckStep = 4;
            HealthCheckProgress = "Step 4/4 — Checking browser caches...";
            try
            {
                if (!BrowserCleaner.IsBusy)
                    await BrowserCleaner.ScanBrowsersCommand.ExecuteAsync(null);

                long browserJunk = BrowserCleaner.TotalSavingsBytes;
                if (browserJunk > 100 * 1024 * 1024) // > 100 MB
                {
                    score -= Math.Min(10, (int)(browserJunk / (100.0 * 1024 * 1024)) * 3);
                    issues.Add($"Browser: {FormatHelper.FormatBytes(browserJunk)} of cache and site data");
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("HealthCheck", "Step 4 (Browser Privacy) failed", ex);
            }

            // Cleanliness factor
            if (LastCleanedDate == default)
                score -= 10;
            else if ((DateTime.Now - LastCleanedDate).TotalDays > 30)
                score -= 5;

            score = Math.Clamp(score, 0, 100);
            SystemHealthScore = score;

            HealthCheckSummary = issues.Count == 0
                ? "Your system is in excellent condition. No issues detected."
                : $"Found {issues.Count} area(s) to improve: {string.Join(" | ", issues)}";

            HealthCheckProgress = "Health check complete.";
            StatusBarText = $"Health check complete — Score: {score}/100";

            NotificationService.ShowSuccess("Health Check Complete",
                $"System health score: {score}/100. {(issues.Count > 0 ? $"{issues.Count} issue(s) found." : "No issues.")}");
        }
        catch (Exception ex)
        {
            HealthCheckProgress = "The health check could not finish. Please try again.";
            StatusBarText = "Health check encountered an error.";
            DiagnosticLogger.Error("MainViewModel", "Health check failed", ex);
        }
        finally
        {
            IsHealthCheckRunning = false;
        }
    }

    [RelayCommand]
    private async Task BoostMemoryAsync()
    {
        if (Memory.IsBusy)
            return;

        StatusBarText = "Boosting memory...";
        await Memory.BoostMemoryCommand.ExecuteAsync(null);
        StatusBarText = Memory.StatusMessage;
    }

    [RelayCommand]
    private void UndoLastClean()
    {
        if (!Cleaner.CanUndoLastClean)
        {
            StatusBarText = "No recent cleanup to undo.";
            return;
        }

        try
        {
            // Launch Windows System Restore UI so the user can revert to the pre-cleanup restore point
            var psi = new System.Diagnostics.ProcessStartInfo(ProcessRunner.SystemTool("rstrui.exe"))
            {
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi)?.Dispose();
            StatusBarText = "System Restore opened — select the AuraClean restore point to undo.";
        }
        catch (Exception ex)
        {
            StatusBarText = "Could not open System Restore. Open it from Control Panel → Recovery.";
            DiagnosticLogger.Warn("MainViewModel", "Failed to launch System Restore", ex);
        }
    }

    [RelayCommand]
    private void ToggleContextMenu()
    {
        var (success, msg, _) = IsContextMenuInstalled
            ? ContextMenuService.UninstallContextMenu()
            : ContextMenuService.InstallContextMenu();

        IsContextMenuInstalled = ContextMenuService.IsContextMenuInstalled();
        ContextMenuStatus = success
            ? (IsContextMenuInstalled ? "Installed" : "Not installed")
            : msg;
        StatusBarText = msg;
    }

    [RelayCommand]
    private void ExportContextMenuScript()
    {
        var (success, path) = ContextMenuService.ExportRegistryScript(install: !IsContextMenuInstalled);
        StatusBarText = success
            ? $"Registry script saved to {path}"
            : "Failed to export registry script.";
    }

    /// <summary>
    /// Computes a system health score (0–100) based on current junk levels.
    /// </summary>
    private void UpdateHealthScore()
    {
        int score = 100;

        if (Cleaner.TotalJunkSize > 0)
        {
            // Every 100MB of junk deducts ~5 points, capped at 50 points
            int junkPenalty = (int)Math.Min(50, Cleaner.TotalJunkSize / (100 * 1024 * 1024) * 5);
            score -= junkPenalty;
        }

        if (LastCleanedDate == default)
        {
            score -= 15; // Never cleaned
        }
        else
        {
            var daysSinceCleaned = (DateTime.Now - LastCleanedDate).TotalDays;
            if (daysSinceCleaned > 30) score -= 10;
            else if (daysSinceCleaned > 7) score -= 5;
        }

        SystemHealthScore = Math.Clamp(score, 0, 100);
    }

    private void ApplyExperienceModeToChildren(bool isAdvancedMode)
    {
        object?[] children =
        [
            Uninstaller, Cleaner, Memory, InstallMonitor, BrowserCleaner, DiskAnalyzer,
            StartupManager, DuplicateFinder, FileShredder, LargeFileFinder, SystemInfo,
            Settings, CleanupHistory, Quarantine, ThreatScanner, SoftwareUpdater,
            DiskOptimizer, FileRecovery, EmptyFolderFinder, AppInstaller, Onboarding
        ];

        foreach (var child in children)
        {
            if (child is IExperienceModeAware modeAware)
                modeAware.SetExperienceMode(isAdvancedMode);
        }
    }
}
