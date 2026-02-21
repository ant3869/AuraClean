using AuraClean.Helpers;
using AuraClean.Models;
using System.Data.SQLite;
using System.IO;

namespace AuraClean.Services;

/// <summary>
/// Browser &amp; Privacy Deep Clean service.
/// Targeted cleaning for Chromium-based browsers (Chrome, Edge, Brave).
/// Includes SQLite VACUUM to shrink database sizes and deep-cache purge
/// for hidden tracking blobs.
/// </summary>
public static class BrowserCleanerService
{
    /// <summary>
    /// Known Chromium-based browser profiles with their typical paths.
    /// </summary>
    private static readonly BrowserProfile[] KnownBrowsers =
    [
        new("Google Chrome",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\User Data")),
        new("Microsoft Edge",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Edge\User Data")),
        new("Brave",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"BraveSoftware\Brave-Browser\User Data")),
        new("Opera",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Opera Software\Opera Stable")),
        new("Vivaldi",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Vivaldi\User Data")),
    ];

    /// <summary>
    /// Subdirectories/files within a Chromium profile that should be cleaned.
    /// </summary>
    private static readonly string[] CleanableSubPaths =
    [
        "Cache",
        "Code Cache",
        "GPUCache",
        "Service Worker",
        "ShaderCache",
        "GrShaderCache",
        "DawnCache",
        "Storage\\ext",
        "blob_storage",
        "IndexedDB",
        "Session Storage",
        "Local Storage\\leveldb",
        "Crashpad",
    ];

    /// <summary>
    /// SQLite databases in a Chromium profile that can be vacuumed.
    /// </summary>
    private static readonly string[] VacuumableDbFiles =
    [
        "History",
        "Favicons",
        "Cookies",
        "Web Data",
        "Login Data",
        "Top Sites",
        "Network Action Predictor",
        "Shortcuts",
    ];

    /// <summary>
    /// Tracking-related files/patterns (hidden blobs).
    /// </summary>
    private static readonly string[] TrackingPatterns =
    [
        "Reporting and NEL",
        "Trust Tokens",
        "optimization_guide*",
        "BudgetDatabase",
        "commerce_subscription_db",
        "Segmentation Platform",
        "Site Characteristics Database",
    ];

    public record BrowserProfile(string Name, string UserDataPath);

    public record BrowserScanResult
    {
        public string BrowserName { get; init; } = string.Empty;
        public string ProfilePath { get; init; } = string.Empty;
        public List<JunkItem> CacheItems { get; init; } = [];
        public List<VacuumTarget> VacuumTargets { get; init; } = [];
        public List<JunkItem> TrackingItems { get; init; } = [];
        public long TotalSizeBytes { get; init; }
        public long PotentialSavingsBytes { get; init; }
    }

    public record VacuumTarget
    {
        public string DbPath { get; init; } = string.Empty;
        public string DbName { get; init; } = string.Empty;
        public long SizeBefore { get; init; }
        public long SizeAfter { get; set; }
        public bool Vacuumed { get; set; }
    }

    /// <summary>
    /// Detects installed Chromium-based browsers and returns their profiles.
    /// </summary>
    public static List<BrowserProfile> DetectBrowsers()
    {
        return KnownBrowsers
            .Where(b => Directory.Exists(b.UserDataPath))
            .ToList();
    }

    /// <summary>
    /// Scans a browser profile for cleanable cache, tracking data, and vacuumable databases.
    /// </summary>
    public static async Task<BrowserScanResult> ScanBrowserAsync(
        BrowserProfile browser,
        bool dryRun = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var cacheItems = new List<JunkItem>();
        var vacuumTargets = new List<VacuumTarget>();
        var trackingItems = new List<JunkItem>();
        long totalSize = 0;
        long savings = 0;

        await Task.Run(() =>
        {
            // Find all profile directories (Default, Profile 1, Profile 2, etc.)
            var profiles = GetProfileDirectories(browser.UserDataPath);

            foreach (var profileDir in profiles)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Scanning {browser.Name}: {Path.GetFileName(profileDir)}...");

                // 1. Cache directories
                foreach (var subPath in CleanableSubPaths)
                {
                    var fullPath = Path.Combine(profileDir, subPath);
                    if (Directory.Exists(fullPath))
                    {
                        long size = GetDirectorySize(fullPath);
                        if (size > 0)
                        {
                            cacheItems.Add(new JunkItem
                            {
                                Path = fullPath,
                                Description = $"{browser.Name} — {subPath}",
                                Type = JunkType.BrowserCache,
                                SizeBytes = size,
                                LastModified = Directory.GetLastWriteTime(fullPath),
                                Category = "Browser Cache"
                            });
                            totalSize += size;
                            savings += size;
                        }
                    }
                }

                // 2. SQLite databases for vacuuming
                foreach (var dbName in VacuumableDbFiles)
                {
                    var dbPath = Path.Combine(profileDir, dbName);
                    if (File.Exists(dbPath))
                    {
                        try
                        {
                            var fi = new FileInfo(dbPath);
                            vacuumTargets.Add(new VacuumTarget
                            {
                                DbPath = dbPath,
                                DbName = $"{browser.Name} — {dbName}",
                                SizeBefore = fi.Length
                            });
                            totalSize += fi.Length;
                        }
                        catch { }
                    }
                }

                // 3. Tracking blobs
                foreach (var pattern in TrackingPatterns)
                {
                    try
                    {
                        // Handle wildcard patterns
                        var searchPattern = pattern.Contains('*') ? pattern : pattern;
                        IEnumerable<string> matches;

                        if (pattern.Contains('*'))
                        {
                            matches = Directory.EnumerateFileSystemEntries(profileDir, searchPattern);
                        }
                        else
                        {
                            var path = Path.Combine(profileDir, pattern);
                            matches = (File.Exists(path) || Directory.Exists(path))
                                ? [path] : [];
                        }

                        foreach (var match in matches)
                        {
                            long size = File.Exists(match)
                                ? new FileInfo(match).Length
                                : (Directory.Exists(match) ? GetDirectorySize(match) : 0);

                            if (size > 0)
                            {
                                trackingItems.Add(new JunkItem
                                {
                                    Path = match,
                                    Description = $"{browser.Name} Tracking — {Path.GetFileName(match)}",
                                    Type = JunkType.BrowserTracking,
                                    SizeBytes = size,
                                    LastModified = File.GetLastWriteTime(match),
                                    Category = "Browser Tracking Data"
                                });
                                totalSize += size;
                                savings += size;
                            }
                        }
                    }
                    catch { }
                }
            }
        }, ct);

        return new BrowserScanResult
        {
            BrowserName = browser.Name,
            ProfilePath = browser.UserDataPath,
            CacheItems = cacheItems,
            VacuumTargets = vacuumTargets,
            TrackingItems = trackingItems,
            TotalSizeBytes = totalSize,
            PotentialSavingsBytes = savings
        };
    }

    /// <summary>
    /// Cleans selected browser items (cache, tracking data) and vacuums databases.
    /// </summary>
    public static async Task<BrowserCleanResult> CleanBrowserAsync(
        BrowserScanResult scanResult,
        bool cleanCache = true,
        bool vacuumDatabases = true,
        bool cleanTracking = true,
        bool dryRun = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        int deleted = 0, skipped = 0;
        long bytesFreed = 0;
        long bytesVacuumed = 0;
        var errors = new List<string>();

        // Check if browser is running
        if (!dryRun && IsBrowserRunning(scanResult.BrowserName))
        {
            return new BrowserCleanResult
            {
                Success = false,
                Message = $"Please close {scanResult.BrowserName} before cleaning.",
                Deleted = 0, Skipped = 0, BytesFreed = 0
            };
        }

        await Task.Run(() =>
        {
            // 1. Clean cache directories
            if (cleanCache)
            {
                foreach (var item in scanResult.CacheItems.Where(i => i.IsSelected))
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Cleaning: {item.Description}...");

                    if (dryRun) { deleted++; bytesFreed += item.SizeBytes; continue; }

                    try
                    {
                        if (Directory.Exists(item.Path))
                        {
                            var size = item.SizeBytes;
                            Directory.Delete(item.Path, recursive: true);
                            bytesFreed += size;
                            deleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        errors.Add($"{item.Path}: {ex.Message}");
                    }
                }
            }

            // 2. Vacuum SQLite databases
            if (vacuumDatabases)
            {
                foreach (var target in scanResult.VacuumTargets)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Vacuuming: {target.DbName}...");

                    if (dryRun) { target.Vacuumed = true; continue; }

                    try
                    {
                        if (File.Exists(target.DbPath) && !FileLockDetector.IsLocked(target.DbPath))
                        {
                            var connStr = $"Data Source={target.DbPath};Version=3;";
                            using var conn = new SQLiteConnection(connStr);
                            conn.Open();
                            using var cmd = conn.CreateCommand();
                            cmd.CommandText = "VACUUM;";
                            cmd.ExecuteNonQuery();
                            conn.Close();

                            target.SizeAfter = new FileInfo(target.DbPath).Length;
                            target.Vacuumed = true;
                            bytesVacuumed += target.SizeBefore - target.SizeAfter;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"VACUUM {target.DbName}: {ex.Message}");
                    }
                }
            }

            // 3. Clean tracking data
            if (cleanTracking)
            {
                foreach (var item in scanResult.TrackingItems.Where(i => i.IsSelected))
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report($"Removing tracking: {item.Description}...");

                    if (dryRun) { deleted++; bytesFreed += item.SizeBytes; continue; }

                    try
                    {
                        if (Directory.Exists(item.Path))
                        {
                            var size = item.SizeBytes;
                            Directory.Delete(item.Path, recursive: true);
                            bytesFreed += size;
                            deleted++;
                        }
                        else if (File.Exists(item.Path))
                        {
                            var size = new FileInfo(item.Path).Length;
                            File.Delete(item.Path);
                            bytesFreed += size;
                            deleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        errors.Add($"{item.Path}: {ex.Message}");
                    }
                }
            }
        }, ct);

        return new BrowserCleanResult
        {
            Success = true,
            Message = dryRun
                ? $"[DRY RUN] Would free {FormatHelper.FormatBytes(bytesFreed + bytesVacuumed)} from {scanResult.BrowserName}"
                : $"Cleaned {deleted} items ({FormatHelper.FormatBytes(bytesFreed)} freed), vacuumed {FormatHelper.FormatBytes(bytesVacuumed)}.",
            Deleted = deleted,
            Skipped = skipped,
            BytesFreed = bytesFreed + bytesVacuumed,
            Errors = errors
        };
    }

    #region Private Helpers

    private static List<string> GetProfileDirectories(string userDataPath)
    {
        var profiles = new List<string>();

        // "Default" profile
        var defaultDir = Path.Combine(userDataPath, "Default");
        if (Directory.Exists(defaultDir))
            profiles.Add(defaultDir);

        // Numbered profiles: "Profile 1", "Profile 2", etc.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(userDataPath, "Profile *"))
                profiles.Add(dir);
        }
        catch { }

        // For Opera, the user data path IS the profile
        if (profiles.Count == 0 && Directory.Exists(userDataPath))
            profiles.Add(userDataPath);

        return profiles;
    }

