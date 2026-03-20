using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the "Shadow" Installation Monitor.
/// Manages snapshot before/after comparisons during software installation.
/// </summary>
public partial class InstallMonitorViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isMonitoring;
    [ObservableProperty] private string _statusMessage = "Enter a program name and click 'Start Monitoring' before installing.";
    [ObservableProperty] private string _programLabel = string.Empty;
    [ObservableProperty] private string? _activeSnapshotId;
    [ObservableProperty] private bool _isDryRun;

    // Snapshot results
    [ObservableProperty] private bool _hasDelta;
    [ObservableProperty] private int _newRegistryKeysCount;
    [ObservableProperty] private int _newFilesCount;
    [ObservableProperty] private long _newFileSizeBytes;
    [ObservableProperty] private int _newDirectoriesCount;

    // Saved snapshots
    [ObservableProperty] private ObservableCollection<SnapshotEntry> _savedSnapshots = [];
    [ObservableProperty] private SnapshotEntry? _selectedSnapshot;

    public string FormattedNewFileSize => FormatHelper.FormatBytes(NewFileSizeBytes);

    public InstallMonitorViewModel()
    {
        _ = LoadSnapshotsAsync().ContinueWith(t =>
        {
            if (t.Exception != null)
                DiagnosticLogger.Warn("InstallMonitorViewModel", "LoadSnapshotsAsync failed", t.Exception.InnerException ?? t.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Takes a "Before" snapshot and enters monitoring mode.
    /// User should install the software, then click "Stop Monitoring."
    /// </summary>
    [RelayCommand]
    private async Task StartMonitoringAsync()
    {
        if (string.IsNullOrWhiteSpace(ProgramLabel))
        {
            StatusMessage = "Please enter a program name to monitor.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Taking 'Before' snapshot for {ProgramLabel}...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var (snapshotId, _) = await InstallMonitorService.TakeSnapshotAsync(
                ProgramLabel, dryRun: IsDryRun, progress: progress);

            ActiveSnapshotId = snapshotId;
            IsMonitoring = true;
            StatusMessage = $"Monitoring active for '{ProgramLabel}'. Install your software now, then click 'Stop Monitoring'.";
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

    /// <summary>
    /// Takes the "After" snapshot, compares with "Before", and generates the delta.
    /// </summary>
    [RelayCommand]
    private async Task StopMonitoringAsync()
    {
        if (ActiveSnapshotId == null)
        {
            StatusMessage = "No active monitoring session.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Taking 'After' snapshot and comparing...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var delta = await InstallMonitorService.CompareAndGenerateDeltaAsync(
                ActiveSnapshotId, dryRun: IsDryRun, progress: progress);

            NewRegistryKeysCount = delta.NewRegistryKeys.Count;
            NewFilesCount = delta.NewFiles.Count;
            NewFileSizeBytes = delta.TotalNewFileSizeBytes;
            NewDirectoriesCount = delta.NewDirectories.Count;
            HasDelta = true;

            OnPropertyChanged(nameof(FormattedNewFileSize));

            IsMonitoring = false;
            ActiveSnapshotId = null;

            StatusMessage = $"Installation tracked: {delta.NewRegistryKeys.Count} registry keys, " +
                          $"{delta.NewFiles.Count} files ({FormatHelper.FormatBytes(delta.TotalNewFileSizeBytes)}), " +
                          $"{delta.NewDirectories.Count} directories. Report saved.";

            await LoadSnapshotsAsync();
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

    /// <summary>
    /// Cancels the current monitoring session without generating a delta.
    /// </summary>
    [RelayCommand]
    private void CancelMonitoring()
    {
        if (ActiveSnapshotId != null)
        {
            InstallMonitorService.DeleteSnapshot(ActiveSnapshotId);
            ActiveSnapshotId = null;
        }

        IsMonitoring = false;
        StatusMessage = "Monitoring cancelled.";
    }

    /// <summary>
    /// Loads saved snapshots from disk.
    /// </summary>
    [RelayCommand]
    private async Task LoadSnapshotsAsync()
    {
        try
        {
            var snapshots = await InstallMonitorService.ListSnapshotsAsync();
            SavedSnapshots = new ObservableCollection<SnapshotEntry>(
                snapshots.Select(s => new SnapshotEntry
                {
                    Id = s.Id,
                    Label = s.Label,
                    Timestamp = s.Timestamp,
                    HasDelta = s.HasDelta
                }));
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("InstallMonitorVM", "Failed to load snapshots", ex);
        }
    }

    [RelayCommand]
    private async Task DeleteSnapshotAsync()
    {
        if (SelectedSnapshot == null) return;

        InstallMonitorService.DeleteSnapshot(SelectedSnapshot.Id);
        StatusMessage = $"Snapshot '{SelectedSnapshot.Label}' deleted.";
        await LoadSnapshotsAsync();
    }


}

/// <summary>
/// Represents a saved snapshot entry for display.
/// </summary>
public partial class SnapshotEntry : ObservableObject
{
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _label = string.Empty;
    [ObservableProperty] private DateTime _timestamp;
    [ObservableProperty] private bool _hasDelta;

    public string FormattedTimestamp => Timestamp.ToString("yyyy-MM-dd HH:mm");
}
