using AuraClean.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

namespace AuraClean.Services;

/// <summary>
/// Recursive empty-folder scanner. Finds directories that contain no files
/// (including nested subdirectories that are themselves empty).
/// Works bottom-up so that deeply nested empty trees are fully detected.
/// </summary>
public static class EmptyFolderFinderService
{
    /// <summary>
    /// Scans the given root paths for empty folders (bottom-up).
    /// A folder is "empty" if it contains zero files in its entire subtree.
    /// The scan roots themselves are never reported, and system locations are skipped.
    /// </summary>
    public static async Task<List<EmptyFolderItem>> ScanAsync(
        IEnumerable<string> rootPaths,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<EmptyFolderItem>();
        var roots = rootPaths
            .Select(PathSafety.Normalize)
            .Where(p => p != null)
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await Task.Run(() =>
        {
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) continue;

                if (IsExcludedLocation(root))
                {
                    progress?.Report($"Skipping protected location {root}");
                    continue;
                }

                progress?.Report($"Scanning {root}...");

                try
                {
                    ScanDirectoryRecursive(root, isRoot: true, results, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (UnauthorizedAccessException) { }
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn("EmptyFolderFinder", $"Error scanning {root}", ex);
                }
            }
        }, ct);

        return results
            .DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Bottom-up recursive scan. Returns true if the directory is empty
    /// (contains no files in its entire subtree).
    /// </summary>
    private static bool ScanDirectoryRecursive(
        string path, bool isRoot, List<EmptyFolderItem> results, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        bool hasFiles;
        string[] subdirs;

        try
        {
            hasFiles = Directory.EnumerateFiles(path).Any();
            subdirs = Directory.GetDirectories(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return false;
        }

        bool allSubdirsEmpty = true;
        foreach (var subdir in subdirs)
        {
            ct.ThrowIfCancellationRequested();

            // Junctions, symlinks, and system locations are treated as "not empty" so their
            // parents are never removed.
            try
            {
                var attrs = File.GetAttributes(subdir);
                if (attrs.HasFlag(FileAttributes.ReparsePoint) || IsExcludedLocation(subdir))
                {
                    allSubdirsEmpty = false;
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                allSubdirsEmpty = false;
                continue;
            }

            if (!ScanDirectoryRecursive(subdir, isRoot: false, results, ct))
                allSubdirsEmpty = false;
        }

        bool isEmpty = !hasFiles && allSubdirsEmpty;

        // The folder the user asked to scan is never itself a deletion candidate.
        if (isEmpty && !isRoot && !PathSafety.IsProtectedRoot(path))
        {
            try
            {
                results.RemoveAll(r => PathSafety.IsSameOrUnder(r.Path, path));
                results.Add(new EmptyFolderItem
                {
                    Path = path,
                    Name = Path.GetFileName(path),
                    ParentPath = Path.GetDirectoryName(path) ?? "",
                    LastModified = Directory.GetLastWriteTime(path),
                    IsSelected = false,
                    EmptySubfolderCount = subdirs.Length
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLogger.Warn("EmptyFolderFinder", $"Could not record {path}", ex);
            }
        }

        return isEmpty;
    }

    /// <summary>
    /// Deletes the selected empty folders. Each folder is re-verified immediately before removal
    /// and removed bottom-up without recursion, so files created after the scan are never lost.
    /// </summary>
    public static async Task<(int Deleted, int Failed)> DeleteAsync(
        IEnumerable<EmptyFolderItem> items,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        int deleted = 0, failed = 0;
        var selectedItems = items.Where(i => i.IsSelected)
            .OrderByDescending(i => i.Path.Length) // Delete deepest first
            .ToList();

        await Task.Run(() =>
        {
            int processed = 0;
            foreach (var item in selectedItems)
            {
                ct.ThrowIfCancellationRequested();
                processed++;
                progress?.Report($"Deleting ({processed}/{selectedItems.Count}): {item.Name}");

                try
                {
                    if (!Directory.Exists(item.Path))
                    {
                        item.IsDeleted = true;
                        deleted++;
                        continue;
                    }

                    if (PathSafety.IsProtectedRoot(item.Path) || IsExcludedLocation(item.Path))
                    {
                        failed++;
                        DiagnosticLogger.Warn("EmptyFolderFinder", $"Refused to delete protected folder {item.Path}");
                        continue;
                    }

                    if (TryDeleteEmptyTree(item.Path))
                    {
                        item.IsDeleted = true;
                        deleted++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed++;
                    DiagnosticLogger.Warn("EmptyFolderFinder", $"Failed to delete {item.Path}", ex);
                }
            }
        }, ct);

        return (deleted, failed);
    }

    /// <summary>
    /// Removes a directory tree only if it still contains no files and no reparse points.
    /// </summary>
    private static bool TryDeleteEmptyTree(string root)
    {
        var rootInfo = new DirectoryInfo(root);
        if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;

        var allOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = 0
        };

        if (rootInfo.EnumerateFiles("*", allOptions).Any())
            return false;

        var subdirs = rootInfo.EnumerateDirectories("*", allOptions).ToList();
        if (subdirs.Any(d => d.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            return false;

        foreach (var dir in subdirs.OrderByDescending(d => d.FullName.Length))
            dir.Delete(recursive: false);

        rootInfo.Delete(recursive: false);
        return true;
    }

    /// <summary>
    /// Returns low-risk temp/cache roots for empty-folder scans.
    /// </summary>
    public static List<string> GetDefaultScanPaths()
    {
        var paths = new List<string>();

        AddIfExists(paths, Path.GetTempPath());
        AddIfExists(paths, Path.Combine(PathSafety.WindowsDirectory, "Temp"));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddIfExists(paths, Path.Combine(localAppData, "Temp"));
        AddIfExists(paths, Path.Combine(localAppData, "CrashDumps"));
        AddIfExists(paths, Path.Combine(localAppData, @"Microsoft\Windows\WER"));

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        AddIfExists(paths, Path.Combine(programData, @"Microsoft\Windows\WER"));

        return paths
            .Select(p => PathSafety.Normalize(p) ?? p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Locations whose empty folders are structural (Windows, Program Files, ProgramData\Microsoft).
    /// The default temp/WER roots are explicitly allowed even though some live under %SystemRoot%.
    /// </summary>
    private static readonly Lazy<List<string>> AllowedSystemRoots = new(GetDefaultScanPaths);

    private static bool IsExcludedLocation(string path)
    {
        foreach (var allowed in AllowedSystemRoots.Value)
        {
            if (PathSafety.IsSameOrUnder(path, allowed))
                return false;
        }

        return PathSafety.IsSystemCriticalLocation(path);
    }

    private static void AddIfExists(List<string> paths, string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            paths.Add(path);
    }
}

/// <summary>
/// Represents an empty folder found during scanning.
/// </summary>
public partial class EmptyFolderItem : ObservableObject
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ParentPath { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public int EmptySubfolderCount { get; set; }

    [ObservableProperty] private bool _isSelected;

    /// <summary>Set by <see cref="EmptyFolderFinderService.DeleteAsync"/> once removed.</summary>
    public bool IsDeleted { get; set; }

    public string DisplayInfo => EmptySubfolderCount > 0
        ? $"Contains {EmptySubfolderCount} empty subfolder(s)"
        : "Empty folder";
}
