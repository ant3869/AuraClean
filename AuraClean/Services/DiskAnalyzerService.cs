using AuraClean.Helpers;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Security;

namespace AuraClean.Services;

/// <summary>
/// Visual Disk Analyzer — recursive directory crawler that generates
/// hierarchical folder-size data for treemap visualization.
/// </summary>
public static class DiskAnalyzerService
{
    /// <summary>
    /// Represents a node in the disk usage tree (file or directory).
    /// </summary>
    public class DiskNode
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public bool IsDirectory { get; set; }
        public int FileCount { get; set; }
        public int DirectoryCount { get; set; }
        public DateTime LastModified { get; set; }
        public List<DiskNode> Children { get; set; } = [];
        
        /// <summary>Percentage of parent's total size.</summary>
        public double SizePercent { get; set; }

        public string FormattedSize => SizeBytes switch
        {
            0 => "0 B",
            < 1024 => $"{SizeBytes} B",
            < 1_048_576 => $"{SizeBytes / 1024.0:F1} KB",
            < 1_073_741_824 => $"{SizeBytes / 1_048_576.0:F1} MB",
            _ => $"{SizeBytes / 1_073_741_824.0:F2} GB"
        };
    }

    /// <summary>
    /// Result of a disk analysis operation.
    /// </summary>
    public class AnalysisResult
    {
        public DiskNode Root { get; set; } = new();
        public long TotalSizeBytes { get; set; }
        public int TotalFiles { get; set; }
        public int TotalDirectories { get; set; }
        public List<DiskNode> LargestFiles { get; set; } = [];
        public List<DiskNode> LargestDirectories { get; set; } = [];
        public TimeSpan ScanDuration { get; set; }
    }

    /// <summary>
    /// Live progress snapshot emitted while a scan is running.
    /// </summary>
    /// <param name="ItemsScanned">Files and folders visited so far.</param>
    /// <param name="BytesScanned">Total size of the files visited so far.</param>
    /// <param name="CurrentPath">Folder currently being read.</param>
    /// <param name="PercentEstimate">
    /// Estimated completion (0–99) when scanning a whole drive, based on the drive's used space;
    /// null when the total is unknown (custom folders).
    /// </param>
    public readonly record struct ScanProgress(
        long ItemsScanned, long BytesScanned, string CurrentPath, double? PercentEstimate);

    private const int TopN = 20;
    private const int MaxChildrenPerNode = 50;
    private const long ProgressIntervalMs = 150;

    /// <summary>
    /// Single-level enumeration that includes hidden/system files (pagefile, hiberfil, etc. are real
    /// disk usage), skips folders it cannot open instead of aborting, and uses a larger buffer so
    /// huge folders (WinSxS, caches) need fewer kernel round-trips. Sizes come from the directory
    /// listing itself, so no per-file metadata call is made.
    /// </summary>
    private static readonly EnumerationOptions ScanOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024
    };

    /// <summary>
    /// Analyzes a directory recursively and builds a size tree.
    /// Folders down to <paramref name="maxDepth"/> become tree nodes; deeper folders are
    /// summed into their ancestor without allocating nodes. Every file at every depth is
    /// counted, sized and considered for the largest-files list.
    /// </summary>
    public static async Task<AnalysisResult> AnalyzeDirectoryAsync(
        string rootPath,
        int maxDepth = 4,
        IProgress<ScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var stopwatch = Stopwatch.StartNew();
        var result = new AnalysisResult();
        var context = new ScanContext(maxDepth, GetExpectedBytes(rootPath), progress, ct);

        await Task.Run(() =>
        {
            // Lower thread priority so UI stays responsive
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            try
            {
                var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(rootPath));
                result.Root = CrawlDirectory(rootPath, rootName, GetLastWriteTimeSafe(rootPath), 0, context);
                result.TotalSizeBytes = result.Root.SizeBytes;
                result.TotalFiles = result.Root.FileCount;
                result.TotalDirectories = result.Root.DirectoryCount;

                if (result.TotalSizeBytes > 0)
                    ComputePercentages(result.Root, result.TotalSizeBytes);
            }
            finally
            {
                Thread.CurrentThread.Priority = ThreadPriority.Normal;
            }
        }, ct);

        result.ScanDuration = stopwatch.Elapsed;
        result.LargestFiles = context.TopFiles.Values.Reverse().Take(TopN).ToList();
        result.LargestDirectories = context.TopDirs.Values.Reverse().Take(TopN).ToList();
        return result;
    }

    /// <summary>
    /// Gets quick stats for all fixed drives on the system.
    /// A drive that errors while being queried (locked, ejected, failing) is skipped.
    /// </summary>
    public static List<DriveStats> GetDriveStats()
    {
        var stats = new List<DriveStats>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed || drive.TotalSize <= 0)
                    continue;

                long total = drive.TotalSize;
                long free = drive.TotalFreeSpace;
                stats.Add(new DriveStats
                {
                    Name = drive.Name,
                    Label = drive.VolumeLabel,
                    TotalBytes = total,
                    FreeBytes = free,
                    UsedBytes = total - free,
                    UsagePercent = (double)(total - free) / total * 100
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLogger.Warn("DiskAnalyzer", $"Skipping drive {drive.Name}: {ex.Message}");
            }
        }
        return stats;
    }

    /// <summary>Comparer that allows duplicate keys in SortedList.</summary>
    private sealed class DuplicateKeyComparer : IComparer<long>
    {
        public int Compare(long x, long y)
        {
            int result = x.CompareTo(y);
            return result == 0 ? 1 : result; // never return 0 so duplicates are allowed
        }
    }

    #region Private Helpers

    /// <summary>One directory-listing entry, captured without a separate metadata call.</summary>
    private readonly record struct ScanEntry(
        string Name, long Length, bool IsDirectory, bool IsReparsePoint, DateTimeOffset LastWriteUtc);

    /// <summary>Mutable state shared across one scan (single background thread).</summary>
    private sealed class ScanContext(
        int maxDepth, long expectedBytes, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        private long _nextReportAt;

        public int MaxDepth { get; } = maxDepth;
        public CancellationToken Ct { get; } = ct;
        public SortedList<long, DiskNode> TopFiles { get; } = new(new DuplicateKeyComparer());
        public SortedList<long, DiskNode> TopDirs { get; } = new(new DuplicateKeyComparer());
        public long ItemsScanned { get; private set; }
        public long BytesScanned { get; private set; }

        public void CountFile(string directory, in ScanEntry entry)
        {
            ItemsScanned++;
            BytesScanned += entry.Length;

            if (TopFiles.Count >= TopN && entry.Length <= TopFiles.Keys[0])
                return;

            TopFiles.Add(entry.Length, new DiskNode
            {
                Name = entry.Name,
                FullPath = Path.Join(directory, entry.Name),
                SizeBytes = entry.Length,
                IsDirectory = false,
                LastModified = entry.LastWriteUtc.LocalDateTime
            });
            if (TopFiles.Count > TopN)
                TopFiles.RemoveAt(0); // Remove smallest
        }

        public void CountDirectory() => ItemsScanned++;

        public void TrackDirectory(DiskNode node)
        {
            if (node.SizeBytes <= 0 || (TopDirs.Count >= TopN && node.SizeBytes <= TopDirs.Keys[0]))
                return;

            TopDirs.Add(node.SizeBytes, new DiskNode
            {
                Name = node.Name,
                FullPath = node.FullPath,
                SizeBytes = node.SizeBytes,
                IsDirectory = true,
                FileCount = node.FileCount,
                DirectoryCount = node.DirectoryCount,
                LastModified = node.LastModified
            });
            if (TopDirs.Count > TopN)
                TopDirs.RemoveAt(0);
        }

        /// <summary>Emits a progress snapshot at most every <see cref="ProgressIntervalMs"/>.</summary>
        public void Report(string currentPath)
        {
            if (progress == null) return;

            long now = Environment.TickCount64;
            if (now < _nextReportAt) return;
            _nextReportAt = now + ProgressIntervalMs;

            double? percent = expectedBytes > 0
                ? Math.Min(99.0, BytesScanned * 100.0 / expectedBytes)
                : null;
            progress.Report(new ScanProgress(ItemsScanned, BytesScanned, currentPath, percent));
        }
    }

    private static DiskNode CrawlDirectory(
        string path, string name, DateTime lastModified, int depth, ScanContext context)
    {
        context.Ct.ThrowIfCancellationRequested();
        context.Report(path);

        var node = new DiskNode
        {
            Name = string.IsNullOrEmpty(name) ? path : name, // Drive roots like "C:\" have no file name
            FullPath = path,
            IsDirectory = true,
            LastModified = lastModified
        };

        foreach (var entry in ReadDirectory(path, context.Ct))
        {
            context.Ct.ThrowIfCancellationRequested();

            if (!entry.IsDirectory)
            {
                node.SizeBytes += entry.Length;
                node.FileCount++;
                context.CountFile(path, entry);
                continue;
            }

            // Junctions / symlinks point elsewhere on disk: following them double-counts and can loop.
            if (entry.IsReparsePoint) continue;

            context.CountDirectory();
            var childPath = Path.Join(path, entry.Name);

            if (depth < context.MaxDepth)
            {
                var child = CrawlDirectory(childPath, entry.Name, entry.LastWriteUtc.LocalDateTime, depth + 1, context);
                node.Children.Add(child);
                node.SizeBytes += child.SizeBytes;
                node.FileCount += child.FileCount;
                node.DirectoryCount += child.DirectoryCount + 1;
            }
            else
            {
                var (size, files, dirs) = SumDirectoryTree(childPath, context);
                node.SizeBytes += size;
                node.FileCount += files;
                node.DirectoryCount += dirs + 1;
            }
        }

        // Sort children by size descending for treemap layout; the treemap shows at most 50
        node.Children.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        if (node.Children.Count > MaxChildrenPerNode)
            node.Children.RemoveRange(MaxChildrenPerNode, node.Children.Count - MaxChildrenPerNode);

        context.TrackDirectory(node);
        return node;
    }

    /// <summary>
    /// Totals a folder subtree below the node depth limit without building nodes.
    /// Iterative so arbitrarily deep trees cannot overflow the stack.
    /// </summary>
    private static (long Size, int Files, int Directories) SumDirectoryTree(string rootPath, ScanContext context)
    {
        long size = 0;
        int files = 0;
        int directories = 0;
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            context.Ct.ThrowIfCancellationRequested();
            var path = pending.Pop();
            context.Report(path);

            foreach (var entry in ReadDirectory(path, context.Ct))
            {
                if (!entry.IsDirectory)
                {
                    size += entry.Length;
                    files++;
                    context.CountFile(path, entry);
                }
                else if (!entry.IsReparsePoint)
                {
                    directories++;
                    context.CountDirectory();
                    pending.Push(Path.Join(path, entry.Name));
                }
            }
        }

        return (size, files, directories);
    }

    /// <summary>
    /// Reads one directory level. Returns whatever was read before an error: a folder that
    /// vanishes, is locked, or fails mid-listing never aborts the whole scan.
    /// </summary>
    private static List<ScanEntry> ReadDirectory(string path, CancellationToken ct)
    {
        var entries = new List<ScanEntry>();
        try
        {
            var enumerable = new FileSystemEnumerable<ScanEntry>(
                path,
                static (ref FileSystemEntry e) => new ScanEntry(
                    e.FileName.ToString(),
                    e.IsDirectory ? 0 : e.Length,
                    e.IsDirectory,
                    (e.Attributes & FileAttributes.ReparsePoint) != 0,
                    e.LastWriteTimeUtc),
                ScanOptions);

            foreach (var entry in enumerable)
            {
                ct.ThrowIfCancellationRequested();
                entries.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Inaccessible, removed, or unreadable mid-scan — keep what we have and move on.
        }
        return entries;
    }

    /// <summary>
    /// Used bytes on the volume when <paramref name="rootPath"/> is a drive root, else 0 (unknown).
    /// </summary>
    private static long GetExpectedBytes(string rootPath)
    {
        try
        {
            var full = Path.GetFullPath(rootPath);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) ||
                !string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(root),
                    StringComparison.OrdinalIgnoreCase))
                return 0;

            var drive = new DriveInfo(root);
            return drive.IsReady ? Math.Max(0, drive.TotalSize - drive.TotalFreeSpace) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or SecurityException)
        {
            return 0;
        }
    }

    private static DateTime GetLastWriteTimeSafe(string path)
    {
        try { return Directory.GetLastWriteTime(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return DateTime.MinValue;
        }
    }

    private static void ComputePercentages(DiskNode node, long parentSize)
    {
        if (parentSize <= 0) return;

        node.SizePercent = (double)node.SizeBytes / parentSize * 100.0;

        foreach (var child in node.Children)
        {
            if (node.SizeBytes > 0)
                child.SizePercent = (double)child.SizeBytes / node.SizeBytes * 100.0;

            if (child.IsDirectory)
                ComputePercentages(child, node.SizeBytes);
        }
    }

    #endregion

    public class DriveStats
    {
        public string Name { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
        public long UsedBytes { get; set; }
        public double UsagePercent { get; set; }

        public string FormattedTotal => FormatHelper.FormatBytes(TotalBytes);
        public string FormattedFree => FormatHelper.FormatBytes(FreeBytes);
        public string FormattedUsed => FormatHelper.FormatBytes(UsedBytes);
    }
}
