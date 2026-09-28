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
    [ObservableProperty] private string _statusMessage = "Ready to manage startup programs.";
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
    [ObservableProperty] private bool _hasScanned;
    public bool HasCheckedItems => SelectedCount > 0;

    public StartupManagerViewModel()
    {
        _ = LoadEntriesAsync().ContinueWith(t =>
        {
            if (t.Exception != null)
                DiagnosticLogger.Warn("StartupManagerViewModel", "LoadEntriesAsync failed", t.Exception.InnerException ?? t.Exception);
        }, TaskContinuationOptions.OnlyOnFaulted);
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
            HookSelectionEvents(Entries);
            ApplyFilter();
            UpdateStats();

            StatusMessage = $"Found {Entries.Count} startup entries ({EnabledCount} enabled, {DisabledCount} disabled).";
            HasScanned = true;
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't load startup programs. Please try again.";
            DiagnosticLogger.Error("StartupManagerVM", "LoadEntriesAsync failed", ex);
            HasScanned = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleSelectedAsync()
    {
        if (IsBusy) return;

        var checkedEntries = FilteredEntries.Where(e => e.IsSelected).ToList();
        if (checkedEntries.Count == 0 && SelectedEntry != null)
            checkedEntries = [SelectedEntry];
        if (checkedEntries.Count == 0)
        {
            StatusMessage = "Select a startup item first.";
            return;
        }

        IsBusy = true;
        int toggled = 0;
        string? lastError = null;

        try
        {
            foreach (var entry in checkedEntries)
            {
                // Enabling only restores an item's original behavior, so both modes can toggle
                // either way; permanently deleting entries stays Advanced-only.
                bool newState = !entry.IsEnabled;
                StatusMessage = newState ? $"Enabling {entry.Name}..." : $"Disabling {entry.Name}...";

                var (success, message) = await StartupManagerService.ToggleStartupEntryAsync(entry, newState);
                if (success) toggled++;
                else lastError = message;
            }
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            DiagnosticLogger.Error("StartupManagerVM", "Toggle failed", ex);
        }
        finally
        {
            UpdateStats();
            ApplyFilter();
            StatusMessage = $"Changed {toggled} of {checkedEntries.Count} startup item(s)." +
                            (lastError != null ? $" Last error: {lastError}" : string.Empty);
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (IsBusy) return;

        if (!IsAdvancedMode)
        {
            StatusMessage = "Turn on Advanced mode to delete startup entries. Normal mode can disable them instead.";
            return;
        }

        var checkedEntries = FilteredEntries.Where(e => e.IsSelected).ToList();
        if (checkedEntries.Count == 0 && SelectedEntry != null)
            checkedEntries = [SelectedEntry];
        if (checkedEntries.Count == 0)
        {
            StatusMessage = "Select a startup item first.";
            return;
        }

        if (SafetyPromptService.IsDryRunEnabled())
        {
            StatusMessage = $"Dry run: would delete {checkedEntries.Count} startup item(s).";
            return;
        }

        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Permanently delete {checkedEntries.Count} startup item(s)? Disabling is reversible; deleting is not " +
                "(registry entries are backed up to %LocalAppData%\\AuraClean\\Backups)."))
        {
            StatusMessage = "Startup deletion cancelled.";
            return;
        }

        IsBusy = true;
        int deleted = 0;
        string? lastError = null;

        try
        {
            foreach (var entry in checkedEntries)
            {
                StatusMessage = $"Deleting {entry.Name}...";
                var (success, message) = await StartupManagerService.DeleteStartupEntryAsync(entry);
                if (success)
                {
                    entry.PropertyChanged -= OnEntryPropertyChanged;
                    Entries.Remove(entry);
                    deleted++;
                }
                else
                {
                    lastError = message;
                }
            }
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            DiagnosticLogger.Error("StartupManagerVM", "Delete failed", ex);
        }
        finally
        {
            ApplyFilter();
            UpdateStats();
            SelectedEntry = null;
            StatusMessage = $"Deleted {deleted} startup item(s)." +
                            (lastError != null ? $" Last error: {lastError}" : string.Empty);
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisableAllHighImpactAsync()
    {
        if (IsBusy) return;

        var highImpact = Entries.Where(e =>
            e.IsEnabled && e.Impact == StartupManagerService.StartupImpact.High).ToList();

        if (highImpact.Count == 0)
        {
            StatusMessage = "No enabled high-impact startup items.";
            return;
        }

        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Disable {highImpact.Count} high-impact startup item(s)?\n\n• " +
                string.Join("\n• ", highImpact.Take(10).Select(e => e.Name)) +
                "\n\nThey can be re-enabled later in Advanced mode.",
                "Disable startup items"))
        {
            StatusMessage = "No changes made.";
            return;
        }

        IsBusy = true;
        int disabled = 0;

        try
        {
            foreach (var entry in highImpact)
            {
                StatusMessage = $"Disabling {entry.Name}...";
                var (success, _) = await StartupManagerService.ToggleStartupEntryAsync(entry, false);
                if (success) disabled++;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("StartupManagerVM", "DisableAllHighImpact failed", ex);
        }
        finally
        {
            UpdateStats();
            ApplyFilter();
            StatusMessage = $"Disabled {disabled} of {highImpact.Count} high-impact startup item(s).";
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFileLocation()
    {
        if (SelectedEntry == null || string.IsNullOrEmpty(SelectedEntry.FilePath)) return;

        if (!ShellHelper.RevealInExplorer(SelectedEntry.FilePath))
            StatusMessage = "The program file could not be found.";
    }
}
