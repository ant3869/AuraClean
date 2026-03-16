using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AuraClean.ViewModels;

public partial class FileRecoveryViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Scan the Recycle Bin to find recoverable files.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private int _totalItems;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _filterType = "All Types";

    [ObservableProperty]
    private ObservableCollection<RecoverableFileEntry> _allFiles = [];

    [ObservableProperty]
    private ObservableCollection<RecoverableFileEntry> _filteredFiles = [];

    [ObservableProperty] private RecoverableFileEntry? _selectedFile;

    public ObservableCollection<string> FileTypes { get; } = ["All Types"];

    private CancellationTokenSource? _cts;

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnFilterTypeChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task ScanRecycleBinAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        HasResults = false;
        StatusMessage = "Scanning Recycle Bin...";
        AllFiles.Clear();
        FilteredFiles.Clear();
        FileTypes.Clear();
        FileTypes.Add("All Types");

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var files = await FileRecoveryService.ScanRecycleBinAsync(progress, _cts.Token);

            var entries = files.Select(f => new RecoverableFileEntry
            {
                FileName = f.FileName,
                OriginalPath = f.OriginalPath,
                RecycleBinPath = f.RecycleBinPath,
                SizeBytes = f.SizeBytes,
                DeletedDate = f.DeletedDate,
                FileType = f.FileType,
                IsFolder = f.IsFolder,
                IsSelected = false
            }).ToList();

            AllFiles = new ObservableCollection<RecoverableFileEntry>(entries);
            TotalItems = entries.Count;

            // Build file type filter list
            var types = entries.Select(e => e.FileType)
                              .Where(t => !string.IsNullOrEmpty(t))
                              .Distinct()
                              .OrderBy(t => t);
            foreach (var t in types)
                FileTypes.Add(t);

            ApplyFilter();
            HasResults = entries.Count > 0;
            StatusMessage = entries.Count > 0
                ? $"Found {entries.Count} recoverable items in Recycle Bin."
                : "Recycle Bin is empty.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            DiagnosticLogger.Error("FileRecoveryVM", "Scan failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        var selected = FilteredFiles.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No files selected for restoration.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Restoring {selected.Count} items...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var recoverableFiles = selected.Select(s => new FileRecoveryService.RecoverableFile
            {
                FileName = s.FileName,
                OriginalPath = s.OriginalPath,
                RecycleBinPath = s.RecycleBinPath,
                SizeBytes = s.SizeBytes,
                DeletedDate = s.DeletedDate,
                FileType = s.FileType,
                IsFolder = s.IsFolder
            });

            var (success, failed) = await FileRecoveryService.RestoreFilesAsync(
                recoverableFiles, progress);

            // Remove successfully restored items from the list
            if (success > 0)
            {
                foreach (var item in selected)
                {
                    AllFiles.Remove(item);
                }
                ApplyFilter();
                TotalItems = AllFiles.Count;

                NotificationService.ShowSuccess("File Recovery",
                    $"{success} file(s) restored successfully.");
            }

            StatusMessage = $"Restore complete: {success} restored, {failed} failed.";
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
    private void SelectAll()
    {
        foreach (var f in FilteredFiles) f.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var f in FilteredFiles) f.IsSelected = false;
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    private void ApplyFilter()
    {
        var filtered = AllFiles.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            var text = FilterText;
            filtered = filtered.Where(f =>
                f.FileName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                f.OriginalPath.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        if (FilterType != "All Types")
        {
            filtered = filtered.Where(f =>
                f.FileType.Equals(FilterType, StringComparison.OrdinalIgnoreCase));
        }

        FilteredFiles = new ObservableCollection<RecoverableFileEntry>(filtered);
    }
}

public partial class RecoverableFileEntry : ObservableObject
{
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _originalPath = string.Empty;
    [ObservableProperty] private string _recycleBinPath = string.Empty;
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private DateTime _deletedDate;
    [ObservableProperty] private string _fileType = string.Empty;
    [ObservableProperty] private bool _isFolder;
    [ObservableProperty] private bool _isSelected;

    public string FormattedSize => SizeBytes switch
    {
        0 => "0 B",
        < 1024 => $"{SizeBytes} B",
        < 1_048_576 => $"{SizeBytes / 1024.0:F1} KB",
        < 1_073_741_824 => $"{SizeBytes / 1_048_576.0:F1} MB",
        _ => $"{SizeBytes / 1_073_741_824.0:F2} GB"
    };

    public string DeletedDateDisplay => DeletedDate == DateTime.MinValue
        ? "Unknown"
        : DeletedDate.ToString("yyyy-MM-dd HH:mm");
}
