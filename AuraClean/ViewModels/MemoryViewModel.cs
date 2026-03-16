using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the Memory Optimizer ("One-Click Boost") feature.
/// </summary>
public partial class MemoryViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Click Boost to optimize memory.";
    [ObservableProperty] private bool _isDryRun;

    // Memory stats
    [ObservableProperty] private long _totalRamBytes;
    [ObservableProperty] private long _usedRamBytes;
    [ObservableProperty] private long _availableRamBytes;
    [ObservableProperty] private double _usagePercent;

    // Last boost results
    [ObservableProperty] private long _lastFreedBytes;
    [ObservableProperty] private int _lastTrimmedCount;
    [ObservableProperty] private bool _hasBoostResult;
    [ObservableProperty] private bool _standbyPurged;

    public string FormattedTotal => FormatHelper.FormatBytes(TotalRamBytes);
    public string FormattedUsed => FormatHelper.FormatBytes(UsedRamBytes);
    public string FormattedAvailable => FormatHelper.FormatBytes(AvailableRamBytes);
    public string FormattedFreed => FormatHelper.FormatBytes(LastFreedBytes);

    public MemoryViewModel()
    {
        _ = RefreshStatsAsync().ContinueWith(t =>
        {
            if (t.Exception != null)
                DiagnosticLogger.Warn("MemoryViewModel", "RefreshStatsAsync failed", t.Exception.InnerException ?? t.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    [RelayCommand]
    private async Task RefreshStatsAsync()
    {
        var snapshot = await MemoryManagerService.GetMemorySnapshotAsync();
        TotalRamBytes = snapshot.TotalPhysicalBytes;
        UsedRamBytes = snapshot.UsedBytes;
        AvailableRamBytes = snapshot.AvailableBytes;
        UsagePercent = snapshot.UsagePercent;

        OnPropertyChanged(nameof(FormattedTotal));
        OnPropertyChanged(nameof(FormattedUsed));
        OnPropertyChanged(nameof(FormattedAvailable));
    }

    [RelayCommand]
    private async Task BoostMemoryAsync()
    {
        IsBusy = true;
        StatusMessage = IsDryRun ? "[DRY RUN] Analyzing memory..." : "Boosting memory...";
        HasBoostResult = false;

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var result = await MemoryManagerService.BoostMemoryAsync(
                purgeStandbyList: true,
                dryRun: IsDryRun,
                progress: progress);

            LastFreedBytes = result.MemoryFreedBytes;
            LastTrimmedCount = result.ProcessesTrimmed;
            StandbyPurged = result.StandbyListPurged;
            HasBoostResult = true;

            await RefreshStatsAsync();

            StatusMessage = IsDryRun
                ? $"[DRY RUN] Estimated {FormatHelper.FormatBytes(result.MemoryFreedBytes)} reclaimable from {result.ProcessesTrimmed} processes."
                : $"Freed {FormatHelper.FormatBytes(result.MemoryFreedBytes)}! Trimmed {result.ProcessesTrimmed} processes" +
                  (result.StandbyListPurged ? ", purged standby list." : ".");

            OnPropertyChanged(nameof(FormattedFreed));
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

}
