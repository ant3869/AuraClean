using AuraClean.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System.Security.Cryptography;

namespace AuraClean.Services;

/// <summary>
/// Scans directories for duplicate files using a multi-pass approach:
///   1. Group by file size (fast filter — most files have unique sizes)
///   2. Compare first 4KB hash (quick elimination of differently-starting files)
///   3. Full SHA-256 hash for final confirmation
/// Memory-efficient: streams files rather than loading into RAM.
/// </summary>
public static partial class DuplicateFinderService
{
    /// <summary>
    /// Represents a group of duplicate files sharing the same content.
    /// </summary>
    public class DuplicateGroup
    {
        public string Hash { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public List<DuplicateFileEntry> Files { get; set; } = [];
        public int Count => Files.Count;
        /// <summary>Shared file name of the group (copies differ only by folder), for group headers.</summary>
        public string DisplayName => Files.Count > 0 ? Files[0].FileName : Hash;
        public long WastedBytes => FileSize * Math.Max(0, Count - 1); // All copies except one are "wasted"
        public string FormattedSize => FormatHelper.FormatBytes(FileSize);
        public string FormattedWasted => FormatHelper.FormatBytes(WastedBytes);
    }

    /// <summary>
    /// Represents a single file within a duplicate group.
    /// </summary>
    public partial class DuplicateFileEntry : ObservableObject
    {
        public string FullPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Directory { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTime LastModified { get; set; }

        /// <summary>Marked for deletion.</summary>
        [ObservableProperty] private bool _isSelected;

        /// <summary>The copy that will be kept.</summary>
        [ObservableProperty] private bool _isKeep;

        public string FormattedSize => FormatHelper.FormatBytes(SizeBytes);

        partial void OnIsKeepChanged(bool value)
        {
            if (value && IsSelected)
                IsSelected = false;
        }

        partial void OnIsSelectedChanged(bool value)
        {
            if (value && IsKeep)
                IsKeep = false;
        }
    }

    /// <summary>
    /// Result of a duplicate scan.
    /// </summary>
    public class DuplicateScanResult
    {
        public List<DuplicateGroup> Groups { get; set; } = [];
        public int TotalDuplicateFiles { get; set; }
        public long TotalWastedBytes { get; set; }
        public int TotalFilesScanned { get; set; }
        public TimeSpan ScanDuration { get; set; }
    }

    /// <summary>
    /// Scans a directory for duplicate files.
    /// </summary>
    /// <param name="rootPath">The directory to scan.</param>
    /// <param name="minSizeBytes">Minimum file size to consider (default 1KB — skip tiny files).</param>
    /// <param name="maxSizeMB">Maximum file size in MB (default 500MB — skip huge files for speed).</param>
    /// <param name="fileExtensions">Optional filter: only scan specific extensions (e.g., ".jpg", ".png").</param>
    /// <param name="recursive">Whether to scan subdirectories.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<DuplicateScanResult> ScanForDuplicatesAsync(
        string rootPath,
        long minSizeBytes = 1024,
        int maxSizeMB = 500,
        string[]? fileExtensions = null,
        bool recursive = true,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var startTime = DateTime.Now;
        var result = new DuplicateScanResult();
        long maxSizeBytes = (long)maxSizeMB * 1024 * 1024;

        await Task.Run(() =>
        {
            // Lower thread priority so UI stays responsive
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

            try
            {
                // ═══ PASS 1: Group by file size ═══
                progress?.Report("Pass 1/3: Grouping files by size...");
                var sizeGroups = new Dictionary<long, List<string>>();
                int totalFiles = 0;

                var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

                foreach (var file in EnumerateFilesSafe(rootPath, searchOption))
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.Length < minSizeBytes || fi.Length > maxSizeBytes) continue;

                        // Extension filter
                        if (fileExtensions != null && fileExtensions.Length > 0)
                        {
                            if (!fileExtensions.Any(ext =>
                                fi.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase)))
                                continue;
                        }

                        // Skip system/hidden files
                        if (fi.Attributes.HasFlag(FileAttributes.System)) continue;

                        totalFiles++;
                        if (totalFiles % 5000 == 0)
                            progress?.Report($"Pass 1/3: Indexed {totalFiles:N0} files...");

                        if (!sizeGroups.TryGetValue(fi.Length, out var list))
                        {
                            list = [];
                            sizeGroups[fi.Length] = list;
                        }
                        list.Add(file);
                    }
                    catch { }
                }

