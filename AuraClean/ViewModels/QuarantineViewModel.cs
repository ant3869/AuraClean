using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the Quarantine Manager page.
/// Manages quarantined files with restore, purge, and manual quarantine operations.
/// </summary>
public partial class QuarantineViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<QuarantineEntryItem> _entries = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Quarantine ready.";
    [ObservableProperty] private int _totalItems;
    [ObservableProperty] private string _totalSizeDisplay = "0 B";
    [ObservableProperty] private int _expiredCount;
    [ObservableProperty] private string _quarantinePath = string.Empty;

    public QuarantineViewModel()
    {
        QuarantinePath = QuarantineService.GetQuarantineDirectory();
        LoadEntries();
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

            var stats = QuarantineService.GetStats();
            TotalItems = stats.TotalItems;
            TotalSizeDisplay = FormatHelper.FormatBytes(stats.TotalSizeBytes);
            ExpiredCount = entries.Count(e => e.IsExpired);

            StatusMessage = $"{TotalItems} items in quarantine ({TotalSizeDisplay}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading quarantine: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AddFilesToQuarantineAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Select files to quarantine",
            Filter = "All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        StatusMessage = "Quarantining files...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var results = await QuarantineService.QuarantineFilesAsync(
                dialog.FileNames, "Manual quarantine", progress);

            LoadEntries();
            StatusMessage = $"Quarantined {results.Count} file(s).";

            // Log to history
            if (results.Count > 0)
            {
                var settings = SettingsService.Load();
                if (settings.LogCleanupOperations)
                {
                    CleanupHistoryService.LogOperation(new CleanupRecord
                    {
                        OperationType = CleanupOperationType.QuarantinePurge,
                        ItemCount = results.Count,
                        BytesFreed = results.Sum(r => r.FileSizeBytes),
                        Details = $"Manually quarantined {results.Count} file(s)"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Quarantine failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        var selected = Entries.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No items selected for restoration.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Restoring files...";

        int restored = 0;
        int failed = 0;
        var progress = new Progress<string>(msg => StatusMessage = msg);

        foreach (var item in selected)
        {
            try
            {
                if (await QuarantineService.RestoreFileAsync(item.Entry.Id, progress))
                    restored++;
                else
                    failed++;
            }
            catch
            {
                failed++;
            }
        }

        LoadEntries();
        StatusMessage = $"Restored {restored} file(s)." + (failed > 0 ? $" {failed} failed." : "");
        IsBusy = false;
    }

    [RelayCommand]
    private async Task PurgeSelectedAsync()
    {
        var selected = Entries.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No items selected for purging.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Permanently deleting selected files...";

        int purged = 0;
        var progress = new Progress<string>(msg => StatusMessage = msg);

        foreach (var item in selected)
        {
            if (await QuarantineService.PurgeFileAsync(item.Entry.Id, progress))
                purged++;
        }

        LoadEntries();
        StatusMessage = $"Permanently deleted {purged} file(s).";
        IsBusy = false;
    }

    [RelayCommand]
    private async Task PurgeExpiredAsync()
    {
        IsBusy = true;
        StatusMessage = "Purging expired items...";

        var progress = new Progress<string>(msg => StatusMessage = msg);
        int count = await QuarantineService.PurgeExpiredAsync(progress);

        LoadEntries();
        StatusMessage = count > 0
            ? $"Purged {count} expired item(s)."
            : "No expired items to purge.";
        IsBusy = false;
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

    [RelayCommand]
    private void OpenQuarantineFolder()
    {
        try
        {
            var path = QuarantineService.GetQuarantineDirectory();
            if (Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", path);
            else
                StatusMessage = "Quarantine folder does not exist yet.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open folder: {ex.Message}";
        }
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
