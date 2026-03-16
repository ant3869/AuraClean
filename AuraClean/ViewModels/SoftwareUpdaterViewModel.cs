using AuraClean.Helpers;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AuraClean.ViewModels;

public partial class SoftwareUpdaterViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Click 'Check for Updates' to scan for outdated software.";
    [ObservableProperty] private bool _isWingetAvailable;
    [ObservableProperty] private int _outdatedCount;

    public ObservableCollection<UpdatableEntry> Programs { get; } = [];

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        IsBusy = true;
        StatusMessage = "Checking winget availability...";
        Programs.Clear();
        OutdatedCount = 0;

        IsWingetAvailable = await SoftwareUpdaterService.IsWingetAvailableAsync();
        if (!IsWingetAvailable)
        {
            StatusMessage = "Windows Package Manager (winget) is not available. Please install it from the Microsoft Store.";
            IsBusy = false;
            return;
        }

        var progress = new Progress<string>(msg => StatusMessage = msg);
        try
        {
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
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UpdateSelectedAsync()
    {
        var selected = Programs.Where(p => p.IsSelected && !p.IsUpdated).ToList();
        if (selected.Count == 0)
        {
            StatusMessage = "No programs selected for update.";
            return;
        }

        IsBusy = true;
        int successCount = 0;

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
            }
        }

        StatusMessage = $"Updated {successCount}/{selected.Count} program(s).";
        if (successCount > 0)
            NotificationService.ShowSuccess("Software Updates",
                $"Successfully updated {successCount} program(s).");

        IsBusy = false;
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
