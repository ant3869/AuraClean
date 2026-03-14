using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AuraClean.ViewModels;

/// <summary>
/// Represents a category of junk items for grouped display in the Cleaner UI.
/// </summary>
public partial class JunkCategory : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isAllSelected = true;

    public ObservableCollection<JunkItem> Items { get; } = [];

    public long TotalSize => Items.Sum(i => i.SizeBytes);
    public int ItemCount => Items.Count;

    public string FormattedTotalSize => TotalSize switch
    {
        0 => "0 B",
        < 1024 => $"{TotalSize} B",
        < 1_048_576 => $"{TotalSize / 1024.0:F1} KB",
        < 1_073_741_824 => $"{TotalSize / 1_048_576.0:F1} MB",
        _ => $"{TotalSize / 1_073_741_824.0:F2} GB"
    };

    partial void OnIsAllSelectedChanged(bool value)
    {
        foreach (var item in Items)
            item.IsSelected = value;
    }
}

/// <summary>
/// ViewModel for the System Cleaner view.
/// Manages scanning, categorized junk display, and selective cleaning.
/// </summary>
public partial class CleanerViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<JunkCategory> _categories = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private string _statusMessage = "Click Analyze to scan for system junk.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private long _totalJunkSize;
    [ObservableProperty] private int _totalJunkCount;
    [ObservableProperty] private double _progressValue;

    // Last cleanup tracking for undo support
    [ObservableProperty] private int _lastCleanedCount;
    [ObservableProperty] private long _lastCleanedBytes;
    [ObservableProperty] private bool _canUndoLastClean;
    [ObservableProperty] private string _lastCleanedSummary = string.Empty;

    public string FormattedTotalSize => TotalJunkSize switch
    {
        0 => "0 B",
        < 1024 => $"{TotalJunkSize} B",
        < 1_048_576 => $"{TotalJunkSize / 1024.0:F1} KB",
        < 1_073_741_824 => $"{TotalJunkSize / 1_048_576.0:F1} MB",
        _ => $"{TotalJunkSize / 1_073_741_824.0:F2} GB"
    };

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        IsBusy = true;
        IsAnalyzing = true;
        StatusMessage = "Analyzing system...";
        Categories.Clear();
        ProgressValue = 0;

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);

            // Run system junk scan and heuristic scan in parallel
            ProgressValue = 10;
            var systemJunkTask = FileCleanerService.AnalyzeSystemJunkAsync(progress);

            ProgressValue = 30;
            var abandonedTask = HeuristicScannerService.ScanForAbandonedFilesAsync(progress);

            var systemJunk = await systemJunkTask;
            ProgressValue = 60;

            var abandoned = await abandonedTask;
            ProgressValue = 80;

            // Combine all results
            var allItems = systemJunk.Concat(abandoned).ToList();

            // Group by category — build all data off-thread then assign once
            var grouped = allItems.GroupBy(i => i.Category)
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var cat = new JunkCategory { Name = g.Key };
                    foreach (var item in g.OrderByDescending(i => i.SizeBytes))
                        cat.Items.Add(item);
                    return cat;
                })
                .ToList();

            // Single assignment to avoid per-item UI updates
            Categories = new ObservableCollection<JunkCategory>(grouped);

            TotalJunkSize = allItems.Sum(i => i.SizeBytes);
            TotalJunkCount = allItems.Count;
            HasResults = allItems.Count > 0;
            ProgressValue = 100;

            StatusMessage = allItems.Count > 0
                ? $"Found {TotalJunkCount} items ({FormattedTotalSize}) of reclaimable space."
                : "System is clean — no junk found!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Analysis error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsAnalyzing = false;
        }
    }

    [RelayCommand]
    private async Task CleanSelectedAsync()
    {
        if (!HasResults) return;

        IsBusy = true;
        StatusMessage = "Creating restore point...";
        ProgressValue = 0;

        try
        {
            // Safety: Create restore point
            var (rpSuccess, rpMsg) = await RestorePointService.CreateRestorePointAsync();
            if (!rpSuccess)
            {
                StatusMessage = $"Warning: {rpMsg} — Proceeding with cleanup...";
                await Task.Delay(1500);
            }

            ProgressValue = 10;

            // Collect all selected items
            var selectedItems = Categories.SelectMany(c => c.Items)
                .Where(i => i.IsSelected).ToList();

            if (selectedItems.Count == 0)
            {
                StatusMessage = "No items selected for cleaning.";
                IsBusy = false;
                return;
            }

            var progress = new Progress<string>(msg => StatusMessage = msg);
            var (deleted, skipped, bytesFreed, errors) =
                await FileCleanerService.CleanItemsAsync(selectedItems, progress);

            ProgressValue = 100;

            StatusMessage = $"Cleaned {deleted} items ({FormatHelper.FormatBytes(bytesFreed)} freed). " +
                           $"{skipped} skipped (locked/in-use).";

            // Track last cleanup for undo support
            LastCleanedCount = deleted;
            LastCleanedBytes = bytesFreed;
            LastCleanedSummary = $"{deleted} items ({FormatHelper.FormatBytes(bytesFreed)}) cleaned at {DateTime.Now:HH:mm:ss}";
            CanUndoLastClean = rpSuccess;

            if (errors.Count > 0)
            {
                var details = string.Join("\n", errors.Take(10));
                if (errors.Count > 10)
                    details += $"\n... and {errors.Count - 10} more.";
                StatusMessage += $" {errors.Count} error(s).";
                DiagnosticLogger.Warn("CleanerViewModel", $"Cleanup errors:\n{details}");
            }

            // Remove cleaned items from displayed categories
            foreach (var category in Categories.ToList())
            {
                var toRemove = category.Items.Where(i => i.IsSelected && !i.IsLocked).ToList();
                foreach (var item in toRemove)
                    category.Items.Remove(item);

                if (category.Items.Count == 0)
                    Categories.Remove(category);
            }

            TotalJunkSize = Categories.SelectMany(c => c.Items).Sum(i => i.SizeBytes);
            TotalJunkCount = Categories.SelectMany(c => c.Items).Count();
            HasResults = TotalJunkCount > 0;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Cleanup error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var category in Categories)
        {
            category.IsAllSelected = true;
            foreach (var item in category.Items)
                item.IsSelected = true;
        }
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var category in Categories)
        {
            category.IsAllSelected = false;
            foreach (var item in category.Items)
                item.IsSelected = false;
        }
    }

}