                result.TotalFilesScanned = totalFiles;

                // Filter to only sizes with 2+ files
                var candidates = sizeGroups.Where(g => g.Value.Count >= 2).ToList();
                progress?.Report($"Pass 1/3 complete: {candidates.Count:N0} size groups with potential duplicates.");

                // ═══ PASS 2: Partial hash (first 4KB) ═══
                progress?.Report("Pass 2/3: Computing partial hashes...");
                var partialHashGroups = new Dictionary<string, List<string>>();
                int processed = 0;

                foreach (var (size, files) in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    processed++;

                    if (processed % 100 == 0)
                        progress?.Report($"Pass 2/3: Processing group {processed:N0}/{candidates.Count:N0}...");

                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        var partialHash = ComputePartialHash(file, 4096);
                        if (partialHash == null) continue;

                        var key = $"{size}_{partialHash}";
                        if (!partialHashGroups.TryGetValue(key, out var list))
                        {
                            list = [];
                            partialHashGroups[key] = list;
                        }
                        list.Add(file);
                    }
                }

                // Filter to only partial hash groups with 2+ files
                var hashCandidates = partialHashGroups.Where(g => g.Value.Count >= 2).ToList();
                progress?.Report($"Pass 2/3 complete: {hashCandidates.Count:N0} groups need full hash verification.");

                // ═══ PASS 3: Full SHA-256 hash ═══
                progress?.Report("Pass 3/3: Computing full file hashes...");
                var fullHashGroups = new Dictionary<string, List<string>>();
                processed = 0;

                foreach (var (_, files) in hashCandidates)
                {
                    ct.ThrowIfCancellationRequested();
                    processed++;

                    if (processed % 50 == 0)
                        progress?.Report($"Pass 3/3: Verifying group {processed:N0}/{hashCandidates.Count:N0}...");

                    foreach (var file in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        var fullHash = ComputeFullHash(file);
                        if (fullHash == null) continue;

                        if (!fullHashGroups.TryGetValue(fullHash, out var list))
                        {
                            list = [];
                            fullHashGroups[fullHash] = list;
                        }
                        list.Add(file);
                    }
                }

