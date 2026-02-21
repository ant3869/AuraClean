using AuraClean.Helpers;
using System.IO;

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
    /// Analyzes a directory recursively and builds a size tree.
    /// Memory-efficient: only stores directory nodes and top-N largest files.
    /// </summary>
    public static async Task<AnalysisResult> AnalyzeDirectoryAsync(
        string rootPath,
        int maxDepth = 4,
        IProgress<string>? progress = null,
        IProgress<double>? percentProgress = null,
        CancellationToken ct = default)
    {
        var startTime = DateTime.Now;
        var result = new AnalysisResult();
        // Bounded collections for top-20 tracking (avoid storing all files)
        var topFiles = new SortedList<long, DiskNode>(new DuplicateKeyComparer());
        var topDirs = new SortedList<long, DiskNode>(new DuplicateKeyComparer());
        const int TopN = 20;
        int scannedCount = 0;

        await Task.Run(() =>
        {
            // Lower thread priority so UI stays responsive
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            try
            {
                progress?.Report($"Analyzing {rootPath}...");
                result.Root = CrawlDirectory(rootPath, 0, maxDepth,
                    topFiles, topDirs, TopN,
                    progress, ref scannedCount, ct);
                result.TotalSizeBytes = result.Root.SizeBytes;
                result.TotalFiles = result.Root.FileCount;
                result.TotalDirectories = result.Root.DirectoryCount;

                // Compute percentages
                if (result.TotalSizeBytes > 0)
                    ComputePercentages(result.Root, result.TotalSizeBytes);
            }
            finally
            {
                Thread.CurrentThread.Priority = ThreadPriority.Normal;
            }
        }, ct);

        result.ScanDuration = DateTime.Now - startTime;

        // Extract top-20 from our bounded collections
        result.LargestFiles = topFiles.Values.Reverse().Take(TopN).ToList();
        result.LargestDirectories = topDirs.Values.Reverse().Take(TopN).ToList();

        progress?.Report($"Analysis complete: {result.TotalFiles:N0} files, " +
                         $"{result.TotalDirectories:N0} directories, " +
                         $"{FormatHelper.FormatBytes(result.TotalSizeBytes)} total.");

        return result;
    }

    /// <summary>
    /// Gets quick stats for all fixed drives on the system.
    /// </summary>
    public static List<DriveStats> GetDriveStats()
    {
        var stats = new List<DriveStats>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            stats.Add(new DriveStats
            {
                Name = drive.Name,
                Label = drive.VolumeLabel,
                TotalBytes = drive.TotalSize,
                FreeBytes = drive.TotalFreeSpace,
                UsedBytes = drive.TotalSize - drive.TotalFreeSpace,
                UsagePercent = (double)(drive.TotalSize - drive.TotalFreeSpace) / drive.TotalSize * 100
            });
        }
        return stats;
    }

    /// <summary>Comparer that allows duplicate keys in SortedList.</summary>
    private class DuplicateKeyComparer : IComparer<long>
    {
        public int Compare(long x, long y)
        {
            int result = x.CompareTo(y);
            return result == 0 ? 1 : result; // never return 0 so duplicates are allowed
        }
    }

    #region Private Helpers

    private static DiskNode CrawlDirectory(
        string path, int depth, int maxDepth,
        SortedList<long, DiskNode> topFiles, SortedList<long, DiskNode> topDirs, int topN,
        IProgress<string>? progress,
        ref int scannedCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var node = new DiskNode
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            IsDirectory = true,
            LastModified = Directory.GetLastWriteTime(path)
        };

        if (string.IsNullOrEmpty(node.Name))
            node.Name = path; // For drive roots like "C:\"

        try
        {
            // Enumerate files — DON'T create child nodes for files (too much memory).
            // Instead just aggregate size and track top-N largest.
            foreach (var file in Directory.EnumerateFiles(path))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(file);
                    long len = fi.Length;
                    node.SizeBytes += len;
                    node.FileCount++;

                    // Track top-N largest files using a bounded sorted list
                    if (topFiles.Count < topN || len > topFiles.Keys[0])
                    {
                        topFiles.Add(len, new DiskNode
                        {
                            Name = fi.Name,
                            FullPath = fi.FullName,
                            SizeBytes = len,
                            IsDirectory = false,
                            LastModified = fi.LastWriteTime
                        });
                        if (topFiles.Count > topN)
                            topFiles.RemoveAt(0); // Remove smallest
                    }

                    scannedCount++;
                    if (scannedCount % 10000 == 0)
                    {
                        progress?.Report($"Scanned {scannedCount:N0} items...");
                    }
                }
                catch { }
            }

            // Recurse into subdirectories (up to maxDepth)
            if (depth < maxDepth)
            {
                foreach (var dir in Directory.EnumerateDirectories(path))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        // Skip system/hidden reparse points (junctions, symlinks)
                        var attrs = File.GetAttributes(dir);
                        if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue;

                        var childNode = CrawlDirectory(dir, depth + 1, maxDepth,
                            topFiles, topDirs, topN, progress, ref scannedCount, ct);

                        node.Children.Add(childNode);
                        node.SizeBytes += childNode.SizeBytes;
                        node.FileCount += childNode.FileCount;
                        node.DirectoryCount += childNode.DirectoryCount + 1;
                    }
                    catch (UnauthorizedAccessException) { }
                    catch (DirectoryNotFoundException) { }
                }
            }
            else
            {
                // Beyond maxDepth, just count sizes without building child tree
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(path))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            long subSize = GetDirectorySizeFast(dir, ct);
                            node.SizeBytes += subSize;
                            node.DirectoryCount++;
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Sort children by size descending for treemap layout
            node.Children.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));

            // Only keep top 50 children to limit memory (treemap only shows 50 anyway)
            if (node.Children.Count > 50)
                node.Children.RemoveRange(50, node.Children.Count - 50);

            // Track top-N largest directories
            if (node.SizeBytes > 0 && (topDirs.Count < topN || node.SizeBytes > topDirs.Keys[0]))
            {
                topDirs.Add(node.SizeBytes, new DiskNode
                {
                    Name = node.Name,
                    FullPath = node.FullPath,
                    SizeBytes = node.SizeBytes,
                    IsDirectory = true,
                    FileCount = node.FileCount,
                    DirectoryCount = node.DirectoryCount,
                    LastModified = node.LastModified
                });
                if (topDirs.Count > topN)
                    topDirs.RemoveAt(0);
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        return node;
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

    private static long GetDirectorySizeFast(string path, CancellationToken ct)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                try { size += new FileInfo(file).Length; }
                catch { }
            }
        }
        catch { }
        return size;
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
