using AuraClean.Helpers;
using System.Globalization;
using System.Windows.Data;

namespace AuraClean.Services;

/// <summary>
/// Backs the cleaner exclusion list stored in
/// <see cref="AppSettings.CleanerExcludedPaths"/>. Matching is case-insensitive; a
/// folder entry excludes everything beneath it (subtree), while a file entry excludes
/// only that exact path. Never throws.
/// </summary>
public static class CleanerExcludeStore
{
    /// <summary>
    /// Returns true when <paramref name="path"/> is covered by the persisted exclusion list.
    /// </summary>
    public static bool IsExcluded(string? path)
    {
        try
        {
            return IsExcluded(path, SettingsService.Load().CleanerExcludedPaths);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("CleanerExcludeStore", "Failed to evaluate exclusion list", ex);
            return false;
        }
    }

    /// <summary>
    /// Pure overload against an explicit exclusion list (used by tests and callers that
    /// already hold settings). Case-insensitive; folder entries cover their subtree.
    /// </summary>
    public static bool IsExcluded(string? path, IEnumerable<string>? exclusions)
    {
        if (string.IsNullOrWhiteSpace(path) || exclusions == null)
            return false;

        var candidate = NormalizePath(path);
        if (candidate.Length == 0)
            return false;

        foreach (var entry in exclusions)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            var excluded = NormalizePath(entry);
            if (excluded.Length == 0)
                continue;

            // Exact match (file or the folder itself)…
            if (candidate.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                return true;

            // …or the candidate lives beneath an excluded folder. The separator guard
            // keeps "C:\Temp" from matching the sibling "C:\Temp2\file.txt".
            // (Drive roots already end in a separator, so they match by prefix alone.)
            if (candidate.Length > excluded.Length &&
                candidate.StartsWith(excluded, StringComparison.OrdinalIgnoreCase) &&
                (excluded.EndsWith('\\') || candidate[excluded.Length] == '\\'))
                return true;
        }

        return false;
    }

    /// <summary>Adds a path to the persisted exclusion list (deduped, case-insensitive).</summary>
    public static void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var settings = SettingsService.Load();
            var list = settings.CleanerExcludedPaths ?? [];
            if (list.Contains(path.Trim(), StringComparer.OrdinalIgnoreCase))
                return;

            settings.CleanerExcludedPaths = [.. list, path.Trim()];
            settings.LastModified = DateTime.Now;
            SettingsService.Save(settings);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("CleanerExcludeStore", "Failed to add exclusion", ex);
        }
    }

    /// <summary>Removes a path from the persisted exclusion list. Returns true when removed.</summary>
    public static bool Remove(string path)
    {
        try
        {
            var settings = SettingsService.Load();
            var list = settings.CleanerExcludedPaths ?? [];
            int removed = list.RemoveAll(p => p.Equals(path.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return false;

            settings.CleanerExcludedPaths = list;
            settings.LastModified = DateTime.Now;
            SettingsService.Save(settings);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("CleanerExcludeStore", "Failed to remove exclusion", ex);
            return false;
        }
    }

    /// <summary>
    /// Normalizes for comparison: forward slashes become backslashes, surrounding
    /// whitespace and trailing separators go (drive roots keep theirs).
    /// </summary>
    internal static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('/', '\\');
        while (normalized.Length > 3 &&
               (normalized.EndsWith('\\')))
            normalized = normalized[..^1];
        return normalized;
    }
}

/// <summary>
/// Shows the "Excluded" reason badge on cleaner rows whose path is in the exclusion list.
/// </summary>
public sealed class PathExcludedToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        CleanerExcludeStore.IsExcluded(value as string)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;
}
