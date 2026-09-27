using AuraClean.Helpers;
using AuraClean.Models;
using Microsoft.Win32;
using System.IO;

namespace AuraClean.Services;

/// <summary>
/// Scans the Windows registry for keys left behind after uninstalls.
/// Only keys whose <em>name</em> matches the uninstalled product are reported: a key that
/// merely contains a value referencing the product (e.g. the shared Run key) is never
/// flagged, because deleting it would destroy unrelated entries.
/// </summary>
public static class RegistryScannerService
{
    private const int MAX_DEPTH = 6;

    /// <summary>Subtrees that are enormous and never named after an application.</summary>
    private static readonly HashSet<string> SkippedSubtrees = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLSID", "Interface", "TypeLib", "Installer", "AppID", "Record",
        "Wow6432Node", "WOW6432Node"
    };

    /// <summary>
    /// Shared keys that must never be deleted, expressed relative to the hive root.
    /// </summary>
    private static readonly HashSet<string> ProtectedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        @"Software",
        @"Software\Classes",
        @"Software\Clients",
        @"Software\Policies",
        @"Software\RegisteredApplications",
        @"Software\WOW6432Node",
        @"Software\Microsoft",
        @"Software\Microsoft\Windows",
        @"Software\Microsoft\Windows\CurrentVersion",
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
        @"Software\Microsoft\Windows\CurrentVersion\App Paths",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer",
        @"Software\Microsoft\Windows\CurrentVersion\Policies",
        @"Software\Microsoft\Windows\CurrentVersion\Installer",
        @"Software\Microsoft\Windows NT",
        @"Software\Microsoft\Windows NT\CurrentVersion",
        @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon",
        @"Software\Microsoft\Windows NT\CurrentVersion\Image File Execution Options",
        @"Software\Microsoft\Cryptography",
        @"Software\Microsoft\Windows Defender",
        @"Software\Microsoft\Office",
        @"Software\Microsoft\Internet Explorer",
        @"Software\Microsoft\Edge",
        @"Software\Microsoft\.NETFramework",
        @"Software\Microsoft\Active Setup",
        @"Software\Microsoft\Windows\Shell",
        @"System",
        @"System\CurrentControlSet",
    };

    /// <summary>
    /// Scans for orphaned registry keys matching the given program name and publisher.
    /// </summary>
    public static async Task<List<JunkItem>> ScanForOrphanedKeysAsync(
        string programName, string publisher,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<JunkItem>();
        if (string.IsNullOrWhiteSpace(programName)) return results;

        var searchTerms = UninstallerService.BuildRemnantDirectorySearchTerms(programName, publisher);
        if (searchTerms.Count == 0) return results;

        await Task.Run(() =>
        {
            var roots = new (RegistryHive Hive, RegistryView View, string Label)[]
            {
                (RegistryHive.CurrentUser, RegistryView.Default, "HKCU"),
                (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM (64-bit)"),
                (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM (32-bit)")
            };

            foreach (var (hive, view, label) in roots)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Scanning {label}\\Software...");

                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var softwareKey = baseKey.OpenSubKey("Software");
                    if (softwareKey == null) continue;

                    ScanKeyRecursive(softwareKey, searchTerms, results, $"{label}\\Software", "Software", 0, ct);
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }
        }, ct);

        return results
            .DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanKeyRecursive(
        RegistryKey parentKey,
        HashSet<string> searchTerms,
        List<JunkItem> results,
        string displayPath,
        string relativePath,
        int depth,
        CancellationToken ct)
    {
        if (depth >= MAX_DEPTH) return;

        string[] subKeyNames;
        try { subKeyNames = parentKey.GetSubKeyNames(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var subKeyName in subKeyNames)
        {
            ct.ThrowIfCancellationRequested();

            var childDisplay = $"{displayPath}\\{subKeyName}";
            var childRelative = $"{relativePath}\\{subKeyName}";

            if (UninstallerService.IsSafeRemnantDirectoryMatch(subKeyName, searchTerms) &&
                !IsProtectedKey(childRelative))
            {
                results.Add(new JunkItem
                {
                    Path = childDisplay,
                    Description = $"Registry key matching uninstalled program: {subKeyName}",
                    Type = JunkType.OrphanedRegistryKey,
                    SizeBytes = 0,
                    LastModified = DateTime.Now,
                    IsSelected = false,
                    LockingProcess = "Review before deleting"
                });
                continue; // The whole subtree belongs to the match.
            }

            if (SkippedSubtrees.Contains(subKeyName))
                continue;

            try
            {
                using var subKey = parentKey.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                ScanKeyRecursive(subKey, searchTerms, results, childDisplay, childRelative, depth + 1, ct);
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// True for shared keys that must never be deleted (hive-relative, case-insensitive),
    /// and for anything shallower than two levels below the hive root.
    /// </summary>
    internal static bool IsProtectedKey(string relativePath)
    {
        var normalized = relativePath.Trim().Trim('\\');
        if (normalized.Length == 0)
            return true;

        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            return true;

        // Wow6432Node mirrors Software\*: evaluate the redirected path as its 64-bit twin.
        if (segments.Length >= 2 &&
            segments[0].Equals("Software", StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length < 3)
                return true;
            normalized = "Software\\" + string.Join('\\', segments.Skip(2));
            segments = normalized.Split('\\');
            if (segments.Length < 2)
                return true;
        }

        return ProtectedKeys.Contains(normalized);
    }

    /// <summary>
    /// Exports a registry key to a .reg backup file before deletion.
    /// Returns the backup path, or null when the export failed.
    /// </summary>
    public static async Task<string?> BackupRegistryKeyAsync(string keyPath)
    {
        try
        {
            var (hive, view, subKey) = ParseKeyPath(keyPath);
            if (hive == null || subKey == null)
                return null;

            var backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AuraClean", "Backups");
            Directory.CreateDirectory(backupDir);

            var backupPath = Path.Combine(backupDir, $"reg_backup_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.reg");
            var hiveName = hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
            var viewSwitch = view switch
            {
                RegistryView.Registry32 => " /reg:32",
                RegistryView.Registry64 => " /reg:64",
                _ => string.Empty
            };

            var result = await ProcessRunner.RunAsync(
                ProcessRunner.SystemTool("reg.exe"),
                $"export \"{hiveName}\\{subKey}\" \"{backupPath}\" /y{viewSwitch}",
                timeout: TimeSpan.FromMinutes(2));

            if (result.Succeeded && File.Exists(backupPath))
                return backupPath;

            DiagnosticLogger.Warn("RegistryScanner", $"reg export failed ({result.ExitCode}): {result.CombinedMessage}");
            return null;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("RegistryScanner", $"Backup failed for {keyPath}", ex);
            return null;
        }
    }

    /// <summary>
    /// Deletes a registry key after successfully backing it up. The key is left untouched when
    /// the backup cannot be written or when the key is a protected shared location.
    /// </summary>
    public static async Task<(bool Success, string Message)> DeleteRegistryKeyAsync(string keyPath)
    {
        var (hive, view, subKeyPath) = ParseKeyPath(keyPath);
        if (hive == null || subKeyPath == null)
            return (false, "Invalid registry key path.");

        if (IsProtectedKey(subKeyPath))
            return (false, "This is a shared system key and was not deleted.");

        try
        {
            using (var baseKey = RegistryKey.OpenBaseKey(hive.Value, view))
            using (var existing = baseKey.OpenSubKey(subKeyPath))
            {
                if (existing == null)
                    return (true, "Already removed.");
            }

            var backup = await BackupRegistryKeyAsync(keyPath);
            if (backup == null)
                return (false, "Backup failed — key was not deleted.");

            using var root = RegistryKey.OpenBaseKey(hive.Value, view);
            root.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);
            return (true, $"Deleted. Backup saved to: {backup}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to delete registry key: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses display paths ("HKCU\…", "HKLM (32-bit)\…", "HKEY_LOCAL_MACHINE\…",
    /// "CurrentUser\…", "LocalMachine\…") into hive, view, and hive-relative subkey.
    /// </summary>
    internal static (RegistryHive? Hive, RegistryView View, string? SubKey) ParseKeyPath(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
            return (null, RegistryView.Default, null);

        var firstSlash = keyPath.IndexOf('\\');
        if (firstSlash <= 0 || firstSlash == keyPath.Length - 1)
            return (null, RegistryView.Default, null);

        var hiveToken = keyPath[..firstSlash].Trim();
        var subKey = keyPath[(firstSlash + 1)..].Trim('\\');

        RegistryView view = RegistryView.Default;
        if (hiveToken.EndsWith("(32-bit)", StringComparison.OrdinalIgnoreCase))
        {
            view = RegistryView.Registry32;
            hiveToken = hiveToken[..^"(32-bit)".Length].Trim();
        }
        else if (hiveToken.EndsWith("(64-bit)", StringComparison.OrdinalIgnoreCase))
        {
            view = RegistryView.Registry64;
            hiveToken = hiveToken[..^"(64-bit)".Length].Trim();
        }

        RegistryHive? hive = hiveToken.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" or "CURRENTUSER" => RegistryHive.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" or "LOCALMACHINE" => RegistryHive.LocalMachine,
            _ => null
        };

        if (hive == RegistryHive.LocalMachine && view == RegistryView.Default)
            view = RegistryView.Registry64;

        return (hive, view, string.IsNullOrEmpty(subKey) ? null : subKey);
    }
}
