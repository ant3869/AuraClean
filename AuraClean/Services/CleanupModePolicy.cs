using AuraClean.Models;

namespace AuraClean.Services;

public static class CleanupModePolicy
{
    public static bool IsNormalModeJunkType(JunkType type) => type switch
    {
        JunkType.TempFile or
        JunkType.WindowsUpdateCache or
        JunkType.Prefetch or
        JunkType.CrashDump or
        JunkType.BranchCache or
        JunkType.ThumbnailCache or
        JunkType.BrowserCache or
        JunkType.DeliveryOptimization or
        JunkType.WindowsErrorReporting or
        JunkType.FontCache or
        JunkType.LogFile => true,
        _ => false
    };

    /// <summary>
    /// Returns whether an item of the given type should be pre-selected for cleaning.
    /// Review-only categories are never pre-selected; the user's per-category cleaner
    /// preferences are honored in both modes.
    /// </summary>
    public static bool IsSelectedByDefault(JunkType type, AppSettings settings, bool isAdvancedMode)
    {
        if (!isAdvancedMode && !IsNormalModeJunkType(type))
            return false;

        return type switch
        {
            JunkType.TempFile => settings.CleanTempFiles,
            JunkType.WindowsUpdateCache or JunkType.DeliveryOptimization => settings.CleanWindowsUpdate,
            JunkType.Prefetch => settings.CleanPrefetch,
            JunkType.CrashDump or JunkType.WindowsErrorReporting => settings.CleanCrashDumps,
            JunkType.RecycleBin => settings.CleanRecycleBin,
            JunkType.BrowserCache => settings.CleanBrowserCache,
            JunkType.ThumbnailCache or JunkType.FontCache => settings.CleanThumbnailCache,
            JunkType.LogFile => settings.CleanWindowsLogs,
            JunkType.BranchCache => true,
            JunkType.WindowsOld or
            JunkType.WinSxS or
            JunkType.AbandonedFile or
            JunkType.RemnantDirectory or
            JunkType.OrphanedRegistryKey or
            JunkType.BrowserTracking => false,
            _ => false
        };
    }

    /// <summary>Applies <see cref="IsSelectedByDefault"/> to every item.</summary>
    public static void ApplyDefaultSelection(IEnumerable<JunkItem> items, AppSettings settings, bool isAdvancedMode)
    {
        foreach (var item in items)
            item.IsSelected = IsSelectedByDefault(item.Type, settings, isAdvancedMode);
    }
}
