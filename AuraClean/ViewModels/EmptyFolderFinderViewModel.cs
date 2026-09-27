using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;

namespace AuraClean.ViewModels;

public partial class EmptyFolderFinderViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<EmptyFolderItem> _emptyFolders = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _statusMessage = "Select folders to scan for empty subfolders.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private int _totalFound;
    [ObservableProperty] private string _selectedPath = string.Empty;
    [ObservableProperty] private ObservableCollection<string> _scanPaths = [];
    [ObservableProperty] private bool _selectAll;

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

        if (TryAddScanPath(SelectedPath.Trim()))
            SelectedPath = string.Empty;
    }

    [RelayCommand]
    private void RemovePath(string path)
    {
        ScanPaths.Remove(path);
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder to scan for empty subfolders",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            TryAddScanPath(dialog.FolderName);
    }

    private bool TryAddScanPath(string path)
    {
        var normalized = PathSafety.Normalize(path);
        if (normalized == null || !Directory.Exists(normalized))
        {
            StatusMessage = "That folder doesn't exist.";
            return false;
        }

        if (PathSafety.IsSystemCriticalLocation(normalized) && !EmptyFolderFinderService.GetDefaultScanPaths()
                .Any(d => PathSafety.IsSameOrUnder(normalized, d)))
        {
            StatusMessage = "Windows and Program Files folders can't be scanned — their empty folders are required.";
            return false;
        }

        if (ScanPaths.Any(p => p.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "That folder is already in the list.";
            return false;
        }

        ScanPaths.Add(normalized);
        StatusMessage = $"Added {normalized}.";
        return true;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;

        // Normal mode always uses the built-in low-risk locations, even if custom folders
        // were added earlier in Advanced mode.
        var roots = IsAdvancedMode
            ? ScanPaths.ToList()
            : EmptyFolderFinderService.GetDefaultScanPaths();

        if (roots.Count == 0)
        {
            StatusMessage = "Add at least one folder to scan.";
            return;
        }

        IsBusy = true;
        IsScanning = true;
        EmptyFolders = [];
        HasResults = false;
        TotalFound = 0;
        SelectAll = false;
        StatusMessage = "Scanning for empty folders...";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var results = await EmptyFolderFinderService.ScanAsync(roots, progress, _cts.Token);

            EmptyFolders = new ObservableCollection<EmptyFolderItem>(results.OrderBy(r => r.Path));

            TotalFound = results.Count;
            HasResults = results.Count > 0;

            StatusMessage = results.Count > 0
                ? $"Found {results.Count} empty folder(s). Select the ones to remove."
                : "No empty folders found.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during the scan. Please try again.";
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
        if (IsBusy) return;

        var selectedCount = EmptyFolders.Count(f => f.IsSelected);
        if (selectedCount == 0)
        {
            StatusMessage = "No folders selected for deletion.";
            return;
        }

        if (SafetyPromptService.IsDryRunEnabled())
        {
            StatusMessage = $"Dry run: would delete {selectedCount} empty folder(s).";
            return;
        }

        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Delete {selectedCount} selected empty folder(s)? Folders that are no longer empty are skipped."))
        {
            StatusMessage = "Empty folder deletion cancelled.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Deleting {selectedCount} empty folder(s)...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var (deleted, failed) = await EmptyFolderFinderService.DeleteAsync(EmptyFolders, progress);

            foreach (var item in EmptyFolders.Where(f => f.IsDeleted).ToList())
                EmptyFolders.Remove(item);

            TotalFound = EmptyFolders.Count;
            HasResults = EmptyFolders.Count > 0;

            StatusMessage = $"Deleted {deleted} empty folder(s)." +
                            (failed > 0 ? $" {failed} skipped (no longer empty, in use, or protected)." : string.Empty);

            if (deleted > 0)
            {
                NotificationService.ShowSuccess("Empty Folders Cleaned", $"Removed {deleted} empty folder(s).");
                CleanupHistoryService.Record(CleanupOperationType.EmptyFolderRemoval, deleted, 0,
                    $"Removed {deleted} empty folder(s)");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Some folders couldn't be deleted. They may be in use or protected.";
            DiagnosticLogger.Error("EmptyFolderFinder", "Delete failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
