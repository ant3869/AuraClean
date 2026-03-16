using AuraClean.Helpers;
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
    /// </summary>
    public static async Task<List<EmptyFolderItem>> ScanAsync(
        IEnumerable<string> rootPaths,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<EmptyFolderItem>();

        await Task.Run(() =>
        {
            foreach (var root in rootPaths)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) continue;

                progress?.Report($"Scanning {root}...");

                try
                {
                    ScanDirectoryRecursive(root, results, ct);
                }
                catch (UnauthorizedAccessException) { }
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn("EmptyFolderFinder",
                        $"Error scanning {root}", ex);
                }
            }
        }, ct);

        return results;
    }

    /// <summary>
    /// Bottom-up recursive scan. Returns true if the directory is empty
    /// (contains no files in its entire subtree).
    /// </summary>
    private static bool ScanDirectoryRecursive(
        string path, List<EmptyFolderItem> results, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        bool hasFiles;
        string[] subdirs;

        try
        {
            // Check for any files in this directory (not recursive)
            hasFiles = Directory.EnumerateFiles(path).Any();
            subdirs = Directory.GetDirectories(path);
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch { return false; }

        // Recurse into subdirectories
        bool allSubdirsEmpty = true;
        foreach (var subdir in subdirs)
        {
            ct.ThrowIfCancellationRequested();

            // Skip system/hidden junction points and reparse points
            try
            {
                var attrs = File.GetAttributes(subdir);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    allSubdirsEmpty = false;
                    continue;
                }
            }
            catch { continue; }

            bool subdirEmpty = ScanDirectoryRecursive(subdir, results, ct);
            if (!subdirEmpty) allSubdirsEmpty = false;
        }

        bool isEmpty = !hasFiles && allSubdirsEmpty;

        if (isEmpty && subdirs.Length == 0)
        {
            // Leaf empty directory — add it
            try
            {
                results.Add(new EmptyFolderItem
                {
                    Path = path,
                    Name = Path.GetFileName(path),
                    ParentPath = Path.GetDirectoryName(path) ?? "",
                    LastModified = Directory.GetLastWriteTime(path),
                    IsSelected = true
                });
            }
            catch { }
        }
        else if (isEmpty && subdirs.Length > 0)
        {
            // Parent of only-empty subdirectories — add as a tree root
            try
            {
                results.Add(new EmptyFolderItem
                {
                    Path = path,
                    Name = Path.GetFileName(path),
                    ParentPath = Path.GetDirectoryName(path) ?? "",
                    LastModified = Directory.GetLastWriteTime(path),
                    IsSelected = true,
                    EmptySubfolderCount = subdirs.Length
                });

                // Remove child entries since parent covers them
                results.RemoveAll(r =>
                    r.Path.StartsWith(path + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) &&
                    !r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            }
            catch { }
        }

        return isEmpty;
    }

    /// <summary>
    /// Deletes the selected empty folders.
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
                    if (Directory.Exists(item.Path))
                    {
                        Directory.Delete(item.Path, recursive: true);
                        deleted++;
                    }
                    else
                    {
                        deleted++; // Already gone
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    DiagnosticLogger.Warn("EmptyFolderFinder",
                        $"Failed to delete {item.Path}", ex);
                }
            }
        }, ct);

        return (deleted, failed);
    }

    /// <summary>
    /// Returns common scan root paths (user profile folders, program files, etc.).
    /// </summary>
    public static List<string> GetDefaultScanPaths()
    {
        var paths = new List<string>();

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(userProfile))
            paths.Add(userProfile);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (Directory.Exists(programFiles))
            paths.Add(programFiles);

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (Directory.Exists(programFilesX86) && programFilesX86 != programFiles)
            paths.Add(programFilesX86);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (Directory.Exists(appData))
            paths.Add(appData);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (Directory.Exists(localAppData))
            paths.Add(localAppData);

        return paths;
    }
}

/// <summary>
/// Represents an empty folder found during scanning.
/// </summary>
public class EmptyFolderItem
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ParentPath { get; set; } = string.Empty;
    public DateTime LastModified { get; set; }
    public bool IsSelected { get; set; } = true;
    public int EmptySubfolderCount { get; set; }

    public string DisplayInfo => EmptySubfolderCount > 0
        ? $"Contains {EmptySubfolderCount} empty subfolder(s)"
        : "Empty folder";
}
