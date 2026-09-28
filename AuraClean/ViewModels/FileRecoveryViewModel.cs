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
        if (IsBusy) return;

        _cts?.Dispose();
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
                MetadataPath = f.MetadataPath,
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
                : "Recycle Bin is empty. Deleted files will appear here.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during the scan. Please try again.";
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
        if (IsBusy) return;

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
            var byPayload = selected.ToDictionary(s => s.RecycleBinPath, StringComparer.OrdinalIgnoreCase);
            var recoverableFiles = selected.Select(s => new FileRecoveryService.RecoverableFile
            {
                FileName = s.FileName,
                OriginalPath = s.OriginalPath,
                RecycleBinPath = s.RecycleBinPath,
                MetadataPath = s.MetadataPath,
                SizeBytes = s.SizeBytes,
                DeletedDate = s.DeletedDate,
                FileType = s.FileType,
                IsFolder = s.IsFolder
            });

            var (restored, failures) = await FileRecoveryService.RestoreFilesAsync(recoverableFiles, progress);

            foreach (var file in restored)
            {
                if (byPayload.TryGetValue(file.RecycleBinPath, out var entry))
                    AllFiles.Remove(entry);
            }
            ApplyFilter();
            TotalItems = AllFiles.Count;
            HasResults = AllFiles.Count > 0;

            if (restored.Count > 0)
                NotificationService.ShowSuccess("File Recovery", $"{restored.Count} item(s) restored.");

            StatusMessage = $"Restore complete: {restored.Count} restored, {failures.Count} failed." +
                            (failures.Count > 0 ? $" {failures[0]}" : string.Empty);
            foreach (var failure in failures.Take(20))
                DiagnosticLogger.Warn("FileRecoveryVM", failure);
        }
        catch (Exception ex)
        {
            StatusMessage = "Some files couldn't be restored. They may no longer be available.";
            DiagnosticLogger.Error("FileRecoveryVM", "Restore failed", ex);
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
    [ObservableProperty] private string _metadataPath = string.Empty;
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private DateTime _deletedDate;
    [ObservableProperty] private string _fileType = string.Empty;
    [ObservableProperty] private bool _isFolder;
    [ObservableProperty] private bool _isSelected;

    public string FormattedSize => FormatHelper.FormatBytes(SizeBytes);

    public string DeletedDateDisplay => DeletedDate <= DateTime.MinValue.AddDays(1)
        ? "Unknown"
        : DeletedDate.ToString("yyyy-MM-dd HH:mm");
}
