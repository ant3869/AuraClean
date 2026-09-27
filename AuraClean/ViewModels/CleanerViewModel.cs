using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AuraClean.ViewModels;

/// <summary>
/// Represents a category of junk items for grouped display in the Cleaner UI.
/// </summary>
public partial class JunkCategory : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isAllSelected = true;

    private bool _syncingSelection;

    public ObservableCollection<JunkItem> Items { get; } = [];

    public JunkCategory()
    {
        Items.CollectionChanged += OnItemsChanged;
    }

    public long TotalSize => Items.Sum(i => i.SizeBytes);
    public int ItemCount => Items.Count;
    public string FormattedTotalSize => FormatHelper.FormatBytes(TotalSize);

    partial void OnIsAllSelectedChanged(bool value)
    {
        if (_syncingSelection)
            return;

        foreach (var item in Items)
            item.IsSelected = value;
    }

    /// <summary>Updates the header checkbox to mirror the item selection without cascading.</summary>
    public void SyncSelectionState()
    {
        _syncingSelection = true;
        try { IsAllSelected = Items.Count > 0 && Items.All(i => i.IsSelected); }
        finally { _syncingSelection = false; }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (JunkItem item in e.OldItems)
                item.PropertyChanged -= OnItemPropertyChanged;
        if (e.NewItems != null)
            foreach (JunkItem item in e.NewItems)
                item.PropertyChanged += OnItemPropertyChanged;

        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(TotalSize));
        OnPropertyChanged(nameof(FormattedTotalSize));
        SyncSelectionState();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JunkItem.IsSelected))
            SyncSelectionState();
        else if (e.PropertyName == nameof(JunkItem.SizeBytes))
        {
            OnPropertyChanged(nameof(TotalSize));
            OnPropertyChanged(nameof(FormattedTotalSize));
        }
    }
}

/// <summary>Raised when a cleanup run finishes (including dry runs).</summary>
public sealed class CleanupCompletedEventArgs(int itemsCleaned, long bytesFreed, bool wasDryRun) : EventArgs
{
    public int ItemsCleaned { get; } = itemsCleaned;
    public long BytesFreed { get; } = bytesFreed;
    public bool WasDryRun { get; } = wasDryRun;
}

