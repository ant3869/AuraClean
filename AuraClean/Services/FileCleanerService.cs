using AuraClean.Helpers;
using AuraClean.Models;
using System.Diagnostics;
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
        var results = new System.Collections.Concurrent.ConcurrentBag<JunkItem>();

        // Resolve user-specific paths on the calling thread
        var userCrashDumps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CrashDumps");
        var thumbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Explorer");
        var userWer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\WER");
        var tempPath = Path.GetTempPath();

        // Build scan descriptors — all independent, safe to parallelize
        var scanJobs = new List<(string Path, JunkType Type, string Desc, string? Pattern)>
        {
            (@"C:\Windows\Temp", JunkType.TempFile, "Windows Temp", null),
            (tempPath, JunkType.TempFile, "User Temp", null),
            (@"C:\Windows\Prefetch", JunkType.Prefetch, "Prefetch File", "*.pf"),
            (userCrashDumps, JunkType.CrashDump, "User Crash Dump", null),
            (@"C:\Windows\Minidump", JunkType.CrashDump, "System Minidump", null),
            (@"C:\Windows\SoftwareDistribution\Download", JunkType.WindowsUpdateCache, "Windows Update Cache", null),
            (@"C:\Windows\BranchCache", JunkType.BranchCache, "BranchCache", null),
            (thumbDir, JunkType.ThumbnailCache, "Thumbnail Cache", "thumbcache_*.db"),
            (@"C:\Windows\SoftwareDistribution\DeliveryOptimization", JunkType.DeliveryOptimization, "Delivery Optimization", null),
            (@"C:\ProgramData\Microsoft\Windows\WER\ReportArchive", JunkType.WindowsErrorReporting, "WER Archive", null),
            (@"C:\ProgramData\Microsoft\Windows\WER\ReportQueue", JunkType.WindowsErrorReporting, "WER Queue", null),
            (userWer, JunkType.WindowsErrorReporting, "User WER", null),
            (@"C:\Windows\ServiceProfiles\LocalService\AppData\Local\FontCache", JunkType.FontCache, "Font Cache", null),
            (@"C:\Windows\Logs", JunkType.LogFile, "Windows Log", null),
            (@"C:\Windows\Panther", JunkType.LogFile, "Setup Log", null),
        };

        // Add Recycle Bin per fixed drive
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var recyclePath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
            scanJobs.Add((recyclePath, JunkType.RecycleBin, $"Recycle Bin ({drive.Name})", null));
        }

        // Run all scans in parallel with bounded concurrency to avoid disk thrashing
        progress?.Report("Scanning system for junk files...");
        await Parallel.ForEachAsync(
            scanJobs,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            (job, token) =>
            {
                var localResults = new List<JunkItem>();
                ScanDirectory(job.Path, job.Type, job.Desc, localResults, token, job.Pattern);
                foreach (var item in localResults)
                    results.Add(item);
                return ValueTask.CompletedTask;
            });

        // 14. Windows.old (if present)
        progress?.Report("Checking for Windows.old...");
        if (Directory.Exists(@"C:\Windows.old"))
            {
                long oldSize = 0;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(@"C:\Windows.old", "*", SearchOption.AllDirectories).Take(5000))
                    {
                        try { oldSize += new FileInfo(f).Length; } catch { /* Per-file size read failure — expected for locked files */ }
                    }
                }
                catch (Exception ex) { DiagnosticLogger.Warn("FileCleanerService", "Failed to enumerate Windows.old", ex); }
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

        // 15. WinSxS Component Store (safe cleanup via DISM)
        // Runs outside Task.Run because it needs async process execution
        progress?.Report("Analyzing Component Store (WinSxS)...");
        try
        {
            var winsxsPath = @"C:\Windows\WinSxS";
            if (Directory.Exists(winsxsPath))
            {
                long winsxsReclaimable = await GetWinSxSReclaimableSizeAsync(ct);
                if (winsxsReclaimable > 0)
                {
                    results.Add(new JunkItem
                    {
                        Path = winsxsPath,
                        Description = "Component Store — reclaimable via DISM cleanup",
                        Type = JunkType.WinSxS,
                        SizeBytes = winsxsReclaimable,
                        LastModified = Directory.GetLastWriteTime(winsxsPath),
                        IsSelected = false
                    });
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("FileCleanerService", "Failed to analyze WinSxS", ex);
        }

        return results.ToList();
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
            // Handle WinSxS items separately (needs DISM, not file deletion)
            var winsxsItems = itemList.Where(i => i.Type == JunkType.WinSxS).ToList();
            var fileItems = itemList.Where(i => i.Type != JunkType.WinSxS).ToList();

            // Clean WinSxS via DISM
            foreach (var winsxsItem in winsxsItems)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report("Running DISM Component Store cleanup...");
                var (success, freedBytes) = await CleanWinSxSAsync(ct);
                if (success)
                {
                    deleted++;
                    bytesFreed += freedBytes > 0 ? freedBytes : winsxsItem.SizeBytes;
                }
                else
                {
                    skipped++;
                    winsxsItem.IsLocked = true;
                    winsxsItem.LockingProcess = "DISM cleanup failed";
                }
            }

            await Task.Run(() =>
            {
                int processed = 0;
                foreach (var item in fileItems)
                {
                    ct.ThrowIfCancellationRequested();
                    processed++;
                    if (processed % 10 == 1 || processed == fileItems.Count)
                        progress?.Report($"Cleaning ({processed}/{fileItems.Count}): {Path.GetFileName(item.Path)}");

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

        // Write audit log for accountability
        WriteCleanupAudit(itemList, deleted, skipped, bytesFreed);

        return (deleted, skipped, bytesFreed, errors);
    }

    /// <summary>
    /// Writes a cleanup audit log to %LocalAppData%\AuraClean\Logs for accountability.
    /// </summary>
    private static void WriteCleanupAudit(List<JunkItem> cleanedItems, int deleted, int skipped, long bytesFreed)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AuraClean", "Logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"Cleanup_Audit_{DateTime.Now:yyyy-MM-dd_HHmmss}.log");

            using var writer = new StreamWriter(logPath);
            writer.WriteLine($"AuraClean Cleanup Audit — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine($"Deleted: {deleted} | Skipped: {skipped} | Freed: {bytesFreed:N0} bytes");
            writer.WriteLine(new string('─', 60));
            foreach (var item in cleanedItems)
            {
                var status = item.IsLocked ? "SKIPPED" : "DELETED";
                writer.WriteLine($"[{status}] {item.Path} ({item.SizeBytes:N0} B) — {item.Type}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("FileCleanerService", "Failed to write cleanup audit log", ex);
        }
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
                    catch { /* Expected: locked or in-use directory */ }
                }
            }
            catch (Exception ex) { DiagnosticLogger.Warn("FileCleanerService", $"Failed to enumerate subdirectories of {dirPath}", ex); }
        }
        catch (Exception ex) { DiagnosticLogger.Warn("FileCleanerService", $"Failed to enumerate files in {dirPath}", ex); }

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
                catch (Exception ex) { DiagnosticLogger.Warn("FileCleanerService", $"Failed to scan subdirectory under {path}", ex); }
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

    /// <summary>
    /// Runs DISM /AnalyzeComponentStore to estimate reclaimable WinSxS space.
    /// Parses the "Reclaimable Packages" size from DISM output.
    /// </summary>
    private static async Task<long> GetWinSxSReclaimableSizeAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "Dism.exe",
                Arguments = "/Online /Cleanup-Image /AnalyzeComponentStore",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return 0;

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            // Parse "Reclaimable Packages : X.XX GB" or "X.XX MB"
            // Also check "Component Store Cleanup Recommended : Yes"
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Reclaimable Packages", StringComparison.OrdinalIgnoreCase))
                {
                    return ParseDismSize(trimmed);
                }
            }

            // Fallback: if "Component Store Cleanup Recommended : Yes", estimate conservatively
            if (output.Contains("Cleanup Recommended : Yes", StringComparison.OrdinalIgnoreCase))
                return 500_000_000; // 500 MB estimate

            return 0;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("FileCleanerService", "DISM AnalyzeComponentStore failed", ex);
            return 0;
        }
    }

    /// <summary>
    /// Parses a DISM size line like "Reclaimable Packages : 1.23 GB" into bytes.
    /// </summary>
    internal static long ParseDismSize(string line)
    {
        // Extract the size portion after the colon
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0) return 0;

        var sizePart = line[(colonIdx + 1)..].Trim();

        if (double.TryParse(
                sizePart.Replace(" GB", "").Replace(" MB", "").Replace(" KB", "").Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            if (sizePart.Contains("GB", StringComparison.OrdinalIgnoreCase))
                return (long)(value * 1_073_741_824);
            if (sizePart.Contains("MB", StringComparison.OrdinalIgnoreCase))
                return (long)(value * 1_048_576);
            if (sizePart.Contains("KB", StringComparison.OrdinalIgnoreCase))
                return (long)(value * 1024);
        }

        return 0;
    }

    /// <summary>
    /// Runs DISM /StartComponentCleanup to safely clean the WinSxS component store.
    /// Returns (success, estimatedBytesFreed).
    /// </summary>
    private static async Task<(bool Success, long FreedBytes)> CleanWinSxSAsync(CancellationToken ct)
    {
        try
        {
            // Measure WinSxS size before cleanup
            long sizeBefore = await GetWinSxSActualSizeAsync();

            var psi = new ProcessStartInfo
            {
                FileName = "Dism.exe",
                Arguments = "/Online /Cleanup-Image /StartComponentCleanup",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return (false, 0);

            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0)
            {
                long sizeAfter = await GetWinSxSActualSizeAsync();
                long freed = sizeBefore > sizeAfter ? sizeBefore - sizeAfter : 0;
                DiagnosticLogger.Info("FileCleanerService",
                    $"WinSxS cleanup succeeded. Freed ~{freed / 1_048_576} MB");
                return (true, freed);
            }

            var stderr = await process.StandardError.ReadToEndAsync(ct);
            DiagnosticLogger.Warn("FileCleanerService",
                $"DISM cleanup exited with code {process.ExitCode}: {stderr}");
            return (false, 0);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("FileCleanerService", "DISM StartComponentCleanup failed", ex);
            return (false, 0);
        }
    }

    /// <summary>
    /// Quick estimation of WinSxS folder size by sampling top-level subdirectories.
    /// </summary>
    private static async Task<long> GetWinSxSActualSizeAsync()
    {
        return await Task.Run(() =>
        {
            long total = 0;
            try
            {
                var winsxs = @"C:\Windows\WinSxS";
                foreach (var file in Directory.EnumerateFiles(winsxs, "*", SearchOption.TopDirectoryOnly))
                {
                    try { total += new FileInfo(file).Length; } catch { }
                }
                // Sample first 200 subdirectories to estimate
                foreach (var dir in Directory.EnumerateDirectories(winsxs).Take(200))
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Take(100))
                    {
                        try { total += new FileInfo(file).Length; } catch { }
                    }
                }
            }
            catch { }
            return total;
        });
    }
}