    private static bool IsBrowserRunning(string browserName)
    {
        var processNames = browserName.ToLowerInvariant() switch
        {
            "google chrome" => new[] { "chrome" },
            "microsoft edge" => new[] { "msedge" },
            "brave" => new[] { "brave" },
            "opera" => new[] { "opera" },
            "vivaldi" => new[] { "vivaldi" },
            _ => Array.Empty<string>()
        };

        return processNames.Any(name =>
            System.Diagnostics.Process.GetProcessesByName(name).Length > 0);
    }

    private static long GetDirectorySize(string path)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; }
                catch { }
            }
        }
        catch { }
        return size;
    }



    /// <summary>
    /// Flushes the Windows DNS resolver cache.
    /// </summary>
    public static async Task<(bool Success, string Message)> FlushDnsCacheAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return (false, "Failed to start ipconfig.");

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            return proc.ExitCode == 0
                ? (true, "DNS cache flushed successfully.")
                : (false, $"ipconfig exited with code {proc.ExitCode}.");
        }
        catch (Exception ex)
        {
            return (false, $"DNS flush error: {ex.Message}");
        }
    }

    #endregion

    public record BrowserCleanResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public int Deleted { get; init; }
        public int Skipped { get; init; }
        public long BytesFreed { get; init; }
        public List<string> Errors { get; init; } = [];
    }
}
