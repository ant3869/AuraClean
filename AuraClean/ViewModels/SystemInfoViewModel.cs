using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the System Information page.
/// Displays detailed hardware and software information about the system.
/// </summary>
public partial class SystemInfoViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<SystemInfoService.InfoEntry> _entries = [];
    [ObservableProperty] private ObservableCollection<SystemInfoService.InfoEntry> _filteredEntries = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Loading system information...";
    [ObservableProperty] private string _filterCategory = "All";
    [ObservableProperty] private string _searchText = string.Empty;

    public ObservableCollection<string> Categories { get; } =
    [
        "All", "Operating System", "Processor", "Memory",
        "Graphics", "Storage", "Network", "Motherboard", "Runtime"
    ];

    public SystemInfoViewModel()
    {
        _ = LoadInfoAsync().ContinueWith(t =>
        {
            if (t.Exception != null)
                DiagnosticLogger.Warn("SystemInfoViewModel", "LoadInfoAsync failed", t.Exception.InnerException ?? t.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    partial void OnFilterCategoryChanged(string value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void FilterByCategory(string category)
    {
        FilterCategory = category;
    }

    private void ApplyFilter()
    {
        var filtered = Entries.AsEnumerable();

        if (FilterCategory != "All")
            filtered = filtered.Where(e => e.Category == FilterCategory);

        if (!string.IsNullOrWhiteSpace(SearchText))
            filtered = filtered.Where(e =>
                e.Label.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                e.Value.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        FilteredEntries = new ObservableCollection<SystemInfoService.InfoEntry>(filtered);
    }

    [RelayCommand]
    private async Task LoadInfoAsync()
    {
        IsBusy = true;
        StatusMessage = "Collecting system information...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var entries = await SystemInfoService.CollectAllAsync(progress);

            Entries = new ObservableCollection<SystemInfoService.InfoEntry>(entries);
            ApplyFilter();

            StatusMessage = $"Loaded {entries.Count} system properties.";
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
    private void CopyToClipboard()
    {
        try
        {
            var text = SystemInfoService.FormatAsText(new(Entries));
            System.Windows.Clipboard.SetText(text);
            StatusMessage = "System information copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to copy: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Entries.Clear();
        FilteredEntries.Clear();
        await LoadInfoAsync();
    }
}
