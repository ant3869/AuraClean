using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the Startup Manager view.
/// Manages enumerating, enabling/disabling, and deleting startup programs.
/// </summary>
public partial class StartupManagerViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<StartupManagerService.StartupEntry> _entries = [];
    [ObservableProperty] private ObservableCollection<StartupManagerService.StartupEntry> _filteredEntries = [];
    [ObservableProperty] private StartupManagerService.StartupEntry? _selectedEntry;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Click Refresh to load startup programs.";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _showDisabledOnly;
    [ObservableProperty] private bool _showEnabledOnly;

    // Stats
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _enabledCount;
    [ObservableProperty] private int _disabledCount;
    [ObservableProperty] private int _highImpactCount;

    // Selection
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _isAllSelected;
    public bool HasCheckedItems => SelectedCount > 0;

    public StartupManagerViewModel()
    {
        _ = LoadEntriesAsync().ContinueWith(t =>
            System.Diagnostics.Debug.WriteLine($"[StartupManagerViewModel] LoadEntriesAsync failed: {t.Exception}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnShowDisabledOnlyChanged(bool value) => ApplyFilter();
    partial void OnShowEnabledOnlyChanged(bool value) => ApplyFilter();

    partial void OnIsAllSelectedChanged(bool value)
    {
        foreach (var entry in FilteredEntries)
            entry.IsSelected = value;
        UpdateSelectionCount();
    }

    private void HookSelectionEvents(IEnumerable<StartupManagerService.StartupEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.PropertyChanged -= OnEntryPropertyChanged;
            entry.PropertyChanged += OnEntryPropertyChanged;
        }
    }

    private void OnEntryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StartupManagerService.StartupEntry.IsSelected))
            UpdateSelectionCount();
    }

    private void UpdateSelectionCount()
    {
        SelectedCount = FilteredEntries.Count(e => e.IsSelected);
        OnPropertyChanged(nameof(HasCheckedItems));
    }

    private void ApplyFilter()
    {
        var filtered = Entries.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            filtered = filtered.Where(e =>
                e.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                e.Publisher.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                e.Command.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        if (ShowEnabledOnly)
            filtered = filtered.Where(e => e.IsEnabled);
        if (ShowDisabledOnly)
            filtered = filtered.Where(e => !e.IsEnabled);

        FilteredEntries = new ObservableCollection<StartupManagerService.StartupEntry>(filtered);
        HookSelectionEvents(FilteredEntries);
        UpdateSelectionCount();
    }

    private void UpdateStats()
    {
        TotalCount = Entries.Count;
        EnabledCount = Entries.Count(e => e.IsEnabled);
        DisabledCount = Entries.Count(e => !e.IsEnabled);
        HighImpactCount = Entries.Count(e => e.Impact == StartupManagerService.StartupImpact.High);
    }

    [RelayCommand]
    private async Task LoadEntriesAsync()
    {
        IsBusy = true;
        StatusMessage = "Loading startup programs...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var entries = await StartupManagerService.GetStartupEntriesAsync(progress);

            Entries = new ObservableCollection<StartupManagerService.StartupEntry>(entries);
            ApplyFilter();
            UpdateStats();

            StatusMessage = $"Found {Entries.Count} startup entries ({EnabledCount} enabled, {DisabledCount} disabled).";
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
    private async Task ToggleSelectedAsync()
    {
        var checkedEntries = FilteredEntries.Where(e => e.IsSelected).ToList();
        if (checkedEntries.Count == 0 && SelectedEntry != null)
            checkedEntries = [SelectedEntry];
        if (checkedEntries.Count == 0) return;

        IsBusy = true;
        int toggled = 0;

        foreach (var entry in checkedEntries)
        {
            bool newState = !entry.IsEnabled;
            StatusMessage = newState
                ? $"Enabling {entry.Name}..."
                : $"Disabling {entry.Name}...";

            try
            {
                var (success, message) = await StartupManagerService.ToggleStartupEntryAsync(entry, newState);
                if (success) toggled++;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        UpdateStats();
        ApplyFilter();
        StatusMessage = $"Toggled {toggled} startup item(s).";
        IsBusy = false;
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var checkedEntries = FilteredEntries.Where(e => e.IsSelected).ToList();
        if (checkedEntries.Count == 0 && SelectedEntry != null)
            checkedEntries = [SelectedEntry];
        if (checkedEntries.Count == 0) return;

        IsBusy = true;
        int deleted = 0;

        foreach (var entry in checkedEntries)
        {
            StatusMessage = $"Deleting {entry.Name}...";

            try
            {
                var (success, message) = await StartupManagerService.DeleteStartupEntryAsync(entry);
                if (success)
                {
                    Entries.Remove(entry);
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        ApplyFilter();
        UpdateStats();
        SelectedEntry = null;
        StatusMessage = $"Deleted {deleted} startup item(s).";
        IsBusy = false;
    }

    [RelayCommand]
    private async Task DisableAllHighImpactAsync()
    {
        IsBusy = true;
        int disabled = 0;

        var highImpact = Entries.Where(e =>
            e.IsEnabled && e.Impact == StartupManagerService.StartupImpact.High).ToList();

        foreach (var entry in highImpact)
        {
            StatusMessage = $"Disabling {entry.Name}...";
            var (success, _) = await StartupManagerService.ToggleStartupEntryAsync(entry, false);
            if (success) disabled++;
        }

        UpdateStats();
        ApplyFilter();
        StatusMessage = $"Disabled {disabled} high-impact startup items.";
        IsBusy = false;
    }

    [RelayCommand]
    private void OpenFileLocation()
    {
        if (SelectedEntry == null || string.IsNullOrEmpty(SelectedEntry.FilePath)) return;

        try
        {
            if (System.IO.File.Exists(SelectedEntry.FilePath))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{SelectedEntry.FilePath}\"");
            }
        }
        catch { }
    }
}
