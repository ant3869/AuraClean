using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using AuraClean.Helpers;

namespace AuraClean.Services;

/// <summary>
/// Scans directories for large files that may be wasting disk space.
/// Supports configurable size thresholds and directory exclusions.
/// </summary>
public static class LargeFileFinderService
{
    /// <summary>
    /// Represents a large file found during scanning.
    /// </summary>
    public class LargeFileEntry : INotifyPropertyChanged
    {
        private bool _isSelected;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? SelectionChanged;

        public string FullPath { get; init; } = string.Empty;
        public string FileName => Path.GetFileName(FullPath);
        public string Directory => Path.GetDirectoryName(FullPath) ?? string.Empty;
        public string Extension => Path.GetExtension(FullPath).ToUpperInvariant();
        public long SizeBytes { get; init; }
        public string FormattedSize => FormatHelper.FormatBytes(SizeBytes);
        public DateTime LastModified { get; init; }
        public string Category => CategorizeFile(Extension);

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                    SelectionChanged?.Invoke();
                }
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static string CategorizeFile(string ext) => ext.ToUpperInvariant() switch
        {
            ".MP4" or ".AVI" or ".MKV" or ".MOV" or ".WMV" or ".FLV" or ".WEBM" => "Video",
            ".MP3" or ".WAV" or ".FLAC" or ".AAC" or ".OGG" or ".WMA" => "Audio",
            ".ISO" or ".IMG" or ".BIN" or ".NRG" => "Disk Image",
            ".ZIP" or ".RAR" or ".7Z" or ".TAR" or ".GZ" or ".BZ2" => "Archive",
            ".EXE" or ".MSI" or ".DLL" => "Executable",
            ".LOG" or ".TXT" or ".CSV" => "Log / Text",
            ".BAK" or ".OLD" or ".TMP" or ".TEMP" => "Backup / Temp",
            ".VMDK" or ".VHD" or ".VHDX" or ".VDI" => "Virtual Disk",
            ".PSD" or ".AI" or ".BMP" or ".TIFF" or ".RAW" => "Image (Large)",
            ".SQLITE" or ".DB" or ".MDF" or ".LDF" => "Database",
            _ => "Other"
        };
    }

    /// <summary>
    /// Result of a large file scan.
    /// </summary>
    public record ScanResult(
        List<LargeFileEntry> Files,
        int TotalFilesScanned,
        int DirectoriesScanned,
        long TotalSizeFound,
        int AccessErrors);

    /// <summary>
    /// Default directories to skip during scanning.
    /// </summary>
    private static readonly HashSet<string> DefaultExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "$Recycle.Bin",
        "System Volume Information",
        "$WinREAgent",
        "Recovery",
        "PerfLogs",
    };

    /// <summary>
    /// Scans the specified directory for files larger than the given threshold.
    /// </summary>
    public static async Task<ScanResult> ScanAsync(
        string rootPath,
        long minimumSizeBytes,
        int maxResults = 500,
        bool includeSystemDirs = false,
        IProgress<(int filesScanned, int found, string currentDir)>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<LargeFileEntry>();
        int totalScanned = 0;
        int dirsScanned = 0;
        int accessErrors = 0;

        await Task.Run(() =>
        {
            // Stack-based directory traversal to avoid StackOverflowException on deep trees
            var dirStack = new Stack<string>();
            dirStack.Push(rootPath);

            while (dirStack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var currentDir = dirStack.Pop();
                dirsScanned++;

                // Skip exclusions
                if (!includeSystemDirs)
                {
                    var dirName = Path.GetFileName(currentDir);
                    if (DefaultExclusions.Contains(dirName))
                        continue;
                }

                // Enumerate files
                try
                {
                    foreach (var filePath in Directory.EnumerateFiles(currentDir))
                    {
                        ct.ThrowIfCancellationRequested();
                        totalScanned++;

                        try
                        {
                            var info = new FileInfo(filePath);
                            if (info.Length >= minimumSizeBytes)
                            {
                                results.Add(new LargeFileEntry
                                {
                                    FullPath = filePath,
                                    SizeBytes = info.Length,
                                    LastModified = info.LastWriteTime,
                                });

                                // Keep results bounded during scan for memory efficiency
                                if (results.Count > maxResults * 2)
                                {
                                    results = results
                                        .OrderByDescending(f => f.SizeBytes)
                                        .Take(maxResults)
                                        .ToList();
                                }
                            }
                        }
                        catch { accessErrors++; }

                        if (totalScanned % 500 == 0)
                            progress?.Report((totalScanned, results.Count, currentDir));
                    }
                }
                catch { accessErrors++; }

                // Enqueue subdirectories
                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                    {
                        try
                        {
                            var dirInfo = new DirectoryInfo(subDir);
                            if ((dirInfo.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0
                                && !includeSystemDirs)
                                continue;

                            dirStack.Push(subDir);
                        }
                        catch { accessErrors++; }
                    }
                }
                catch { accessErrors++; }
            }
        }, ct);

        // Final sort and trim
        results = results
            .OrderByDescending(f => f.SizeBytes)
            .Take(maxResults)
            .ToList();

        long totalSize = results.Sum(f => f.SizeBytes);

        return new ScanResult(results, totalScanned, dirsScanned, totalSize, accessErrors);
    }

    /// <summary>
    /// Gets the available drives on the system with their free space info.
    /// </summary>
    public static List<(string Name, string Label, long TotalSize, long FreeSpace)> GetDrives()
    {
        var drives = new List<(string, string, long, long)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                {
                    drives.Add((
                        drive.Name,
                        string.IsNullOrEmpty(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                        drive.TotalSize,
                        drive.AvailableFreeSpace));
                }
            }
            catch { }
        }
        return drives;
    }

    /// <summary>
    /// Safely deletes a file (non-shredded, normal deletion).
    /// </summary>
    public static (bool success, string message) DeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return (false, "File not found.");

            var info = new FileInfo(path);
            if (info.IsReadOnly)
                info.IsReadOnly = false;

            File.Delete(path);
            return (true, $"Deleted: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed: {ex.Message}");
        }
    }
}
