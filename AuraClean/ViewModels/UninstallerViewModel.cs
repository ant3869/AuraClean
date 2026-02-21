using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

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
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private ObservableCollection<JunkItem> _postUninstallJunk = [];
    [ObservableProperty] private bool _hasPostUninstallResults;
    [ObservableProperty] private bool _isDryRun;

    public UninstallerViewModel()
    {
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
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
            ApplyFilter();
            StatusMessage = $"Found {Programs.Count} installed programs.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading programs: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        if (SelectedProgram == null) return;

        IsBusy = true;
        StatusMessage = $"Uninstalling {SelectedProgram.DisplayName}...";

        try
        {
            var progress = new Progress<string>(msg => StatusMessage = msg);
            var (success, message) = await UninstallerService.RunUninstallAsync(SelectedProgram, progress);
            StatusMessage = message;

            if (success)
            {
                // Refresh the program list
                await LoadProgramsAsync();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Uninstall error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
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
                : "No leftovers found — clean uninstall!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
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
            StatusMessage = $"Cleanup error: {ex.Message}";
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
            ? $"[DRY RUN] Analyzing force uninstall for {SelectedProgram.DisplayName}..."
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
            StatusMessage = $"Force uninstall error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

}
