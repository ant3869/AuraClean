using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the Cleanup History page.
/// Displays past cleanup operations with summary statistics and export capability.
/// </summary>
public partial class CleanupHistoryViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<CleanupRecord> _records = [];
    [ObservableProperty] private ObservableCollection<CleanupRecord> _filteredRecords = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Loading history...";
    [ObservableProperty] private string _filterType = "All";
    [ObservableProperty] private string _searchText = string.Empty;

    // Summary stats
    [ObservableProperty] private int _totalOperations;
    [ObservableProperty] private long _totalBytesFreed;
    [ObservableProperty] private int _totalItemsCleaned;
    [ObservableProperty] private string _lastOperationDate = "Never";
    [ObservableProperty] private string _totalBytesFreedDisplay = "0 B";

    public ObservableCollection<string> FilterTypes { get; } = new(
        new[] { "All" }.Concat(Enum.GetValues<CleanupOperationType>().Select(t => t.ToDisplayString())));

    public CleanupHistoryViewModel()
    {
        LoadHistory();

        // Keep the page current when other features log operations while it is open.
        CleanupHistoryService.HistoryChanged += () =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;
            dispatcher.BeginInvoke(LoadHistory);
        };
    }

    partial void OnFilterTypeChanged(string value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void LoadHistory()
    {
        IsBusy = true;

        try
        {
            var history = CleanupHistoryService.LoadHistory();
            Records = new ObservableCollection<CleanupRecord>(history.Records);

            var summary = CleanupHistoryService.BuildSummary(history.Records);
            TotalOperations = summary.TotalOperations;
            TotalBytesFreed = summary.TotalBytesFreed;
            TotalBytesFreedDisplay = FormatHelper.FormatBytes(summary.TotalBytesFreed);
            TotalItemsCleaned = summary.TotalItemsCleaned;
            LastOperationDate = summary.LastOperation?.ToString("MMM dd, yyyy HH:mm") ?? "Never";

            ApplyFilter();
            StatusMessage = $"Loaded {Records.Count} history entries.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't load cleanup history. Please try again.";
            DiagnosticLogger.Error("CleanupHistoryVM", "Failed to load history", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (Records.Count == 0)
            return;

        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Delete all {Records.Count} cleanup history record(s)? This cannot be undone.", "Clear history"))
            return;

        if (!CleanupHistoryService.ClearHistory())
        {
            StatusMessage = "Couldn't clear the history file. Please try again.";
            return;
        }

        Records.Clear();
        FilteredRecords.Clear();
        TotalOperations = 0;
        TotalBytesFreed = 0;
        TotalBytesFreedDisplay = "0 B";
        TotalItemsCleaned = 0;
        LastOperationDate = "Never";
        StatusMessage = "History cleared.";
    }

    [RelayCommand]
    private void ExportHistory()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Cleanup History",
                Filter = "Text File (*.txt)|*.txt|All Files (*.*)|*.*",
                FileName = $"AuraClean_History_{DateTime.Now:yyyyMMdd}.txt",
                DefaultExt = ".txt"
            };

            if (dialog.ShowDialog() == true)
            {
                var text = CleanupHistoryService.ExportAsText();
                System.IO.File.WriteAllText(dialog.FileName, text);
                StatusMessage = $"History exported to {dialog.FileName}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't save the export. Check that the location is writable and try again.";
            DiagnosticLogger.Error("CleanupHistoryVM", "Export failed", ex);
        }
    }

    [RelayCommand]
    private void CopyToClipboard()
    {
        try
        {
            var text = CleanupHistoryService.ExportAsText();
            System.Windows.Clipboard.SetText(text);
            StatusMessage = "History copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't copy to clipboard. Please try again.";
            DiagnosticLogger.Error("CleanupHistoryVM", "Copy to clipboard failed", ex);
        }
    }

    [RelayCommand]
    private void FilterByType(string type)
    {
        FilterType = type;
    }

    private void ApplyFilter()
    {
        var filtered = Records.AsEnumerable();

        if (FilterType != "All")
        {
            filtered = filtered.Where(r => r.OperationType.ToDisplayString() == FilterType);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var query = SearchText.Trim();
            filtered = filtered.Where(r =>
                r.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (r.Details ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        FilteredRecords = new ObservableCollection<CleanupRecord>(filtered);
    }
}
