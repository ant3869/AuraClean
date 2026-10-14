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
    /// Advanced system maintenance whose effects are hard or impossible to undo (removes the
    /// previous Windows installation / superseded servicing components). Never selected by bulk
    /// actions or default selection; only by deliberate per-item selection, and always confirmed.
    /// </summary>
    public static bool IsHighRiskType(JunkType type) => type is JunkType.WinSxS or JunkType.WindowsOld;

    /// <summary>Whether Select All / category-level selection may select items of this type.</summary>
    public static bool IsBulkSelectable(JunkType type) => !IsHighRiskType(type);

    /// <summary>What cleaning this category does and what can no longer be undone; null if routine.</summary>
    public static string? DescribeHighRisk(JunkType type) => type switch
    {
        JunkType.WinSxS =>
            "Runs Windows' own Component Store cleanup (DISM /StartComponentCleanup). It removes superseded " +
            "versions of Windows components, can take several minutes, and cannot be safely interrupted once " +
            "started. Updates installed before the cleanup can no longer be rolled back to those older component versions.",
        JunkType.WindowsOld =>
            "Permanently deletes the previous Windows installation (Windows.old). You will no longer be able to " +
            "go back to the previous version of Windows, and the files cannot be recovered.",
        _ => null
    };

    /// <summary>
    /// Confirmation text for a cleanup that includes high-risk categories (one paragraph per
    /// category, with size). Null when the items contain nothing high-risk.
    /// </summary>
    public static string? BuildHighRiskWarning(IEnumerable<JunkItem> items)
    {
        var groups = items.Where(i => IsHighRiskType(i.Type))
            .GroupBy(i => i.Type)
            .OrderBy(g => g.Key)
            .ToList();
        if (groups.Count == 0)
            return null;

        var lines = groups.Select(g =>
            $"• {g.First().Category} ({Helpers.FormatHelper.FormatBytes(g.Sum(i => i.SizeBytes))}): {DescribeHighRisk(g.Key)}");
        return "This cleanup includes advanced system maintenance:\n\n" +
               string.Join("\n\n", lines) +
               "\n\nThis is not part of routine cleanup and cannot be undone. Continue?";
    }

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
