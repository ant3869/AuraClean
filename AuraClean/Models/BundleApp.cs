using CommunityToolkit.Mvvm.ComponentModel;

namespace AuraClean.Models;

/// <summary>
/// Represents a free/open-source application available for bundle installation.
/// </summary>
public partial class BundleApp : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string IconKind { get; init; } = "Application"; // MaterialDesign PackIcon kind
    public string DownloadUrl { get; init; } = string.Empty;
    public string InstallerArgs { get; init; } = string.Empty; // Silent install arguments
    public string Website { get; init; } = string.Empty;
    public string License { get; init; } = "Free";
    public bool IsPortable { get; init; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private InstallStatus _status = InstallStatus.Ready;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _progressPercent;
}

public enum InstallStatus
{
    Ready,
    Downloading,
    Installing,
    Completed,
    Failed,
    Skipped
}
