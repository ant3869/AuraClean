using AuraClean.Models;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace AuraClean.Services;

/// <summary>
/// Enumerates installed programs from the registry and triggers uninstalls.
/// Implements the "Surgical Uninstaller" with post-execution heuristic scanning.
/// </summary>
public static class UninstallerService
{
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
                            var systemComponent = appKey.GetValue("SystemComponent");
                            if (systemComponent is int sc && sc == 1) continue;

                            // Skip sub-components
                            var parentKey = appKey.GetValue("ParentKeyName");
                            if (parentKey != null) continue;

                            var displayName = appKey.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(displayName)) continue;

                            var program = new InstalledProgram
                            {
                                DisplayName = displayName,
                                DisplayVersion = appKey.GetValue("DisplayVersion") as string ?? "",
                                Publisher = appKey.GetValue("Publisher") as string ?? "",
                                InstallLocation = appKey.GetValue("InstallLocation") as string ?? "",
                                UninstallString = appKey.GetValue("UninstallString") as string ?? "",
                                QuietUninstallString = appKey.GetValue("QuietUninstallString") as string ?? "",
                                DisplayIcon = appKey.GetValue("DisplayIcon") as string ?? "",
                                InstallDate = appKey.GetValue("InstallDate") as string ?? "",
                                EstimatedSizeKB = Convert.ToInt64(appKey.GetValue("EstimatedSize") ?? 0),
                                IsWindowsInstaller = (appKey.GetValue("WindowsInstaller") is int wi && wi == 1),
                                RegistryKeyPath = $"{subKey}\\{keyName}",
                                RegistryView = view
                            };

                            programs.Add(program);
                        }
                        catch (System.Security.SecurityException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
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

            return process.ExitCode == 0
                ? (true, $"{program.DisplayName} uninstalled successfully.")
                : (true, $"Uninstaller exited with code {process.ExitCode}. " +
                          "The program may have been partially removed.");
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

        return results;
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
            var searchTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Build search terms
            if (!string.IsNullOrWhiteSpace(program.DisplayName))
                searchTerms.Add(program.DisplayName);
            if (!string.IsNullOrWhiteSpace(program.Publisher))
                searchTerms.Add(program.Publisher);

            // Extract program name segments (e.g., "Adobe Photoshop" → "Adobe", "Photoshop")
            foreach (var name in new[] { program.DisplayName, program.Publisher })
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                foreach (var word in name.Split([' ', '-', '_'],
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (word.Length >= 4) searchTerms.Add(word);
                }
            }

            // Remove noise
            foreach (var noise in new[] { "Windows", "Microsoft", "Update", "Corporation" })
                searchTerms.Remove(noise);

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
                    foreach (var dir in Directory.EnumerateDirectories(basePath))
                    {
                        ct.ThrowIfCancellationRequested();
                        var dirName = Path.GetFileName(dir);

                        if (searchTerms.Any(term =>
                            dirName.Contains(term, StringComparison.OrdinalIgnoreCase)))
                        {
                            long size = GetDirectorySize(dir);
                            results.Add(new JunkItem
                            {
                                Path = dir,
                                Description = $"Remnant directory: {dirName}",
                                Type = JunkType.RemnantDirectory,
                                SizeBytes = size,
                                LastModified = Directory.GetLastWriteTime(dir)
                            });
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }
            }

            return results;
        }, ct);
    }

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
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; }
                catch { }
            }
        }
        catch { }
        return size;
    }
}
