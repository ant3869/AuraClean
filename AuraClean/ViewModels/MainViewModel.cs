using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.ComponentModel;
using System.IO;
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
        "EmptyFolders", "AppInstaller", "LeftoverRestore"
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

    // Child ViewModels (lazy: constructed on first access, i.e. first navigate or Dashboard binding).
    // NOTE: Cleaner/Uninstaller/Onboarding are still created at startup via Dashboard/Onboarding
    // bindings; the rest wait until first navigate. IsAnyOperationRunning, UpdateHealthScore, and
    // ApplyExperienceModeToChildren check IsValueCreated to avoid forcing creation.
    private readonly Lazy<UninstallerViewModel> _uninstaller = new(() => new UninstallerViewModel());
    public UninstallerViewModel Uninstaller => _uninstaller.Value;
    private readonly Lazy<CleanerViewModel> _cleaner; // init in ctor (subscribes CleanupCompleted)
    public CleanerViewModel Cleaner => _cleaner.Value;
    private readonly Lazy<MemoryViewModel> _memory = new(() => new MemoryViewModel());
    public MemoryViewModel Memory => _memory.Value;
    private readonly Lazy<InstallMonitorViewModel> _installMonitor = new(() => new InstallMonitorViewModel());
    public InstallMonitorViewModel InstallMonitor => _installMonitor.Value;
    private readonly Lazy<BrowserCleanerViewModel> _browserCleaner = new(() => new BrowserCleanerViewModel());
    public BrowserCleanerViewModel BrowserCleaner => _browserCleaner.Value;
    private readonly Lazy<DiskAnalyzerViewModel> _diskAnalyzer = new(() => new DiskAnalyzerViewModel());
    public DiskAnalyzerViewModel DiskAnalyzer => _diskAnalyzer.Value;
    private readonly Lazy<StartupManagerViewModel> _startupManager = new(() => new StartupManagerViewModel());
    public StartupManagerViewModel StartupManager => _startupManager.Value;
    private readonly Lazy<DuplicateFinderViewModel> _duplicateFinder = new(() => new DuplicateFinderViewModel());
    public DuplicateFinderViewModel DuplicateFinder => _duplicateFinder.Value;
    private readonly Lazy<FileShredderViewModel> _fileShredder = new(() => new FileShredderViewModel());
    public FileShredderViewModel FileShredder => _fileShredder.Value;
    private readonly Lazy<LargeFileFinderViewModel> _largeFileFinder = new(() => new LargeFileFinderViewModel());
    public LargeFileFinderViewModel LargeFileFinder => _largeFileFinder.Value;
    private readonly Lazy<SystemInfoViewModel> _systemInfo = new(() => new SystemInfoViewModel());
    public SystemInfoViewModel SystemInfo => _systemInfo.Value;
    private readonly Lazy<SettingsViewModel> _settings = new(() => new SettingsViewModel());
    public SettingsViewModel Settings => _settings.Value;
    private readonly Lazy<CleanupHistoryViewModel> _cleanupHistory = new(() => new CleanupHistoryViewModel());
    public CleanupHistoryViewModel CleanupHistory => _cleanupHistory.Value;
    private readonly Lazy<QuarantineViewModel> _quarantine = new(() => new QuarantineViewModel());
    public QuarantineViewModel Quarantine => _quarantine.Value;
    private readonly Lazy<ThreatScannerViewModel> _threatScanner = new(() => new ThreatScannerViewModel());
    public ThreatScannerViewModel ThreatScanner => _threatScanner.Value;
    private readonly Lazy<SoftwareUpdaterViewModel> _softwareUpdater = new(() => new SoftwareUpdaterViewModel());
    public SoftwareUpdaterViewModel SoftwareUpdater => _softwareUpdater.Value;
    private readonly Lazy<DiskOptimizerViewModel> _diskOptimizer = new(() => new DiskOptimizerViewModel());
    public DiskOptimizerViewModel DiskOptimizer => _diskOptimizer.Value;
    private readonly Lazy<FileRecoveryViewModel> _fileRecovery = new(() => new FileRecoveryViewModel());
    public FileRecoveryViewModel FileRecovery => _fileRecovery.Value;
    private readonly Lazy<EmptyFolderFinderViewModel> _emptyFolderFinder = new(() => new EmptyFolderFinderViewModel());
    public EmptyFolderFinderViewModel EmptyFolderFinder => _emptyFolderFinder.Value;
    private readonly Lazy<AppInstallerViewModel> _appInstaller = new(() => new AppInstallerViewModel());
    public AppInstallerViewModel AppInstaller => _appInstaller.Value;
    private readonly Lazy<OnboardingViewModel> _onboarding = new(() => new OnboardingViewModel());
    public OnboardingViewModel Onboarding => _onboarding.Value;
    private readonly Lazy<LeftoverRestoreViewModel> _leftoverRestore = new(() => new LeftoverRestoreViewModel());
    public LeftoverRestoreViewModel LeftoverRestore => _leftoverRestore.Value;

    // Dashboard freshness tiles (D1): cheapest existing data, never a new service.
    [ObservableProperty] private string _diskFreeDisplay = "Not scanned yet";
    [ObservableProperty] private string _diskFreeDetail = "Open System Cleaner or Storage Map to scan";
    [ObservableProperty] private string _hardwareGradeDisplay = "—";
    [ObservableProperty] private string _hardwareGradeDetail = "Visit System Info for the full breakdown";
    [ObservableProperty] private bool _cleanerAnalyzedOnce;
    private bool _dashboardPrimed;

    /// <summary>
    /// Junk tile copy: the size when results exist, "Nothing to clean" when an analyze
    /// ran but found zero junk, "Not scanned yet" when no analyze has ever run.
    /// </summary>
    public string JunkTileDisplay =>
        _cleaner.IsValueCreated && Cleaner.HasResults ? Cleaner.FormattedTotalSize
        : CleanerAnalyzedOnce ? "Nothing to clean"
        : "Not scanned yet";

    partial void OnCleanerAnalyzedOnceChanged(bool value) => OnPropertyChanged(nameof(JunkTileDisplay));

    private void OnCleanerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanerViewModel.IsAnalyzing))
        {
            if (_cleaner.IsValueCreated && Cleaner.IsAnalyzing)
                CleanerAnalyzedOnce = true;
            return;
        }

        if (e.PropertyName == nameof(CleanerViewModel.HasResults) ||
            e.PropertyName == nameof(CleanerViewModel.FormattedTotalSize) ||
            e.PropertyName == nameof(CleanerViewModel.TotalJunkSize))
            OnPropertyChanged(nameof(JunkTileDisplay));
    }

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
    [ObservableProperty] private bool _isToolsExpanded = false;
    [ObservableProperty] private bool _isSystemExpanded = false;

    /// <summary>
    /// True while any feature is scanning, cleaning, installing, or otherwise working.
    /// Used to warn before the window is closed mid-operation.
    /// Checks IsValueCreated first so the check itself never forces VM creation.
    /// </summary>
    public bool IsAnyOperationRunning =>
        IsHealthCheckRunning ||
        (_cleaner.IsValueCreated && Cleaner.IsBusy) ||
        (_uninstaller.IsValueCreated && Uninstaller.IsBusy) ||
        (_browserCleaner.IsValueCreated && BrowserCleaner.IsBusy) ||
        (_memory.IsValueCreated && Memory.IsBusy) ||
        (_threatScanner.IsValueCreated && (ThreatScanner.IsScanning || ThreatScanner.IsQuarantining)) ||
        (_fileShredder.IsValueCreated && FileShredder.IsBusy) ||
        (_duplicateFinder.IsValueCreated && DuplicateFinder.IsBusy) ||
        (_largeFileFinder.IsValueCreated && LargeFileFinder.IsBusy) ||
        (_emptyFolderFinder.IsValueCreated && EmptyFolderFinder.IsBusy) ||
        (_quarantine.IsValueCreated && Quarantine.IsBusy) ||
        (_startupManager.IsValueCreated && StartupManager.IsBusy) ||
        (_diskOptimizer.IsValueCreated && DiskOptimizer.IsBusy) ||
        (_appInstaller.IsValueCreated && AppInstaller.IsBusy) ||
        (_softwareUpdater.IsValueCreated && SoftwareUpdater.IsBusy) ||
        (_fileRecovery.IsValueCreated && FileRecovery.IsBusy) ||
        (_installMonitor.IsValueCreated && InstallMonitor.IsBusy) ||
        (_leftoverRestore.IsValueCreated && LeftoverRestore.IsBusy);

    public MainViewModel()
    {
        // Cleaner subscribes CleanupCompleted on first creation (not in ctor, to stay lazy).
        _cleaner = new Lazy<CleanerViewModel>(() =>
        {
            var vm = new CleanerViewModel();
            vm.CleanupCompleted += OnCleanupCompleted;
            vm.PropertyChanged += OnCleanerPropertyChanged;
            return vm;
        });

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
        await RecoverInterruptedLeftoversAsync();

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
    /// Resolves any leftover-removal entry left Pending by a crash or an unexpected shutdown,
    /// so a stale journal never lingers until the user happens to open Leftover Backups or run
    /// another deep clean. Safe to run every startup: <see cref="LeftoverBackupStore.RecoverInterrupted"/>
    /// only touches entries still Pending and is a no-op once the journal is settled.
    /// </summary>
    private async Task RecoverInterruptedLeftoversAsync()
    {
        try
        {
            var store = LeftoverBackupStore.CreateDefault();
            var report = await Task.Run(store.RecoverInterrupted);
            if (report.Count > 0)
            {
                CleanupHistoryService.Record(CleanupOperationType.LeftoverRestore, report.Count, 0,
                    $"Startup recovery resolved {report.Count} interrupted leftover operation(s)");
                StatusBarText = $"Resolved {report.Count} interrupted leftover operation(s) from a previous session.";
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("MainViewModel", "Startup leftover recovery failed", ex);
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

    /// <summary>
    /// Primes the dashboard on first show: kicks off the Uninstaller load and the Cleaner
    /// analyze (reusing their existing commands — no duplicated scan logic) when they have
    /// never run, so stat tiles show real data instead of zeros. Safe to call repeatedly;
    /// only fires once. Also refreshes the disk-free and hardware-grade tiles.
    /// </summary>
    [RelayCommand]
    private void PrimeDashboard()
    {
        RefreshDashboardTiles();

        if (_dashboardPrimed)
            return;
        _dashboardPrimed = true;

        try
        {
            // NOTE: dashboard bindings already force Uninstaller/Cleaner creation at
            // startup, so no IsValueCreated guard here — just skip if already scanned.
            if (!Uninstaller.HasScanned && !Uninstaller.IsBusy)
                Uninstaller.LoadProgramsCommand.Execute(null);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("MainViewModel", "Dashboard prime: Uninstaller load failed", ex);
        }

        try
        {
            if (!Cleaner.HasResults && !Cleaner.IsBusy && Cleaner.TotalJunkSize <= 0)
            {
                _ = Cleaner.AnalyzeCommand.ExecuteAsync(null).ContinueWith(t =>
                {
                    UpdateHealthScore();
                    RefreshDashboardTiles();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("MainViewModel", "Dashboard prime: Cleaner analyze failed", ex);
        }
    }

    /// <summary>
    /// Refreshes the dashboard-only tiles from the cheapest existing data:
    /// disk-free % via DriveInfo on the system drive (no new service), hardware grade
    /// only when the SystemInfo VM has already computed it (never blocks dashboard).
    /// </summary>
    private void RefreshDashboardTiles()
    {
        try
        {
            var systemDrive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            if (systemDrive.IsReady && systemDrive.TotalSize > 0)
            {
                double freePct = systemDrive.AvailableFreeSpace * 100.0 / systemDrive.TotalSize;
                DiskFreeDisplay = $"{freePct:0}% free";
                DiskFreeDetail = $"{systemDrive.Name.TrimEnd('\\')} · {FormatHelper.FormatBytes(systemDrive.AvailableFreeSpace)} of {FormatHelper.FormatBytes(systemDrive.TotalSize)} free";
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("MainViewModel", "Dashboard tile: disk-free lookup failed", ex);
        }

        try
        {
            // Never force SystemInfo creation: the grade appears only after a SystemInfo visit.
            if (_systemInfo.IsValueCreated && !string.IsNullOrWhiteSpace(SystemInfo.OverallGrade)
                && SystemInfo.OverallGrade != "—")
            {
                HardwareGradeDisplay = $"Grade {SystemInfo.OverallGrade} ({SystemInfo.OverallScore})";
                HardwareGradeDetail = string.IsNullOrWhiteSpace(SystemInfo.OverallGradeLabel)
                    ? "From System Info"
                    : SystemInfo.OverallGradeLabel;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("MainViewModel", "Dashboard tile: hardware grade lookup failed", ex);
        }
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
    private void ToggleTools() => IsToolsExpanded = !IsToolsExpanded;

    [RelayCommand]
    private void ToggleSystem() => IsSystemExpanded = !IsSystemExpanded;

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
                if (!StartupManager.IsBusy)
                    await StartupManager.LoadEntriesCommand.ExecuteAsync(null);
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
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Error("HealthCheck", "Step 4 (Browser Privacy) failed", ex);
            }

            // Single shared formula: identical score before/after the check given same inputs.
            var (score, computedIssues) = ComputeHealthScore();
            issues.AddRange(computedIssues);
            SystemHealthScore = score;
            RefreshDashboardTiles();

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
    /// Computes a system health score (0–100) from the four inputs the hero tooltip
    /// promises: junk, threats, startup load, and browser caches — plus a recency
    /// factor for how long ago the last cleanup ran. Missing (never-scanned) inputs
    /// count 0 and never fail. Used by BOTH UpdateHealthScore and RunHealthCheckAsync
    /// so the score is identical given the same inputs.
    /// </summary>
    private (int Score, List<string> Issues) ComputeHealthScore()
    {
        int score = 100;
        var issues = new List<string>();

        // Junk: every 100MB deducts ~5 points, capped at 30.
        try
        {
            if (_cleaner.IsValueCreated && Cleaner.TotalJunkSize > 0)
            {
                int junkPenalty = (int)Math.Min(30, Cleaner.TotalJunkSize / (100.0 * 1024 * 1024) * 5);
                score -= junkPenalty;
                if (junkPenalty > 0)
                    issues.Add($"Junk: {Cleaner.FormattedTotalSize} found");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("MainViewModel", "Health score: junk input failed", ex);
        }

        // Threats: critical/high hit hard, medium lightly.
        try
        {
            if (_threatScanner.IsValueCreated && ThreatScanner.HasResults)
            {
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
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("MainViewModel", "Health score: threat input failed", ex);
        }

        // Startup load: more than 5 high-impact enabled items deducts.
        try
        {
            if (_startupManager.IsValueCreated && StartupManager.HasScanned)
            {
                int highImpactStartup = StartupManager.Entries.Count(e => e.IsEnabled &&
                    e.Impact == StartupManagerService.StartupImpact.High);
                if (highImpactStartup > 5)
                {
                    score -= Math.Min(15, (highImpactStartup - 5) * 3);
                    issues.Add($"Startup: {highImpactStartup} high-impact items");
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("MainViewModel", "Health score: startup input failed", ex);
        }

        // Browser caches: over 100MB deducts.
        try
        {
            if (_browserCleaner.IsValueCreated && BrowserCleaner.HasResults)
            {
                long browserJunk = BrowserCleaner.TotalSavingsBytes;
                if (browserJunk > 100 * 1024 * 1024)
                {
                    score -= Math.Min(10, (int)(browserJunk / (100.0 * 1024 * 1024)) * 3);
                    issues.Add($"Browser: {FormatHelper.FormatBytes(browserJunk)} of cache and site data");
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("MainViewModel", "Health score: browser input failed", ex);
        }

        // Recency factor.
        if (LastCleanedDate == default)
            score -= 10;
        else if ((DateTime.Now - LastCleanedDate).TotalDays > 30)
            score -= 5;

        return (Math.Clamp(score, 0, 100), issues);
    }

    private void UpdateHealthScore()
    {
        SystemHealthScore = ComputeHealthScore().Score;
    }

    private void ApplyExperienceModeToChildren(bool isAdvancedMode)
    {
        // Only touch already-created VMs; not-yet-created VMs read the current mode
        // from ExperienceModeService on construction (see ExperienceModePartials).
        object?[] children =
        [
            _uninstaller.IsValueCreated ? _uninstaller.Value : null,
            _cleaner.IsValueCreated ? _cleaner.Value : null,
            _memory.IsValueCreated ? _memory.Value : null,
            _installMonitor.IsValueCreated ? _installMonitor.Value : null,
            _browserCleaner.IsValueCreated ? _browserCleaner.Value : null,
            _diskAnalyzer.IsValueCreated ? _diskAnalyzer.Value : null,
            _startupManager.IsValueCreated ? _startupManager.Value : null,
            _duplicateFinder.IsValueCreated ? _duplicateFinder.Value : null,
            _fileShredder.IsValueCreated ? _fileShredder.Value : null,
            _largeFileFinder.IsValueCreated ? _largeFileFinder.Value : null,
            _systemInfo.IsValueCreated ? _systemInfo.Value : null,
            _settings.IsValueCreated ? _settings.Value : null,
            _cleanupHistory.IsValueCreated ? _cleanupHistory.Value : null,
            _quarantine.IsValueCreated ? _quarantine.Value : null,
            _threatScanner.IsValueCreated ? _threatScanner.Value : null,
            _softwareUpdater.IsValueCreated ? _softwareUpdater.Value : null,
            _diskOptimizer.IsValueCreated ? _diskOptimizer.Value : null,
            _fileRecovery.IsValueCreated ? _fileRecovery.Value : null,
            _emptyFolderFinder.IsValueCreated ? _emptyFolderFinder.Value : null,
            _appInstaller.IsValueCreated ? _appInstaller.Value : null,
            _onboarding.IsValueCreated ? _onboarding.Value : null,
        ];

        foreach (var child in children)
        {
            if (child is IExperienceModeAware modeAware)
                modeAware.SetExperienceMode(isAdvancedMode);
        }
    }
}
