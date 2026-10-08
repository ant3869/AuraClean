using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System.Collections.ObjectModel;
using System.IO;

namespace AuraClean.ViewModels;

/// <summary>
/// Message sent whenever something outside the Leftover Backups page changes the backup
/// journal (a Deep Scan removal, or startup's interrupted-operation recovery), so the page's
/// cached ViewModel (held alive by <c>MainViewModel._leftoverRestore</c>) refreshes without
/// requiring the user to revisit the page or click "Check for Interrupted Operations".
/// </summary>
public sealed class LeftoverBackupChangedMessage
{
    public static readonly LeftoverBackupChangedMessage Instance = new();
}

/// <summary>
/// ViewModel for the Leftover Backups page: the recovery experience for uninstall leftovers
/// that <see cref="LeftoverBackupStore"/> moved into a restorable backup instead of deleting.
/// Resolving entries left Pending by an interrupted operation runs idempotently both here
/// (<see cref="RecoverInterruptedAsync"/>) and once at application startup
/// (see <c>MainViewModel.RunStartupMaintenanceAsync</c>); running it again is always safe,
/// since <see cref="LeftoverBackupStore.RecoverInterrupted"/> only resolves entries still
/// Pending and leaves settled ones untouched.
/// </summary>
public partial class LeftoverRestoreViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<LeftoverBackupEntryItem> _entries = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "No leftover backups yet.";
    [ObservableProperty] private int _restorableCount;
    [ObservableProperty] private int _backedUpCount;
    [ObservableProperty] private string _totalSizeDisplay = "0 B";
    [ObservableProperty] private string _backupPath = string.Empty;

    private readonly LeftoverBackupStore _store;

    public LeftoverRestoreViewModel() : this(LeftoverBackupStore.CreateDefault()) { }

    internal LeftoverRestoreViewModel(LeftoverBackupStore store)
    {
        _store = store;
        BackupPath = _store.RootDirectory;
        LoadEntries();

        // Refresh when a Deep Scan removal or startup recovery changes the journal elsewhere;
        // this ViewModel is held alive by MainViewModel for the app's lifetime, so without this
        // the page would show stale data until the user revisits it or clicks Check/Restore.
        WeakReferenceMessenger.Default.Register<LeftoverBackupChangedMessage>(this, (_, _) =>
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
            var items = _store.GetEntries()
                .OrderByDescending(e => e.CreatedUtc)
                .Select(e => new LeftoverBackupEntryItem(e, LeftoverBackupStore.OccupiesOriginalPath(e)))
                .ToList();

            Entries = new ObservableCollection<LeftoverBackupEntryItem>(items);
            RestorableCount = items.Count(i => i.IsRestorable);

            // Only Moved entries still occupy real disk space in the backup folder; a Restored
            // entry's content has already moved back to its original path, so it must not be
            // counted toward current storage usage even though it stays in the list below as
            // history.
            var active = items.Where(i => i.Entry.State == LeftoverBackupState.Moved).ToList();
            BackedUpCount = active.Count;
            TotalSizeDisplay = FormatHelper.FormatBytes(active.Sum(i => i.SizeBytes));

            StatusMessage = Entries.Count == 0
                ? "No leftover backups yet."
                : $"{BackedUpCount} backed-up item(s) using {TotalSizeDisplay}, {RestorableCount} ready to restore.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't load leftover backups. Please try again.";
            DiagnosticLogger.Error("LeftoverRestoreVM", "LoadEntries failed", ex);
        }
    }

    /// <summary>
    /// Resolves any entry left Pending by a crash or an interrupted shutdown. Safe to call any
    /// number of times: entries that are already Moved or Restored are never touched.
    /// </summary>
    [RelayCommand]
    private async Task RecoverInterruptedAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = "Checking for interrupted leftover operations...";
        try
        {
            var report = await Task.Run(_store.RecoverInterrupted);
            LoadEntries();
            StatusMessage = report.Count == 0
                ? "No interrupted leftover operations were found."
                : string.Join(" ", report);
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't check for interrupted operations. Please try again.";
            DiagnosticLogger.Error("LeftoverRestoreVM", "RecoverInterruptedAsync failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync(LeftoverBackupEntryItem? item)
    {
        if (IsBusy || item == null) return;

        if (!item.IsRestorable)
        {
            StatusMessage = item.HasConflict
                ? $"Can't restore: something already exists at {item.OriginalPath}."
                : $"Can't restore: this entry is {item.StateDisplay}, not restorable.";
            return;
        }

        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Restore this item to its original location?\n\n{item.OriginalPath}", "Restore leftover"))
        {
            StatusMessage = "Restore cancelled.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Restoring {Path.GetFileName(item.OriginalPath)}...";
        try
        {
            var (success, message) = await Task.Run(() => _store.Restore(item.Entry.Id));
            if (success)
            {
                // A restore moves the item back from the backup to its original location on the
                // same volume — it frees no disk space, so bytesFreed is 0, not item.SizeBytes
                // (which would otherwise inflate total/per-type cleanup statistics).
                CleanupHistoryService.Record(CleanupOperationType.LeftoverRestore, 1, 0,
                    $"Restored leftover {item.OriginalPath} ({item.ProgramName})");
            }
            StatusMessage = message;
        }
        catch (Exception ex)
        {
            StatusMessage = "Restore failed. Please try again.";
            DiagnosticLogger.Error("LeftoverRestoreVM", "RestoreAsync failed", ex);
        }
        finally
        {
            LoadEntries();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        if (!Directory.Exists(BackupPath))
        {
            StatusMessage = "The leftover backup folder does not exist yet.";
            return;
        }
        if (!ShellHelper.RevealInExplorer(BackupPath))
            StatusMessage = "Couldn't open the leftover backup folder.";
    }
}

/// <summary>
/// Wraps a <see cref="LeftoverBackupEntry"/> for UI binding: display-formatted fields plus the
/// pre-computed conflict/restorability the restore list needs to show before the user acts.
/// </summary>
public sealed class LeftoverBackupEntryItem(LeftoverBackupEntry entry, bool hasConflict)
{
    public LeftoverBackupEntry Entry { get; } = entry;

    public string OriginalPath => Entry.OriginalPath;
    public string ProgramName => Entry.ProgramName;
    public string Evidence => Entry.Evidence;
    public long SizeBytes => Entry.SizeBytes;
    public string SizeDisplay => Helpers.FormatHelper.FormatBytes(Entry.SizeBytes);
    public string CreatedDisplay => Entry.CreatedUtc.ToLocalTime().ToString("MMM dd, yyyy  HH:mm");

    public bool HasConflict { get; } = hasConflict;

    public string StateDisplay => Entry.State switch
    {
        LeftoverBackupState.Pending => "Pending recovery",
        LeftoverBackupState.Moved => HasConflict ? "Needs review" : "Backed up",
        LeftoverBackupState.Restored => "Restored",
        _ => Entry.State.ToString()
    };

    /// <summary>Only a Moved entry with nothing occupying its original path can be restored.</summary>
    public bool IsRestorable => Entry.State == LeftoverBackupState.Moved && !HasConflict;

    public string ConflictMessage => HasConflict
        ? $"Something already exists at {OriginalPath}; restoring would overwrite it, so it is refused."
        : string.Empty;
}
