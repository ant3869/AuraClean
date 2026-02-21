using CommunityToolkit.Mvvm.ComponentModel;

namespace AuraClean.Models;

/// <summary>
/// The type of junk that was discovered.
/// </summary>
public enum JunkType
{
    TempFile,
    WindowsUpdateCache,
    Prefetch,
    CrashDump,
    BranchCache,
    ThumbnailCache,
    OrphanedRegistryKey,
    RemnantDirectory,
    AbandonedFile,
    BrowserCache,
    BrowserTracking,
    RecycleBin,
    DeliveryOptimization,
    WindowsErrorReporting,
    FontCache,
    LogFile,
    WindowsOld
}

/// <summary>
/// A single item of system junk discovered during analysis.
/// </summary>
public partial class JunkItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private JunkType _type;
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private string _lockingProcess = string.Empty;
    [ObservableProperty] private DateTime _lastModified;

    /// <summary>Human-readable category label for UI grouping. Can be overridden.</summary>
    private string? _categoryOverride;
    public string Category
    {
        get => _categoryOverride ?? Type switch
        {
            JunkType.TempFile => "Temporary Files",
            JunkType.WindowsUpdateCache => "Windows Update Cache",
            JunkType.Prefetch => "Prefetch Data",
            JunkType.CrashDump => "Crash Dumps",
            JunkType.BranchCache => "BranchCache",
            JunkType.ThumbnailCache => "Thumbnail Cache",
            JunkType.OrphanedRegistryKey => "Orphaned Registry Keys",
            JunkType.RemnantDirectory => "Remnant Directories",
            JunkType.AbandonedFile => "Abandoned Files",
            JunkType.BrowserCache => "Browser Cache",
            JunkType.BrowserTracking => "Browser Tracking Data",
            JunkType.RecycleBin => "Recycle Bin",
            JunkType.DeliveryOptimization => "Delivery Optimization",
            JunkType.WindowsErrorReporting => "Windows Error Reporting",
            JunkType.FontCache => "Font Cache",
            JunkType.LogFile => "Log Files",
            JunkType.WindowsOld => "Windows.old",
            _ => "Other"
        };
        set => _categoryOverride = value;
    }

    public string FormattedSize => SizeBytes switch
    {
        0 => "",
        < 1024 => $"{SizeBytes} B",
        < 1_048_576 => $"{SizeBytes / 1024.0:F1} KB",
        < 1_073_741_824 => $"{SizeBytes / 1_048_576.0:F1} MB",
        _ => $"{SizeBytes / 1_073_741_824.0:F2} GB"
    };
}
