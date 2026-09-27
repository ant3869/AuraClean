using AuraClean.Helpers;
using AuraClean.Models;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace AuraClean.Services;

/// <summary>
/// System hygiene engine that scans and cleans temp files, Windows Update cache,
/// Prefetch, crash dumps, BranchCache, thumbnail cache, and other system junk.
/// </summary>
public static class FileCleanerService
{
    private static readonly HashSet<string> ProtectedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff",
        ".webp", ".heic", ".heif", ".avif", ".jxl",
        ".raw", ".dng", ".cr2", ".cr3", ".nef", ".nrw", ".arw",
        ".srf", ".sr2", ".orf", ".rw2", ".raf", ".pef", ".srw",
        ".x3f", ".psd", ".xcf", ".svg"
    };

    /// <summary>
    /// Temp files younger than this are left alone: running installers and apps extract
    /// working files to %TEMP% and break if they disappear mid-use.
    /// </summary>
    internal static readonly TimeSpan TempFileMinimumAge = TimeSpan.FromHours(24);

    /// <summary>Recent logs are kept so in-progress diagnostics are not destroyed.</summary>
    internal static readonly TimeSpan LogFileMinimumAge = TimeSpan.FromDays(7);

    private const int MaxFilesPerAggregatedDirectory = 20_000;

    private static string WindowsDir => PathSafety.WindowsDirectory;

    private static string ProgramDataDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>
    /// Minimum age a file must reach before it may be cleaned, per junk type.
    /// </summary>
    internal static TimeSpan? GetMinimumAge(JunkType type) => type switch
    {
        JunkType.TempFile => TempFileMinimumAge,
        JunkType.LogFile => LogFileMinimumAge,
        _ => null
    };

    /// <summary>
    /// Whether an aggregated directory item may itself be removed once emptied.
    /// Log folders under %SystemRoot% are expected to exist by the services that write to them.
    /// </summary>
    private static bool ShouldRemoveEmptiedDirectory(JunkType type) => type is not JunkType.LogFile;

    /// <summary>
    /// Analyzes the system for junk files and returns categorized results.
    /// </summary>
    public static async Task<List<JunkItem>> AnalyzeSystemJunkAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new System.Collections.Concurrent.ConcurrentBag<JunkItem>();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userCrashDumps = Path.Combine(localAppData, "CrashDumps");
        var thumbDir = Path.Combine(localAppData, @"Microsoft\Windows\Explorer");
        var userWer = Path.Combine(localAppData, @"Microsoft\Windows\WER");
        var tempPath = Path.GetTempPath();

        var scanJobs = new List<(string Path, JunkType Type, string Desc, string? Pattern)>
        {
            (Path.Combine(WindowsDir, "Temp"), JunkType.TempFile, "Windows Temp", null),
            (tempPath, JunkType.TempFile, "User Temp", null),
            (Path.Combine(WindowsDir, "Prefetch"), JunkType.Prefetch, "Prefetch File", "*.pf"),
            (userCrashDumps, JunkType.CrashDump, "User Crash Dump", null),
            (Path.Combine(WindowsDir, "Minidump"), JunkType.CrashDump, "System Minidump", null),
            (Path.Combine(WindowsDir, @"SoftwareDistribution\Download"), JunkType.WindowsUpdateCache, "Windows Update Cache", null),
            (Path.Combine(WindowsDir, "BranchCache"), JunkType.BranchCache, "BranchCache", null),
            (thumbDir, JunkType.ThumbnailCache, "Thumbnail Cache", "thumbcache_*.db"),
            (Path.Combine(WindowsDir, @"SoftwareDistribution\DeliveryOptimization"), JunkType.DeliveryOptimization, "Delivery Optimization", null),
            (Path.Combine(ProgramDataDir, @"Microsoft\Windows\WER\ReportArchive"), JunkType.WindowsErrorReporting, "WER Archive", null),
            (Path.Combine(ProgramDataDir, @"Microsoft\Windows\WER\ReportQueue"), JunkType.WindowsErrorReporting, "WER Queue", null),
            (userWer, JunkType.WindowsErrorReporting, "User WER", null),
            (Path.Combine(WindowsDir, @"ServiceProfiles\LocalService\AppData\Local\FontCache"), JunkType.FontCache, "Font Cache", null),
            (Path.Combine(WindowsDir, "Logs"), JunkType.LogFile, "Windows Log", null),
            (Path.Combine(WindowsDir, "Panther"), JunkType.LogFile, "Setup Log", null),
        };

        // Only the current user's Recycle Bin: emptying other accounts' bins would destroy
        // files those users may still want to restore.
        var userSid = GetCurrentUserSid();
        if (userSid != null)
        {
            foreach (var drive in GetFixedDrives())
            {
                var recyclePath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", userSid);
                scanJobs.Add((recyclePath, JunkType.RecycleBin, $"Recycle Bin ({drive.Name})", null));
            }
        }

        progress?.Report("Scanning system for junk files...");
        await Parallel.ForEachAsync(
            scanJobs.DistinctBy(j => j.Path, StringComparer.OrdinalIgnoreCase),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            (job, token) =>
            {
                var localResults = new List<JunkItem>();
                ScanDirectory(job.Path, job.Type, job.Desc, localResults, token, job.Pattern);
                foreach (var item in localResults)
                    results.Add(item);
                return ValueTask.CompletedTask;
            });

        progress?.Report("Checking for Windows.old...");
        var windowsOld = Path.Combine(Path.GetPathRoot(WindowsDir) ?? @"C:\", "Windows.old");
        if (Directory.Exists(windowsOld))
        {
            var (oldSize, _) = await Task.Run(() => MeasureDirectory(windowsOld, null, MaxFilesPerAggregatedDirectory * 5, ct), ct);
            if (oldSize > 0)
            {
                results.Add(new JunkItem
                {
                    Path = windowsOld,
                    Description = "Windows.old (previous installation)",
                    Type = JunkType.WindowsOld,
                    SizeBytes = oldSize,
                    LastModified = SafeGetLastWriteTime(windowsOld),
                    IsSelected = false
                });
            }
        }

        progress?.Report("Analyzing Component Store (WinSxS)...");
        try
        {
            var winsxsPath = Path.Combine(WindowsDir, "WinSxS");
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
                        LastModified = SafeGetLastWriteTime(winsxsPath),
                        IsSelected = false
                    });
                }
            }
        }
        catch (OperationCanceledException) { throw; }
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
        CancellationToken ct = default,
        bool dryRun = false)
    {
        int deleted = 0, skipped = 0;
        long bytesFreed = 0;
        var errors = new List<string>();
        var stoppedServices = new List<string>();
        var itemList = items.Where(i => i.IsSelected).ToList();

        foreach (var item in itemList)
        {
            item.IsLocked = false;
            item.LockingProcess = string.Empty;
        }

        if (dryRun)
        {
            var protectedItems = itemList.Where(i => IsProtectedUserMediaFile(i.Path)).ToList();
            foreach (var item in protectedItems)
            {
                item.IsLocked = true;
                item.LockingProcess = "Protected photo or screenshot file";
            }

            var cleanableItems = itemList.Except(protectedItems).ToList();
            return (cleanableItems.Count, protectedItems.Count, cleanableItems.Sum(i => i.SizeBytes), errors);
        }

        try
        {
            var servicesToStop = new Dictionary<JunkType, string[]>
            {
                [JunkType.WindowsUpdateCache] = ["wuauserv", "bits"],
                [JunkType.DeliveryOptimization] = ["DoSvc"],
                [JunkType.FontCache] = ["FontCache"],
            };

            foreach (var (junkType, serviceNames) in servicesToStop)
            {
                if (!itemList.Any(i => i.Type == junkType))
                    continue;

                foreach (var svc in serviceNames)
                {
                    if (stoppedServices.Contains(svc, StringComparer.OrdinalIgnoreCase))
                        continue;

                    progress?.Report($"Stopping {svc} service...");
                    if (await TryStopServiceAsync(svc))
                        stoppedServices.Add(svc);
                }
            }

            var winsxsItems = itemList.Where(i => i.Type == JunkType.WinSxS).ToList();
            var fileItems = itemList.Where(i => i.Type != JunkType.WinSxS).ToList();

            foreach (var winsxsItem in winsxsItems)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report("Running DISM Component Store cleanup (this can take several minutes)...");
                if (await CleanWinSxSAsync(ct))
                {
                    deleted++;
                    bytesFreed += winsxsItem.SizeBytes;
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
                        CleanSingleItem(item, ref deleted, ref skipped, ref bytesFreed, ct);
                    }
                    catch (OperationCanceledException) { throw; }
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
                        item.LockingProcess = DescribeLockers(item.Path);
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
                        item.IsLocked = true;
                        item.LockingProcess = ex.Message;
                        errors.Add($"{item.Path} — {ex.Message}");
                        DiagnosticLogger.Warn("FileCleanerService", $"Failed to clean: {item.Path}", ex);
                    }
                }
            }, ct);
        }
        finally
        {
            // Restart services in reverse order so dependencies come back correctly.
            for (int i = stoppedServices.Count - 1; i >= 0; i--)
            {
                var svc = stoppedServices[i];
                if (!await TryStartServiceAsync(svc))
                    errors.Add($"Failed to restart {svc} service.");
            }
        }

        WriteCleanupAudit(itemList, deleted, skipped, bytesFreed);

        return (deleted, skipped, bytesFreed, errors);
    }

    private static void CleanSingleItem(JunkItem item, ref int deleted, ref int skipped, ref long bytesFreed, CancellationToken ct)
    {
        if (File.Exists(item.Path))
        {
            if (IsProtectedUserMediaFile(item.Path))
            {
                skipped++;
                item.IsLocked = true;
                item.LockingProcess = "Protected photo or screenshot file";
                return;
            }

            var info = new FileInfo(item.Path);
            var minAge = GetMinimumAge(item.Type);
            if (minAge.HasValue && DateTime.UtcNow - info.LastWriteTimeUtc < minAge.Value)
            {
                skipped++;
                item.IsLocked = true;
                item.LockingProcess = "Recently modified — kept for safety";
                return;
            }

            var size = info.Length;
            if (info.IsReadOnly)
                info.IsReadOnly = false;
            File.Delete(item.Path);
            bytesFreed += size;
            deleted++;
            return;
        }

        if (Directory.Exists(item.Path))
        {
            if (PathSafety.IsProtectedRoot(item.Path))
            {
                skipped++;
                item.IsLocked = true;
                item.LockingProcess = "Protected folder — contents only";
                DiagnosticLogger.Warn("FileCleanerService", $"Refused to clean protected folder {item.Path}");
                return;
            }

            var olderThanUtc = GetMinimumAge(item.Type) is { } age ? DateTime.UtcNow - age : (DateTime?)null;
            var (dirDeleted, dirSkipped, dirBytes, protectedMediaSkipped) =
                CleanDirectoryBestEffort(item.Path, olderThanUtc, ct);
            deleted += dirDeleted;
            skipped += dirSkipped;
            bytesFreed += dirBytes;

            if (dirSkipped > 0)
            {
                item.IsLocked = true;
                item.LockingProcess = protectedMediaSkipped > 0
                    ? "Contains protected photo or screenshot files"
                    : "Contains locked, recent, or in-use files";
                return;
            }

            if (ShouldRemoveEmptiedDirectory(item.Type))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(item.Path).Any())
                        Directory.Delete(item.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    item.IsLocked = true;
                    item.LockingProcess = "Directory could not be removed";
                }
            }
            return;
        }

        // Path no longer exists (already cleaned or moved) — nothing left to do.
        deleted++;
    }

    private static string DescribeLockers(string path)
    {
        try
        {
            var lockers = FileLockDetector.GetLockingProcesses(path);
            return lockers.Count > 0 ? string.Join(", ", lockers) : "System";
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("FileCleanerService", $"Lock detection failed for {path}", ex);
            return "Unknown process";
        }
    }

    /// <summary>
    /// Writes a cleanup audit log to %LocalAppData%\AuraClean\Logs for accountability.
    /// </summary>
    private static void WriteCleanupAudit(List<JunkItem> cleanedItems, int deleted, int skipped, long bytesFreed)
    {
        try
        {
            Directory.CreateDirectory(DiagnosticLogger.LogDirectory);
            var logPath = Path.Combine(DiagnosticLogger.LogDirectory, $"Cleanup_Audit_{DateTime.Now:yyyy-MM-dd_HHmmss}.log");

            using var writer = new StreamWriter(logPath);
            writer.WriteLine($"AuraClean Cleanup Audit — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine($"Deleted: {deleted} | Skipped: {skipped} | Freed: {bytesFreed:N0} bytes");
            writer.WriteLine(new string('─', 60));
            foreach (var item in cleanedItems)
            {
                var status = item.IsLocked ? $"SKIPPED ({item.LockingProcess})" : "DELETED";
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
    /// skipping locked, protected, and too-recent ones. Never follows junctions or symlinks,
    /// so a link planted inside a temp folder cannot redirect deletion elsewhere.
    /// </summary>
    internal static (int Deleted, int Skipped, long BytesFreed, int ProtectedMediaSkipped)
        CleanDirectoryBestEffort(string dirPath, DateTime? olderThanUtc = null, CancellationToken ct = default)
    {
        int deleted = 0, skippedCount = 0, protectedMediaSkipped = 0;
        long bytesFreed = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dirPath, "*", PathSafety.RecursiveNoReparse))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (IsProtectedUserMediaFile(file))
                    {
                        skippedCount++;
                        protectedMediaSkipped++;
                        continue;
                    }

                    var info = new FileInfo(file);
                    if (olderThanUtc.HasValue && info.LastWriteTimeUtc > olderThanUtc.Value)
                    {
                        skippedCount++;
                        continue;
                    }

                    var size = info.Length;
                    if (info.IsReadOnly)
                        info.IsReadOnly = false;
                    File.Delete(file);
                    bytesFreed += size;
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skippedCount++;
                }
            }

            RemoveEmptySubdirectories(dirPath, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            skippedCount++;
            DiagnosticLogger.Warn("FileCleanerService", $"Failed to enumerate files in {dirPath}", ex);
        }

        return (deleted, skippedCount, bytesFreed, protectedMediaSkipped);
    }

    private static void RemoveEmptySubdirectories(string dirPath, CancellationToken ct)
    {
        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(dirPath, "*", PathSafety.RecursiveNoReparse)
                .OrderByDescending(d => d.Length)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("FileCleanerService", $"Failed to enumerate subdirectories of {dirPath}", ex);
            return;
        }

        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Expected: locked or in-use directory.
            }
        }
    }

    internal static bool ContainsProtectedUserMedia(string dirPath, int maxFiles = 1000)
    {
        try
        {
            return Directory.EnumerateFiles(dirPath, "*", PathSafety.RecursiveNoReparse)
                .Take(maxFiles)
                .Any(IsProtectedUserMediaFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unknown contents: treat as protected so nothing is presented for deletion.
            return true;
        }
    }

    internal static bool IsProtectedUserMediaFile(string path)
    {
        try
        {
            var extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) && ProtectedImageExtensions.Contains(extension);
        }
        catch (ArgumentException)
        {
            return false;
        }
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

        var minAge = GetMinimumAge(type);
        DateTime? olderThanUtc = minAge.HasValue ? DateTime.UtcNow - minAge.Value : null;

        try
        {
            var topLevelFiles = searchPattern != null
                ? Directory.EnumerateFiles(path, searchPattern, PathSafety.TopLevelNoReparse)
                : Directory.EnumerateFiles(path, "*", PathSafety.TopLevelNoReparse);

            foreach (var file in topLevelFiles)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(file);
                    if (olderThanUtc.HasValue && fi.LastWriteTimeUtc > olderThanUtc.Value)
                        continue;

                    results.Add(new JunkItem
                    {
                        Path = file,
                        Description = $"{label}: {fi.Name}",
                        Type = type,
                        SizeBytes = fi.Length,
                        LastModified = fi.LastWriteTime
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // File vanished or is inaccessible — skip it.
                }
            }

            // Pattern-based scans (Prefetch *.pf, thumbcache_*.db) only look at the top level.
            if (searchPattern != null)
                return;

            foreach (var dir in Directory.EnumerateDirectories(path, "*", PathSafety.TopLevelNoReparse))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var (size, fileCount) = MeasureDirectory(dir, olderThanUtc, MaxFilesPerAggregatedDirectory, ct);
                    var dirName = Path.GetFileName(dir);

                    if (fileCount > 0)
                    {
                        results.Add(new JunkItem
                        {
                            Path = dir,
                            Description = $"{label}: {dirName} ({fileCount:N0} files)",
                            Type = type,
                            SizeBytes = size,
                            LastModified = SafeGetLastWriteTime(dir)
                        });
                    }
                    else if (ShouldRemoveEmptiedDirectory(type) && IsEmptyTree(dir))
                    {
                        results.Add(new JunkItem
                        {
                            Path = dir,
                            Description = $"{label}: Empty folder {dirName}",
                            Type = type,
                            SizeBytes = 0,
                            LastModified = SafeGetLastWriteTime(dir)
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn("FileCleanerService", $"Failed to scan subdirectory under {path}", ex);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            DiagnosticLogger.Warn("FileCleanerService", $"Could not scan {path}", ex);
        }
    }

    /// <summary>
    /// Sums eligible files (never following reparse points). Returns (bytes, fileCount).
    /// </summary>
    private static (long Size, int FileCount) MeasureDirectory(string dir, DateTime? olderThanUtc, int maxFiles, CancellationToken ct)
    {
        long size = 0;
        int count = 0;
        try
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", PathSafety.RecursiveNoReparse))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (olderThanUtc.HasValue && file.LastWriteTimeUtc > olderThanUtc.Value)
                        continue;

                    size += file.Length;
                    if (++count >= maxFiles)
                        break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip unreadable file metadata.
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("FileCleanerService", $"Failed to measure {dir}", ex);
        }

        return (size, count);
    }

    private static bool IsEmptyTree(string dir)
    {
        try
        {
            return !Directory.EnumerateFiles(dir, "*", PathSafety.RecursiveNoReparse).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DateTime SafeGetLastWriteTime(string path)
    {
        try { return Directory.GetLastWriteTime(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static IEnumerable<DriveInfo> GetFixedDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool usable;
            try { usable = drive.IsReady && drive.DriveType == DriveType.Fixed; }
            catch (IOException) { usable = false; }

            if (usable)
                yield return drive;
        }
    }

    private static string? GetCurrentUserSid()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("FileCleanerService", "Could not resolve current user SID", ex);
            return null;
        }
    }

    // ══════════════════════════════════════════
    //  SERVICES
    // ══════════════════════════════════════════

    /// <summary>
    /// Attempts to stop a running Windows service. Returns true when AuraClean issued the stop,
    /// even if the wait timed out — the caller must then restart it afterwards.
    /// Returns false when the service was not running (so it must not be started later).
    /// </summary>
    private static Task<bool> TryStopServiceAsync(string serviceName) => Task.Run(() =>
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            if (sc.Status != ServiceControllerStatus.Running || !sc.CanStop)
                return false;

            sc.Stop();
            try
            {
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            catch (System.ServiceProcess.TimeoutException ex)
            {
                DiagnosticLogger.Warn("FileCleanerService", $"Service '{serviceName}' did not stop within 30s", ex);
            }
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("FileCleanerService", $"Could not stop service '{serviceName}'", ex);
            return false;
        }
    });

    /// <summary>
    /// Starts a service that AuraClean previously stopped. Returns true if it is running.
    /// </summary>
    private static Task<bool> TryStartServiceAsync(string serviceName) => Task.Run(() =>
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            sc.Refresh();

            if (sc.Status == ServiceControllerStatus.Running)
                return true;

            if (sc.Status == ServiceControllerStatus.StopPending)
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));

            if (sc.Status == ServiceControllerStatus.Stopped)
                sc.Start();

            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("FileCleanerService", $"Could not start service '{serviceName}'", ex);
            return false;
        }
    });

    // ══════════════════════════════════════════
    //  WINSXS (DISM)
    // ══════════════════════════════════════════

    /// <summary>
    /// Runs DISM /AnalyzeComponentStore and returns the reclaimable size
    /// ("Backups and Disabled Features" + "Cache and Temporary Data").
    /// </summary>
    private static async Task<long> GetWinSxSReclaimableSizeAsync(CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                ProcessRunner.SystemTool("Dism.exe"),
                "/Online /Cleanup-Image /AnalyzeComponentStore /English",
                ct, timeout: TimeSpan.FromMinutes(15));

            if (!result.Succeeded)
            {
                DiagnosticLogger.Warn("FileCleanerService", $"DISM analyze exited with {result.ExitCode}: {result.CombinedMessage}");
                return 0;
            }

            return ParseDismReclaimable(result.StandardOutput);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("FileCleanerService", "DISM AnalyzeComponentStore failed", ex);
            return 0;
        }
    }

    /// <summary>
    /// Extracts the reclaimable byte count from DISM /AnalyzeComponentStore output.
    /// </summary>
    internal static long ParseDismReclaimable(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return 0;

        long reclaimable = 0;
        bool found = false;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Backups and Disabled Features", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Cache and Temporary Data", StringComparison.OrdinalIgnoreCase))
            {
                reclaimable += ParseDismSize(trimmed);
                found = true;
            }
        }

        bool recommended = Regex.IsMatch(output, @"Cleanup Recommended\s*:\s*Yes", RegexOptions.IgnoreCase);
        if (!recommended)
            return 0;

        // DISM recommends cleanup but the size lines were missing or unparsable.
        return found && reclaimable > 0 ? reclaimable : 500L * 1024 * 1024;
    }

    /// <summary>
    /// Parses a DISM size line like "Cache and Temporary Data : 1.23 GB" into bytes.
    /// Supports bytes/KB/MB/GB/TB and either decimal separator.
    /// </summary>
    internal static long ParseDismSize(string line)
    {
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0) return 0;

        var match = Regex.Match(line[(colonIdx + 1)..],
            @"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>bytes|byte|KB|MB|GB|TB)\b",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return 0;

        var numberText = match.Groups["value"].Value.Replace(',', '.');
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return 0;

        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "TB" => 1_099_511_627_776d,
            "GB" => 1_073_741_824d,
            "MB" => 1_048_576d,
            "KB" => 1024d,
            _ => 1d
        };

        return (long)(value * multiplier);
    }

    /// <summary>
    /// Runs DISM /StartComponentCleanup. The servicing stack must not be interrupted, so
    /// cancellation stops waiting but lets DISM finish in the background.
    /// </summary>
    private static async Task<bool> CleanWinSxSAsync(CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                ProcessRunner.SystemTool("Dism.exe"),
                "/Online /Cleanup-Image /StartComponentCleanup /English",
                ct, killOnCancel: false);

            if (result.Succeeded)
            {
                DiagnosticLogger.Info("FileCleanerService", "WinSxS component cleanup completed.");
                return true;
            }

            DiagnosticLogger.Warn("FileCleanerService",
                $"DISM cleanup exited with code {result.ExitCode}: {result.CombinedMessage}");
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("FileCleanerService", "DISM StartComponentCleanup failed", ex);
            return false;
        }
    }
}
