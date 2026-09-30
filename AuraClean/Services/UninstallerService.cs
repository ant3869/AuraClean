using AuraClean.Helpers;
using AuraClean.Models;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace AuraClean.Services;

/// <summary>
/// Enumerates installed programs from the registry and triggers uninstalls.
/// Implements the "Surgical Uninstaller" with post-execution heuristic scanning.
/// </summary>
public static class UninstallerService
{
    private const int MsiUserCancelled = 1602;
    private const int MsiSuccessRebootInitiated = 1641;
    private const int MsiSuccessRebootRequired = 3010;
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Registry paths containing the Uninstall entries for installed programs.
    /// </summary>
    private static readonly (RegistryHive Hive, RegistryView View, string SubKey)[] UninstallPaths =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.LocalMachine, RegistryView.Registry32,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (RegistryHive.CurrentUser, RegistryView.Default,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
    ];

    /// <summary>
    /// Enumerates all installed programs from the Windows registry.
    /// Filters out system components and sub-components.
    /// </summary>
    public static async Task<List<InstalledProgram>> GetInstalledProgramsAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var programs = new List<InstalledProgram>();

            foreach (var (hive, view, subKey) in UninstallPaths)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Scanning {hive}\\{subKey}...");

                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstallKey = baseKey.OpenSubKey(subKey);
                    if (uninstallKey == null) continue;

