using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using System.Management;
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
    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private string _statusBarText = "AuraClean — Ready";
    [ObservableProperty] private DateTime _lastCleanedDate;
    [ObservableProperty] private bool _isHealthCheckRunning;
    [ObservableProperty] private string _healthCheckProgress = string.Empty;
    [ObservableProperty] private int _healthCheckStep;
    [ObservableProperty] private int _healthCheckTotalSteps = 4;
    [ObservableProperty] private string _healthCheckSummary = string.Empty;

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

    public MainViewModel()
    {
        IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);

        IsContextMenuInstalled = ContextMenuService.IsContextMenuInstalled();
        ContextMenuStatus = IsContextMenuInstalled ? "Installed" : "Not installed";

        // Load last cleaned date from settings (fallback: never)
        var settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AuraClean");
        var settingsFile = Path.Combine(settingsDir, "last_cleaned.txt");
        if (File.Exists(settingsFile) &&
            DateTime.TryParse(File.ReadAllText(settingsFile), out var lastCleaned))
        {
            LastCleanedDate = lastCleaned;
        }

        UpdateHealthScore();
        _ = LoadSystemInfoAsync().ContinueWith(t =>
        {
            if (t.Exception != null)
                DiagnosticLogger.Warn("MainViewModel", "LoadSystemInfoAsync failed", t.Exception.InnerException ?? t.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Loads system information (OS, CPU, RAM, uptime) on a background thread.
    /// </summary>
    private async Task LoadSystemInfoAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                // OS info
                OsName = $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version}";
                try
                {
                    using var mos = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem");
                    foreach (var obj in mos.Get())
                    {
                        OsName = obj["Caption"]?.ToString()?.Trim() ?? OsName;
                        break;
                    }
                }
                catch (Exception ex) { DiagnosticLogger.Warn("MainViewModel", "WMI OS query failed", ex); }

                // CPU info
                try
                {
                    using var mos = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                    foreach (var obj in mos.Get())
                    {
                        CpuName = obj["Name"]?.ToString()?.Trim() ?? "Unknown";
                        break;
                    }
                }
                catch { CpuName = $"{Environment.ProcessorCount} cores"; }

                // RAM
                try
                {
                    var gcInfo = GC.GetGCMemoryInfo();
                    long totalBytes = gcInfo.TotalAvailableMemoryBytes;
                    TotalRam = totalBytes switch
                    {
                        < 1_073_741_824 => $"{totalBytes / 1_048_576.0:F0} MB",
                        _ => $"{totalBytes / 1_073_741_824.0:F1} GB"
                    };
                }
                catch { TotalRam = "N/A"; }

                // Uptime
                var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
                SystemUptime = uptime.Days > 0
                    ? $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m"
                    : $"{uptime.Hours}h {uptime.Minutes}m";
            }
            catch (Exception ex) { DiagnosticLogger.Warn("MainViewModel", "LoadSystemInfoAsync failed", ex); }
        });
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
    }

    [RelayCommand]
    private void NavigateTo(string viewName)
    {
        CurrentViewName = viewName;
    }

    [RelayCommand]
    private async Task QuickAnalyzeAsync()
    {
        StatusBarText = "Running quick analysis...";
        await Cleaner.AnalyzeCommand.ExecuteAsync(null);
        UpdateHealthScore();
        StatusBarText = "Analysis complete.";
    }

    [RelayCommand]
    private async Task QuickCleanAsync()
    {
        StatusBarText = "Running cleanup...";
        await Cleaner.CleanSelectedCommand.ExecuteAsync(null);
        UpdateHealthScore();
        SaveLastCleanedDate();
        StatusBarText = "Cleanup complete.";
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
            await Cleaner.AnalyzeCommand.ExecuteAsync(null);

            if (Cleaner.TotalJunkSize > 0)
            {
                int junkPenalty = (int)Math.Min(30, Cleaner.TotalJunkSize / (100.0 * 1024 * 1024) * 5);
                score -= junkPenalty;
                issues.Add($"Junk: {Cleaner.FormattedTotalSize} found");
            }

            // Step 2: Threat Scan
            HealthCheckStep = 2;
            HealthCheckProgress = "Step 2/4 — Scanning for threats...";
            ThreatScanner.SelectedScanMode = ScanMode.Quick;
            await ThreatScanner.StartScanCommand.ExecuteAsync(null);

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

            // Step 3: Startup Analysis
            HealthCheckStep = 3;
            HealthCheckProgress = "Step 3/4 — Analyzing startup items...";
            var startupEntries = await StartupManagerService.GetStartupEntriesAsync();
            int highImpactStartup = startupEntries.Count(e => e.IsEnabled &&
                e.Impact == StartupManagerService.StartupImpact.High);

            if (highImpactStartup > 5)
            {
                score -= Math.Min(15, (highImpactStartup - 5) * 3);
                issues.Add($"Startup: {highImpactStartup} high-impact items");
            }

            // Step 4: Browser Privacy
            HealthCheckStep = 4;
            HealthCheckProgress = "Step 4/4 — Checking browser privacy...";
            await BrowserCleaner.ScanBrowsersCommand.ExecuteAsync(null);

            long browserJunk = BrowserCleaner.TotalSizeBytes;
            if (browserJunk > 100 * 1024 * 1024) // > 100 MB
            {
                score -= Math.Min(10, (int)(browserJunk / (100.0 * 1024 * 1024)) * 3);
                issues.Add($"Browser: {FormatHelper.FormatBytes(browserJunk)} of tracking data");
            }

            // Cleanliness factor
            if (LastCleanedDate == default)
                score -= 10;
            else if ((DateTime.Now - LastCleanedDate).TotalDays > 30)
                score -= 5;

            score = Math.Clamp(score, 0, 100);
            SystemHealthScore = score;

            // Build summary
            if (issues.Count == 0)
            {
                HealthCheckSummary = "Your system is in excellent condition. No issues detected.";
            }
            else
            {
                HealthCheckSummary = $"Found {issues.Count} area(s) to improve: {string.Join(" | ", issues)}";
            }

            HealthCheckProgress = "Health check complete.";
            StatusBarText = $"Health check complete — Score: {score}/100";

            NotificationService.ShowSuccess("Health Check Complete",
                $"System health score: {score}/100. {(issues.Count > 0 ? $"{issues.Count} issue(s) found." : "No issues.")}");
        }
        catch (Exception ex)
        {
            HealthCheckProgress = $"Error during health check: {ex.Message}";
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
            // Launch Windows System Restore UI so user can revert to the pre-cleanup restore point
            var psi = new System.Diagnostics.ProcessStartInfo("rstrui.exe")
            {
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            StatusBarText = "System Restore opened — select the AuraClean restore point to undo.";
        }
        catch (Exception ex)
        {
            StatusBarText = $"Could not open System Restore: {ex.Message}";
            DiagnosticLogger.Warn("MainViewModel", "Failed to launch System Restore", ex);
        }
    }

    [RelayCommand]
    private void ToggleContextMenu()
    {
        if (IsContextMenuInstalled)
        {
            var (success, msg, _) = ContextMenuService.UninstallContextMenu();
            ContextMenuStatus = success ? "Removed" : msg;
        }
        else
        {
            var (success, msg, _) = ContextMenuService.InstallContextMenu();
            ContextMenuStatus = success ? "Installed" : msg;
        }
        IsContextMenuInstalled = ContextMenuService.IsContextMenuInstalled();
        StatusBarText = ContextMenuStatus;
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
        // Base score: 100
        int score = 100;

        // Deduct points based on junk found
        if (Cleaner.TotalJunkSize > 0)
        {
            // Every 100MB of junk deducts ~5 points, capped at 50 points
            int junkPenalty = (int)Math.Min(50, Cleaner.TotalJunkSize / (100 * 1024 * 1024) * 5);
            score -= junkPenalty;
        }

        // Deduct if not cleaned recently
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

    private void SaveLastCleanedDate()
    {
        LastCleanedDate = DateTime.Now;
        try
        {
            var settingsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AuraClean");
            Directory.CreateDirectory(settingsDir);
            File.WriteAllText(Path.Combine(settingsDir, "last_cleaned.txt"),
                DateTime.Now.ToString("o"));
        }
        catch (Exception ex) { DiagnosticLogger.Warn("MainViewModel", "Failed to persist last-cleaned date", ex); }
    }
}
