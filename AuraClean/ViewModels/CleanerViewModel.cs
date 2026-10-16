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
    public string ItemCountLabel => FormatHelper.Pluralize(ItemCount, "item");

    /// <summary>Screen readers announce a group by its ToString() when no AutomationProperties.Name is set.</summary>
    public override string ToString() => $"{Name}, {ItemCountLabel}, {FormattedTotalSize}";

    partial void OnIsAllSelectedChanged(bool value)
    {
        if (_syncingSelection)
            return;

        SetBulkSelection(value);
    }

    /// <summary>True when at least one row can be picked by a bulk action (not high-risk, not excluded).</summary>
    public bool HasBulkSelectableItems => Items.Any(IsBulkSelectable);

    private static bool IsBulkSelectable(JunkItem item) =>
        Services.CleanupModePolicy.IsBulkSelectable(item.Type) &&
        !Services.CleanerExcludeStore.IsExcluded(item.Path);

    /// <summary>
    /// The single entry point for category-level and global Select All / Deselect All.
    /// Selecting skips high-risk rows (WinSxS, Windows.old — deliberate per-row selection only) and
    /// excluded paths; deselecting clears every row. Works even when the header state would not change.
    /// </summary>
    public void SetBulkSelection(bool selected)
    {
        foreach (var item in Items)
        {
            if (!selected || Services.CleanerExcludeStore.IsExcluded(item.Path))
                item.IsSelected = false;
            else if (Services.CleanupModePolicy.IsBulkSelectable(item.Type))
                item.IsSelected = true;
            // High-risk rows are left exactly as the user set them: bulk actions never select
            // them, and Select All does not silently undo a deliberate per-row choice.
        }
        SyncSelectionState();
    }

    /// <summary>Updates the header checkbox to mirror the item selection without cascading.</summary>
    public void SyncSelectionState()
    {
        _syncingSelection = true;
        try
        {
            // The header reflects the bulk-selectable rows (not excluded, not high-risk) so neither an
            // exclusion list nor a review-only row forces the group header into the unchecked state.
            var selectable = Items.Where(IsBulkSelectable).ToList();
            IsAllSelected = selectable.Count > 0 && selectable.All(i => i.IsSelected);
            OnPropertyChanged(nameof(HasBulkSelectableItems));
        }
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
        OnPropertyChanged(nameof(ItemCountLabel));
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
        if (value.Count == 0)
            ExcludedCount = 0;
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

    /// <summary>
    /// True only once an analysis ran to completion (not cancelled or failed), so "nothing to
    /// clean" can be claimed honestly. Reset whenever a new analysis starts.
    /// </summary>
    [ObservableProperty] private bool _hasCompletedAnalysis;

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

    // Exclusion-list tracking (D1): excluded rows are deselected and badged, never cleaned.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExcludedItems))]
    [NotifyPropertyChangedFor(nameof(ExcludedNotice))]
    private int _excludedCount;

    public bool HasExcludedItems => ExcludedCount > 0;
    public string ExcludedNotice => ExcludedCount == 0
        ? string.Empty
        : $"{ExcludedCount} item(s) deselected — on your exclusion list";

    // Last cleanup tracking for undo support
    [ObservableProperty] private int _lastCleanedCount;
    [ObservableProperty] private long _lastCleanedBytes;
    [ObservableProperty] private bool _canUndoLastClean;
    [ObservableProperty] private string _lastCleanedSummary = string.Empty;

    public string FormattedTotalSize => FormatHelper.FormatBytes(TotalJunkSize);

    private CancellationTokenSource? _cts;

    /// <summary>
    /// The system-junk scan (progress, token, includeReviewOnlyCategories). Production uses
    /// <see cref="FileCleanerService.AnalyzeSystemJunkAsync"/>; tests inject a stub so the UI-thread
    /// behavior of <see cref="AnalyzeAsync"/> can be verified without touching the file system.
    /// </summary>
    public Func<IProgress<string>, CancellationToken, bool, Task<List<JunkItem>>> JunkScanner { get; set; } =
        (progress, ct, includeReviewOnly) =>
            FileCleanerService.AnalyzeSystemJunkAsync(progress, ct, includeReviewOnlyCategories: includeReviewOnly);

    /// <summary>
    /// Confirmation seam (message, title, alwaysAsk). Production uses the safety prompt; tests inject
    /// a stub so no dialog is shown and no cleanup can run.
    /// </summary>
    public Func<string, string, bool, bool> ConfirmAction { get; set; } =
        (message, title, alwaysAsk) => SafetyPromptService.ConfirmDestructiveAction(message, title, alwaysAsk);

    private bool CanAnalyze() => !IsBusy;
    private bool CanClean() => !IsBusy && HasResults;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        IsAnalyzing = true;
        HasCompletedAnalysis = false;
        StatusMessage = "Analyzing system...";
        Categories = [];
        HasResults = false;
        TotalJunkSize = 0;
        TotalJunkCount = 0;
        ProgressValue = 0;
        ExcludedCount = 0;

        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var settings = SettingsService.Load();
            var advanced = IsAdvancedMode;

            ProgressValue = 10;

            // Both scans do synchronous file-system probing (drive enumeration, Directory.Exists, ...)
            // before their first await. Started directly they ran on the UI thread, freezing the whole
            // window ("Not Responding", blank) for the length of that probing — including the automatic
            // scan at dashboard startup. Task.Run keeps that work off the UI thread.
            var ct = _cts.Token;
            var systemJunkTask = Task.Run(() => JunkScanner(progress, ct, advanced), ct);

            var abandonedTask = advanced && settings.RunHeuristicScan
                ? Task.Run(() => HeuristicScannerService.ScanForAbandonedFilesAsync(settings.AbandonedFileDaysThreshold, progress, ct), ct)
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

            // D1: exclusion-listed paths are deselected (rows stay visible with an
            // "Excluded" badge so the user can see what was skipped and why).
            int excluded = 0;
            foreach (var item in allItems)
            {
                if (CleanerExcludeStore.IsExcluded(item.Path))
                {
                    item.IsSelected = false;
                    excluded++;
                }
            }
            ExcludedCount = excluded;

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
            HasCompletedAnalysis = true;
            ProgressValue = 100;

            StatusMessage = allItems.Count == 0
                ? "No unnecessary files found."
                : advanced
                    ? $"Found {FormatHelper.Pluralize(TotalJunkCount, "item")} ({FormattedTotalSize}) of reclaimable space."
                    : $"Found {FormatHelper.Pluralize(TotalJunkCount, "low-risk cleanup item")} ({FormattedTotalSize}). Advanced mode shows review-only categories.";
            if (excluded > 0)
                StatusMessage += $" {excluded} item(s) deselected — on your exclusion list.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Analysis cancelled.";
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
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelAnalysis() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanClean))]
    private Task CleanSelectedAsync() => CleanCoreAsync(lowRiskOnly: false, alwaysConfirm: false);

    /// <summary>
    /// One-click path (dashboard Quick Clean): the user has not reviewed the list, so only
    /// low-risk categories are cleaned — even in Advanced mode — and the confirmation prompt
    /// cannot be disabled by the "show confirmation dialogs" setting.
    /// </summary>
    public Task CleanLowRiskAsync() => CleanCoreAsync(lowRiskOnly: true, alwaysConfirm: true);

    private async Task CleanCoreAsync(bool lowRiskOnly, bool alwaysConfirm)
    {
        if (!HasResults || IsBusy) return;

        var settings = SettingsService.Load();
        var selectedItems = Categories.SelectMany(c => c.Items)
            .Where(i => i.IsSelected)
            .Where(i => (IsAdvancedMode && !lowRiskOnly) || CleanupModePolicy.IsNormalModeJunkType(i.Type))
            .Where(i => !CleanerExcludeStore.IsExcluded(i.Path))
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

                StatusMessage = $"Dry run: would clean {FormatHelper.Pluralize(wouldClean, "item")} " +
                    $"({FormatHelper.FormatBytes(wouldFree)}). {protectedSkipped} protected media file(s) skipped.";
                ProgressValue = 100;
                CanUndoLastClean = false;
                CleanupCompleted?.Invoke(this, new CleanupCompletedEventArgs(0, 0, wasDryRun: true));
                return;
            }

            var totalBytes = selectedItems.Sum(i => i.SizeBytes);

            // High-risk rows (WinSxS, Windows.old) re-evaluated from the live selection at execution
            // time, so a stale or programmatic selection cannot skip the mandatory confirmation.
            var highRiskWarning = CleanupModePolicy.BuildHighRiskWarning(selectedItems);
            var confirmMessage = highRiskWarning != null
                ? highRiskWarning + $"\n\nTotal selected: {FormatHelper.Pluralize(selectedItems.Count, "item")} ({FormatHelper.FormatBytes(totalBytes)})."
                : IsAdvancedMode && !lowRiskOnly
                ? $"Clean {FormatHelper.Pluralize(selectedItems.Count, "selected item")} ({FormatHelper.FormatBytes(totalBytes)})? This permanently removes files."
                : $"Clean {FormatHelper.Pluralize(selectedItems.Count, "low-risk item")} ({FormatHelper.FormatBytes(totalBytes)})? This permanently removes files.";

            var confirmTitle = highRiskWarning != null ? "Confirm advanced system cleanup" : "Confirm destructive action";
            if (!ConfirmAction(confirmMessage, confirmTitle, alwaysConfirm || highRiskWarning != null))
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

            StatusMessage = $"Cleaned {FormatHelper.Pluralize(deleted, "item")} ({FormatHelper.FormatBytes(bytesFreed)} freed). " +
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

            var targeted = selectedItems.ToHashSet();

            // Remove cleaned items from displayed categories; keep skipped ones visible.
            foreach (var category in Categories.ToList())
            {
                // Only rows this run actually targeted: a low-risk-only run must not drop selected
                // review-only rows from the list as if they had been cleaned.
                var toRemove = category.Items.Where(i => targeted.Contains(i) && !i.IsLocked).ToList();
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
            category.SetBulkSelection(true);
        OnPropertyChanged(nameof(SmartCleanLabel));
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var category in Categories)
            category.SetBulkSelection(false);
        OnPropertyChanged(nameof(SmartCleanLabel));
    }
}
