using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

namespace AuraClean.ViewModels;

/// <summary>
/// Message sent when the quarantine manifest has changed (items added/removed).
/// </summary>
public sealed class QuarantineChangedMessage
{
    public static readonly QuarantineChangedMessage Instance = new();
}

/// <summary>
/// ViewModel for the Quarantine Manager page.
/// Manages quarantined files with restore, purge, and manual quarantine operations.
/// </summary>
public partial class QuarantineViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<QuarantineEntryItem> _entries = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "No quarantined items.";
    [ObservableProperty] private int _totalItems;
    [ObservableProperty] private string _totalSizeDisplay = "0 B";
    [ObservableProperty] private int _expiredCount;
    [ObservableProperty] private string _quarantinePath = string.Empty;

    public string SmartDeleteLabel
    {
        get
        {
            var count = Entries.Count(e => e.IsSelected);
            return count > 0 ? $"Delete {count} item{(count != 1 ? "s" : "")}" : "Delete Selected";
        }
    }

    public string SmartPurgeLabel => ExpiredCount > 0
        ? $"Purge {ExpiredCount} expired"
        : "Purge Expired";

    public QuarantineViewModel()
    {
        QuarantinePath = QuarantineService.GetQuarantineDirectory();
        LoadEntries();

        // Listen for external quarantine changes (e.g. from ThreatScanner)
        WeakReferenceMessenger.Default.Register<QuarantineChangedMessage>(this, (_, _) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                LoadEntries();
            else
                dispatcher.BeginInvoke(LoadEntries);
        });
    }

    [RelayCommand]
    private void LoadEntries()
    {
        try
        {
            var entries = QuarantineService.GetAllEntries();
            var items = entries
                .OrderByDescending(e => e.QuarantinedAt)
                .Select(e => new QuarantineEntryItem(e))
                .ToList();

            Entries = new ObservableCollection<QuarantineEntryItem>(items);
            HookEntrySelectionEvents();

            var stats = QuarantineService.GetStats();
            TotalItems = stats.TotalItems;
            TotalSizeDisplay = FormatHelper.FormatBytes(stats.TotalSizeBytes);
            ExpiredCount = entries.Count(e => e.IsExpired);

            StatusMessage = $"{TotalItems} items in quarantine ({TotalSizeDisplay}).";
            OnPropertyChanged(nameof(SmartPurgeLabel));
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't load quarantine data. Please try again.";
            DiagnosticLogger.Error("QuarantineVM", "LoadEntries failed", ex);
        }
    }

    [RelayCommand]
    private async Task AddFilesToQuarantineAsync()
    {
        if (IsBusy) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Select files to quarantine",
            Filter = "All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        var blocked = dialog.FileNames.Where(f => !PathSafety.IsSafeToDeleteFile(f, out _)).ToList();
        var allowed = dialog.FileNames.Except(blocked, StringComparer.OrdinalIgnoreCase).ToList();

        IsBusy = true;
        StatusMessage = "Quarantining files...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var results = await QuarantineService.QuarantineFilesAsync(allowed, "Manual quarantine", progress);

            LoadEntries();
            StatusMessage = $"Quarantined {results.Count} of {dialog.FileNames.Length} file(s)." +
                            (blocked.Count > 0 ? $" {blocked.Count} Windows/system file(s) were skipped." : string.Empty);

            CleanupHistoryService.Record(CleanupOperationType.ManualQuarantine, results.Count,
                results.Sum(r => r.FileSizeBytes), $"Manually quarantined {results.Count} file(s)");
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't quarantine the selected files. They may be in use.";
            DiagnosticLogger.Error("QuarantineVM", "AddFilesToQuarantineAsync failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        if (IsBusy) return;

        var selected = Entries.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No items selected for restoration.";
            return;
        }

        var flagged = selected.Count(e => e.Reason.StartsWith("Threat detected", StringComparison.OrdinalIgnoreCase));
        if (flagged > 0 && !SafetyPromptService.ConfirmDestructiveAction(
                $"{flagged} of the selected file(s) were quarantined as threats. Restoring them puts them back " +
                "where they can run again.\n\nRestore anyway?", "Restore flagged files"))
        {
            StatusMessage = "Restore cancelled.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Restoring files...";

        int restored = 0, failed = 0;
        long restoredBytes = 0;
        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            foreach (var item in selected)
            {
                if (await QuarantineService.RestoreFileAsync(item.Entry.Id, progress))
                {
                    restored++;
                    restoredBytes += item.FileSizeBytes;
                }
                else
                {
                    failed++;
                }
            }

            CleanupHistoryService.Record(CleanupOperationType.QuarantineRestore, restored, 0,
                $"Restored {restored} file(s) ({FormatHelper.FormatBytes(restoredBytes)})");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("QuarantineVM", "RestoreSelectedAsync failed", ex);
        }
        finally
        {
            LoadEntries();
            StatusMessage = $"Restored {restored} file(s)." + (failed > 0 ? $" {failed} failed." : "");
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PurgeSelectedAsync()
    {
        if (IsBusy) return;

        var selected = Entries.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No items selected for purging.";
            return;
        }

        if (SafetyPromptService.IsDryRunEnabled())
        {
            StatusMessage = $"Dry run: would permanently delete {selected.Count} quarantined file(s).";
            return;
        }

        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Permanently delete {selected.Count} selected quarantined file(s)? They can no longer be restored."))
        {
            StatusMessage = "Quarantine purge cancelled.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Permanently deleting selected files...";

        int purged = 0;
        long purgedBytes = 0;
        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            foreach (var item in selected)
            {
                if (await QuarantineService.PurgeFileAsync(item.Entry.Id, progress))
                {
                    purged++;
                    purgedBytes += item.FileSizeBytes;
                }
            }

            CleanupHistoryService.Record(CleanupOperationType.QuarantinePurge, purged, purgedBytes,
                $"Permanently deleted {purged} quarantined file(s)");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("QuarantineVM", "PurgeSelectedAsync failed", ex);
        }
        finally
        {
            LoadEntries();
            StatusMessage = $"Permanently deleted {purged} file(s).";
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PurgeExpiredAsync()
    {
        if (IsBusy) return;

        if (ExpiredCount == 0)
        {
            StatusMessage = "No expired items to purge.";
            return;
        }

        if (SafetyPromptService.IsDryRunEnabled())
        {
            StatusMessage = $"Dry run: would purge {ExpiredCount} expired quarantined item(s).";
            return;
        }

        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Permanently delete {ExpiredCount} expired quarantined item(s)?"))
        {
            StatusMessage = "Expired quarantine purge cancelled.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Purging expired items...";

        int count = 0;
        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            count = await QuarantineService.PurgeExpiredAsync(progress);
            CleanupHistoryService.Record(CleanupOperationType.QuarantinePurge, count, 0,
                $"Purged {count} expired quarantined item(s)");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("QuarantineVM", "PurgeExpiredAsync failed", ex);
        }
        finally
        {
            LoadEntries();
            StatusMessage = count > 0
                ? $"Purged {count} expired item(s)."
                : "No expired items were purged.";
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Entries)
            item.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var item in Entries)
            item.IsSelected = false;
    }

    private void HookEntrySelectionEvents()
    {
        foreach (var item in Entries)
        {
            item.PropertyChanged -= OnEntryPropertyChanged;
            item.PropertyChanged += OnEntryPropertyChanged;
        }
        OnPropertyChanged(nameof(SmartDeleteLabel));
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuarantineEntryItem.IsSelected))
            OnPropertyChanged(nameof(SmartDeleteLabel));
    }

    [RelayCommand]
    private void OpenQuarantineFolder()
    {
        var path = QuarantineService.GetQuarantineDirectory();
        if (!Directory.Exists(path))
            StatusMessage = "Quarantine folder does not exist yet.";
        else if (!ShellHelper.RevealInExplorer(path))
            StatusMessage = "Couldn't open the quarantine folder.";
    }
}

/// <summary>
/// Wrapper around QuarantineEntry to add IsSelected for UI binding.
/// </summary>
public partial class QuarantineEntryItem : ObservableObject
{
    public QuarantineEntry Entry { get; }

    [ObservableProperty] private bool _isSelected;

    public QuarantineEntryItem(QuarantineEntry entry)
    {
        Entry = entry;
    }

    // Convenience pass-through properties for XAML binding
    public string FileName => Entry.FileName;
    public string OriginalPath => Entry.OriginalPath;
    public string Reason => Entry.Reason;
    public string QuarantinedAtDisplay => Entry.QuarantinedAtDisplay;
    public string SizeDisplay => Entry.SizeDisplay;
    public string ExpiresIn => Entry.ExpiresIn;
    public bool IsExpired => Entry.IsExpired;
    public long FileSizeBytes => Entry.FileSizeBytes;
}
