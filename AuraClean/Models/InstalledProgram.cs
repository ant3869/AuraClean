using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace AuraClean.Models;

/// <summary>
/// Represents a program installed on the system, sourced from the Windows Uninstall registry keys.
/// </summary>
public partial class InstalledProgram : ObservableObject
{
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _displayVersion = string.Empty;
    [ObservableProperty] private string _publisher = string.Empty;
    [ObservableProperty] private string _installLocation = string.Empty;
    [ObservableProperty] private string _uninstallString = string.Empty;
    [ObservableProperty] private string _quietUninstallString = string.Empty;
    [ObservableProperty] private string _displayIcon = string.Empty;
    [ObservableProperty] private string _installDate = string.Empty;
    [ObservableProperty] private long _estimatedSizeKB;
    [ObservableProperty] private bool _isWindowsInstaller;
    [ObservableProperty] private bool _isSelected;

    /// <summary>The registry key path this entry was read from (for post-uninstall cleanup).</summary>
    public string RegistryKeyPath { get; set; } = string.Empty;

    /// <summary>Registry view (32-bit vs 64-bit) that this entry belongs to.</summary>
    public RegistryView RegistryView { get; set; }

    /// <summary>Registry hive that this entry belongs to.</summary>
    public RegistryHive RegistryHive { get; set; } = RegistryHive.LocalMachine;

    public string FormattedSize =>
        EstimatedSizeKB switch
        {
            0 => "Unknown",
            < 1024 => $"{EstimatedSizeKB} KB",
            < 1_048_576 => $"{EstimatedSizeKB / 1024.0:F1} MB",
            _ => $"{EstimatedSizeKB / 1_048_576.0:F2} GB"
        };
}
