using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the Large File Finder feature.
/// Scans directories for files exceeding a configurable size threshold.
/// </summary>
public partial class LargeFileFinderViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<LargeFileFinderService.LargeFileEntry> _files = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Select a drive or folder and set a minimum file size.";
    [ObservableProperty] private string _scanPath = string.Empty;
    [ObservableProperty] private int _minimumSizeMB = 100;
    [ObservableProperty] private int _progressFilesScanned;
    [ObservableProperty] private int _progressFilesFound;
    [ObservableProperty] private string _progressCurrentDir = string.Empty;
    [ObservableProperty] private LargeFileFinderService.LargeFileEntry? _selectedFile;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private long _selectedSize;
    [ObservableProperty] private bool _isAllSelected;

    public string FormattedSelectedSize => FormatHelper.FormatBytes(SelectedSize);

    // Results summary
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private int _totalFilesScanned;
    [ObservableProperty] private int _resultCount;
    [ObservableProperty] private long _totalSizeFound;
    [ObservableProperty] private int _accessErrors;

    // Drives
    [ObservableProperty] private ObservableCollection<DriveOption> _drives = [];
    [ObservableProperty] private DriveOption? _selectedDrive;

    // Filter
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _filterCategory = "All";
    [ObservableProperty] private ObservableCollection<LargeFileFinderService.LargeFileEntry> _filteredFiles = [];

    public string FormattedTotalSize => FormatHelper.FormatBytes(TotalSizeFound);

    public ObservableCollection<string> Categories { get; } =
    [
        "All", "Video", "Audio", "Archive", "Disk Image",
        "Executable", "Backup / Temp", "Virtual Disk",
        "Database", "Log / Text", "Image (Large)", "Other"
    ];

    public ObservableCollection<int> SizePresets { get; } = [10, 25, 50, 100, 250, 500, 1000];

    private CancellationTokenSource? _cts;

    public LargeFileFinderViewModel()
    {
        var settings = SettingsService.Load();
        MinimumSizeMB = (int)Math.Clamp(settings.DefaultLargeFileSizeMb, 1L, int.MaxValue);

        LoadDrives();

        if (Drives.Count > 0)
        {
            SelectedDrive = Drives[0];
            ScanPath = Drives[0].Path;
        }
    }

    partial void OnSelectedDriveChanged(DriveOption? value)
    {
        if (value != null)
            ScanPath = value.Path;
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnFilterCategoryChanged(string value) => ApplyFilter();

    partial void OnIsAllSelectedChanged(bool value)
    {
        foreach (var file in FilteredFiles)
            file.IsSelected = value;
        UpdateSelectionCount();
    }

    private void LoadDrives()
    {
        var driveList = LargeFileFinderService.GetDrives();
        Drives.Clear();
        foreach (var (name, label, totalSize, freeSpace) in driveList)
        {
            Drives.Add(new DriveOption
            {
                Path = name,
                Label = $"{name} {label}",
                FreeSpace = FormatHelper.FormatBytes(freeSpace),
                TotalSize = FormatHelper.FormatBytes(totalSize),
            });
        }
    }

    private void ApplyFilter()
    {
        var filtered = Files.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            filtered = filtered.Where(f =>
                f.FileName.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                f.Directory.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
        }

        if (FilterCategory != "All")
        {
            filtered = filtered.Where(f => f.Category == FilterCategory);
        }

        FilteredFiles = new ObservableCollection<LargeFileFinderService.LargeFileEntry>(filtered);
        UpdateSelectionCount();
    }

    private void HookSelectionEvents(IEnumerable<LargeFileFinderService.LargeFileEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.SelectionChanged -= OnItemSelectionChanged;
            entry.SelectionChanged += OnItemSelectionChanged;
        }
    }

    private void OnItemSelectionChanged() => UpdateSelectionCount();

    private void UpdateSelectionCount()
    {
        SelectedCount = FilteredFiles.Count(f => f.IsSelected);
        SelectedSize = FilteredFiles.Where(f => f.IsSelected).Sum(f => f.SizeBytes);
        OnPropertyChanged(nameof(FormattedSelectedSize));
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a folder to scan for large files",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            ScanPath = dialog.FolderName;
            SelectedDrive = null;
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;

        if (string.IsNullOrWhiteSpace(ScanPath))
        {
            StatusMessage = "Please select a drive or folder to scan.";
            return;
        }

        if (!Directory.Exists(ScanPath))
        {
            StatusMessage = "The selected path does not exist.";
            return;
        }

        IsBusy = true;
        HasResults = false;
        Files.Clear();
        FilteredFiles.Clear();
        ProgressFilesScanned = 0;
        ProgressFilesFound = 0;

        long minBytes = (long)Math.Max(1, MinimumSizeMB) * 1024 * 1024;
        StatusMessage = $"Scanning {ScanPath} for files ≥ {MinimumSizeMB} MB...";

        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<(int filesScanned, int found, string currentDir)>(p =>
            {
                ProgressFilesScanned = p.filesScanned;
                ProgressFilesFound = p.found;
                ProgressCurrentDir = p.currentDir.Length > 60
                    ? "..." + p.currentDir[^57..]
                    : p.currentDir;
            });

            var result = await LargeFileFinderService.ScanAsync(
                ScanPath, minBytes, 500, false, progress, _cts.Token);

            Files = new ObservableCollection<LargeFileFinderService.LargeFileEntry>(result.Files);
            HookSelectionEvents(Files);
            TotalFilesScanned = result.TotalFilesScanned;
            ResultCount = result.Files.Count;
            TotalSizeFound = result.TotalSizeFound;
            AccessErrors = result.AccessErrors;
            HasResults = true;

            OnPropertyChanged(nameof(FormattedTotalSize));
            ApplyFilter();

            StatusMessage = $"Found {result.Files.Count} files ({FormatHelper.FormatBytes(result.TotalSizeFound)}) " +
                            $"out of {FormatHelper.FormatCount(result.TotalFilesScanned)} scanned. " +
                            (result.AccessErrors > 0 ? $"{result.AccessErrors} access errors." : "");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during the scan. Please try again.";
            DiagnosticLogger.Error("LargeFileFinderVM", "Scan failed", ex);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void OpenFileLocation()
    {
        if (SelectedFile == null) return;

        if (!ShellHelper.RevealInExplorer(SelectedFile.FullPath))
            StatusMessage = "Couldn't open the file location.";
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (IsBusy) return;

        if (!IsAdvancedMode)
        {
            StatusMessage = "Turn on Advanced mode to delete large files. Normal mode is review-only for personal files.";
            return;
        }

        var checkedFiles = FilteredFiles.Where(f => f.IsSelected).ToList();
        if (checkedFiles.Count == 0)
        {
            StatusMessage = "No files selected for deletion.";
            return;
        }

        var blocked = checkedFiles.Where(f => !PathSafety.IsSafeToDeleteFile(f.FullPath, out _)).ToList();
        checkedFiles = checkedFiles.Except(blocked).ToList();
        if (checkedFiles.Count == 0)
        {
            StatusMessage = "The selected files are Windows or system files and can't be deleted here.";
            return;
        }

        var bytes = checkedFiles.Sum(f => f.SizeBytes);
        if (SafetyPromptService.IsDryRunEnabled())
        {
            StatusMessage = $"Dry run: would delete {checkedFiles.Count} file(s) ({FormatHelper.FormatBytes(bytes)}).";
            return;
        }

        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Permanently delete {checkedFiles.Count} selected large file(s) ({FormatHelper.FormatBytes(bytes)})?" +
                (blocked.Count > 0 ? $"\n\n{blocked.Count} system file(s) will be skipped." : string.Empty)))
        {
            StatusMessage = "Large file deletion cancelled.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Deleting {checkedFiles.Count} file(s)...";

        int deleted = 0;
        long freedBytes = 0;
        var failedFiles = new List<string>();

        try
        {
            var outcomes = await Task.Run(() => checkedFiles
                .Select(file => (File: file, Result: LargeFileFinderService.DeleteFile(file.FullPath)))
                .ToList());

            foreach (var (file, (success, message)) in outcomes)
            {
                if (success)
                {
                    freedBytes += file.SizeBytes;
                    Files.Remove(file);
                    deleted++;
                }
                else
                {
                    failedFiles.Add(file.FileName);
                    DiagnosticLogger.Warn("LargeFileFinderVM", $"Failed to delete {file.FullPath}: {message}");
                }
            }

            CleanupHistoryService.Record(CleanupOperationType.LargeFileRemoval, deleted, freedBytes,
                $"Large files in {ScanPath}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("LargeFileFinderVM", "DeleteSelectedAsync failed", ex);
        }
        finally
        {
            TotalSizeFound = Math.Max(0, TotalSizeFound - freedBytes);
            OnPropertyChanged(nameof(FormattedTotalSize));
            ResultCount = Files.Count;
            HasResults = true;
            ApplyFilter();
            SelectedFile = null;
            StatusMessage = $"Deleted {deleted} file(s), freed {FormatHelper.FormatBytes(freedBytes)}." +
                (failedFiles.Count > 0 ? $" {failedFiles.Count} failed (in use or access denied)." : "") +
                (blocked.Count > 0 ? $" {blocked.Count} system file(s) skipped." : "");
            IsBusy = false;
        }
    }

    /// <summary>
    /// Represents a drive available for scanning.
    /// </summary>
    public class DriveOption
    {
        public string Path { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public string FreeSpace { get; init; } = string.Empty;
        public string TotalSize { get; init; } = string.Empty;
        public string Display => $"{Label} — {FreeSpace} free of {TotalSize}";
    }
}
