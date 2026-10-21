using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the Large File Finder feature.
/// Scans directories for files exceeding a configurable size threshold.
/// </summary>
public partial class LargeFileFinderViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<LargeFileFinderService.LargeFileEntry> _files = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Select a drive or folder and set a minimum file size.";
    [ObservableProperty] private string _scanPath = string.Empty;
    [ObservableProperty] private int _minimumSizeMB = 100;
    [ObservableProperty] private int _progressFilesScanned;
    [ObservableProperty] private int _progressFilesFound;
    [ObservableProperty] private string _progressCurrentDir = string.Empty;
    [ObservableProperty] private LargeFileFinderService.LargeFileEntry? _selectedFile;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private long _selectedSize;
    [ObservableProperty] private bool _isAllSelected;

    public string FormattedSelectedSize => FormatHelper.FormatBytes(SelectedSize);

    // Results summary
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private int _totalFilesScanned;
    [ObservableProperty] private int _resultCount;
    [ObservableProperty] private long _totalSizeFound;
    [ObservableProperty] private int _accessErrors;

    // Files that a normal delete couldn't remove (in use / access denied), offered for Force Delete.
    [ObservableProperty] private bool _hasFailedDeletes;
    [ObservableProperty] private int _failedDeleteCount;
    private List<LargeFileFinderService.LargeFileEntry> _lastFailedDeletes = [];

    // Drives
    [ObservableProperty] private ObservableCollection<DriveOption> _drives = [];
    [ObservableProperty] private DriveOption? _selectedDrive;

    // Filter
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private string _filterCategory = "All";
    [ObservableProperty] private ObservableCollection<LargeFileFinderService.LargeFileEntry> _filteredFiles = [];

    public string FormattedTotalSize => FormatHelper.FormatBytes(TotalSizeFound);

    public ObservableCollection<string> Categories { get; } =
    [
        "All", "Video", "Audio", "Archive", "Disk Image",
        "Executable", "Backup / Temp", "Virtual Disk",
        "Database", "Log / Text", "Image (Large)", "Other"
    ];

    public ObservableCollection<int> SizePresets { get; } = [10, 25, 50, 100, 250, 500, 1000];

    private CancellationTokenSource? _cts;

    public LargeFileFinderViewModel()
    {
        var settings = SettingsService.Load();
        MinimumSizeMB = (int)Math.Clamp(settings.DefaultLargeFileSizeMb, 1L, MaxMinimumSizeMB);
        MinimumSizeText = MinimumSizeMB.ToString(System.Globalization.CultureInfo.InvariantCulture);

        LoadDrives();

        if (Drives.Count > 0)
        {
            SelectedDrive = Drives[0];
            ScanPath = Drives[0].Path;
        }
    }

    partial void OnSelectedDriveChanged(DriveOption? value)
    {
        if (value != null)
            ScanPath = value.Path;
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnFilterCategoryChanged(string value) => ApplyFilter();

    partial void OnIsAllSelectedChanged(bool value)
    {
        foreach (var file in FilteredFiles)
            file.IsSelected = value;
        UpdateSelectionCount();
    }

    private void LoadDrives()
    {
        var driveList = LargeFileFinderService.GetDrives();
        Drives.Clear();
        foreach (var (name, label, totalSize, freeSpace) in driveList)
        {
            Drives.Add(new DriveOption
            {
                Path = name,
                Label = $"{name} {label}",
                FreeSpace = FormatHelper.FormatBytes(freeSpace),
                TotalSize = FormatHelper.FormatBytes(totalSize),
            });
        }
    }

    private void ApplyFilter()
    {
        var filtered = Files.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(FilterText))
        {
            filtered = filtered.Where(f =>
                f.FileName.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ||
                f.Directory.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
        }

        if (FilterCategory != "All")
        {
            filtered = filtered.Where(f => f.Category == FilterCategory);
        }

        FilteredFiles = new ObservableCollection<LargeFileFinderService.LargeFileEntry>(filtered);
        UpdateSelectionCount();
    }

    private void HookSelectionEvents(IEnumerable<LargeFileFinderService.LargeFileEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.SelectionChanged -= OnItemSelectionChanged;
            entry.SelectionChanged += OnItemSelectionChanged;
        }
    }

    private void OnItemSelectionChanged() => UpdateSelectionCount();

    private void UpdateSelectionCount()
    {
        SelectedCount = FilteredFiles.Count(f => f.IsSelected);
        SelectedSize = FilteredFiles.Where(f => f.IsSelected).Sum(f => f.SizeBytes);
        OnPropertyChanged(nameof(FormattedSelectedSize));
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a folder to scan for large files",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            UseFolder(dialog.FolderName);
    }

    /// <summary>
    /// Makes <paramref name="folder"/> the scan location AND shows it: the location box is bound to the drive list,
    /// so a folder that is not a drive used to leave the box blank. It is added as an entry (replacing any earlier
    /// browsed folder) and selected.
    /// </summary>
    public void UseFolder(string folder)
    {
        var existing = Drives.FirstOrDefault(d => !d.IsFolder &&
            string.Equals(d.Path, folder, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectedDrive = existing;
            return;
        }

        foreach (var old in Drives.Where(d => d.IsFolder).ToList())
            Drives.Remove(old);

        var entry = new DriveOption { Path = folder, IsFolder = true };
        Drives.Insert(0, entry);
        SelectedDrive = entry;
        ScanPath = folder;
    }

    /// <summary>Largest accepted minimum size: 1 TB expressed in MB.</summary>
    public const int MaxMinimumSizeMB = 1_048_576;

    /// <summary>
    /// Validates the minimum-size text. Returns null when valid (and the parsed megabytes), otherwise a message
    /// meant for the user — never a framework conversion error.
    /// </summary>
    public static string? ValidateMinimumSize(string? text, out int megabytes)
    {
        megabytes = 0;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "Enter a size in MB, for example 100.";
        if (!trimmed.All(char.IsAsciiDigit))
            return "Use a whole number of MB, for example 100.";
        if (!int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value > MaxMinimumSizeMB)
            return $"Too large. The maximum is {MaxMinimumSizeMB:N0} MB.";
        if (value < 1)
            return "The minimum size must be at least 1 MB.";

        megabytes = value;
        return null;
    }

    /// <summary>The text in the Min. Size box (free-typed or picked from the presets).</summary>
    [ObservableProperty] private string _minimumSizeText = "100";

    /// <summary>User-facing problem with <see cref="MinimumSizeText"/>; empty when the input is valid.</summary>
    [ObservableProperty] private string _minSizeError = string.Empty;

    partial void OnMinimumSizeTextChanged(string value)
    {
        var error = ValidateMinimumSize(value, out var mb);
        MinSizeError = error ?? string.Empty;
        if (error == null)
            MinimumSizeMB = mb;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;

        // Validate first: invalid input must never start a scan (the scan used to silently fall back to a default).
        var sizeError = ValidateMinimumSize(MinimumSizeText, out var validatedMb);
        if (sizeError != null)
        {
            MinSizeError = sizeError;
            StatusMessage = $"Can't scan yet — Min. Size: {sizeError}";
            return;
        }
        MinimumSizeMB = validatedMb;

        if (string.IsNullOrWhiteSpace(ScanPath))
        {
            StatusMessage = "Please select a drive or folder to scan.";
            return;
        }

        if (!Directory.Exists(ScanPath))
        {
            StatusMessage = "The selected path does not exist.";
            return;
        }

        IsBusy = true;
        HasResults = false;
        Files.Clear();
        FilteredFiles.Clear();
        ProgressFilesScanned = 0;
        ProgressFilesFound = 0;
        _lastFailedDeletes = [];
        FailedDeleteCount = 0;
        HasFailedDeletes = false;

        long minBytes = (long)Math.Max(1, MinimumSizeMB) * 1024 * 1024;
        StatusMessage = $"Scanning {ScanPath} for files ≥ {MinimumSizeMB} MB...";

        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<(int filesScanned, int found, string currentDir)>(p =>
            {
                ProgressFilesScanned = p.filesScanned;
                ProgressFilesFound = p.found;
                ProgressCurrentDir = p.currentDir.Length > 60
                    ? "..." + p.currentDir[^57..]
                    : p.currentDir;
            });

            var result = await LargeFileFinderService.ScanAsync(
                ScanPath, minBytes, 500, false, progress, _cts.Token);

            Files = new ObservableCollection<LargeFileFinderService.LargeFileEntry>(result.Files);
            HookSelectionEvents(Files);
            TotalFilesScanned = result.TotalFilesScanned;
            ResultCount = result.Files.Count;
            TotalSizeFound = result.TotalSizeFound;
            AccessErrors = result.AccessErrors;
            HasResults = true;

            OnPropertyChanged(nameof(FormattedTotalSize));
            ApplyFilter();

            StatusMessage = $"Found {result.Files.Count} files ({FormatHelper.FormatBytes(result.TotalSizeFound)}) " +
                            $"out of {FormatHelper.FormatCount(result.TotalFilesScanned)} scanned. " +
                            (result.AccessErrors > 0 ? $"{result.AccessErrors} access errors." : "");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during the scan. Please try again.";
            DiagnosticLogger.Error("LargeFileFinderVM", "Scan failed", ex);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _cts?.Cancel();
    }

    [RelayCommand]
    private void OpenFileLocation()
    {
        if (SelectedFile == null) return;

        if (!ShellHelper.RevealInExplorer(SelectedFile.FullPath))
            StatusMessage = "Couldn't open the file location.";
    }

    /// <summary>Normal mode removes to the Recycle Bin; Advanced mode deletes permanently.</summary>
    public string DeleteButtonLabel => IsAdvancedMode ? "Delete" : "Move to Recycle Bin";

    public string DeleteButtonHint => IsAdvancedMode
        ? "Permanently delete the ticked files"
        : "Move the ticked files to the Recycle Bin (restorable)";

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (IsBusy) return;

        var checkedFiles = FilteredFiles.Where(f => f.IsSelected).ToList();
        if (checkedFiles.Count == 0)
        {
            StatusMessage = "Tick the files you want to remove first.";
            return;
        }

        // Normal mode never deletes permanently: files go to the Recycle Bin.
        var useRecycleBin = !IsAdvancedMode;

        var blocked = checkedFiles.Where(f => !PathSafety.IsSafeToDeleteFile(f.FullPath, out _)).ToList();
        checkedFiles = checkedFiles.Except(blocked).ToList();
        if (checkedFiles.Count == 0)
        {
            StatusMessage = "The selected files are Windows or system files and can't be deleted here.";
            return;
        }

        var bytes = checkedFiles.Sum(f => f.SizeBytes);
        if (SafetyPromptService.IsDryRunEnabled())
        {
            StatusMessage = $"Dry run: would delete {checkedFiles.Count} file(s) ({FormatHelper.FormatBytes(bytes)}).";
            return;
        }

        var risky = checkedFiles.Count(f => PathSafety.IsLikelyAppDependency(f.FullPath));
        var confirmText =
            (useRecycleBin
                ? $"Move {checkedFiles.Count} selected large file(s) ({FormatHelper.FormatBytes(bytes)}) to the Recycle Bin?\n\n" +
                  "You can restore them from the Recycle Bin; the space is freed when it is emptied."
                : $"Permanently delete {checkedFiles.Count} selected large file(s) ({FormatHelper.FormatBytes(bytes)})?\n\nThis cannot be undone.") +
            (blocked.Count > 0 ? $"\n\n{blocked.Count} system file(s) will be skipped." : string.Empty) +
            (risky > 0
                ? $"\n\nWarning: {risky} of these look like program files or live inside an app folder " +
                  "(AppData, virtual environments, package caches). Removing them can break the app that uses them."
                : string.Empty);

        var confirmed = risky > 0
            ? SafetyPromptService.ConfirmSecurityDecision(confirmText, "Remove large files")
            : SafetyPromptService.ConfirmDestructiveAction(confirmText, "Remove large files");
        if (!confirmed)
        {
            StatusMessage = "Large file removal cancelled.";
            return;
        }

        IsBusy = true;
        StatusMessage = useRecycleBin
            ? $"Moving {checkedFiles.Count} file(s) to the Recycle Bin..."
            : $"Deleting {checkedFiles.Count} file(s)...";

        int deleted = 0;
        long freedBytes = 0;
        var failedFiles = new List<string>();
        var failedEntries = new List<LargeFileFinderService.LargeFileEntry>();

        try
        {
            var outcomes = await Task.Run(() => checkedFiles
                .Select(file => (File: file, Result: LargeFileFinderService.DeleteFile(file.FullPath, useRecycleBin)))
                .ToList());

            foreach (var (file, (success, message)) in outcomes)
            {
                if (success)
                {
                    freedBytes += file.SizeBytes;
                    Files.Remove(file);
                    deleted++;
                }
                else
                {
                    failedFiles.Add(file.FileName);
                    failedEntries.Add(file);
                    DiagnosticLogger.Warn("LargeFileFinderVM", $"Failed to delete {file.FullPath}: {message}");
                }
            }

            CleanupHistoryService.Record(CleanupOperationType.LargeFileRemoval, deleted, freedBytes,
                $"Large files in {ScanPath}{(useRecycleBin ? " (Recycle Bin)" : string.Empty)}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("LargeFileFinderVM", "DeleteSelectedAsync failed", ex);
        }
        finally
        {
            TotalSizeFound = Math.Max(0, TotalSizeFound - freedBytes);
            OnPropertyChanged(nameof(FormattedTotalSize));
            ResultCount = Files.Count;
            HasResults = true;
            ApplyFilter();
            SelectedFile = null;
            _lastFailedDeletes = failedEntries;
            FailedDeleteCount = failedEntries.Count;
            HasFailedDeletes = failedEntries.Count > 0;
            StatusMessage = (useRecycleBin
                    ? $"Moved {deleted} file(s) ({FormatHelper.FormatBytes(freedBytes)}) to the Recycle Bin."
                    : $"Deleted {deleted} file(s), freed {FormatHelper.FormatBytes(freedBytes)}.") +
                (failedFiles.Count > 0 ? $" {failedFiles.Count} failed (in use or access denied)." : "") +
                (blocked.Count > 0 ? $" {blocked.Count} system file(s) skipped." : "");
            IsBusy = false;
        }
    }

    /// <summary>
    /// Force-deletes files a normal delete left behind because they were locked or access was
    /// denied. Advanced mode only: this can terminate the program holding the file open, or — if
    /// that fails too — schedule the file for deletion on the next restart. ForceDeleteService
    /// itself re-checks every path against the same protected-path rules as a normal delete and
    /// refuses to touch Windows/system files or anything on the never-kill process list.
    /// </summary>
    [RelayCommand]
    private async Task ForceDeleteFailedAsync()
    {
        if (IsBusy || !HasFailedDeletes) return;

        if (!IsAdvancedMode)
        {
            StatusMessage = "Turn on Advanced mode to force-delete locked files.";
            return;
        }

        var targets = _lastFailedDeletes.ToList();
        var warning =
            $"Force Delete will try to identify and close the program(s) holding {targets.Count} file(s) open " +
            "so they can be removed. Closing a program this way loses any unsaved work in it. " +
            "A file that still can't be removed is scheduled for deletion on the next restart instead.\n\n" +
            "This cannot be undone. Continue?";

        if (!SafetyPromptService.ConfirmSecurityDecision(warning, "Force Delete locked files"))
        {
            StatusMessage = "Force delete cancelled.";
            return;
        }

        bool dryRun = SafetyPromptService.IsDryRunEnabled();

        IsBusy = true;
        StatusMessage = dryRun
            ? $"[Preview] Checking what force-deleting {targets.Count} file(s) would require..."
            : $"Force-deleting {targets.Count} file(s)...";

        int deleted = 0, scheduledForReboot = 0, stillFailed = 0;
        long freedBytes = 0;
        var terminatedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remainingFailed = new List<LargeFileFinderService.LargeFileEntry>();

        try
        {
            foreach (var entry in targets)
            {
                var progress = new Progress<string>(msg => StatusMessage = msg);
                var result = await ForceDeleteService.ForceDeleteAsync(
                    entry.FullPath, terminateLockers: true, scheduleBootDelete: true, dryRun: dryRun, progress: progress);

                foreach (var proc in result.KilledProcesses)
                    terminatedProcesses.Add(proc);

                switch (result.ActionTaken)
                {
                    case ForceDeleteService.ForceDeleteAction.DeletedDirectly:
                    case ForceDeleteService.ForceDeleteAction.TerminatedAndDeleted:
                        freedBytes += entry.SizeBytes;
                        Files.Remove(entry);
                        deleted++;
                        break;
                    case ForceDeleteService.ForceDeleteAction.ScheduledForBootDeletion:
                        // Not actually gone yet — leave it in the list so the UI doesn't claim
                        // space was freed before the restart that will actually free it.
                        scheduledForReboot++;
                        remainingFailed.Add(entry);
                        break;
                    case ForceDeleteService.ForceDeleteAction.DryRunOnly:
                        remainingFailed.Add(entry);
                        break;
                    default:
                        stillFailed++;
                        remainingFailed.Add(entry);
                        DiagnosticLogger.Warn("LargeFileFinderVM", $"Force delete failed for {entry.FullPath}: {result.Message}");
                        break;
                }
            }

            if (deleted > 0)
                CleanupHistoryService.Record(CleanupOperationType.LargeFileRemoval, deleted, freedBytes,
                    $"Large files in {ScanPath} (force deleted)");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("LargeFileFinderVM", "ForceDeleteFailedAsync failed", ex);
        }
        finally
        {
            TotalSizeFound = Math.Max(0, TotalSizeFound - freedBytes);
            OnPropertyChanged(nameof(FormattedTotalSize));
            ResultCount = Files.Count;
            ApplyFilter();
            _lastFailedDeletes = remainingFailed;
            FailedDeleteCount = remainingFailed.Count;
            HasFailedDeletes = remainingFailed.Count > 0;

            StatusMessage = dryRun
                ? $"[Preview] No files removed. {targets.Count} file(s) checked for locks."
                : $"Force-deleted {deleted} file(s), freed {FormatHelper.FormatBytes(freedBytes)}." +
                  (terminatedProcesses.Count > 0 ? $" Closed: {string.Join(", ", terminatedProcesses)}." : "") +
                  (scheduledForReboot > 0 ? $" {scheduledForReboot} scheduled for deletion on next restart." : "") +
                  (stillFailed > 0 ? $" {stillFailed} still couldn't be removed." : "");
            IsBusy = false;
        }
    }

    /// <summary>
    /// Represents a drive available for scanning.
    /// </summary>
    public class DriveOption
    {
        public string Path { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public string FreeSpace { get; init; } = string.Empty;
        public string TotalSize { get; init; } = string.Empty;

        /// <summary>A folder picked with Browse (as opposed to a drive); shown by its full path.</summary>
        public bool IsFolder { get; init; }

        public string Display => IsFolder ? $"Folder — {Path}" : $"{Label} — {FreeSpace} free of {TotalSize}";
    }
}
