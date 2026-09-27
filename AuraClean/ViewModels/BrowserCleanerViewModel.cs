using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the Browser &amp; Privacy Deep Clean view.
/// </summary>
public partial class BrowserCleanerViewModel : ObservableObject
{
    private readonly object _browserResultsLock = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Ready to scan browsers and cached data.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _isDryRun;

    [ObservableProperty]
    private ObservableCollection<BrowserResultEntry> _browserResults = [];

    public BrowserCleanerViewModel()
    {
        BindingOperations.EnableCollectionSynchronization(BrowserResults, _browserResultsLock);
    }

    partial void OnBrowserResultsChanged(ObservableCollection<BrowserResultEntry> value)
    {
        BindingOperations.EnableCollectionSynchronization(value, _browserResultsLock);
    }

    [ObservableProperty] private long _totalSizeBytes;
    [ObservableProperty] private long _totalSavingsBytes;
    [ObservableProperty] private int _totalItemCount;

    // Cleaning options
    [ObservableProperty] private bool _cleanCache = true;
    [ObservableProperty] private bool _vacuumDatabases = true;
    [ObservableProperty] private bool _cleanTracking = true;
    [ObservableProperty] private bool _flushDns = true;

    public string FormattedTotalSize => FormatHelper.FormatBytes(TotalSizeBytes);
    public string FormattedTotalSavings => FormatHelper.FormatBytes(TotalSavingsBytes);

    [RelayCommand]
    private async Task ScanBrowsersAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = "Detecting installed browsers...";
        BrowserResults.Clear();
        HasResults = false;
        TotalSizeBytes = 0;
        TotalSavingsBytes = 0;
        TotalItemCount = 0;

