using AuraClean.Helpers;
using AuraClean.Models;
using System.IO;
using System.ServiceProcess;

namespace AuraClean.Services;

/// <summary>
/// System hygiene engine that scans and cleans temp files, Windows Update cache,
/// Prefetch, crash dumps, BranchCache, thumbnail cache, and other system junk.
/// </summary>
public static class FileCleanerService
{
    /// <summary>
    /// Analyzes the system for junk files and returns categorized results.
    /// </summary>
    public static async Task<List<JunkItem>> AnalyzeSystemJunkAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<JunkItem>();

        await Task.Run(() =>
        {
            // 1. Windows Temp
            progress?.Report("Scanning Windows Temp folder...");
            ScanDirectory(@"C:\Windows\Temp", JunkType.TempFile, "Windows Temp", results, ct);

            // 2. User Temp
            progress?.Report("Scanning User Temp folder...");
            ScanDirectory(Path.GetTempPath(), JunkType.TempFile, "User Temp", results, ct);

            // 3. Prefetch
            progress?.Report("Scanning Prefetch data...");
            ScanDirectory(@"C:\Windows\Prefetch", JunkType.Prefetch, "Prefetch File", results, ct, "*.pf");

            // 4. Crash Dumps — User
            progress?.Report("Scanning crash dumps...");
            var userCrashDumps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CrashDumps");
            ScanDirectory(userCrashDumps, JunkType.CrashDump, "User Crash Dump", results, ct);

            // 5. Crash Dumps — System
            ScanDirectory(@"C:\Windows\Minidump", JunkType.CrashDump, "System Minidump", results, ct);

            // 6. Windows Update Cache (SoftwareDistribution\Download)
            progress?.Report("Scanning Windows Update cache...");
            ScanDirectory(@"C:\Windows\SoftwareDistribution\Download",
                JunkType.WindowsUpdateCache, "Windows Update Cache", results, ct);

            // 7. BranchCache
            progress?.Report("Scanning BranchCache...");
            ScanDirectory(@"C:\Windows\BranchCache",
                JunkType.BranchCache, "BranchCache", results, ct);

            // 8. Thumbnail cache
            progress?.Report("Scanning thumbnail cache...");
            var thumbDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Windows\Explorer");
            ScanDirectory(thumbDir, JunkType.ThumbnailCache, "Thumbnail Cache", results, ct,
                "thumbcache_*.db");

            // 9. Recycle Bin (per-drive hidden $Recycle.Bin)
            progress?.Report("Scanning Recycle Bin...");
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                var recyclePath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                ScanDirectory(recyclePath, JunkType.RecycleBin, $"Recycle Bin ({drive.Name})", results, ct);
            }

            // 10. Delivery Optimization cache
            progress?.Report("Scanning Delivery Optimization cache...");
            ScanDirectory(@"C:\Windows\SoftwareDistribution\DeliveryOptimization",
                JunkType.DeliveryOptimization, "Delivery Optimization", results, ct);

            // 11. Windows Error Reporting
            progress?.Report("Scanning Windows Error Reports...");
            ScanDirectory(@"C:\ProgramData\Microsoft\Windows\WER\ReportArchive",
                JunkType.WindowsErrorReporting, "WER Archive", results, ct);
            ScanDirectory(@"C:\ProgramData\Microsoft\Windows\WER\ReportQueue",
                JunkType.WindowsErrorReporting, "WER Queue", results, ct);
            var userWer = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Windows\WER");
            ScanDirectory(userWer, JunkType.WindowsErrorReporting, "User WER", results, ct);

            // 12. Font Cache
            progress?.Report("Scanning Font Cache...");
            ScanDirectory(@"C:\Windows\ServiceProfiles\LocalService\AppData\Local\FontCache",
                JunkType.FontCache, "Font Cache", results, ct);

            // 13. Windows Log files
            progress?.Report("Scanning Windows logs...");
            ScanDirectory(@"C:\Windows\Logs", JunkType.LogFile, "Windows Log", results, ct);
            ScanDirectory(@"C:\Windows\Panther", JunkType.LogFile, "Setup Log", results, ct);

            // 14. Windows.old (if present)
            progress?.Report("Checking for Windows.old...");
            if (Directory.Exists(@"C:\Windows.old"))
            {
                long oldSize = 0;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(@"C:\Windows.old", "*", SearchOption.AllDirectories).Take(5000))
                    {
                        try { oldSize += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { }
                if (oldSize > 0)
                {
                    results.Add(new JunkItem
                    {
                        Path = @"C:\Windows.old",
                        Description = "Windows.old (previous installation)",
                        Type = JunkType.WindowsOld,
                        SizeBytes = oldSize,
                        LastModified = Directory.GetLastWriteTime(@"C:\Windows.old"),
                        IsSelected = false // Don't auto-select — user should decide
                    });
                }
            }

        }, ct);

