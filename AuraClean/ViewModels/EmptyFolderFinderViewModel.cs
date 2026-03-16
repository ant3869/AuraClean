using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AuraClean.ViewModels;

public partial class EmptyFolderFinderViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<EmptyFolderItem> _emptyFolders = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusMessage = "Select folders to scan for empty directories.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private int _totalFound;
    [ObservableProperty] private string _selectedPath = string.Empty;
    [ObservableProperty] private ObservableCollection<string> _scanPaths = [];
    [ObservableProperty] private bool _selectAll = true;

    private CancellationTokenSource? _cts;

    public EmptyFolderFinderViewModel()
    {
        foreach (var path in EmptyFolderFinderService.GetDefaultScanPaths())
            ScanPaths.Add(path);
    }

    partial void OnSelectAllChanged(bool value)
    {
        foreach (var item in EmptyFolders)
            item.IsSelected = value;
    }

    [RelayCommand]
    private void AddPath()
    {
        if (string.IsNullOrWhiteSpace(SelectedPath)) return;

        var path = SelectedPath.Trim();
        if (System.IO.Directory.Exists(path) && !ScanPaths.Contains(path))
        {
            ScanPaths.Add(path);
            SelectedPath = string.Empty;
        }
    }

    [RelayCommand]
    private void RemovePath(string path)
    {
        ScanPaths.Remove(path);
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select folder to scan for empty directories",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            if (!ScanPaths.Contains(dialog.SelectedPath))
                ScanPaths.Add(dialog.SelectedPath);
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (ScanPaths.Count == 0)
        {
            StatusMessage = "Add at least one folder to scan.";
            return;
        }

        IsBusy = true;
        IsScanning = true;
        EmptyFolders.Clear();
        HasResults = false;
        TotalFound = 0;
        StatusMessage = "Scanning for empty folders...";

        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var results = await EmptyFolderFinderService.ScanAsync(
                ScanPaths, progress, _cts.Token);

            EmptyFolders = new ObservableCollection<EmptyFolderItem>(
                results.OrderBy(r => r.Path));

            TotalFound = results.Count;
            HasResults = results.Count > 0;

            StatusMessage = results.Count > 0
                ? $"Found {results.Count} empty folder(s) ready to clean."
                : "No empty folders found — your system is tidy!";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
            DiagnosticLogger.Error("EmptyFolderFinder", "Scan failed", ex);
        }
        finally
        {
            IsBusy = false;
            IsScanning = false;
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
    private async Task DeleteSelectedAsync()
    {
        var selectedCount = EmptyFolders.Count(f => f.IsSelected);
        if (selectedCount == 0)
        {
            StatusMessage = "No folders selected for deletion.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Deleting {selectedCount} empty folder(s)...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var (deleted, failed) = await EmptyFolderFinderService.DeleteAsync(
                EmptyFolders, progress);

            // Remove deleted items from the list
            var deletedItems = EmptyFolders.Where(f => f.IsSelected && System.IO.Directory.Exists(f.Path) == false).ToList();
            foreach (var item in deletedItems)
                EmptyFolders.Remove(item);

            TotalFound = EmptyFolders.Count;
            HasResults = EmptyFolders.Count > 0;

            StatusMessage = $"Deleted {deleted} empty folder(s). {(failed > 0 ? $"{failed} failed." : "")}";

            NotificationService.ShowSuccess("Empty Folders Cleaned",
                $"Removed {deleted} empty folder(s).");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Delete error: {ex.Message}";
            DiagnosticLogger.Error("EmptyFolderFinder", "Delete failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
