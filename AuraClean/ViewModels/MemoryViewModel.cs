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
    [ObservableProperty] private string _statusMessage = "Ready to optimize memory usage.";
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

    /// <summary>Dry run never actually trims anything, so the result card must say so, not claim a byte figure.</summary>
    public string FreedLabel => IsDryRun ? "Memory Freed (preview — no changes made)" : "Memory Freed";
    public string TrimmedLabel => IsDryRun ? "Processes eligible" : "Processes trimmed";

    partial void OnIsDryRunChanged(bool value)
    {
        OnPropertyChanged(nameof(FreedLabel));
        OnPropertyChanged(nameof(TrimmedLabel));
    }

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
        MemoryManagerService.MemorySnapshot snapshot;
        try
        {
            snapshot = await MemoryManagerService.GetMemorySnapshotAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("MemoryVM", "Memory snapshot failed", ex);
            return;
        }

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
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = IsDryRun ? "[Preview] Analyzing memory..." : "Boosting memory...";
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
                ? $"[Preview] No changes made. {result.ProcessesTrimmed} process(es) are eligible to be trimmed — " +
                  "the actual amount freed depends on live system state and is only known after running a real boost."
                : $"Freed {FormatHelper.FormatBytes(result.MemoryFreedBytes)}! Trimmed {result.ProcessesTrimmed} processes" +
                  (result.StandbyListPurged ? ", cleared cached memory." : ".");

            OnPropertyChanged(nameof(FormattedFreed));

            if (!IsDryRun)
                CleanupHistoryService.Record(CleanupOperationType.MemoryBoost, result.ProcessesTrimmed,
                    result.MemoryFreedBytes, result.StandbyListPurged ? "Working sets trimmed, standby list purged" : "Working sets trimmed");
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during memory optimization. Please try again.";
            DiagnosticLogger.Error("MemoryVM", "BoostMemoryAsync failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

}
