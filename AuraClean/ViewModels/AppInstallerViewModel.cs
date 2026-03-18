using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AuraClean.ViewModels;

public partial class AppInstallerViewModel : ObservableObject
{
    [ObservableProperty] private ObservableCollection<BundleApp> _apps = [];
    [ObservableProperty] private ObservableCollection<string> _categories = [];
    [ObservableProperty] private string _selectedCategory = "All";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _statusMessage = "Select the applications you want to install.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isInstalling;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _installedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _overallProgress;
    [ObservableProperty] private string _currentAppName = string.Empty;

    private readonly List<BundleApp> _allApps;
    private CancellationTokenSource? _cts;

    public AppInstallerViewModel()
    {
        _allApps = AppInstallerService.GetAppCatalog();

        var cats = new List<string> { "All" };
        cats.AddRange(_allApps.Select(a => a.Category).Distinct().OrderBy(c => c));
        Categories = new ObservableCollection<string>(cats);

        ApplyFilter();
    }

    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var filtered = _allApps.AsEnumerable();

        if (SelectedCategory != "All")
            filtered = filtered.Where(a => a.Category == SelectedCategory);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var q = SearchText.Trim();
            filtered = filtered.Where(a =>
                a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.Category.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        Apps = new ObservableCollection<BundleApp>(filtered);
        UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = _allApps.Count(a => a.IsSelected);
    }

    [RelayCommand]
    private void ToggleApp(BundleApp app)
    {
        app.IsSelected = !app.IsSelected;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var app in Apps)
            app.IsSelected = true;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var app in Apps)
            app.IsSelected = false;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void SelectCategory(string category)
    {
        foreach (var app in _allApps.Where(a => a.Category == category))
            app.IsSelected = true;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private async Task InstallSelectedAsync()
    {
        var toInstall = _allApps.Where(a => a.IsSelected).ToList();
        if (toInstall.Count == 0)
        {
            StatusMessage = "No applications selected. Check the ones you want to install.";
            return;
        }

        IsBusy = true;
        IsInstalling = true;
        InstalledCount = 0;
        FailedCount = 0;
        OverallProgress = 0;
        _cts = new CancellationTokenSource();

        StatusMessage = $"Installing {toInstall.Count} application(s)...";

        try
        {
            for (int i = 0; i < toInstall.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();

                var app = toInstall[i];
                CurrentAppName = app.Name;
                app.Status = InstallStatus.Downloading;
                app.StatusMessage = "Starting download...";

                var appProgress = new Progress<(int percent, string message)>(p =>
                {
                    app.ProgressPercent = p.percent;
                    app.StatusMessage = p.message;
                    OverallProgress = (int)((i * 100.0 + p.percent) / toInstall.Count);
                });

                try
                {
                    await AppInstallerService.InstallAppAsync(app, appProgress, _cts.Token);
                    app.Status = InstallStatus.Completed;
                    app.StatusMessage = "Installed!";
                    InstalledCount++;
                }
                catch (OperationCanceledException)
                {
                    app.Status = InstallStatus.Skipped;
                    app.StatusMessage = "Cancelled";
                    throw;
                }
                catch (Exception ex)
                {
                    app.Status = InstallStatus.Failed;
                    app.StatusMessage = $"Failed: {ex.Message}";
                    FailedCount++;
                    DiagnosticLogger.Error("AppInstaller", $"Failed to install {app.Name}", ex);
                }

                OverallProgress = (int)(((i + 1) * 100.0) / toInstall.Count);
            }

            StatusMessage = $"Done! {InstalledCount} installed" +
                            (FailedCount > 0 ? $", {FailedCount} failed" : "") + ".";

            NotificationService.ShowSuccess("App Bundle Installer",
                $"Installed {InstalledCount} of {toInstall.Count} application(s).");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"Installation cancelled. {InstalledCount} installed before cancel.";
        }
        finally
        {
            IsInstalling = false;
            IsBusy = false;
            CurrentAppName = string.Empty;
            AppInstallerService.CleanupDownloads();
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelInstall()
    {
        _cts?.Cancel();
    }
}
