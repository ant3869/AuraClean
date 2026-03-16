using AuraClean.Helpers;
using System.Diagnostics;
using System.IO;
using System.Management;

namespace AuraClean.Services;

/// <summary>
/// Disk Optimizer — detects drive type (HDD/SSD) and runs defragment or TRIM/optimize
/// using the built-in Windows defrag.exe tool.
/// </summary>
public static class DiskOptimizerService
{
    public enum DriveMediaType
    {
        Unknown,
        HDD,
        SSD
    }

    public enum OptimizeStatus
    {
        Pending,
        Running,
        Success,
        Failed,
        Skipped
    }

    public class DriveOptimizeInfo
    {
        public string DriveLetter { get; set; } = string.Empty;
        public string VolumeLabel { get; set; } = string.Empty;
        public DriveMediaType MediaType { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
        public string FileSystem { get; set; } = string.Empty;
        public int FragmentPercent { get; set; }
        public OptimizeStatus Status { get; set; } = OptimizeStatus.Pending;
        public string StatusMessage { get; set; } = string.Empty;
        public bool IsSelected { get; set; }

        public string MediaTypeLabel => MediaType switch
        {
            DriveMediaType.SSD => "SSD",
            DriveMediaType.HDD => "HDD",
            _ => "Unknown"
        };

        public string OptimizeAction => MediaType switch
        {
            DriveMediaType.SSD => "TRIM / Optimize",
            DriveMediaType.HDD => "Defragment",
            _ => "Optimize"
        };

        public string FormattedTotal => FormatHelper.FormatBytes(TotalBytes);
        public string FormattedFree => FormatHelper.FormatBytes(FreeBytes);
    }

    /// <summary>
    /// Enumerates all fixed drives and detects their media type via WMI.
    /// </summary>
    public static async Task<List<DriveOptimizeInfo>> GetDrivesAsync()
    {
        return await Task.Run(() =>
        {
            var drives = new List<DriveOptimizeInfo>();
            var mediaTypes = DetectMediaTypes();

            foreach (var di in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                var letter = di.Name.TrimEnd('\\');
                drives.Add(new DriveOptimizeInfo
                {
                    DriveLetter = letter,
                    VolumeLabel = string.IsNullOrEmpty(di.VolumeLabel) ? "Local Disk" : di.VolumeLabel,
                    TotalBytes = di.TotalSize,
                    FreeBytes = di.TotalFreeSpace,
                    FileSystem = di.DriveFormat,
                    MediaType = mediaTypes.GetValueOrDefault(letter.Substring(0, 1), DriveMediaType.Unknown),
                    IsSelected = true
                });
            }

            return drives;
        });
    }