        try
        {
            var browsers = BrowserCleanerService.DetectBrowsers();
            if (browsers.Count == 0)
            {
                StatusMessage = "No supported browsers found. Chromium and Firefox-based browsers are supported.";
                return;
            }

            long totalSize = 0;
            long totalSavings = 0;
            int totalItems = 0;

            foreach (var browser in browsers)
            {
                var progress = new Progress<string>(msg => StatusMessage = msg);
                var result = await BrowserCleanerService.ScanBrowserAsync(browser, progress: progress);

                BrowserResults.Add(new BrowserResultEntry
                {
                    BrowserName = result.BrowserName,
                    ProfilePath = result.ProfilePath,
                    ScanResult = result,
                    TotalSize = result.TotalSizeBytes,
                    CacheItemCount = result.CacheItems.Count,
                    VacuumTargetCount = result.VacuumTargets.Count,
                    TrackingItemCount = result.TrackingItems.Count,
                    IsSelected = true
                });

                totalSize += result.TotalSizeBytes;
                totalSavings += result.CacheItems.Sum(i => i.SizeBytes) +
                                (IsAdvancedMode ? result.TrackingItems.Sum(i => i.SizeBytes) : 0);
                totalItems += result.CacheItems.Count + (IsAdvancedMode ? result.TrackingItems.Count : 0);
            }

            TotalSizeBytes = totalSize;
            TotalSavingsBytes = totalSavings;
            TotalItemCount = totalItems;
            HasResults = true;

            OnPropertyChanged(nameof(FormattedTotalSize));
            OnPropertyChanged(nameof(FormattedTotalSavings));

            StatusMessage = $"Found {browsers.Count} browser(s): {FormatHelper.FormatBytes(totalSavings)} reclaimable.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during the browser scan. Please try again.";
            DiagnosticLogger.Error("BrowserCleanerVM", "Browser scan failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CleanSelectedAsync()
    {
        if (!HasResults || IsBusy) return;

        var selectedBrowsers = BrowserResults.Where(b => b.IsSelected).ToList();
        if (selectedBrowsers.Count == 0)
        {
            StatusMessage = "No browsers selected for cleaning.";
            return;
        }

        bool dryRun = IsDryRun || SafetyPromptService.IsDryRunEnabled();
        bool cleanTracking = IsAdvancedMode && CleanTracking;
        bool vacuum = IsAdvancedMode && VacuumDatabases;

        if (!dryRun)
        {
            var scope = cleanTracking
                ? "caches and site data (you will be signed out of websites)"
                : "caches";
            if (!SafetyPromptService.ConfirmDestructiveAction(
                    $"Clean {scope} for {selectedBrowsers.Count} browser(s)? Close the browsers first.",
                    "Confirm browser cleanup"))
            {
                StatusMessage = "Browser cleanup cancelled.";
                return;
            }
        }

        IsBusy = true;
        long totalFreed = 0;
        int totalDeleted = 0, cleanedBrowsers = 0;
        var allErrors = new List<string>();
        var blocked = new List<string>();

        try
        {
            foreach (var entry in selectedBrowsers)
            {
                var progress = new Progress<string>(msg => StatusMessage = msg);
                StatusMessage = $"Cleaning {entry.BrowserName}...";

                var result = await BrowserCleanerService.CleanBrowserAsync(
                    entry.ScanResult,
                    cleanCache: CleanCache,
                    vacuumDatabases: vacuum,
                    cleanTracking: cleanTracking,
                    dryRun: dryRun,
                    progress: progress);

                if (!result.Success)
                {
                    blocked.Add(result.Message);
                    continue;
                }

                cleanedBrowsers++;
                totalFreed += result.BytesFreed;
                totalDeleted += result.Deleted;
                allErrors.AddRange(result.Errors);
            }

            StatusMessage = dryRun
                ? $"[Preview] Would free {FormatHelper.FormatBytes(totalFreed)} across {cleanedBrowsers} browser(s)."
                : $"Cleaned {totalDeleted} items, freed {FormatHelper.FormatBytes(totalFreed)} across {cleanedBrowsers} browser(s)." +
                  (allErrors.Count > 0 ? $" {allErrors.Count} item(s) were in use." : "");

            if (blocked.Count > 0)
                StatusMessage += " " + string.Join(" ", blocked);

            if (IsAdvancedMode && FlushDns && !dryRun)
            {
                var (dnsOk, dnsMsg) = await BrowserCleanerService.FlushDnsCacheAsync();
                StatusMessage += dnsOk ? " Network cache cleared." : $" DNS: {dnsMsg}";
            }

            if (!dryRun && totalFreed > 0)
            {
                NotificationService.ShowSuccess("Browser Cleanup Complete",
                    $"Freed {FormatHelper.FormatBytes(totalFreed)} across {cleanedBrowsers} browser(s).");
                CleanupHistoryService.Record(CleanupOperationType.BrowserClean, totalDeleted, totalFreed,
                    string.Join(", ", selectedBrowsers.Select(b => b.BrowserName)) +
                    (cleanTracking ? " (including site data)" : string.Empty));
            }

            foreach (var error in allErrors.Take(20))
                DiagnosticLogger.Warn("BrowserCleanerVM", error);
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during browser cleanup. Some items may not have been removed.";
            DiagnosticLogger.Error("BrowserCleanerVM", "Browser cleanup failed", ex);
        }
        finally
        {
            IsBusy = false;
        }

        // Sizes are stale after cleaning — rescan so the numbers are accurate.
        if (!dryRun && cleanedBrowsers > 0)
        {
            var summary = StatusMessage;
            await ScanBrowsersAsync();
            StatusMessage = summary;
        }
    }
}

/// <summary>
/// Display entry for a browser scan result.
/// </summary>
public partial class BrowserResultEntry : ObservableObject
{
    [ObservableProperty] private string _browserName = string.Empty;
    [ObservableProperty] private string _profilePath = string.Empty;
    [ObservableProperty] private long _totalSize;
    [ObservableProperty] private int _cacheItemCount;
    [ObservableProperty] private int _vacuumTargetCount;
    [ObservableProperty] private int _trackingItemCount;
    [ObservableProperty] private bool _isSelected = true;

    public BrowserCleanerService.BrowserScanResult ScanResult { get; set; } = null!;

    public string FormattedSize => TotalSize switch
    {
        < 1024 => $"{TotalSize} B",
        < 1_048_576 => $"{TotalSize / 1024.0:F1} KB",
        < 1_073_741_824 => $"{TotalSize / 1_048_576.0:F1} MB",
        _ => $"{TotalSize / 1_073_741_824.0:F2} GB"
    };
}
