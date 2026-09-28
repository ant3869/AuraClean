using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace AuraClean.ViewModels;

/// <summary>
/// ViewModel for the Uninstaller view.
/// Manages the installed program list, search/filter, and deep uninstall workflow.
/// </summary>
public partial class UninstallerViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<InstalledProgram> _programs = [];
    [ObservableProperty] private ObservableCollection<InstalledProgram> _filteredPrograms = [];
    [ObservableProperty] private InstalledProgram? _selectedProgram;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Ready to manage installed programs.";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private ObservableCollection<JunkItem> _postUninstallJunk = [];
    [ObservableProperty] private bool _hasPostUninstallResults;
    [ObservableProperty] private bool _isDryRun;
    [ObservableProperty] private bool _hasScanned;

    // Selection
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _isAllSelected;
    public bool HasCheckedItems => SelectedCount > 0;

    /// <summary>The program whose leftovers are currently listed (it may already be uninstalled).</summary>
    private InstalledProgram? _leftoverScanTarget;
    private Task? _loadTask;
    private bool _suppressSelectAllCascade;

    private readonly DispatcherTimer _searchDebounceTimer;

    public UninstallerViewModel()
    {
        _searchDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            ApplyFilter();
        };
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnIsAllSelectedChanged(bool value)
    {
        if (_suppressSelectAllCascade)
            return;

        foreach (var program in FilteredPrograms)
            program.IsSelected = value;
        UpdateSelectionCount();
    }

    private void HookSelectionEvents(IEnumerable<InstalledProgram> programs)
    {
        foreach (var program in programs)
        {
            program.PropertyChanged -= OnProgramPropertyChanged;
            program.PropertyChanged += OnProgramPropertyChanged;
        }
    }

    private void OnProgramPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledProgram.IsSelected))
            UpdateSelectionCount();
    }

    private void UpdateSelectionCount()
    {
        SelectedCount = FilteredPrograms.Count(p => p.IsSelected);
        OnPropertyChanged(nameof(HasCheckedItems));

        _suppressSelectAllCascade = true;
        try { IsAllSelected = FilteredPrograms.Count > 0 && SelectedCount == FilteredPrograms.Count; }
        finally { _suppressSelectAllCascade = false; }
    }

    private void ApplyFilter()
    {
        var query = SearchText?.Trim() ?? string.Empty;

        FilteredPrograms = string.IsNullOrEmpty(query)
            ? new ObservableCollection<InstalledProgram>(Programs)
            : new ObservableCollection<InstalledProgram>(
                Programs.Where(p =>
                    p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    p.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase)));

        UpdateSelectionCount();
    }

    [RelayCommand]
    private Task LoadProgramsAsync()
    {
        _loadTask = LoadProgramsCoreAsync();
        return _loadTask;
    }

    private async Task LoadProgramsCoreAsync()
    {
        IsBusy = true;
        StatusMessage = "Loading installed programs...";

        try
        {
            var previousSelection = SelectedProgram?.RegistryKeyPath;
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var programs = await UninstallerService.GetInstalledProgramsAsync(progress);

            Programs = new ObservableCollection<InstalledProgram>(programs);
            HookSelectionEvents(Programs);
            ApplyFilter();

            if (previousSelection != null)
                SelectedProgram = Programs.FirstOrDefault(p => p.RegistryKeyPath == previousSelection);

            StatusMessage = $"Found {Programs.Count} installed programs.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't load installed programs. Please try again.";
            DiagnosticLogger.Error("UninstallerVM", "LoadProgramsAsync failed", ex);
        }
        finally
        {
            HasScanned = true;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Selects the installed program that owns <paramref name="path"/> (used by the Explorer
    /// "Deep Uninstall with AuraClean" context-menu entry).
    /// </summary>
    public async Task FocusProgramForPathAsync(string path)
    {
        try
        {
            if (_loadTask != null)
                await _loadTask;
            else if (!HasScanned)
                await LoadProgramsAsync();

            var fullPath = PathSafety.Normalize(path) ?? path;
            var match = Programs
                .Select(p => (Program: p, Score: MatchScore(p, fullPath)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Program)
                .FirstOrDefault();

            if (match != null)
            {
                SearchText = match.DisplayName;
                ApplyFilter();
                SelectedProgram = match;
                StatusMessage = $"Selected {match.DisplayName}. Review it, then choose Uninstall.";
                return;
            }

            string hint = Path.GetFileNameWithoutExtension(fullPath);
            try
            {
                if (File.Exists(fullPath))
                {
                    var info = FileVersionInfo.GetVersionInfo(fullPath);
                    if (!string.IsNullOrWhiteSpace(info.ProductName))
                        hint = info.ProductName!;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLogger.Warn("UninstallerVM", $"Could not read version info for {fullPath}", ex);
            }

            SearchText = hint;
            ApplyFilter();
            StatusMessage = FilteredPrograms.Count > 0
                ? $"Showing programs matching '{hint}'."
                : $"No installed program was found for {Path.GetFileName(fullPath)}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't locate the program for that file.";
            DiagnosticLogger.Error("UninstallerVM", "FocusProgramForPathAsync failed", ex);
        }
    }

    private static int MatchScore(InstalledProgram program, string path)
    {
        var installDir = PathSafety.Normalize(program.InstallLocation);
        if (installDir != null && !PathSafety.IsProtectedRoot(installDir) && PathSafety.IsSameOrUnder(path, installDir))
            return 1000 + installDir.Length;

        if (!string.IsNullOrEmpty(program.UninstallString) &&
            program.UninstallString.Contains(path, StringComparison.OrdinalIgnoreCase))
            return 500;

        if (!string.IsNullOrEmpty(program.DisplayIcon) &&
            program.DisplayIcon.Contains(path, StringComparison.OrdinalIgnoreCase))
            return 400;

        return 0;
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        if (IsBusy) return;

        var checkedPrograms = FilteredPrograms.Where(p => p.IsSelected).ToList();
        if (checkedPrograms.Count == 0 && SelectedProgram != null)
            checkedPrograms = [SelectedProgram];
        if (checkedPrograms.Count == 0)
        {
            StatusMessage = "Select a program to uninstall.";
            return;
        }

        if (IsDryRun || SafetyPromptService.IsDryRunEnabled())
        {
            StatusMessage = $"Dry run: would run uninstallers for {checkedPrograms.Count} program(s): " +
                            string.Join(", ", checkedPrograms.Take(3).Select(p => p.DisplayName)) +
                            (checkedPrograms.Count > 3 ? "…" : string.Empty);
            return;
        }

        var names = string.Join("\n• ", checkedPrograms.Take(8).Select(p => p.DisplayName));
        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Run the uninstaller for {checkedPrograms.Count} program(s)?\n\n• {names}" +
                (checkedPrograms.Count > 8 ? $"\n…and {checkedPrograms.Count - 8} more" : string.Empty)))
        {
            StatusMessage = "Uninstall cancelled.";
            return;
        }

        IsBusy = true;
        int uninstalled = 0;
        var succeeded = new List<InstalledProgram>();
        var failures = new List<string>();

        try
        {
            foreach (var program in checkedPrograms)
            {
                StatusMessage = $"Uninstalling {program.DisplayName}...";

                try
                {
                    var progress = new Progress<string>(msg => StatusMessage = msg);
                    var (success, message) = await UninstallerService.RunUninstallAsync(program, progress);
                    if (success)
                    {
                        uninstalled++;
                        succeeded.Add(program);
                    }
                    else
                    {
                        failures.Add($"{program.DisplayName}: {message}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{program.DisplayName}: {ex.Message}");
                    DiagnosticLogger.Error("UninstallerVM", $"Uninstall failed for {program.DisplayName}", ex);
                }
            }

            CleanupHistoryService.Record(CleanupOperationType.Uninstall, uninstalled,
                succeeded.Sum(p => p.EstimatedSizeKB) * 1024,
                string.Join(", ", succeeded.Select(p => p.DisplayName)));
        }
        finally
        {
            IsBusy = false;
        }

        await LoadProgramsCoreAsync();

        StatusMessage = failures.Count == 0
            ? $"Uninstalled {uninstalled} program(s)."
            : $"Uninstalled {uninstalled} program(s). {failures.Count} did not finish: {failures[0]}";

        // A single successful removal in Advanced mode flows straight into the leftover scan,
        // which is the point of a "deep" uninstall.
        if (IsAdvancedMode && succeeded.Count == 1)
            await ScanLeftoversForAsync(succeeded[0]);
    }

    [RelayCommand]
    private async Task DeepScanAsync()
    {
        if (!IsAdvancedMode)
        {
            StatusMessage = "Turn on Advanced mode to scan and clean leftover files.";
            return;
        }

        if (SelectedProgram == null)
        {
            StatusMessage = "Select a program to scan for leftovers.";
            return;
        }

        await ScanLeftoversForAsync(SelectedProgram);
    }

    private async Task ScanLeftoversForAsync(InstalledProgram program)
    {
        if (IsBusy) return;

        IsScanning = true;
        IsBusy = true;
        _leftoverScanTarget = program;
        StatusMessage = $"Deep scanning for {program.DisplayName} leftovers...";
        PostUninstallJunk = [];
        HasPostUninstallResults = false;

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var junk = await UninstallerService.PostUninstallScanAsync(program, progress);

            PostUninstallJunk = new ObservableCollection<JunkItem>(junk);
            HasPostUninstallResults = PostUninstallJunk.Count > 0;
            StatusMessage = junk.Count > 0
                ? $"Found {junk.Count} possible leftover(s) for {program.DisplayName}. Review before cleaning."
                : $"No leftovers found for {program.DisplayName}.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during the scan. Please try again.";
            DiagnosticLogger.Error("UninstallerVM", "DeepScanAsync failed", ex);
        }
        finally
        {
            IsBusy = false;
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task CleanLeftoversAsync()
    {
        if (IsBusy) return;

        if (!IsAdvancedMode)
        {
            StatusMessage = "Turn on Advanced mode to clean leftover files and registry entries.";
            return;
        }

        if (PostUninstallJunk.Count == 0) return;

        var selectedItems = PostUninstallJunk.Where(j => j.IsSelected).ToList();
        if (selectedItems.Count == 0)
        {
            StatusMessage = "No leftover items selected for cleanup.";
            return;
        }

        var settings = SettingsService.Load();
        if (IsDryRun || settings.DryRunMode)
        {
            var registryCount = selectedItems.Count(j => j.Type == JunkType.OrphanedRegistryKey);
            var fileItems = selectedItems.Where(j => j.Type != JunkType.OrphanedRegistryKey);
            var (_, protectedSkipped, wouldFree, _) =
                await FileCleanerService.CleanItemsAsync(fileItems, dryRun: true);

            StatusMessage = $"Dry run: would clean {selectedItems.Count - protectedSkipped} leftover item(s) " +
                $"({registryCount} registry, {FormatHelper.FormatBytes(wouldFree)} files). " +
                $"{protectedSkipped} protected media file(s) skipped.";
            return;
        }

        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Clean {selectedItems.Count} selected leftover item(s)? Registry keys are backed up first. " +
                "Review vendor/shared folders carefully."))
        {
            StatusMessage = "Leftover cleanup cancelled.";
            return;
        }

        IsBusy = true;

        try
        {
            if (settings.CreateRestorePointBeforeClean)
            {
                StatusMessage = "Creating restore point...";
                var (rpSuccess, rpMsg) = await RestorePointService.CreateRestorePointAsync(
                    $"AuraClean - Deep Clean {_leftoverScanTarget?.DisplayName}");

                if (!rpSuccess)
                {
                    StatusMessage = $"Warning: {rpMsg} — Proceeding with cleanup...";
                    await Task.Delay(2000);
                }
            }

            var progress = new Progress<string>(msg => StatusMessage = msg);

            int regCleaned = 0;
            foreach (var item in selectedItems.Where(j => j.Type == JunkType.OrphanedRegistryKey))
            {
                var (success, message) = await RegistryScannerService.DeleteRegistryKeyAsync(item.Path);
                item.IsLocked = !success;
                item.LockingProcess = success ? string.Empty : message;
                if (success) regCleaned++;
            }

            var fileItems = selectedItems.Where(j => j.Type != JunkType.OrphanedRegistryKey).ToList();
            var (deleted, skipped, bytesFreed, _) =
                await FileCleanerService.CleanItemsAsync(fileItems, progress);

            var regFailed = selectedItems.Count(j => j.Type == JunkType.OrphanedRegistryKey && j.IsLocked);
            StatusMessage = $"Cleaned: {deleted + regCleaned} items ({FormatHelper.FormatBytes(bytesFreed)} freed). " +
                           $"Skipped: {skipped + regFailed}.";

            CleanupHistoryService.Record(CleanupOperationType.RegistryClean, regCleaned, 0,
                $"Leftover registry keys for {_leftoverScanTarget?.DisplayName}");
            CleanupHistoryService.Record(CleanupOperationType.Uninstall, deleted, bytesFreed,
                $"Leftover files for {_leftoverScanTarget?.DisplayName}");

            foreach (var item in selectedItems.Where(j => !j.IsLocked).ToList())
                PostUninstallJunk.Remove(item);
            HasPostUninstallResults = PostUninstallJunk.Count > 0;
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong during cleanup. Some items may not have been removed.";
            DiagnosticLogger.Error("UninstallerVM", "CleanLeftoversAsync failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Force-uninstalls a program with a broken/missing uninstaller: removes its install folder
    /// (after safety checks) and uninstall entry, then lists leftovers for review.
    /// </summary>
    [RelayCommand]
    private async Task ForceUninstallAsync()
    {
        if (IsBusy) return;

        if (!IsAdvancedMode)
        {
            StatusMessage = "Turn on Advanced mode to force uninstall broken programs.";
            return;
        }

        var program = SelectedProgram;
        if (program == null)
        {
            StatusMessage = "Select a program to force uninstall.";
            return;
        }

        var settings = SettingsService.Load();
        var effectiveDryRun = IsDryRun || settings.DryRunMode;

        if (!effectiveDryRun &&
            !SafetyPromptService.ConfirmDestructiveAction(
                $"Force uninstall {program.DisplayName}?\n\nThis deletes its install folder" +
                (string.IsNullOrWhiteSpace(program.InstallLocation) ? string.Empty : $" ({program.InstallLocation})") +
                " and its uninstall entry without running its own uninstaller."))
        {
            StatusMessage = "Force uninstall cancelled.";
            return;
        }

        IsBusy = true;
        StatusMessage = effectiveDryRun
            ? $"[Preview] Analyzing force uninstall for {program.DisplayName}..."
            : $"Force uninstalling {program.DisplayName}...";

        bool removed = false;
        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);

            if (!effectiveDryRun && settings.CreateRestorePointBeforeClean)
            {
                var (rpSuccess, rpMsg) = await RestorePointService.CreateRestorePointAsync(
                    $"AuraClean - Force Uninstall {program.DisplayName}");
                if (!rpSuccess)
                {
                    StatusMessage = $"Warning: {rpMsg} — Proceeding...";
                    await Task.Delay(1500);
                }
            }

            var result = await ForceDeleteService.ForceUninstallAsync(
                program, dryRun: effectiveDryRun, progress: progress);

            StatusMessage = result.Message;
            removed = result.Success && !effectiveDryRun;

            if (removed)
            {
                CleanupHistoryService.Record(CleanupOperationType.Uninstall,
                    result.FilesDeleted + result.RegistryKeysRemoved, program.EstimatedSizeKB * 1024,
                    $"Force uninstall of {program.DisplayName}");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "Force uninstall failed. The program may require manual removal.";
            DiagnosticLogger.Error("UninstallerVM", "ForceUninstallAsync failed", ex);
        }
        finally
        {
            IsBusy = false;
        }

        if (removed)
        {
            var summary = StatusMessage;
            await LoadProgramsCoreAsync();
            await ScanLeftoversForAsync(program);
            if (!HasPostUninstallResults)
                StatusMessage = summary;
        }
    }
}
