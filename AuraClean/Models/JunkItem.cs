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
    WindowsOld,
    WinSxS
}

/// <summary>
/// A single item of system junk discovered during analysis.
/// </summary>
public partial class JunkItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Category))]
    [NotifyPropertyChangedFor(nameof(IsHighRisk))]
    [NotifyPropertyChangedFor(nameof(RiskNote))]
    private JunkType _type;

    /// <summary>Advanced system maintenance (WinSxS, Windows.old): never bulk-selected, always confirmed.</summary>
    public bool IsHighRisk => Services.CleanupModePolicy.IsHighRiskType(Type);

    /// <summary>Plain-language explanation of what cleaning this item does; null for routine items.</summary>
    public string? RiskNote => Services.CleanupModePolicy.DescribeHighRisk(Type);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedSize))]
    private long _sizeBytes;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private string _lockingProcess = string.Empty;
    [ObservableProperty] private DateTime _lastModified;

    /// <summary>Ownership verdict and evidence for uninstall leftovers; null for other junk.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOwnershipBlocked), nameof(CanSelect), nameof(OwnershipSummary))]
    private OwnershipAssessment? _ownership;

    public bool IsOwnershipBlocked => Ownership?.IsBlocked == true;
    public bool CanSelect => !IsOwnershipBlocked;
    public string OwnershipSummary => Ownership?.Summary ?? string.Empty;

    /// <summary>A blocked leftover can never become selected, whatever the UI binding does.</summary>
    partial void OnIsSelectedChanged(bool value)
    {
        if (value && IsOwnershipBlocked)
            IsSelected = false;
    }

    partial void OnOwnershipChanged(OwnershipAssessment? value)
    {
        if (value?.IsBlocked == true)
            IsSelected = false;
    }

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
            JunkType.WinSxS => "Component Store (WinSxS)",
            _ => "Other"
        };
        set => _categoryOverride = value;
    }

    public string FormattedSize => SizeBytes <= 0 ? string.Empty : Helpers.FormatHelper.FormatBytes(SizeBytes);

    /// <summary>Screen readers announce a list row by its ToString() when no AutomationProperties.Name is set.</summary>
    public override string ToString()
    {
        var label = string.IsNullOrWhiteSpace(Description) ? Path : Description;
        return SizeBytes > 0 ? $"{label}, {FormattedSize}" : label;
    }
}