        return results;
    }

    /// <summary>
    /// Deletes the selected junk items. Handles locked files gracefully.
    /// Stops/starts services as needed (e.g., Windows Update, Font Cache).
    /// Uses attempt-based deletion instead of pre-flight lock checks.
    /// </summary>
    public static async Task<(int Deleted, int Skipped, long BytesFreed, List<string> Errors)> CleanItemsAsync(
        IEnumerable<JunkItem> items,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        int deleted = 0, skipped = 0;
        long bytesFreed = 0;
        var errors = new List<string>();
        var stoppedServices = new List<string>();
        var itemList = items.Where(i => i.IsSelected).ToList();

        try
        {
            // Stop services that lock files we want to clean
            var servicesToStop = new Dictionary<JunkType, string[]>
            {
                [JunkType.WindowsUpdateCache] = ["wuauserv"],
                [JunkType.FontCache] = ["FontCache"],
                [JunkType.ThumbnailCache] = ["WSearch"],
            };

            foreach (var (junkType, serviceNames) in servicesToStop)
            {
                if (itemList.Any(i => i.Type == junkType))
                {
                    foreach (var svc in serviceNames)
                    {
                        progress?.Report($"Stopping {svc} service...");
                        if (await TryStopServiceAsync(svc))
                            stoppedServices.Add(svc);
                    }
                }
            }

            // Run all file I/O on a background thread
            await Task.Run(() =>
            {
                int processed = 0;
                foreach (var item in itemList)
                {
                    ct.ThrowIfCancellationRequested();
                    processed++;
                    if (processed % 10 == 1 || processed == itemList.Count)
                        progress?.Report($"Cleaning ({processed}/{itemList.Count}): {Path.GetFileName(item.Path)}");

                    try
                    {
                        if (File.Exists(item.Path))
                        {
                            // Attempt-based: just try to delete, handle failure gracefully
                            var size = new FileInfo(item.Path).Length;
                            File.Delete(item.Path);
                            bytesFreed += size;
                            deleted++;
                        }
                        else if (Directory.Exists(item.Path))
                        {
                            // For directories: delete files individually, skip locked ones,
                            // then try to remove the (hopefully empty) directory tree.
                            var (dirDeleted, dirSkipped, dirBytes) = CleanDirectoryBestEffort(item.Path);
                            deleted += dirDeleted;
                            skipped += dirSkipped;
                            bytesFreed += dirBytes;

                            if (dirSkipped == 0)
                            {
                                // All files deleted — remove directory structure
                                try { Directory.Delete(item.Path, recursive: true); }
                                catch { /* non-empty remnant — leave it */ }
                            }
                        }
                        else
                        {
                            // Path no longer exists (already cleaned or moved)
                            deleted++;
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        skipped++;
                        item.IsLocked = true;
                        item.LockingProcess = "Access Denied";
                    }
                    catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020) /* ERROR_SHARING_VIOLATION */)
                    {
                        skipped++;
                        item.IsLocked = true;
                        // Try to identify what's locking it
                        try
                        {
                            var lockers = FileLockDetector.GetLockingProcesses(item.Path);
                            item.LockingProcess = lockers.Count > 0
                                ? string.Join(", ", lockers)
                                : "System";
                        }
                        catch { item.LockingProcess = "Unknown process"; }
                    }
                    catch (IOException)
                    {
                        skipped++;
                        item.IsLocked = true;
                        item.LockingProcess = "In use";
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        errors.Add($"{item.Path} — {ex.Message}");
                        DiagnosticLogger.Warn("FileCleanerService", $"Failed to clean: {item.Path}", ex);
                    }
                }
            }, ct);
        }
        finally
        {
            // Restart all stopped services
            foreach (var svc in stoppedServices)
            {
                try { await TryStartServiceAsync(svc); }
                catch { errors.Add($"Failed to restart {svc} service."); }
            }
        }

        return (deleted, skipped, bytesFreed, errors);
    }

    /// <summary>
    /// Best-effort directory cleaning: deletes as many individual files as possible,
    /// skipping locked ones. Returns counts of deleted, skipped, and bytes freed.
    /// </summary>
    private static (int Deleted, int Skipped, long BytesFreed) CleanDirectoryBestEffort(string dirPath)
    {
        int deleted = 0, skippedCount = 0;
        long bytesFreed = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    bytesFreed += size;
                    deleted++;
                }
                catch
                {
                    skippedCount++;
                }
            }

            // Clean up empty subdirectories bottom-up
            try
            {
                foreach (var dir in Directory.GetDirectories(dirPath, "*", SearchOption.AllDirectories)
                             .OrderByDescending(d => d.Length))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(dir).Any())
                            Directory.Delete(dir);
                    }
                    catch { }
                }
            }
            catch { }
        }
        catch { }

        return (deleted, skippedCount, bytesFreed);
    }

    /// <summary>
    /// Scans a directory for junk files and adds them to the results list.
    /// For non-pattern scans, aggregates subdirectories into single items
    /// to keep UI item counts manageable (prevents UI freezing on large temp dirs).
    /// </summary>
    private static void ScanDirectory(string path, JunkType type, string label,
        List<JunkItem> results, CancellationToken ct, string? searchPattern = null)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            if (searchPattern != null)
            {
                // Pattern-based: scan top-level only (Prefetch *.pf, Thumbnail thumbcache_*.db)
                foreach (var file in Directory.EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var fi = new FileInfo(file);
                        results.Add(new JunkItem
                        {
                            Path = file,
                            Description = $"{label}: {fi.Name}",
                            Type = type,
                            SizeBytes = fi.Length,
                            LastModified = fi.LastWriteTime
                        });
                    }
                    catch { }
                }
                return;
            }

            // No pattern: scan root-level files individually,
            // then aggregate each subdirectory into ONE JunkItem.
            foreach (var file in Directory.EnumerateFiles(path))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(file);
                    results.Add(new JunkItem
                    {
                        Path = file,
                        Description = $"{label}: {fi.Name}",
                        Type = type,
                        SizeBytes = fi.Length,
                        LastModified = fi.LastWriteTime
                    });
                }
                catch { }
            }

            // Subdirectories — one JunkItem per directory (with summed size)
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    int fileCount = 0;
                    long size = 0;
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(2000))
                    {
                        try { size += new FileInfo(file).Length; fileCount++; }
                        catch { }
                    }

                    var dirName = Path.GetFileName(dir);
                    if (fileCount > 0)
                    {
                        results.Add(new JunkItem
                        {
                            Path = dir,
                            Description = $"{label}: {dirName} ({fileCount:N0} files)",
                            Type = type,
                            SizeBytes = size,
                            LastModified = Directory.GetLastWriteTime(dir)
                        });
                    }
                    else if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        // Empty directory — still cleanable
                        results.Add(new JunkItem
                        {
                            Path = dir,
                            Description = $"{label}: Empty folder {dirName}",
                            Type = type,
                            SizeBytes = 0,
                            LastModified = Directory.GetLastWriteTime(dir)
                        });
                    }
                }
                catch { }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>
    /// Attempts to stop a Windows service. Returns true if stopped or already stopped.
    /// </summary>
    private static async Task<bool> TryStopServiceAsync(string serviceName)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                if (sc.Status == ServiceControllerStatus.Stopped)
                    return true;
                if (sc.Status == ServiceControllerStatus.Running)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    return sc.Status == ServiceControllerStatus.Stopped;
                }
                return false;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("FileCleanerService", $"Could not stop service '{serviceName}'", ex);
                return false;
            }
        });
    }

    /// <summary>
    /// Attempts to start a Windows service. Returns true if started or already running.
    /// </summary>
    private static async Task<bool> TryStartServiceAsync(string serviceName)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                if (sc.Status == ServiceControllerStatus.Running)
                    return true;
                if (sc.Status == ServiceControllerStatus.Stopped)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    return sc.Status == ServiceControllerStatus.Running;
                }
                return false;
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("FileCleanerService", $"Could not start service '{serviceName}'", ex);
                return false;
            }
        });
    }
}
