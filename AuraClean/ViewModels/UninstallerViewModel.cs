using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
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
    }

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            FilteredPrograms = new ObservableCollection<InstalledProgram>(Programs);
        }
        else
        {
            FilteredPrograms = new ObservableCollection<InstalledProgram>(
                Programs.Where(p =>
                    p.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                    p.Publisher.Contains(SearchText, StringComparison.OrdinalIgnoreCase)));
        }
        UpdateSelectionCount();
    }

    [RelayCommand]
    private async Task LoadProgramsAsync()
    {
        IsBusy = true;
        StatusMessage = "Loading installed programs...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var programs = await UninstallerService.GetInstalledProgramsAsync(progress);

            Programs = new ObservableCollection<InstalledProgram>(programs);
            HookSelectionEvents(Programs);
            ApplyFilter();
            StatusMessage = $"Found {Programs.Count} installed programs.";
            HasScanned = true;
        }
        catch (Exception ex)
        {
            StatusMessage = "Couldn't load installed programs. Please try again.";
            DiagnosticLogger.Error("UninstallerVM", "LoadProgramsAsync failed", ex);
            HasScanned = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        var checkedPrograms = FilteredPrograms.Where(p => p.IsSelected).ToList();
        if (checkedPrograms.Count == 0 && SelectedProgram != null)
            checkedPrograms = [SelectedProgram];
        if (checkedPrograms.Count == 0) return;

        if (SafetyPromptService.IsDryRunEnabled())
        {
            StatusMessage = $"Dry run: would run uninstallers for {checkedPrograms.Count} program(s).";
            return;
        }

        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Run uninstallers for {checkedPrograms.Count} selected program(s)?"))
        {
            StatusMessage = "Uninstall cancelled.";
            return;
        }

        IsBusy = true;
        int uninstalled = 0;

        foreach (var program in checkedPrograms)
        {
            StatusMessage = $"Uninstalling {program.DisplayName}...";

            try
            {
                var progress = new Progress<string>(msg => StatusMessage = msg);
                var (success, message) = await UninstallerService.RunUninstallAsync(program, progress);
                if (success) uninstalled++;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Couldn't uninstall {program.DisplayName}. It may require manual removal.";
                DiagnosticLogger.Error("UninstallerVM", $"Uninstall failed for {program.DisplayName}", ex);
            }
        }

        StatusMessage = $"Uninstalled {uninstalled} program(s).";
        await LoadProgramsAsync();
        IsBusy = false;
    }

    [RelayCommand]
    private async Task DeepScanAsync()
    {
        if (!IsAdvancedMode)
        {
            StatusMessage = "Turn on Advanced mode to scan and clean leftover files.";
            return;
        }

        if (SelectedProgram == null) return;

        IsScanning = true;
        IsBusy = true;
        StatusMessage = $"Deep scanning for {SelectedProgram.DisplayName} leftovers...";
        PostUninstallJunk.Clear();

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var junk = await UninstallerService.PostUninstallScanAsync(
                SelectedProgram, progress);

            PostUninstallJunk = new ObservableCollection<JunkItem>(junk);
            HasPostUninstallResults = PostUninstallJunk.Count > 0;
            StatusMessage = junk.Count > 0
                ? $"Found {junk.Count} leftover items."
                : "No leftover files found.";
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
        if (settings.DryRunMode)
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
                $"Clean {selectedItems.Count} selected leftover item(s)? Review vendor/shared folders carefully."))
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
                    $"AuraClean - Deep Clean {SelectedProgram?.DisplayName}");

                if (!rpSuccess)
                {
                    StatusMessage = $"Warning: {rpMsg} — Proceeding with cleanup...";
                    await Task.Delay(2000);
                }
            }

            var progress = new Progress<string>(msg => StatusMessage = msg);

            // Clean registry keys
            int regCleaned = 0;
            foreach (var item in selectedItems.Where(j => j.Type == JunkType.OrphanedRegistryKey))
            {
                var (success, message) = await RegistryScannerService.DeleteRegistryKeyAsync(item.Path);
                if (success)
                {
                    item.IsLocked = false;
                    item.LockingProcess = string.Empty;
                    regCleaned++;
                }
                else
                {
                    item.IsLocked = true;
                    item.LockingProcess = message;
                }
            }

            // Clean file system items
            var fileItems = selectedItems.Where(j => j.Type != JunkType.OrphanedRegistryKey);
            var (deleted, skipped, bytesFreed, errors) =
                await FileCleanerService.CleanItemsAsync(fileItems, progress);

            StatusMessage = $"Cleaned: {deleted + regCleaned} items ({FormatHelper.FormatBytes(bytesFreed)} freed). " +
                           $"Skipped: {skipped}.";

            // Refresh scan results, keeping unselected and failed/skipped items visible.
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
    /// Force-uninstalls a program with broken/missing MSI installer.
    /// Uses the ForceDeleteService to handle locked files and removes all traces.
    /// </summary>
    [RelayCommand]
    private async Task ForceUninstallAsync()
    {
        if (!IsAdvancedMode)
        {
            StatusMessage = "Turn on Advanced mode to force uninstall broken programs.";
            return;
        }

        if (SelectedProgram == null) return;

        var settings = SettingsService.Load();
        var effectiveDryRun = IsDryRun || settings.DryRunMode;

        if (!effectiveDryRun &&
            !SafetyPromptService.ConfirmDestructiveAction(
                $"Force uninstall {SelectedProgram.DisplayName}? This removes files and registry entries directly."))
        {
            StatusMessage = "Force uninstall cancelled.";
            return;
        }

        IsBusy = true;
        StatusMessage = effectiveDryRun
            ? $"[Preview] Analyzing force uninstall for {SelectedProgram.DisplayName}..."
            : $"Force uninstalling {SelectedProgram.DisplayName}...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);

            if (!effectiveDryRun && settings.CreateRestorePointBeforeClean)
            {
                // Create restore point before force uninstall
                var (rpSuccess, rpMsg) = await RestorePointService.CreateRestorePointAsync(
                    $"AuraClean - Force Uninstall {SelectedProgram.DisplayName}");
                if (!rpSuccess)
                {
                    StatusMessage = $"Warning: {rpMsg} — Proceeding...";
                    await Task.Delay(1500);
                }
            }

            var result = await ForceDeleteService.ForceUninstallAsync(
                SelectedProgram, dryRun: effectiveDryRun, progress: progress);

            StatusMessage = result.Message;

            if (result.Success && !effectiveDryRun)
            {
                await LoadProgramsAsync();
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
    }

}
