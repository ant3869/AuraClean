using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the Browser &amp; Privacy Deep Clean view.
/// </summary>
public partial class BrowserCleanerViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Click Scan to detect browsers and analyze caches.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _isDryRun;

    [ObservableProperty]
    private ObservableCollection<BrowserResultEntry> _browserResults = [];

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
        IsBusy = true;
        StatusMessage = "Detecting installed browsers...";
        BrowserResults.Clear();
        HasResults = false;

        try
        {
            var browsers = BrowserCleanerService.DetectBrowsers();
            if (browsers.Count == 0)
            {
                StatusMessage = "No supported Chromium-based browsers detected.";
                IsBusy = false;
                return;
            }

            long totalSize = 0;
            long totalSavings = 0;
            int totalItems = 0;

            foreach (var browser in browsers)
            {
                var progress = new Progress<string>(msg => StatusMessage = msg);
                var result = await BrowserCleanerService.ScanBrowserAsync(browser, progress: progress);

                var entry = new BrowserResultEntry
                {
                    BrowserName = result.BrowserName,
                    ProfilePath = result.ProfilePath,
                    ScanResult = result,
                    TotalSize = result.TotalSizeBytes,
                    CacheItemCount = result.CacheItems.Count,
                    VacuumTargetCount = result.VacuumTargets.Count,
                    TrackingItemCount = result.TrackingItems.Count,
                    IsSelected = true
                };

                BrowserResults.Add(entry);
                totalSize += result.TotalSizeBytes;
                totalSavings += result.PotentialSavingsBytes;
                totalItems += result.CacheItems.Count + result.TrackingItems.Count;
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
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CleanSelectedAsync()
    {
        if (!HasResults) return;

        IsBusy = true;
        var selectedBrowsers = BrowserResults.Where(b => b.IsSelected).ToList();
        if (selectedBrowsers.Count == 0)
        {
            StatusMessage = "No browsers selected for cleaning.";
            IsBusy = false;
            return;
        }

        long totalFreed = 0;
        int totalDeleted = 0;
        var allErrors = new List<string>();

        foreach (var entry in selectedBrowsers)
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            StatusMessage = $"Cleaning {entry.BrowserName}...";

            var result = await BrowserCleanerService.CleanBrowserAsync(
                entry.ScanResult,
                cleanCache: CleanCache,
                vacuumDatabases: VacuumDatabases,
                cleanTracking: CleanTracking,
                dryRun: IsDryRun,
                progress: progress);

            totalFreed += result.BytesFreed;
            totalDeleted += result.Deleted;
            allErrors.AddRange(result.Errors);

            if (!result.Success)
            {
                StatusMessage = result.Message;
                IsBusy = false;
                return;
            }
        }

        StatusMessage = IsDryRun
            ? $"[DRY RUN] Would free {FormatHelper.FormatBytes(totalFreed)} across {selectedBrowsers.Count} browser(s)."
            : $"Cleaned {totalDeleted} items, freed {FormatHelper.FormatBytes(totalFreed)} across {selectedBrowsers.Count} browser(s)." +
              (allErrors.Count > 0 ? $" {allErrors.Count} error(s)." : "");

        // Flush DNS cache if enabled
        if (FlushDns && !IsDryRun)
        {
            StatusMessage += " Flushing DNS cache...";
            var (dnsOk, dnsMsg) = await BrowserCleanerService.FlushDnsCacheAsync();
            StatusMessage = StatusMessage.Replace(" Flushing DNS cache...", "") +
                            (dnsOk ? " DNS cache flushed." : $" DNS: {dnsMsg}");
        }

        IsBusy = false;
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
