using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the Duplicate File Finder view.
/// </summary>
public partial class DuplicateFinderViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Select a folder and scan for duplicate files.";
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private string _selectedPath = string.Empty;
    [ObservableProperty] private double _progressPercent;

    // Options
    [ObservableProperty] private int _minSizeKB = 1;
    [ObservableProperty] private int _maxSizeMB = 500;
    [ObservableProperty] private bool _recursive = true;
    [ObservableProperty] private string _extensionFilter = string.Empty;

    // Results
    [ObservableProperty] private ObservableCollection<DuplicateFinderService.DuplicateGroup> _duplicateGroups = [];
    [ObservableProperty] private int _totalGroupCount;
    [ObservableProperty] private int _totalDuplicateCount;
    [ObservableProperty] private long _totalWastedBytes;
    [ObservableProperty] private int _totalFilesScanned;
    [ObservableProperty] private string _scanDuration = string.Empty;

    // Drives for quick selection
    [ObservableProperty] private ObservableCollection<string> _quickPaths = [];

    public string FormattedWasted => FormatHelper.FormatBytes(TotalWastedBytes);

    /// <summary>Copies currently marked for deletion (selected and not kept).</summary>
    [ObservableProperty] private int _selectedDeleteCount;

    public string DeleteButtonLabel => SelectedDeleteCount > 0
        ? $"Delete Selected ({SelectedDeleteCount:N0})"
        : "Delete Selected";

    partial void OnSelectedDeleteCountChanged(int value) => OnPropertyChanged(nameof(DeleteButtonLabel));

    private CancellationTokenSource? _scanCts;

    public DuplicateFinderViewModel()
    {
        var settings = SettingsService.Load();
        MinSizeKB = (int)Math.Clamp(settings.DefaultMinDuplicateSizeMb * 1024, 1L, int.MaxValue);

        // Populate quick paths
        var paths = new List<string>();
        paths.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        paths.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        paths.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            paths.Add(drive.Name);
        QuickPaths = new ObservableCollection<string>(paths);

        if (QuickPaths.Count > 0)
            SelectedPath = QuickPaths[0];
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;

        if (string.IsNullOrWhiteSpace(SelectedPath) || !Directory.Exists(SelectedPath))
        {
            StatusMessage = "Please select a valid folder.";
            return;
        }

        if (PathSafety.IsSystemCriticalLocation(SelectedPath))
        {
            StatusMessage = "Windows and Program Files folders can't be scanned — duplicate files there are required by installed software.";
            return;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        IsBusy = true;
        HasResults = false;
        DuplicateGroups.Clear();
        StatusMessage = "Scanning for duplicates...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);

            // Parse extension filter
            string[]? extensions = null;
            if (!string.IsNullOrWhiteSpace(ExtensionFilter))
            {
                extensions = ExtensionFilter.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.StartsWith('.') ? e : $".{e}")
                    .ToArray();
            }

            var result = await DuplicateFinderService.ScanForDuplicatesAsync(
                SelectedPath,
                minSizeBytes: Math.Max(1, MinSizeKB) * 1024L,
                maxSizeMB: Math.Max(1, MaxSizeMB),
                fileExtensions: extensions,
                recursive: Recursive,
                progress: progress,
                ct: _scanCts.Token);

            if (!IsAdvancedMode)
                ApplyNormalModeReviewDefaults(result.Groups);

            SetResults(result.Groups);
            TotalGroupCount = result.Groups.Count;
            TotalDuplicateCount = result.TotalDuplicateFiles;
            TotalWastedBytes = result.TotalWastedBytes;
            TotalFilesScanned = result.TotalFilesScanned;
            ScanDuration = $"{result.ScanDuration.TotalSeconds:F1}s";
            HasResults = result.Groups.Count > 0;

            OnPropertyChanged(nameof(FormattedWasted));

            StatusMessage = result.Groups.Count > 0
                ? $"Found {result.Groups.Count:N0} duplicate groups ({result.TotalDuplicateFiles:N0} duplicate files, " +
                  $"{FormatHelper.FormatBytes(result.TotalWastedBytes)} wasted) in {ScanDuration}."
                : $"No duplicates found among {result.TotalFilesScanned:N0} files.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during the scan. Please try again.";
            DiagnosticLogger.Error("DuplicateFinderVM", "Scan failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _scanCts?.Cancel();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (!HasResults || IsBusy) return;

        var selected = DuplicateGroups.SelectMany(g => g.Files)
            .Where(f => f.IsSelected && !f.IsKeep)
            .ToList();

        if (selected.Count == 0)
        {
            StatusMessage = "Tick the copies you want to delete, or click \"Keep this\" on the copy to keep.";
            return;
        }

        var bytes = selected.Sum(f => f.SizeBytes);
        if (SafetyPromptService.IsDryRunEnabled())
        {
            StatusMessage = $"Dry run: would delete {selected.Count} duplicate file(s) ({FormatHelper.FormatBytes(bytes)}).";
            return;
        }

        // Normal mode never deletes permanently: copies go to the Recycle Bin.
        var useRecycleBin = !IsAdvancedMode;
        var risky = selected.Count(f => PathSafety.IsLikelyAppDependency(f.FullPath));
        var message =
            $"{(useRecycleBin ? "Move" : "Permanently delete")} {selected.Count:N0} duplicate cop{(selected.Count == 1 ? "y" : "ies")} " +
            $"({FormatHelper.FormatBytes(bytes)}){(useRecycleBin ? " to the Recycle Bin" : string.Empty)}?\n\n" +
            "At least one copy of every file is always kept." +
            (useRecycleBin
                ? "\nYou can restore them from the Recycle Bin; the space is freed when it is emptied."
                : "\nThis cannot be undone.") +
            (risky > 0
                ? $"\n\nWarning: {risky:N0} of these look like program files or live inside an app folder " +
                  "(AppData, virtual environments, package caches). Apps usually need their own copy — deleting it can break them."
                : string.Empty);

        var confirmed = risky > 0
            ? SafetyPromptService.ConfirmSecurityDecision(message, "Delete duplicate files")
            : SafetyPromptService.ConfirmDestructiveAction(message, "Delete duplicate files");
        if (!confirmed)
        {
            StatusMessage = "Duplicate deletion cancelled.";
            return;
        }

        IsBusy = true;
        StatusMessage = useRecycleBin
            ? $"Moving {selected.Count:N0} duplicate files to the Recycle Bin..."
            : $"Deleting {selected.Count:N0} duplicate files...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var (deleted, failed, bytesFreed) = await DuplicateFinderService.DeleteDuplicatesAsync(
                DuplicateGroups, useRecycleBin, progress);

            var skipped = failed > 0 ? $" {failed} skipped (in use, changed since the scan, or protected)." : string.Empty;
            StatusMessage = useRecycleBin
                ? $"Moved {deleted:N0} files ({FormatHelper.FormatBytes(bytesFreed)}) to the Recycle Bin.{skipped}"
                : $"Deleted {deleted:N0} files ({FormatHelper.FormatBytes(bytesFreed)} freed).{skipped}";

            CleanupHistoryService.Record(CleanupOperationType.DuplicateRemoval, deleted, bytesFreed,
                $"Duplicates in {SelectedPath}{(useRecycleBin ? " (Recycle Bin)" : string.Empty)}");

            // Rebuild results so the (non-observable) group lists refresh in the UI.
            var remaining = new List<DuplicateFinderService.DuplicateGroup>();
            foreach (var group in DuplicateGroups)
            {
                group.Files.RemoveAll(f => !File.Exists(f.FullPath));
                if (group.Files.Count > 1)
                    remaining.Add(group);
            }
            SetResults(remaining);

            TotalGroupCount = DuplicateGroups.Count;
            TotalDuplicateCount = DuplicateGroups.Sum(g => g.Count - 1);
            TotalWastedBytes = DuplicateGroups.Sum(g => g.WastedBytes);
            HasResults = DuplicateGroups.Count > 0;
            OnPropertyChanged(nameof(FormattedWasted));
        }
        catch (Exception ex)
        {
            StatusMessage = "Some duplicates couldn't be deleted. They may be in use.";
            DiagnosticLogger.Error("DuplicateFinderVM", "Delete duplicates failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAllDuplicates()
    {
        if (!IsAdvancedMode)
        {
            StatusMessage = "Turn on Advanced mode to auto-select duplicate files for deletion.";
            return;
        }

        foreach (var group in DuplicateGroups)
        {
            var keep = group.Files.FirstOrDefault(f => f.IsKeep)
                       ?? group.Files.OrderBy(f => f.LastModified).First();
            foreach (var file in group.Files)
            {
                file.IsKeep = ReferenceEquals(file, keep);
                file.IsSelected = !file.IsKeep;
            }
        }
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var group in DuplicateGroups)
            foreach (var file in group.Files)
                file.IsSelected = false;
    }

    /// <summary>
    /// Keeps <paramref name="file"/> and marks every other copy in its group for deletion — the
    /// usual "keep this one, remove the rest" decision, made explicitly per group.
    /// </summary>
    [RelayCommand]
    private void KeepFile(DuplicateFinderService.DuplicateFileEntry? file)
    {
        if (file == null || IsBusy)
            return;

        var group = DuplicateGroups.FirstOrDefault(g => g.Files.Contains(file));
        if (group == null)
            return;

        foreach (var copy in group.Files)
        {
            var keep = ReferenceEquals(copy, file);
            copy.IsKeep = keep;
            copy.IsSelected = !keep;
        }

        StatusMessage = $"Keeping {file.FullPath}. {group.Files.Count - 1} other cop{(group.Files.Count == 2 ? "y is" : "ies are")} marked for deletion.";
    }

    private void SetResults(IEnumerable<DuplicateFinderService.DuplicateGroup> groups)
    {
        foreach (var file in DuplicateGroups.SelectMany(g => g.Files))
            file.PropertyChanged -= OnFileSelectionChanged;

        DuplicateGroups = new ObservableCollection<DuplicateFinderService.DuplicateGroup>(groups);

        foreach (var file in DuplicateGroups.SelectMany(g => g.Files))
            file.PropertyChanged += OnFileSelectionChanged;

        RecountSelection();
    }

    private void OnFileSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DuplicateFinderService.DuplicateFileEntry.IsSelected)
            or nameof(DuplicateFinderService.DuplicateFileEntry.IsKeep))
        {
            RecountSelection();
        }
    }

    private void RecountSelection() =>
        SelectedDeleteCount = DuplicateGroups.SelectMany(g => g.Files).Count(f => f.IsSelected && !f.IsKeep);

    [RelayCommand]
    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        ShellHelper.RevealInExplorer(path);
    }

    public static void ApplyNormalModeReviewDefaults(IEnumerable<DuplicateFinderService.DuplicateGroup> groups)
    {
        foreach (var group in groups)
        {
            foreach (var file in group.Files)
            {
                file.IsKeep = false;
                file.IsSelected = false;
            }
        }
    }
}
