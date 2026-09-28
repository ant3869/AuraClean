using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows.Data;

namespace AuraClean.ViewModels;

public partial class SoftwareUpdaterViewModel : ObservableObject
{
    private readonly object _programsLock = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Ready to check for software updates.";
    [ObservableProperty] private bool _isWingetAvailable;
    [ObservableProperty] private int _outdatedCount;
    [ObservableProperty] private bool _hasScanned;

    public ObservableCollection<UpdatableEntry> Programs { get; } = [];

    public SoftwareUpdaterViewModel()
    {
        BindingOperations.EnableCollectionSynchronization(Programs, _programsLock);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = "Checking for update tools...";
        Programs.Clear();
        OutdatedCount = 0;

        var progress = new Progress<string>(msg => StatusMessage = msg);
        try
        {
            IsWingetAvailable = await SoftwareUpdaterService.IsWingetAvailableAsync();
            if (!IsWingetAvailable)
            {
                StatusMessage = "The Windows update tool isn't available. Install 'App Installer' from the Microsoft Store to enable updates.";
                return;
            }

            var outdated = await SoftwareUpdaterService.CheckForUpdatesAsync(progress);
            foreach (var p in outdated)
            {
                Programs.Add(new UpdatableEntry
                {
                    Name = p.Name,
                    Id = p.Id,
                    InstalledVersion = p.InstalledVersion,
                    AvailableVersion = p.AvailableVersion,
                    Source = p.Source,
                    IsSelected = true
                });
            }

            OutdatedCount = Programs.Count;
            StatusMessage = Programs.Count > 0
                ? $"Found {Programs.Count} program(s) with available updates."
                : "All programs are up to date!";
        }
        catch (Exception ex)
        {
            StatusMessage = "Something went wrong while checking for updates. Please try again.";
            DiagnosticLogger.Error("SoftwareUpdaterVM", "CheckForUpdatesAsync failed", ex);
        }
        finally
        {
            HasScanned = true;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateSelectedAsync()
    {
        if (IsBusy) return;

        var selected = Programs.Where(p => p.IsSelected && !p.IsUpdated).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No programs selected for update.";
            return;
        }

        if (!SafetyPromptService.ConfirmDestructiveAction(
                $"Update {selected.Count} program(s) with winget? Close them first; installers run silently.",
                "Confirm updates"))
        {
            StatusMessage = "Update cancelled.";
            return;
        }

        IsBusy = true;
        int successCount = 0;

        try
        {
            foreach (var entry in selected)
            {
                entry.UpdateStatus = "Updating...";
                StatusMessage = $"Updating {entry.Name}...";

                var program = new SoftwareUpdaterService.OutdatedProgram
                {
                    Name = entry.Name,
                    Id = entry.Id,
                    InstalledVersion = entry.InstalledVersion,
                    AvailableVersion = entry.AvailableVersion,
                    Source = entry.Source
                };

                var (success, message) = await SoftwareUpdaterService.UpdateProgramAsync(program);

                if (success)
                {
                    entry.IsUpdated = true;
                    entry.UpdateStatus = "Updated";
                    successCount++;
                }
                else
                {
                    entry.UpdateStatus = "Failed";
                    DiagnosticLogger.Warn("SoftwareUpdaterVM", $"{entry.Name}: {message}");
                }
            }

            if (successCount > 0)
                NotificationService.ShowSuccess("Software Updates",
                    $"Successfully updated {successCount} program(s).");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("SoftwareUpdaterVM", "UpdateSelectedAsync failed", ex);
        }
        finally
        {
            StatusMessage = $"Updated {successCount}/{selected.Count} program(s)." +
                            (successCount < selected.Count ? " See each row's status for failures." : string.Empty);
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var p in Programs) p.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var p in Programs) p.IsSelected = false;
    }
}

public partial class UpdatableEntry : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private string _installedVersion = string.Empty;
    [ObservableProperty] private string _availableVersion = string.Empty;
    [ObservableProperty] private string _source = string.Empty;
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private bool _isUpdated;
    [ObservableProperty] private string _updateStatus = string.Empty;
}
