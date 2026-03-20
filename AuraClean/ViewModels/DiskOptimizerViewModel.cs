using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AuraClean.ViewModels;

public partial class DiskOptimizerViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Select drives to optimize.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _currentOperation = string.Empty;

    [ObservableProperty]
    private ObservableCollection<DriveOptimizeEntry> _drives = [];

    private CancellationTokenSource? _cts;

    public DiskOptimizerViewModel()
    {
        _ = LoadDrivesAsync();
    }

    private async Task LoadDrivesAsync()
    {
        try
        {
            var driveInfos = await DiskOptimizerService.GetDrivesAsync();
            Drives = new ObservableCollection<DriveOptimizeEntry>(
                driveInfos.Select(d => new DriveOptimizeEntry
                {
                    DriveLetter = d.DriveLetter,
                    VolumeLabel = d.VolumeLabel,
                    MediaType = d.MediaType,
                    TotalBytes = d.TotalBytes,
                    FreeBytes = d.FreeBytes,
                    FileSystem = d.FileSystem,
                    IsSelected = true,
                    Status = DiskOptimizerService.OptimizeStatus.Pending,
                    StatusMessage = "Ready"
                }));
            HasResults = Drives.Count > 0;
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't load drive information. Please try again.";
            DiagnosticLogger.Error("DiskOptimizer", "Failed to load drives", ex);
        }
    }

    [RelayCommand]
    private async Task AnalyzeDrivesAsync()
    {
        var selected = Drives.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Please select at least one drive.";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        StatusMessage = "Analyzing drives...";

        try
        {
            foreach (var drive in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                drive.Status = DiskOptimizerService.OptimizeStatus.Running;
                drive.StatusMessage = "Analyzing...";
                CurrentOperation = $"Analyzing {drive.DriveLetter}...";

                var (fragmentPercent, report) = await DiskOptimizerService.AnalyzeDriveAsync(
                    drive.DriveLetter, _cts.Token);

                drive.FragmentPercent = fragmentPercent;
                drive.Status = DiskOptimizerService.OptimizeStatus.Pending;
                drive.StatusMessage = drive.MediaType == DiskOptimizerService.DriveMediaType.SSD
                    ? "SSD — optimization recommended"
                    : fragmentPercent > 5
                        ? $"{fragmentPercent}% fragmented — defrag recommended"
                        : $"{fragmentPercent}% fragmented — OK";
            }

            StatusMessage = "Analysis complete. Select drives and click Optimize.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Analysis cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during analysis. Please try again.";
            DiagnosticLogger.Error("DiskOptimizerVM", "AnalyzeDrivesAsync failed", ex);
        }
        finally
        {
            IsBusy = false;
            CurrentOperation = string.Empty;
        }
    }

    [RelayCommand]
    private async Task OptimizeDrivesAsync()
    {
        var selected = Drives.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "Please select at least one drive.";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;

        int success = 0, failed = 0;

        try
        {
            foreach (var drive in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                drive.Status = DiskOptimizerService.OptimizeStatus.Running;
                drive.StatusMessage = drive.MediaType == DiskOptimizerService.DriveMediaType.SSD
                    ? "Optimizing..."
                    : "Defragmenting...";

                CurrentOperation = $"Optimizing {drive.DriveLetter} ({drive.MediaTypeLabel})...";
                StatusMessage = CurrentOperation;

                var progress = new Progress<string>(msg => drive.StatusMessage = msg);

                var (ok, output) = await DiskOptimizerService.OptimizeDriveAsync(
                    drive.DriveLetter, progress, _cts.Token);

                if (ok)
                {
                    drive.Status = DiskOptimizerService.OptimizeStatus.Success;
                    drive.StatusMessage = "Optimized successfully";
                    success++;
                }
                else
                {
                    drive.Status = DiskOptimizerService.OptimizeStatus.Failed;
                    drive.StatusMessage = "Optimization failed — run as Administrator";
                    failed++;
                }
            }

            StatusMessage = $"Done — {success} optimized, {failed} failed.";
            if (success > 0)
            {
                NotificationService.ShowSuccess("Disk Optimization Complete",
                    $"{success} drive(s) optimized successfully.");
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Optimization cancelled.";
            foreach (var d in selected.Where(d => d.Status == DiskOptimizerService.OptimizeStatus.Running))
            {
                d.Status = DiskOptimizerService.OptimizeStatus.Pending;
                d.StatusMessage = "Cancelled";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during optimization. Please try again.";
            DiagnosticLogger.Error("DiskOptimizerVM", "OptimizeDrivesAsync failed", ex);
        }
        finally
        {
            IsBusy = false;
            CurrentOperation = string.Empty;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task RefreshDrivesAsync()
    {
        await LoadDrivesAsync();
        StatusMessage = "Drive list refreshed.";
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var d in Drives) d.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var d in Drives) d.IsSelected = false;
    }
}

public partial class DriveOptimizeEntry : ObservableObject
{
    [ObservableProperty] private string _driveLetter = string.Empty;
    [ObservableProperty] private string _volumeLabel = string.Empty;
    [ObservableProperty] private DiskOptimizerService.DriveMediaType _mediaType;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private long _freeBytes;
    [ObservableProperty] private string _fileSystem = string.Empty;
    [ObservableProperty] private int _fragmentPercent;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private DiskOptimizerService.OptimizeStatus _status;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public string MediaTypeLabel => MediaType switch
    {
        DiskOptimizerService.DriveMediaType.SSD => "SSD",
        DiskOptimizerService.DriveMediaType.HDD => "HDD",
        _ => "Unknown"
    };

    public string OptimizeAction => MediaType switch
    {
        DiskOptimizerService.DriveMediaType.SSD => "TRIM / Optimize",
        DiskOptimizerService.DriveMediaType.HDD => "Defragment",
        _ => "Optimize"
    };

    public string FormattedTotal => FormatHelper.FormatBytes(TotalBytes);
    public string FormattedFree => FormatHelper.FormatBytes(FreeBytes);

    public string DisplayName => $"{VolumeLabel} ({DriveLetter})";
}
