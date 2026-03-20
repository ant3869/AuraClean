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
        if (PostUninstallJunk.Count == 0) return;

        IsBusy = true;
        StatusMessage = "Creating restore point...";

        try
        {
            // Safety: Create restore point before cleaning
            var (rpSuccess, rpMsg) = await RestorePointService.CreateRestorePointAsync(
                $"AuraClean - Deep Clean {SelectedProgram?.DisplayName}");

            if (!rpSuccess)
            {
                StatusMessage = $"Warning: {rpMsg} — Proceeding with cleanup...";
                await Task.Delay(2000);
            }

            var progress = new Progress<string>(msg => StatusMessage = msg);

            // Clean registry keys
            int regCleaned = 0;
            foreach (var item in PostUninstallJunk.Where(j =>
                j.IsSelected && j.Type == JunkType.OrphanedRegistryKey))
            {
                var (success, _) = await RegistryScannerService.DeleteRegistryKeyAsync(item.Path);
                if (success) regCleaned++;
            }

            // Clean file system items
            var fileItems = PostUninstallJunk.Where(j =>
                j.IsSelected && j.Type != JunkType.OrphanedRegistryKey);
            var (deleted, skipped, bytesFreed, errors) =
                await FileCleanerService.CleanItemsAsync(fileItems, progress);

            StatusMessage = $"Cleaned: {deleted + regCleaned} items ({FormatHelper.FormatBytes(bytesFreed)} freed). " +
                           $"Skipped: {skipped}.";

            // Refresh scan results
            PostUninstallJunk.Clear();
            HasPostUninstallResults = false;
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
        if (SelectedProgram == null) return;

        IsBusy = true;
        StatusMessage = IsDryRun
            ? $"[Preview] Analyzing force uninstall for {SelectedProgram.DisplayName}..."
            : $"Force uninstalling {SelectedProgram.DisplayName}...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);

            if (!IsDryRun)
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
                SelectedProgram, dryRun: IsDryRun, progress: progress);

            StatusMessage = result.Message;

            if (result.Success && !IsDryRun)
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