    /// <summary>
    /// Runs defrag /O (optimize) on the given drive, which automatically
    /// performs defragmentation for HDDs and retrim for SSDs.
    /// </summary>
    public static async Task<(bool Success, string Output)> OptimizeDriveAsync(
        string driveLetter,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var letter = driveLetter.TrimEnd('\\', ':');
        var volume = $"{letter}:";

        progress?.Report($"Optimizing {volume}...");
        DiagnosticLogger.Info("DiskOptimizer", $"Starting optimization of {volume}");

        try
        {
            // defrag /O optimizes: defrag for HDD, retrim for SSD
            var result = await RunDefragAsync($"{volume} /O /U", progress, ct);
            if (result.Success)
            {
                DiagnosticLogger.Info("DiskOptimizer", $"Optimization of {volume} completed successfully");
            }
            else
            {
                DiagnosticLogger.Info("DiskOptimizer", $"Optimization of {volume} failed: {result.Output}");
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            DiagnosticLogger.Info("DiskOptimizer", $"Optimization of {volume} was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("DiskOptimizer", $"Optimization of {volume} threw exception", ex);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Analyzes fragmentation level of a drive using defrag /A.
    /// Returns the fragmentation percentage (0 for SSDs).
    /// </summary>
    public static async Task<(int FragmentPercent, string Report)> AnalyzeDriveAsync(
        string driveLetter,
        CancellationToken ct = default)
    {
        var letter = driveLetter.TrimEnd('\\', ':');
        var volume = $"{letter}:";

        try
        {
            var result = await RunDefragAsync($"{volume} /A /U", null, ct);
            var fragmentPercent = ParseFragmentationPercent(result.Output);
            return (fragmentPercent, result.Output);
        }
        catch
        {
            return (0, "Unable to analyze drive.");
        }
    }

    private static int ParseFragmentationPercent(string output)
    {
        // Look for patterns like "Total fragmented = 5%" or "xx% fragmented"
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("fragmented", StringComparison.OrdinalIgnoreCase))
            {
                // Try to extract a percentage number
                var parts = trimmed.Split(new[] { ' ', '%', '=' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    if (int.TryParse(part, out var pct) && pct is >= 0 and <= 100)
                        return pct;
                }
            }
        }
        return 0;
    }

    private static async Task<(bool Success, string Output)> RunDefragAsync(
        string arguments,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "defrag.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var output = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                output.AppendLine(e.Data);
                progress?.Report(e.Data.Trim());
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                output.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        return (process.ExitCode == 0, output.ToString());
    }

    /// <summary>
    /// Uses WMI to detect whether each physical disk is SSD or HDD.
    /// Maps drive letters to media types.
    /// </summary>
    private static Dictionary<string, DriveMediaType> DetectMediaTypes()
    {
        var result = new Dictionary<string, DriveMediaType>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Map physical disks to their media type
            var diskMediaTypes = new Dictionary<int, DriveMediaType>();

            using (var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, MediaType FROM Win32_DiskDrive"))
            {
                foreach (var disk in searcher.Get())
                {
                    var deviceId = disk["DeviceID"]?.ToString() ?? "";
                    // Extract disk number from \\.\PHYSICALDRIVE0
                    if (int.TryParse(deviceId.Replace("\\\\.\\PHYSICALDRIVE", ""), out var diskNum))
                    {
                        var mediaType = Convert.ToInt32(disk["MediaType"] ?? 0);
                        // MediaType: 3=HDD, 4=SSD (Win32_DiskDrive may not reliably report this)
                        // Fall back to MSFT_PhysicalDisk if needed
                    }
                }
            }

            // Use MSFT_PhysicalDisk for more reliable SSD detection
            using (var searcher = new ManagementObjectSearcher(
                @"\\.\root\Microsoft\Windows\Storage",
                "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk"))
            {
                foreach (var disk in searcher.Get())
                {
                    if (int.TryParse(disk["DeviceId"]?.ToString(), out var diskNum))
                    {
                        var mediaType = Convert.ToInt32(disk["MediaType"] ?? 0);
                        // 3 = HDD, 4 = SSD, 5 = SCM
                        diskMediaTypes[diskNum] = mediaType switch
                        {
                            4 or 5 => DriveMediaType.SSD,
                            3 => DriveMediaType.HDD,
                            _ => DriveMediaType.Unknown
                        };
                    }
                }
            }

            // Map partitions to drive letters
            using (var searcher = new ManagementObjectSearcher(
                "ASSOCIATORS OF {Win32_DiskDrive.DeviceID='\\\\.\\PHYSICALDRIVE0'} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
            {
                // Use a different approach: enumerate Win32_DiskDrive then follow associations
            }

            // Simpler approach: query partition-to-letter mapping for each disk
            foreach (var (diskNum, mediaType) in diskMediaTypes)
            {
                try
                {
                    var query = $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\\\\\.\\\\PHYSICALDRIVE{diskNum}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition";
                    using var partSearcher = new ManagementObjectSearcher(query);
                    foreach (var partition in partSearcher.Get())
                    {
                        var partId = partition["DeviceID"]?.ToString() ?? "";
                        var logQuery = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partId}'}} WHERE AssocClass=Win32_LogicalDiskToPartition";
                        using var logSearcher = new ManagementObjectSearcher(logQuery);
                        foreach (var logical in logSearcher.Get())
                        {
                            var letter = logical["DeviceID"]?.ToString()?.TrimEnd(':');
                            if (!string.IsNullOrEmpty(letter))
                                result[letter] = mediaType;
                        }
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("DiskOptimizer", "Failed to detect media types via WMI", ex);
        }

        return result;
    }
}
