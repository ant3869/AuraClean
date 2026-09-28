using AuraClean.Models;
using AuraClean.Services;

namespace AuraClean.ViewModels;

public partial class CleanerViewModel : IExperienceModeAware
{
    private bool _isAdvancedMode = ExperienceModeService.IsAdvancedMode();
    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (SetProperty(ref _isAdvancedMode, value))
            {
                OnPropertyChanged(nameof(IsNormalMode));
                OnPropertyChanged(nameof(SmartCleanLabel));
            }
        }
    }
    public bool IsNormalMode => !IsAdvancedMode;

    public void SetExperienceMode(bool isAdvancedMode)
    {
        if (IsAdvancedMode == isAdvancedMode)
            return;

        IsAdvancedMode = isAdvancedMode;

        // Results gathered in the other mode may include review-only categories (or hide
        // selectable ones), so they are discarded rather than silently reused.
        if (HasResults && !IsBusy)
        {
            Categories = [];
            HasResults = false;
            TotalJunkSize = 0;
            TotalJunkCount = 0;
            StatusMessage = "Mode changed — run Analyze again to refresh the results.";
        }
    }
}

public partial class BrowserCleanerViewModel : IExperienceModeAware
{
    private bool _isAdvancedMode = ExperienceModeService.IsAdvancedMode();
    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (SetProperty(ref _isAdvancedMode, value))
                OnPropertyChanged(nameof(IsNormalMode));
        }
    }
    public bool IsNormalMode => !IsAdvancedMode;
    public void SetExperienceMode(bool isAdvancedMode)
    {
        IsAdvancedMode = isAdvancedMode;
        if (!isAdvancedMode)
        {
            CleanCache = true;
            CleanTracking = false;
            VacuumDatabases = false;
            FlushDns = false;
        }
    }
}

public partial class DuplicateFinderViewModel : IExperienceModeAware
{
    private bool _isAdvancedMode = ExperienceModeService.IsAdvancedMode();
    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (SetProperty(ref _isAdvancedMode, value))
                OnPropertyChanged(nameof(IsNormalMode));
        }
    }
    public bool IsNormalMode => !IsAdvancedMode;
    public void SetExperienceMode(bool isAdvancedMode) => IsAdvancedMode = isAdvancedMode;
}

public partial class LargeFileFinderViewModel : IExperienceModeAware
{
    private bool _isAdvancedMode = ExperienceModeService.IsAdvancedMode();
    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (SetProperty(ref _isAdvancedMode, value))
            {
                OnPropertyChanged(nameof(IsNormalMode));
                OnPropertyChanged(nameof(DeleteButtonLabel));
                OnPropertyChanged(nameof(DeleteButtonHint));
            }
        }
    }
    public bool IsNormalMode => !IsAdvancedMode;
    public void SetExperienceMode(bool isAdvancedMode) => IsAdvancedMode = isAdvancedMode;
}

public partial class EmptyFolderFinderViewModel : IExperienceModeAware
{
    private bool _isAdvancedMode = ExperienceModeService.IsAdvancedMode();
    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (SetProperty(ref _isAdvancedMode, value))
                OnPropertyChanged(nameof(IsNormalMode));
        }
    }
    public bool IsNormalMode => !IsAdvancedMode;
    public void SetExperienceMode(bool isAdvancedMode) => IsAdvancedMode = isAdvancedMode;
}