                // Build final result groups
                foreach (var (hash, files) in fullHashGroups.Where(g => g.Value.Count >= 2))
                {
                    var entries = new List<DuplicateFileEntry>();
                    foreach (var file in files)
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            entries.Add(new DuplicateFileEntry
                            {
                                FullPath = file,
                                FileName = fi.Name,
                                Directory = fi.DirectoryName ?? "",
                                SizeBytes = fi.Length,
                                LastModified = fi.LastWriteTime
                            });
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // File vanished between hashing and reporting.
                        }
                    }

                    if (entries.Count < 2)
                        continue;

                    // Keep the oldest copy (most likely the original); the rest are selected.
                    var ordered = entries.OrderBy(e => e.LastModified).ThenBy(e => e.FullPath.Length).ToList();
                    ordered[0].IsKeep = true;
                    foreach (var dup in ordered.Skip(1))
                        dup.IsSelected = true;

                    result.Groups.Add(new DuplicateGroup
                    {
                        Hash = hash[..16], // Truncated for display
                        FileSize = ordered[0].SizeBytes,
                        Files = ordered
                    });
                }

                // Sort by wasted bytes (most wasted first)
                result.Groups.Sort((a, b) => b.WastedBytes.CompareTo(a.WastedBytes));
                result.TotalDuplicateFiles = result.Groups.Sum(g => g.Count - 1);
                result.TotalWastedBytes = result.Groups.Sum(g => g.WastedBytes);
            }
            finally
            {
                Thread.CurrentThread.Priority = ThreadPriority.Normal;
            }
        }, ct);

        result.ScanDuration = DateTime.Now - startTime;
        progress?.Report($"Scan complete: {result.Groups.Count:N0} duplicate groups, " +
                         $"{FormatHelper.FormatBytes(result.TotalWastedBytes)} wasted space.");

        return result;
    }

    /// <summary>
    /// Deletes selected duplicate files from the scan results.
    /// </summary>
    public static async Task<(int Deleted, int Failed, long BytesFreed)> DeleteDuplicatesAsync(
        IEnumerable<DuplicateGroup> groups,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        int deleted = 0, failed = 0;
        long bytesFreed = 0;
        var groupList = groups.ToList();

        await Task.Run(() =>
        {
            foreach (var group in groupList)
            {
                var toDelete = group.Files.Where(f => f.IsSelected && !f.IsKeep).ToList();
                if (toDelete.Count == 0)
                    continue;

                // Invariant: at least one copy of every group must survive. If the user
                // selected every file, keep the first unselected-or-oldest copy.
                var survivors = group.Files.Except(toDelete).Where(f => File.Exists(f.FullPath)).ToList();
                if (survivors.Count == 0)
                {
                    var keep = toDelete.OrderBy(f => f.LastModified).First();
                    toDelete.Remove(keep);
                    keep.IsSelected = false;
                    keep.IsKeep = true;
                    DiagnosticLogger.Warn("DuplicateFinderService",
                        $"Every copy was selected; keeping {keep.FullPath}");
                }

                foreach (var file in toDelete)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Deleting: {file.FileName}...");

                    try
                    {
                        var info = new FileInfo(file.FullPath);
                        if (!info.Exists)
                            continue;

                        // The file changed since it was hashed — it may no longer be a duplicate.
                        if (info.Length != file.SizeBytes || info.LastWriteTime != file.LastModified)
                        {
                            failed++;
                            DiagnosticLogger.Warn("DuplicateFinderService", $"Skipped modified file: {file.FullPath}");
                            continue;
                        }

                        if (!PathSafety.IsSafeToDeleteFile(file.FullPath, out var reason))
                        {
                            failed++;
                            DiagnosticLogger.Warn("DuplicateFinderService", $"Skipped {file.FullPath}: {reason}");
                            continue;
                        }

                        if (info.IsReadOnly)
                            info.IsReadOnly = false;
                        info.Delete();
                        bytesFreed += file.SizeBytes;
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        DiagnosticLogger.Warn("DuplicateFinderService", $"Failed to delete duplicate: {file.FullPath}", ex);
                        failed++;
                    }
                }
            }
        }, ct);

        return (deleted, failed, bytesFreed);
    }

    #region Private Helpers

    private static IEnumerable<string> EnumerateFilesSafe(string path, SearchOption option)
    {
        if (option == SearchOption.TopDirectoryOnly)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(path); }
            catch { yield break; }
            foreach (var f in files) yield return f;
            yield break;
        }

        // Recursive with error handling for each directory
        var stack = new Stack<string>();
        stack.Push(path);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            // Enumerate files in current dir
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir); }
            catch { continue; }

            foreach (var f in files)
                yield return f;

            // Push subdirectories
            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(dir))
                {
                    try
                    {
                        var attrs = File.GetAttributes(subDir);
                        if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue;

                        // Duplicate DLLs/assets inside Windows or Program Files are required
                        // copies — never offer them for deletion.
                        if (PathSafety.IsSystemCriticalLocation(subDir)) continue;

                        stack.Push(subDir);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch { }
        }
    }

    private static string? ComputePartialHash(string filePath, int bytesToRead)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[bytesToRead];
            int bytesRead = stream.Read(buffer, 0, bytesToRead);
            if (bytesRead == 0) return null;

            var hash = SHA256.HashData(buffer.AsSpan(0, bytesRead));
            return Convert.ToHexString(hash);
        }
        catch
        {
            return null;
        }
    }

    private static string? ComputeFullHash(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return null;
        }
    }

    #endregion
}
