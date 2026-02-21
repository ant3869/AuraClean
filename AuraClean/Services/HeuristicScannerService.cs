using AuraClean.Models;
using Microsoft.Win32;
using System.IO;

namespace AuraClean.Services;

/// <summary>
/// Identifies "Abandoned Files" — directories in common app data locations
/// that have no associated registry entry AND haven't been modified in >180 days.
/// </summary>
public static class HeuristicScannerService
{
    private const int ABANDONED_DAYS_THRESHOLD = 180;

    /// <summary>
    /// Scans common application directories for abandoned files/folders.
    /// A directory is considered "abandoned" if:
    ///   1. No installed program (from the Uninstall registry) maps to it by name.
    ///   2. No file in the directory has been modified in the last 180 days.
    /// </summary>
    public static async Task<List<JunkItem>> ScanForAbandonedFilesAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<JunkItem>();

        // Get all known program names from the registry for cross-referencing
        var knownNames = await GetKnownProgramNamesAsync(ct);

        var scanPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        var cutoffDate = DateTime.Now.AddDays(-ABANDONED_DAYS_THRESHOLD);

        await Parallel.ForEachAsync(
            scanPaths.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)),
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            (basePath, token) =>
            {
                progress?.Report($"Heuristic scan: {Path.GetFileName(basePath)}...");

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(basePath))
                    {
                        token.ThrowIfCancellationRequested();
                        var dirName = Path.GetFileName(dir);

                        // Skip well-known system directories
                        if (IsProtectedDirectory(dirName)) continue;

                        // Check 1: Does any known program name match this directory?
                        bool hasRegistryMatch = knownNames.Any(name =>
                            dirName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                            name.Contains(dirName, StringComparison.OrdinalIgnoreCase));

                        if (hasRegistryMatch) continue;

                        // Check 2: Is the most recent file modification older than the threshold?
                        if (!IsDirectoryStale(dir, cutoffDate)) continue;

                        // Both conditions met — this is an abandoned directory
                        // Use a quick size estimate (cap at 1000 files to avoid long scans)
                        long size = GetDirectorySizeCapped(dir, 1000);

                        lock (results)
                        {
                            results.Add(new JunkItem
                            {
                                Path = dir,
                                Description = $"Abandoned: No registry match, inactive >{ABANDONED_DAYS_THRESHOLD} days",
                                Type = JunkType.AbandonedFile,
                                SizeBytes = size,
                                LastModified = GetMostRecentWriteTime(dir)
                            });
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }

                return ValueTask.CompletedTask;
            });

        return results;
    }

    /// <summary>
    /// Gets all known program DisplayNames from the Uninstall registry keys.
    /// </summary>
    private static async Task<HashSet<string>> GetKnownProgramNamesAsync(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var paths = new (RegistryHive Hive, RegistryView View, string SubKey)[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (RegistryHive.LocalMachine, RegistryView.Registry32,
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (RegistryHive.CurrentUser, RegistryView.Default,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
            };

            foreach (var (hive, view, subKey) in paths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstallKey = baseKey.OpenSubKey(subKey);
                    if (uninstallKey == null) continue;

                    foreach (var keyName in uninstallKey.GetSubKeyNames())
                    {
                        try
                        {
                            using var appKey = uninstallKey.OpenSubKey(keyName);
                            var displayName = appKey?.GetValue("DisplayName") as string;
                            if (!string.IsNullOrWhiteSpace(displayName))
                            {
                                names.Add(displayName);
                                // Also add individual significant words
                                foreach (var word in displayName.Split([' ', '-', '_'],
                                    StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (word.Length >= 4)
                                        names.Add(word);
                                }
                            }

                            var publisher = appKey?.GetValue("Publisher") as string;
                            if (!string.IsNullOrWhiteSpace(publisher))
                                names.Add(publisher);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Remove common noise terms that would match too many directories
            foreach (var noise in new[] { "Windows", "Microsoft", "Update", "Common",
                "Application", "Data", "Program", "Files", "System", "User", "Local" })
            {
                names.Remove(noise);
            }

            return names;
        }, ct);
    }

    /// <summary>
    /// Gets directory size, but caps file enumeration to avoid scanning huge directories.
    /// </summary>
    private static long GetDirectorySizeCapped(string path, int maxFiles)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(maxFiles))
            {
                try { size += new FileInfo(file).Length; }
                catch { }
            }
        }
        catch { }
        return size;
    }

    /// <summary>
    /// Checks if no file in the directory has been modified since the cutoff date.
    /// </summary>
    private static bool IsDirectoryStale(string path, DateTime cutoff)
    {
        try
        {
            return !Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Take(500) // Limit to avoid very large dirs
                .Any(file =>
                {
                    try { return File.GetLastWriteTime(file) > cutoff; }
                    catch { return false; }
                });
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the most recent LastWriteTime of any file in the directory.
    /// </summary>
    private static DateTime GetMostRecentWriteTime(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Take(200)
                .Select(f =>
                {
                    try { return File.GetLastWriteTime(f); }
                    catch { return DateTime.MinValue; }
                })
                .DefaultIfEmpty(Directory.GetLastWriteTime(path))
                .Max();
        }
        catch
        {
            return Directory.GetLastWriteTime(path);
        }
    }

    /// <summary>
    /// Returns true if the directory name is a well-known system/framework directory
    /// that should never be flagged as abandoned.
    /// </summary>
    private static bool IsProtectedDirectory(string dirName)
    {
        var protected_ = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Windows", "Packages", "ProgramData", "Temp", "tmp",
            ".NET", "dotnet", "NuGet", "npm", "pip", "conda", "Python",
            "Git", "GitHub", "VS Code", "Visual Studio", "JetBrains",
            "Google", "Apple", "Mozilla", "Intel", "AMD", "NVIDIA",
            "VirtualStore", "INetCache", "INetCookies", "History",
            "ConnectedDevicesPlatform", "D3DSCache", "WindowsApps",
            "SquirrelTemp", "CrashDumps", "Explorer", "Comms",
            "Default", "Public", "System32", "SysWOW64"
        };

        return protected_.Contains(dirName) || dirName.StartsWith(".");
    }
}