                    foreach (var keyName in uninstallKey.GetSubKeyNames())
                    {
                        ct.ThrowIfCancellationRequested();

                        try
                        {
                            using var appKey = uninstallKey.OpenSubKey(keyName);
                            if (appKey == null) continue;

                            // Skip system components
                            if (ReadInt64(appKey, "SystemComponent") == 1) continue;

                            // Skip sub-components
                            var parentKey = appKey.GetValue("ParentKeyName");
                            if (parentKey != null) continue;

                            var displayName = appKey.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(displayName)) continue;

                            var program = new InstalledProgram
                            {
                                DisplayName = displayName.Trim(),
                                DisplayVersion = ReadString(appKey, "DisplayVersion"),
                                Publisher = ReadString(appKey, "Publisher"),
                                InstallLocation = ReadString(appKey, "InstallLocation").Trim().Trim('"'),
                                UninstallString = ReadString(appKey, "UninstallString"),
                                QuietUninstallString = ReadString(appKey, "QuietUninstallString"),
                                DisplayIcon = ReadString(appKey, "DisplayIcon"),
                                InstallDate = ReadString(appKey, "InstallDate"),
                                EstimatedSizeKB = ReadInt64(appKey, "EstimatedSize"),
                                IsWindowsInstaller = ReadInt64(appKey, "WindowsInstaller") == 1,
                                RegistryKeyPath = $"{subKey}\\{keyName}",
                                RegistryHive = hive,
                                RegistryView = view
                            };

                            programs.Add(program);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // One malformed entry must never hide every other installed program.
                            DiagnosticLogger.Warn("UninstallerService", $"Skipped unreadable uninstall entry '{keyName}'", ex);
                        }
                    }
                }
                catch (System.Security.SecurityException ex) { DiagnosticLogger.Warn("UninstallerService", $"Registry access denied scanning uninstall hive: {subKey}", ex); }
                catch (UnauthorizedAccessException ex) { DiagnosticLogger.Warn("UninstallerService", $"Registry access denied scanning uninstall hive: {subKey}", ex); }
                catch (IOException ex)
                {
                    DiagnosticLogger.Warn("UninstallerService", $"Could not read {subKey}", ex);
                }
            }

            // Deduplicate by DisplayName (same app can appear in multiple registry views)
            return programs
                .GroupBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, ct);
    }

    /// <summary>
    /// Triggers the standard Windows uninstaller for the given program.
    /// Returns true if the uninstaller process exited successfully.
    /// </summary>
    public static async Task<(bool Success, string Message)> RunUninstallAsync(
        InstalledProgram program, IProgress<string>? progress = null)
    {
        var uninstallCmd = !string.IsNullOrWhiteSpace(program.QuietUninstallString)
            ? program.QuietUninstallString
            : program.UninstallString;

        if (string.IsNullOrWhiteSpace(uninstallCmd))
            return (false, "No uninstall command found for this program.");

        progress?.Report($"Running uninstaller for {program.DisplayName}...");

        try
        {
            // Parse the uninstall command into executable + arguments
            var (fileName, arguments) = ParseUninstallString(uninstallCmd, program.IsWindowsInstaller);

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(psi);
            if (process == null)
                return (false, "Failed to start the uninstaller process.");

            await process.WaitForExitAsync();

            return process.ExitCode switch
            {
                0 => (true, $"{program.DisplayName} uninstalled successfully."),
                MsiSuccessRebootInitiated or MsiSuccessRebootRequired =>
                    (true, $"{program.DisplayName} uninstalled. Restart Windows to finish removal."),
                MsiUserCancelled => (false, $"Uninstall of {program.DisplayName} was cancelled."),
                _ => (false, $"Uninstaller exited with code {process.ExitCode}. " +
                             "The program may have been partially removed.")
            };
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return (false, "The elevation prompt was declined.");
        }
        catch (Exception ex)
        {
            return (false, $"Error running uninstaller: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a post-uninstall heuristic scan for leftover registry keys and files.
    /// </summary>
    public static async Task<List<JunkItem>> PostUninstallScanAsync(
        InstalledProgram program,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<JunkItem>();

        // 1. Scan registry for orphaned keys
        progress?.Report("Scanning registry for orphaned keys...");
        var regResults = await RegistryScannerService.ScanForOrphanedKeysAsync(
            program.DisplayName, program.Publisher, progress, ct);
        results.AddRange(regResults);

        // 2. Scan file system for remnant directories
        progress?.Report("Scanning file system for remnant directories...");
        var fsResults = await ScanForRemnantFilesAsync(program, progress, ct);
        results.AddRange(fsResults);

        // 3. Value-level registry traces (Run/RunOnce, Uninstall, Services, Tasks, App Paths, MUI)
        progress?.Report("Scanning registry values for program traces...");
        var traceResults = await RegistryScannerService.ScanForProgramTracesAsync(
            program.DisplayName, program.Publisher, program.InstallLocation, progress, ct);
        results.AddRange(traceResults);

        return results
            .DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Scans AppData, ProgramData, and Common Files for directories
    /// matching the uninstalled program's name or publisher.
    /// </summary>
    private static async Task<List<JunkItem>> ScanForRemnantFilesAsync(
        InstalledProgram program,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var results = new List<JunkItem>();
            var searchTerms = BuildRemnantDirectorySearchTerms(
                program.DisplayName,
                program.Publisher);

            if (searchTerms.Count == 0)
                return results;

            // Directories to scan
            var scanPaths = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86)
            };

            foreach (var basePath in scanPaths.Where(p => !string.IsNullOrEmpty(p)))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Scanning {basePath}...");

                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(basePath, "*", PathSafety.TopLevelNoReparse))
                    {
                        ct.ThrowIfCancellationRequested();
                        var dirName = Path.GetFileName(dir);

                        if (IsSafeRemnantDirectoryMatch(dirName, searchTerms) &&
                            PathSafety.IsSafeToDeleteDirectory(dir, out _))
                        {
                            TryAddRemnantDirectory(dir, results);
                        }

                        // Vendor\product nesting (e.g. Roaming\Vendor\Product): one level deeper.
                        if (IsAppDataRoot(basePath))
                            ScanOneLevelDeeper(dir, searchTerms, results, ct);
                    }
                }
                catch (UnauthorizedAccessException ex) { DiagnosticLogger.Warn("UninstallerService", $"Access denied scanning leftover directories under: {basePath}", ex); }
                catch (DirectoryNotFoundException ex) { DiagnosticLogger.Warn("UninstallerService", $"Leftover scan path vanished: {basePath}", ex); }
            }

            // The install location itself, when it survives the uninstall, is the
            // highest-signal leftover of all.
            TryAddInstallLocationRemnant(program, results);

            // Startup folder entries whose names match the product terms.
            ScanStartupFolder(program, searchTerms, results);

            return results;
        }, ct);
    }

    private static bool IsAppDataRoot(string basePath) =>
        basePath.Equals(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            StringComparison.OrdinalIgnoreCase) ||
        basePath.Equals(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            StringComparison.OrdinalIgnoreCase) ||
        basePath.Equals(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            StringComparison.OrdinalIgnoreCase);

    private static void TryAddRemnantDirectory(string dir, List<JunkItem> results)
    {
        try
        {
            var dirName = Path.GetFileName(dir);
            long size = GetDirectorySize(dir);
            results.Add(new JunkItem
            {
                Path = dir,
                Description = $"Remnant directory: {dirName}",
                Type = JunkType.RemnantDirectory,
                SizeBytes = size,
                LastModified = Directory.GetLastWriteTime(dir),
                IsSelected = false,
                LockingProcess = "Review before deleting"
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("UninstallerService", $"Skipped unreadable remnant directory: {dir}", ex);
        }
    }

    private static void ScanOneLevelDeeper(
        string parentDir, HashSet<string> searchTerms, List<JunkItem> results, CancellationToken ct)
    {
        try
        {
            foreach (var child in Directory.EnumerateDirectories(parentDir, "*", PathSafety.TopLevelNoReparse))
            {
                ct.ThrowIfCancellationRequested();
                var childName = Path.GetFileName(child);

                if (IsSafeRemnantDirectoryMatch(childName, searchTerms) &&
                    PathSafety.IsSafeToDeleteDirectory(child, out _))
                {
                    TryAddRemnantDirectory(child, results);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("UninstallerService", $"Skipped unreadable nested scan dir: {parentDir}", ex);
        }
    }

    private static void TryAddInstallLocationRemnant(
        InstalledProgram program, List<JunkItem> results)
    {
        var installDir = PathSafety.Normalize(program.InstallLocation);
        if (installDir == null || !Directory.Exists(installDir))
            return;

        // A surviving install folder is always reported — the uninstaller left it behind.
        if (results.Any(r => r.Path.Equals(installDir, StringComparison.OrdinalIgnoreCase)))
            return;

        if (!PathSafety.IsSafeToDeleteDirectory(installDir, out _) ||
            PathSafety.IsSystemCriticalLocation(installDir))
            return;

        try
        {
            results.Add(new JunkItem
            {
                Path = installDir,
                Description = $"Surviving install folder: {program.DisplayName}",
                Type = JunkType.RemnantDirectory,
                SizeBytes = GetDirectorySize(installDir),
                LastModified = Directory.GetLastWriteTime(installDir),
                IsSelected = false,
                LockingProcess = "Review before deleting"
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("UninstallerService", $"Skipped unreadable install location: {installDir}", ex);
        }
    }

    private static void ScanStartupFolder(
        InstalledProgram program, HashSet<string> searchTerms, List<JunkItem> results)
    {
        if (searchTerms.Count == 0)
            return;

        var startupPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        };

        foreach (var startupPath in startupPaths.Where(p => !string.IsNullOrEmpty(p)))
        {
            try
            {
                if (!Directory.Exists(startupPath))
                    continue;

                foreach (var file in Directory.EnumerateFiles(startupPath, "*", PathSafety.TopLevelNoReparse))
                {
                    var nameNoExt = Path.GetFileNameWithoutExtension(file) ?? string.Empty;
                    if (MatchesAnySearchTerm(nameNoExt, searchTerms) &&
                        PathSafety.IsSafeToDeleteFile(file, out _))
                    {
                        FileInfo info;
                        try { info = new FileInfo(file); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            DiagnosticLogger.Warn("UninstallerService", $"Skipped unreadable startup entry: {file}", ex);
                            continue;
                        }

                        results.Add(new JunkItem
                        {
                            Path = file,
                            Description = $"Startup entry: {Path.GetFileName(file)}",
                            Type = JunkType.AbandonedFile,
                            SizeBytes = info.Length,
                            LastModified = info.LastWriteTime,
                            IsSelected = false,
                            LockingProcess = "Review before deleting"
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLogger.Warn("UninstallerService", $"Skipped unreadable startup folder: {startupPath}", ex);
            }
        }

        // Also match .lnk targets pointing back at the install location.
        var installDir = PathSafety.Normalize(program.InstallLocation);
        if (installDir != null)
        {
            foreach (var startupPath in startupPaths.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(startupPath, "*.lnk", PathSafety.TopLevelNoReparse))
                    {
                        if (results.Any(r => r.Path.Equals(file, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var target = ResolveShortcutTarget(file);
                        if (target != null && PathSafety.IsSameOrUnder(target, installDir) &&
                            PathSafety.IsSafeToDeleteFile(file, out _))
                        {
                            FileInfo info;
                            try { info = new FileInfo(file); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                DiagnosticLogger.Warn("UninstallerService", $"Skipped unreadable startup entry: {file}", ex);
                                continue;
                            }

                            results.Add(new JunkItem
                            {
                                Path = file,
                                Description = $"Startup shortcut to removed app: {Path.GetFileName(file)}",
                                Type = JunkType.AbandonedFile,
                                SizeBytes = info.Length,
                                LastModified = info.LastWriteTime,
                                IsSelected = false,
                                LockingProcess = "Review before deleting"
                            });
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLogger.Warn("UninstallerService", $"Skipped unreadable startup folder: {startupPath}", ex);
                }
            }
        }
    }

    /// <summary>Reads a .lnk target path via the Shell COM object (late-bound, no new references).</summary>
    private static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return null;

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                try
                {
                    string? target = shortcut.TargetPath as string;
                    return string.IsNullOrWhiteSpace(target) ? null : target;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                }
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            DiagnosticLogger.Warn("UninstallerService", $"Could not resolve shortcut target: {lnkPath}", ex);
            return null;
        }
    }

    /// <summary>
    /// True when any search term covers the name verbatim (normalized) or when any
    /// ≥4-letter segment of the terms appears as a substring of the normalized name
    /// (mirrors <see cref="RegistryScannerService.IsTraceValueMatch"/>).
    /// </summary>
    internal static bool MatchesAnySearchTerm(string name, HashSet<string> searchTerms)
    {
        var normalized = NormalizeSearchTerm(name);
        if (string.IsNullOrEmpty(normalized))
            return false;

        if (searchTerms.Contains(normalized))
            return true;

        foreach (var term in searchTerms)
        {
            foreach (var segment in SplitSearchWords(term))
            {
                var normalizedSegment = NormalizeSearchTerm(segment);
                if (normalizedSegment.Length >= 4 &&
                    normalizedSegment.Any(char.IsLetter) &&
                    normalized.Contains(normalizedSegment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    internal static HashSet<string> BuildRemnantDirectorySearchTerms(
        string displayName,
        string publisher)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var publisherTerms = SplitSearchWords(publisher)
            .Select(NormalizeSearchTerm)
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fullName = NormalizeSearchTerm(displayName);
        if (IsSpecificSearchTerm(fullName, minimumLength: 4))
            terms.Add(fullName);

        foreach (var word in SplitSearchWords(displayName))
        {
            var term = NormalizeSearchTerm(word);
            if (IsSpecificSearchTerm(term, minimumLength: 5) && !publisherTerms.Contains(term))
                terms.Add(term);
        }

        return terms;
    }

    internal static bool IsSafeRemnantDirectoryMatch(
        string directoryName,
        HashSet<string> searchTerms)
    {
        var normalized = NormalizeSearchTerm(directoryName);
        return searchTerms.Contains(normalized);
    }

    private static bool IsSpecificSearchTerm(string term, int minimumLength) =>
        term.Length >= minimumLength &&
        term.Any(char.IsLetter) &&
        !RemnantNoiseTerms.Contains(term);

    private static IEnumerable<string> SplitSearchWords(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return Regex.Split(value, @"[^A-Za-z0-9]+")
            .Where(w => !string.IsNullOrWhiteSpace(w));
    }

    private static string NormalizeSearchTerm(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>
    /// Trace-matcher split: normalized letter/digit segments (lowercase, no separators).
    /// Shared with <see cref="RegistryScannerService"/> for value-level trace matching.
    /// </summary>
    internal static IEnumerable<string> SplitForTrace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return SplitSearchWords(value)
            .Select(NormalizeSearchTerm)
            .Where(t => t.Length > 0);
    }

    /// <summary>
    /// Trace-matcher normalization: the input stripped to lowercase letters/digits.
    /// Shared with <see cref="RegistryScannerService"/> for value-level trace matching.
    /// </summary>
    internal static string NormalizeForTrace(string value) =>
        NormalizeSearchTerm(value ?? string.Empty);

    private static readonly HashSet<string> RemnantNoiseTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "microsoft", "update", "version", "corporation",
        "software", "technologies", "technology", "company", "installer",
        "setup", "helper", "service", "application", "program", "inc",
        "llc", "ltd"
    };

    /// <summary>
    /// Parses a Windows UninstallString into a filename and arguments.
    /// Handles quoted paths, MsiExec, and direct exe paths.
    /// </summary>
    private static (string FileName, string Arguments) ParseUninstallString(
        string uninstallString, bool isWindowsInstaller)
    {
        uninstallString = uninstallString.Trim();

        // MSI-based installs: use msiexec /x {GUID}
        if (isWindowsInstaller || uninstallString.Contains("MsiExec", StringComparison.OrdinalIgnoreCase))
        {
            // Extract the product GUID
            var guidMatch = System.Text.RegularExpressions.Regex.Match(
                uninstallString, @"\{[0-9A-Fa-f\-]+\}");
            if (guidMatch.Success)
            {
                return ("msiexec.exe", $"/x {guidMatch.Value}");
            }
        }

        // Quoted path: "C:\path\to\app.exe" /args
        if (uninstallString.StartsWith('"'))
        {
            int closeQuote = uninstallString.IndexOf('"', 1);
            if (closeQuote > 0)
            {
                string file = uninstallString[1..closeQuote];
                string args = closeQuote + 1 < uninstallString.Length
                    ? uninstallString[(closeQuote + 1)..].TrimStart()
                    : "";
                return (file, args);
            }
        }

        // Unquoted: split on first space after .exe
        int exeIdx = uninstallString.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0)
        {
            int splitAt = exeIdx + 4;
            return (uninstallString[..splitAt], uninstallString[splitAt..].TrimStart());
        }

        return (uninstallString, "");
    }

    /// <summary>
    /// Calculates the total size of a directory recursively.
    /// </summary>
    internal static long GetDirectorySize(string path)
    {
        long size = 0;
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", PathSafety.RecursiveNoReparse))
            {
                try { size += file.Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLogger.Warn("UninstallerService", $"Skipped unreadable file during size calc: {file.FullName}", ex);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("UninstallerService", $"Could not measure {path}", ex);
        }
        return size;
    }

    private static string ReadString(RegistryKey key, string name) =>
        key.GetValue(name) switch
        {
            string s => s,
            string[] multi => string.Join(" ", multi),
            null => string.Empty,
            var other => other.ToString() ?? string.Empty
        };

    private static long ReadInt64(RegistryKey key, string name) =>
        key.GetValue(name) switch
        {
            int i => i,
            long l => l,
            string s when long.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            byte[] { Length: >= 4 } bytes => BitConverter.ToInt32(bytes, 0),
            _ => 0
        };
}
