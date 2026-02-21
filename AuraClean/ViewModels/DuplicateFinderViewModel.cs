using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the Duplicate File Finder view.
/// </summary>
public partial class DuplicateFinderViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Select a directory and click Scan to find duplicate files.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _selectedPath = string.Empty;
    [ObservableProperty] private double _progressPercent;

    // Options
    [ObservableProperty] private int _minSizeKB = 1;
    [ObservableProperty] private int _maxSizeMB = 500;
    [ObservableProperty] private bool _recursive = true;
    [ObservableProperty] private string _extensionFilter = string.Empty;

    // Results
    [ObservableProperty] private ObservableCollection<DuplicateFinderService.DuplicateGroup> _duplicateGroups = [];
    [ObservableProperty] private int _totalGroupCount;
    [ObservableProperty] private int _totalDuplicateCount;
    [ObservableProperty] private long _totalWastedBytes;
    [ObservableProperty] private int _totalFilesScanned;
    [ObservableProperty] private string _scanDuration = string.Empty;

    // Drives for quick selection
    [ObservableProperty] private ObservableCollection<string> _quickPaths = [];

    public string FormattedWasted => FormatHelper.FormatBytes(TotalWastedBytes);

    private CancellationTokenSource? _scanCts;

    public DuplicateFinderViewModel()
    {
        // Populate quick paths
        var paths = new List<string>();
        paths.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        paths.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        paths.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            paths.Add(drive.Name);
        QuickPaths = new ObservableCollection<string>(paths);

        if (QuickPaths.Count > 0)
            SelectedPath = QuickPaths[0];
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPath) || !Directory.Exists(SelectedPath))
        {
            StatusMessage = "Please select a valid directory.";
            return;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        IsBusy = true;
        HasResults = false;
        DuplicateGroups.Clear();
        StatusMessage = "Scanning for duplicates...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);

            // Parse extension filter
            string[]? extensions = null;
            if (!string.IsNullOrWhiteSpace(ExtensionFilter))
            {
                extensions = ExtensionFilter.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.StartsWith('.') ? e : $".{e}")
                    .ToArray();
            }

            var result = await DuplicateFinderService.ScanForDuplicatesAsync(
                SelectedPath,
                minSizeBytes: MinSizeKB * 1024L,
                maxSizeMB: MaxSizeMB,
                fileExtensions: extensions,
                recursive: Recursive,
                progress: progress,
                ct: _scanCts.Token);

            DuplicateGroups = new ObservableCollection<DuplicateFinderService.DuplicateGroup>(result.Groups);
            TotalGroupCount = result.Groups.Count;
            TotalDuplicateCount = result.TotalDuplicateFiles;
            TotalWastedBytes = result.TotalWastedBytes;
            TotalFilesScanned = result.TotalFilesScanned;
            ScanDuration = $"{result.ScanDuration.TotalSeconds:F1}s";
            HasResults = result.Groups.Count > 0;

            OnPropertyChanged(nameof(FormattedWasted));

            StatusMessage = result.Groups.Count > 0
                ? $"Found {result.Groups.Count:N0} duplicate groups ({result.TotalDuplicateFiles:N0} duplicate files, " +
                  $"{FormatHelper.FormatBytes(result.TotalWastedBytes)} wasted) in {ScanDuration}."
                : $"No duplicates found among {result.TotalFilesScanned:N0} files.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
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
    private void CancelScan()
    {
        _scanCts?.Cancel();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (!HasResults) return;

        var selectedCount = DuplicateGroups.SelectMany(g => g.Files)
            .Count(f => f.IsSelected && !f.IsKeep);

        if (selectedCount == 0)
        {
            StatusMessage = "No files selected for deletion.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Deleting {selectedCount} duplicate files...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var (deleted, failed, bytesFreed) = await DuplicateFinderService.DeleteDuplicatesAsync(
                DuplicateGroups, progress: progress);

            StatusMessage = $"Deleted {deleted} files ({FormatHelper.FormatBytes(bytesFreed)} freed). " +
                           (failed > 0 ? $"{failed} failed." : "");

            // Refresh results — remove deleted files from groups
            foreach (var group in DuplicateGroups.ToList())
            {
                var toRemove = group.Files.Where(f => f.IsSelected && !f.IsKeep && !System.IO.File.Exists(f.FullPath)).ToList();
                foreach (var file in toRemove)
                    group.Files.Remove(file);

                if (group.Files.Count <= 1)
                    DuplicateGroups.Remove(group);
            }

            TotalGroupCount = DuplicateGroups.Count;
            TotalDuplicateCount = DuplicateGroups.Sum(g => g.Count - 1);
            TotalWastedBytes = DuplicateGroups.Sum(g => g.WastedBytes);
            HasResults = DuplicateGroups.Count > 0;
            OnPropertyChanged(nameof(FormattedWasted));
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
    private void SelectAllDuplicates()
    {
        foreach (var group in DuplicateGroups)
        {
            bool first = true;
            foreach (var file in group.Files)
            {
                if (first) { file.IsKeep = true; file.IsSelected = false; first = false; }
                else { file.IsKeep = false; file.IsSelected = true; }
            }
        }
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var group in DuplicateGroups)
            foreach (var file in group.Files)
                file.IsSelected = false;
    }

    [RelayCommand]
    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (System.IO.File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", path);
        }
        catch { }
    }
}
