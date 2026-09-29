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

    /// <summary>
    /// Every low-risk (Normal mode) category — the default set for headless scheduled runs.
    /// </summary>
    public static IReadOnlyList<JunkType> GetNormalModeDefaults() => Enum.GetValues<JunkType>()
        .Where(IsNormalModeJunkType)
        .ToList();

    /// <summary>
    /// Validates configured <c>ScheduledCleanupCategories</c> names (JunkType names,
    /// case-insensitive). Unknown names and review-only categories are dropped because an
    /// unattended run must never remove anything the user hasn't approved for Normal mode.
    /// Empty (or fully invalid) input falls back to <see cref="GetNormalModeDefaults"/>.
    /// Never throws.
    /// </summary>
    public static HashSet<JunkType> ResolveScheduledCategories(IEnumerable<string>? configuredNames)
    {
        var defaults = new HashSet<JunkType>(GetNormalModeDefaults());

        try
        {
            var names = configuredNames?
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            if (names == null || names.Count == 0)
                return defaults;

            var resolved = new HashSet<JunkType>();
            foreach (var name in names)
            {
                if (Enum.TryParse<JunkType>(name.Trim(), ignoreCase: true, out var type) &&
                    IsNormalModeJunkType(type))
                    resolved.Add(type);
            }

            return resolved.Count > 0 ? resolved : defaults;
        }
        catch (Exception ex)
        {
            Helpers.DiagnosticLogger.Warn("CleanupModePolicy", "Failed to resolve scheduled categories", ex);
            return defaults;
        }
    }
}
