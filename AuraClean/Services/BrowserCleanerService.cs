using AuraClean.Helpers;
using AuraClean.Models;
using System.Data.SQLite;
using System.IO;

namespace AuraClean.Services;

/// <summary>
/// Browser &amp; Privacy Deep Clean service.
/// Targeted cleaning for Chromium-based browsers (Chrome, Edge, Brave, Vivaldi, Opera)
/// and Mozilla Firefox.
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
                @"Google\Chrome\User Data"), BrowserEngine.Chromium),
        new("Microsoft Edge",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Edge\User Data"), BrowserEngine.Chromium),
        new("Brave",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"BraveSoftware\Brave-Browser\User Data"), BrowserEngine.Chromium),
        new("Opera",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Opera Software\Opera Stable"), BrowserEngine.Chromium),
        new("Vivaldi",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Vivaldi\User Data"), BrowserEngine.Chromium),
        new("Mozilla Firefox",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Mozilla\Firefox\Profiles"), BrowserEngine.Firefox),
    ];

    public enum BrowserEngine { Chromium, Firefox }

    /// <summary>
    /// Pure caches within a Chromium profile: regenerated automatically, no user data.
    /// </summary>
    private static readonly string[] CleanableSubPaths =
    [
        "Cache",
        "Code Cache",
        "GPUCache",
        "ShaderCache",
        "GrShaderCache",
        "DawnCache",
        "DawnGraphiteCache",
        "DawnWebGPUCache",
        "Service Worker\\CacheStorage",
        "Service Worker\\ScriptCache",
    ];

    /// <summary>
    /// Site data (logins, offline documents, web-app state, extension storage). Removing it signs
    /// the user out of websites, so it is only offered as "tracking data" in Advanced mode.
    /// </summary>
    private static readonly string[] SiteDataSubPaths =
    [
        "IndexedDB",
        "Local Storage\\leveldb",
        "Session Storage",
        "blob_storage",
        "Service Worker\\Database",
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

    public record BrowserProfile(string Name, string UserDataPath, BrowserEngine Engine = BrowserEngine.Chromium);

    /// <summary>
    /// Subdirectories/files within a Firefox profile that should be cleaned.
    /// </summary>
    private static readonly string[] FirefoxCleanableSubPaths =
    [
        "cache2",
        "jumpListCache",
        "thumbnails",
        "startupCache",
        "shader-cache",
        "crashes",
        "minidumps",
        "datareporting",
        "saved-telemetry-pings",
    ];

    /// <summary>Firefox site storage (IndexedDB / localStorage for every site).</summary>
    private static readonly string[] FirefoxSiteDataSubPaths =
    [
        "storage\\default",
    ];

    /// <summary>
    /// SQLite databases in a Firefox profile that can be vacuumed.
    /// </summary>
    private static readonly string[] FirefoxVacuumableDbFiles =
    [
        "places.sqlite",
        "cookies.sqlite",
        "formhistory.sqlite",
        "webappsstore.sqlite",
        "favicons.sqlite",
        "content-prefs.sqlite",
        "permissions.sqlite",
        "storage.sqlite",
    ];

    /// <summary>
    /// Firefox tracking-related files.
    /// </summary>
    private static readonly string[] FirefoxTrackingPatterns =
    [
        "SiteSecurityServiceState.bin",
        "SecurityPreloadState.bin",
        "sessionCheckpoints.json",
        "cookies.sqlite-wal",
        "cookies.sqlite-shm",
    ];

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
            var profiles = GetProfileDirectories(browser);

            var cleanPaths = browser.Engine == BrowserEngine.Firefox ? FirefoxCleanableSubPaths : CleanableSubPaths;
            var siteDataPaths = browser.Engine == BrowserEngine.Firefox ? FirefoxSiteDataSubPaths : SiteDataSubPaths;
            var vacuumFiles = browser.Engine == BrowserEngine.Firefox ? FirefoxVacuumableDbFiles : VacuumableDbFiles;
            var trackPatterns = browser.Engine == BrowserEngine.Firefox ? FirefoxTrackingPatterns : TrackingPatterns;

            foreach (var profileDir in profiles)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Scanning {browser.Name}: {Path.GetFileName(profileDir)}...");

                // 1. Cache directories (Firefox keeps its disk cache under %LocalAppData%)
                var cacheRoots = new List<string> { profileDir };
                if (browser.Engine == BrowserEngine.Firefox)
                {
                    var localProfile = GetFirefoxLocalProfileDirectory(profileDir);
                    if (localProfile != null)
                        cacheRoots.Add(localProfile);
                }

                foreach (var cacheRoot in cacheRoots)
                foreach (var subPath in cleanPaths)
                {
                    var fullPath = Path.Combine(cacheRoot, subPath);
                    if (Directory.Exists(fullPath) && cacheItems.All(c => !c.Path.Equals(fullPath, StringComparison.OrdinalIgnoreCase)))
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
                foreach (var dbName in vacuumFiles)
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
                        catch (Exception ex)
                        {
                            DiagnosticLogger.Warn("BrowserCleaner", $"Skipped unreadable vacuum target: {dbPath}", ex);
                        }
                    }
                }

                // 3. Site data (Advanced "tracking" category — signs you out of websites)
                foreach (var subPath in siteDataPaths)
                {
                    var fullPath = Path.Combine(profileDir, subPath);
                    if (!Directory.Exists(fullPath))
                        continue;

                    long size = GetDirectorySize(fullPath);
                    if (size <= 0)
                        continue;

                    trackingItems.Add(new JunkItem
                    {
                        Path = fullPath,
                        Description = $"{browser.Name} Site Data — {subPath} (signs you out of websites)",
                        Type = JunkType.BrowserTracking,
                        SizeBytes = size,
                        LastModified = Directory.GetLastWriteTime(fullPath),
                        Category = "Browser Tracking Data"
                    });
                    totalSize += size;
                    savings += size;
                }

                // 4. Tracking blobs
                foreach (var pattern in trackPatterns)
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
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Warn("BrowserCleaner", $"Skipped unreadable tracking pattern '{pattern}' in profile '{profileDir}'", ex);
                    }
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
                            bytesFreed += DeleteBrowserFolder(item.Path, out _);
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
                            var connStr = new SQLiteConnectionStringBuilder
                            {
                                DataSource = target.DbPath,
                                Version = 3,
                                Pooling = false,
                                FailIfMissing = true
                            }.ToString();
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
                            bytesFreed += DeleteBrowserFolder(item.Path, out _);
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

    private static List<string> GetProfileDirectories(BrowserProfile browser)
    {
        var profiles = new List<string>();
        var userDataPath = browser.UserDataPath;

        if (browser.Engine == BrowserEngine.Firefox)
        {
            // Firefox stores profiles as random-named subdirectories (e.g. "a1b2c3d4.default-release")
            if (Directory.Exists(userDataPath))
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(userDataPath))
                        profiles.Add(dir);
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn("BrowserCleaner", $"Failed to enumerate Firefox profiles in {userDataPath}", ex);
                }
            }
            return profiles;
        }

        // Chromium: "Default" profile
        var defaultDir = Path.Combine(userDataPath, "Default");
        if (Directory.Exists(defaultDir))
            profiles.Add(defaultDir);

        // Numbered profiles: "Profile 1", "Profile 2", etc.
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(userDataPath, "Profile *"))
                profiles.Add(dir);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("BrowserCleaner", $"Failed to enumerate numbered profiles in {userDataPath}", ex);
        }

        // For Opera, the user data path IS the profile
        if (profiles.Count == 0 && Directory.Exists(userDataPath))
            profiles.Add(userDataPath);

        return profiles;
    }

    /// <summary>
    /// Maps a roaming Firefox profile (…\\Roaming\\Mozilla\\Firefox\\Profiles\\x.default) to its local
    /// cache twin (…\\Local\\Mozilla\\Firefox\\Profiles\\x.default), if it exists.
    /// </summary>
    private static string? GetFirefoxLocalProfileDirectory(string roamingProfileDir)
    {
        var localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Mozilla\Firefox\Profiles");
        var candidate = Path.Combine(localRoot, Path.GetFileName(roamingProfileDir));
        return Directory.Exists(candidate) ? candidate : null;
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
            "mozilla firefox" => new[] { "firefox" },
            _ => Array.Empty<string>()
        };

        return processNames.Any(name =>
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(name);
            var running = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            return running;
        });
    }

    private static long GetDirectorySize(string path)
    {
        long size = 0;
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", PathSafety.RecursiveNoReparse))
            {
                try { size += file.Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLogger.Warn("BrowserCleaner", $"Skipped unreadable file during size calc: {file.FullName}", ex);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("BrowserCleaner", $"Could not measure {path}", ex);
        }
        return size;
    }

    /// <summary>
    /// Deletes a browser data folder's contents without following links, then the folder itself.
    /// Returns bytes freed; locked files are left in place.
    /// </summary>
    private static long DeleteBrowserFolder(string path, out bool fullyRemoved)
    {
        var (_, skipped, bytes, _) = FileCleanerService.CleanDirectoryBestEffort(path);
        fullyRemoved = false;
        if (skipped == 0)
        {
            try
            {
                Directory.Delete(path, recursive: false);
                fullyRemoved = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Directory still in use; contents are gone which is what matters.
                DiagnosticLogger.Warn("BrowserCleaner", $"Folder emptied but not removed: {path}", ex);
            }
        }
        return bytes;
    }



    /// <summary>
    /// Flushes the Windows DNS resolver cache.
    /// </summary>
    public static async Task<(bool Success, string Message)> FlushDnsCacheAsync()
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                ProcessRunner.SystemTool("ipconfig.exe"), "/flushdns", timeout: TimeSpan.FromSeconds(30));

            return result.Succeeded
                ? (true, "DNS cache flushed successfully.")
                : (false, $"ipconfig exited with code {result.ExitCode}.");
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