public partial class UninstallerViewModel : IExperienceModeAware
{
    private bool _isAdvancedMode = ExperienceModeService.IsAdvancedMode();
    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (SetProperty(ref _isAdvancedMode, value))
            {
                OnPropertyChanged(nameof(IsNormalMode));
                OnPropertyChanged(nameof(ShowPostUninstallResults));
                OnPropertyChanged(nameof(ShowProgramSelectionBadge));
            }
        }
    }
    public bool IsNormalMode => !IsAdvancedMode;
    public bool ShowPostUninstallResults => IsAdvancedMode && HasPostUninstallResults;
    public bool ShowProgramSelectionBadge => IsAdvancedMode && HasCheckedItems;

    public void SetExperienceMode(bool isAdvancedMode)
    {
        IsAdvancedMode = isAdvancedMode;
        if (!isAdvancedMode)
        {
            IsAllSelected = false;
            foreach (var program in Programs)
                program.IsSelected = false;
            UpdateSelectionCount();
        }
    }

    partial void OnHasPostUninstallResultsChanged(bool value) =>
        OnPropertyChanged(nameof(ShowPostUninstallResults));

    partial void OnSelectedCountChanged(int value) =>
        OnPropertyChanged(nameof(ShowProgramSelectionBadge));
}

public partial class ThreatScannerViewModel : IExperienceModeAware
{
    private bool _isAdvancedMode = ExperienceModeService.IsAdvancedMode();
    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (SetProperty(ref _isAdvancedMode, value))
                OnPropertyChanged(nameof(IsNormalMode));
        }
    }
    public bool IsNormalMode => !IsAdvancedMode;
    public void SetExperienceMode(bool isAdvancedMode)
    {
        IsAdvancedMode = isAdvancedMode;
        if (!isAdvancedMode && SelectedScanMode != ScanMode.Quick)
            SelectedScanMode = ScanMode.Quick;
    }
}

public partial class StartupManagerViewModel : IExperienceModeAware
{
    private bool _isAdvancedMode = ExperienceModeService.IsAdvancedMode();
    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (SetProperty(ref _isAdvancedMode, value))
            {
                OnPropertyChanged(nameof(IsNormalMode));
                OnPropertyChanged(nameof(StartupToggleLabel));
                OnPropertyChanged(nameof(ShowStartupSelectionBadge));
            }
        }
    }
    public bool IsNormalMode => !IsAdvancedMode;
    public string StartupToggleLabel => "Enable / Disable";
    public bool ShowStartupSelectionBadge => HasCheckedItems;
    public void SetExperienceMode(bool isAdvancedMode)
    {
        IsAdvancedMode = isAdvancedMode;
        if (!isAdvancedMode)
        {
            IsAllSelected = false;
            foreach (var entry in Entries)
                entry.IsSelected = false;
            UpdateSelectionCount();
        }
    }

    partial void OnSelectedCountChanged(int value) =>
        OnPropertyChanged(nameof(ShowStartupSelectionBadge));
}

public partial class DiskOptimizerViewModel : IExperienceModeAware
{
    private bool _isAdvancedMode = ExperienceModeService.IsAdvancedMode();
    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (SetProperty(ref _isAdvancedMode, value))
                OnPropertyChanged(nameof(IsNormalMode));
        }
    }
    public bool IsNormalMode => !IsAdvancedMode;
    public void SetExperienceMode(bool isAdvancedMode) => IsAdvancedMode = isAdvancedMode;
}

public partial class FileShredderViewModel : IExperienceModeAware
{
    private bool _isAdvancedMode = ExperienceModeService.IsAdvancedMode();
    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (SetProperty(ref _isAdvancedMode, value))
                OnPropertyChanged(nameof(IsNormalMode));
        }
    }
    public bool IsNormalMode => !IsAdvancedMode;
    public void SetExperienceMode(bool isAdvancedMode) => IsAdvancedMode = isAdvancedMode;
}

public partial class SoftwareUpdaterViewModel : IExperienceModeAware
{
    private bool _isAdvancedMode = ExperienceModeService.IsAdvancedMode();
    public bool IsAdvancedMode
    {
        get => _isAdvancedMode;
        set
        {
            if (SetProperty(ref _isAdvancedMode, value))
                OnPropertyChanged(nameof(IsNormalMode));
        }
    }
    public bool IsNormalMode => !IsAdvancedMode;
    public void SetExperienceMode(bool isAdvancedMode) => IsAdvancedMode = isAdvancedMode;
}
