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

    public ObservableCollection<string> FilterTypes { get; } =
    [
        "All", "System Cleanup", "Browser Privacy Clean",
        "Registry Cleanup", "Program Uninstall", "Duplicate Removal",
        "Large File Removal", "RAM Boost", "Secure Shred", "Quarantine Purge"
    ];

    public CleanupHistoryViewModel()
    {
        LoadHistory();
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

            var summary = CleanupHistoryService.GetSummary();
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
            StatusMessage = $"Error loading history: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        CleanupHistoryService.ClearHistory();
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
            StatusMessage = $"Export failed: {ex.Message}";
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
            StatusMessage = $"Copy failed: {ex.Message}";
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
            filtered = filtered.Where(r =>
                r.Summary.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                r.Details.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        FilteredRecords = new ObservableCollection<CleanupRecord>(filtered);
    }
}