/// <summary>
/// ViewModel for the System Cleaner view.
/// Manages scanning, categorized junk display, and selective cleaning.
/// </summary>
public partial class CleanerViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<JunkCategory> _categories = [];

    /// <summary>Raised after every completed cleanup so the dashboard can refresh.</summary>
    public event EventHandler<CleanupCompletedEventArgs>? CleanupCompleted;

    partial void OnCategoriesChanged(ObservableCollection<JunkCategory> value)
    {
        HookItemSelectionEvents();
    }

    public string SmartCleanLabel
    {
        get
        {
            if (!HasResults) return "CLEAN SELECTED";
            var selected = Categories.SelectMany(c => c.Items).Where(i => i.IsSelected).ToList();
            if (selected.Count == 0) return "Select items to clean";
            var totalSize = selected.Sum(i => i.SizeBytes);
            var prefix = IsAdvancedMode ? "CLEAN" : "CLEAN SAFE ITEMS";
            return $"{prefix} {selected.Count} ITEM{(selected.Count != 1 ? "S" : "")} · {FormatHelper.FormatBytes(totalSize)}";
        }
    }

    private void HookItemSelectionEvents()
    {
        foreach (var cat in Categories)
        {
            foreach (var item in cat.Items)
            {
                item.PropertyChanged -= OnJunkItemPropertyChanged;
                item.PropertyChanged += OnJunkItemPropertyChanged;
            }
        }
        OnPropertyChanged(nameof(SmartCleanLabel));
    }

    private void OnJunkItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JunkItem.IsSelected))
            OnPropertyChanged(nameof(SmartCleanLabel));
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanSelectedCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private string _statusMessage = "Ready to analyze your system for unnecessary files.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmartCleanLabel))]
    [NotifyCanExecuteChangedFor(nameof(CleanSelectedCommand))]
    private bool _hasResults;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedTotalSize))]
    private long _totalJunkSize;

    [ObservableProperty] private int _totalJunkCount;
    [ObservableProperty] private double _progressValue;

    // Last cleanup tracking for undo support
    [ObservableProperty] private int _lastCleanedCount;
    [ObservableProperty] private long _lastCleanedBytes;
    [ObservableProperty] private bool _canUndoLastClean;
    [ObservableProperty] private string _lastCleanedSummary = string.Empty;

    public string FormattedTotalSize => FormatHelper.FormatBytes(TotalJunkSize);

    private bool CanAnalyze() => !IsBusy;
    private bool CanClean() => !IsBusy && HasResults;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        IsAnalyzing = true;
        StatusMessage = "Analyzing system...";
        Categories = [];
        HasResults = false;
        TotalJunkSize = 0;
        TotalJunkCount = 0;
        ProgressValue = 0;

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var settings = SettingsService.Load();
            var advanced = IsAdvancedMode;

            ProgressValue = 10;
            var systemJunkTask = FileCleanerService.AnalyzeSystemJunkAsync(progress);

            var abandonedTask = advanced && settings.RunHeuristicScan
                ? HeuristicScannerService.ScanForAbandonedFilesAsync(settings.AbandonedFileDaysThreshold, progress)
                : Task.FromResult(new List<JunkItem>());

            var systemJunk = await systemJunkTask;
            ProgressValue = 60;

            var abandoned = await abandonedTask;
            ProgressValue = 80;

            var allItems = systemJunk.Concat(abandoned)
                .Where(i => !FileCleanerService.IsProtectedUserMediaFile(i.Path))
                .Where(i => advanced || CleanupModePolicy.IsNormalModeJunkType(i.Type))
                .ToList();

            CleanupModePolicy.ApplyDefaultSelection(allItems, settings, advanced);

            var grouped = allItems.GroupBy(i => i.Category)
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var cat = new JunkCategory { Name = g.Key };
                    foreach (var item in g.OrderByDescending(i => i.SizeBytes))
                        cat.Items.Add(item);
                    cat.SyncSelectionState();
                    return cat;
                })
                .ToList();

            Categories = new ObservableCollection<JunkCategory>(grouped);

            TotalJunkSize = allItems.Sum(i => i.SizeBytes);
            TotalJunkCount = allItems.Count;
            HasResults = allItems.Count > 0;
            ProgressValue = 100;

            StatusMessage = allItems.Count == 0
                ? "No unnecessary files found."
                : advanced
                    ? $"Found {TotalJunkCount} items ({FormattedTotalSize}) of reclaimable space."
                    : $"Found {TotalJunkCount} low-risk cleanup item(s) ({FormattedTotalSize}). Advanced mode shows review-only categories.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during analysis. Please try again.";
            DiagnosticLogger.Error("CleanerVM", "Analysis failed", ex);
        }
        finally
        {
            IsBusy = false;
            IsAnalyzing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanSelectedAsync()
    {
        if (!HasResults || IsBusy) return;

        var settings = SettingsService.Load();
        var selectedItems = Categories.SelectMany(c => c.Items)
            .Where(i => i.IsSelected)
            .Where(i => IsAdvancedMode || CleanupModePolicy.IsNormalModeJunkType(i.Type))
            .ToList();

        if (selectedItems.Count == 0)
        {
            StatusMessage = "No items selected for cleaning.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Preparing cleanup...";
        ProgressValue = 0;

        try
        {
            if (settings.DryRunMode)
            {
                var (wouldClean, protectedSkipped, wouldFree, _) =
                    await FileCleanerService.CleanItemsAsync(selectedItems, dryRun: true);

                StatusMessage = $"Dry run: would clean {wouldClean} items " +
                    $"({FormatHelper.FormatBytes(wouldFree)}). {protectedSkipped} protected media file(s) skipped.";
                ProgressValue = 100;
                CanUndoLastClean = false;
                CleanupCompleted?.Invoke(this, new CleanupCompletedEventArgs(0, 0, wasDryRun: true));
                return;
            }

            var totalBytes = selectedItems.Sum(i => i.SizeBytes);
            var confirmMessage = IsAdvancedMode
                ? $"Clean {selectedItems.Count} selected item(s) ({FormatHelper.FormatBytes(totalBytes)})? This permanently removes files."
                : $"Clean {selectedItems.Count} low-risk item(s) ({FormatHelper.FormatBytes(totalBytes)})?";

            if (!SafetyPromptService.ConfirmDestructiveAction(confirmMessage))
            {
                StatusMessage = "Cleanup cancelled.";
                return;
            }

            var rpSuccess = false;
            if (settings.CreateRestorePointBeforeClean)
            {
                StatusMessage = "Creating restore point...";

                var (created, rpMsg) = await RestorePointService.CreateRestorePointAsync();
                rpSuccess = created;
                if (!rpSuccess)
                {
                    StatusMessage = $"Warning: {rpMsg} — Proceeding with cleanup...";
                    DiagnosticLogger.Warn("CleanerVM", $"Restore point not created: {rpMsg}");
                    await Task.Delay(1500);
                }
            }

            ProgressValue = 10;

            var progress = new Progress<string>(msg => StatusMessage = msg);
            var (deleted, skipped, bytesFreed, errors) =
                await FileCleanerService.CleanItemsAsync(selectedItems, progress);

            ProgressValue = 100;

            StatusMessage = $"Cleaned {deleted} items ({FormatHelper.FormatBytes(bytesFreed)} freed). " +
                           $"{skipped} skipped (locked, recent, or protected).";

            LastCleanedCount = deleted;
            LastCleanedBytes = bytesFreed;
            LastCleanedSummary = $"{deleted} items ({FormatHelper.FormatBytes(bytesFreed)}) cleaned at {DateTime.Now:HH:mm:ss}";
            CanUndoLastClean = rpSuccess && deleted > 0;

            if (errors.Count > 0)
            {
                var details = string.Join("\n", errors.Take(10));
                if (errors.Count > 10)
                    details += $"\n... and {errors.Count - 10} more.";
                StatusMessage += $" {errors.Count} error(s).";
                DiagnosticLogger.Warn("CleanerViewModel", $"Cleanup errors:\n{details}");
            }

            if (deleted > 0)
            {
                NotificationService.ShowSuccess("Cleanup Complete",
                    $"Freed {FormatHelper.FormatBytes(bytesFreed)} by cleaning {deleted} items.");
                CleanupHistoryService.Record(CleanupOperationType.SystemClean, deleted, bytesFreed,
                    $"{selectedItems.Count} selected item(s), {skipped} skipped");
            }

            // Remove cleaned items from displayed categories; keep skipped ones visible.
            foreach (var category in Categories.ToList())
            {
                var toRemove = category.Items.Where(i => i.IsSelected && !i.IsLocked).ToList();
                foreach (var item in toRemove)
                {
                    item.PropertyChanged -= OnJunkItemPropertyChanged;
                    category.Items.Remove(item);
                }

                if (category.Items.Count == 0)
                    Categories.Remove(category);
            }

            TotalJunkSize = Categories.SelectMany(c => c.Items).Sum(i => i.SizeBytes);
            TotalJunkCount = Categories.SelectMany(c => c.Items).Count();
            HasResults = TotalJunkCount > 0;
            OnPropertyChanged(nameof(SmartCleanLabel));

            CleanupCompleted?.Invoke(this, new CleanupCompletedEventArgs(deleted, bytesFreed, wasDryRun: false));
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during cleanup. Some items may not have been removed.";
            DiagnosticLogger.Error("CleanerVM", "Cleanup failed", ex);
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
            category.IsAllSelected = true;
        OnPropertyChanged(nameof(SmartCleanLabel));
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var category in Categories)
            category.IsAllSelected = false;
        OnPropertyChanged(nameof(SmartCleanLabel));
    }
}
