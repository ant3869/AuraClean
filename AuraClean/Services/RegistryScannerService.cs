using AuraClean.Models;
using Microsoft.Win32;
using System.IO;

namespace AuraClean.Services;

/// <summary>
/// Recursively scans the Windows registry for orphaned keys left behind after uninstalls.
/// Targets HKCU\Software and HKLM\Software, both 32-bit and 64-bit views.
/// </summary>
public static class RegistryScannerService
{
    private const int MAX_DEPTH = 6;

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

        // Build search terms: program name segments + publisher
        var searchTerms = BuildSearchTerms(programName, publisher);

        await Task.Run(() =>
        {
            // Scan all four combinations: HKCU/HKLM × 32-bit/64-bit
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

                    ScanKeyRecursive(softwareKey, searchTerms, results,
                        $"{label}\\Software", 0, progress, ct);
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }
        }, ct);

        return results;
    }

    /// <summary>
    /// Scans a single registry key for matches and recurses into subkeys.
    /// </summary>
    private static void ScanKeyRecursive(
        RegistryKey parentKey,
        HashSet<string> searchTerms,
        List<JunkItem> results,
        string currentPath,
        int depth,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (depth >= MAX_DEPTH) return;

        string[] subKeyNames;
        try { subKeyNames = parentKey.GetSubKeyNames(); }
        catch { return; }

        foreach (var subKeyName in subKeyNames)
        {
            ct.ThrowIfCancellationRequested();

            string fullPath = $"{currentPath}\\{subKeyName}";

            // Check if the key name itself matches any search term
            bool nameMatches = searchTerms.Any(term =>
                subKeyName.Contains(term, StringComparison.OrdinalIgnoreCase));

            if (nameMatches)
            {
                results.Add(new JunkItem
                {
                    Path = fullPath,
                    Description = $"Registry key matching uninstalled program: {subKeyName}",
                    Type = JunkType.OrphanedRegistryKey,
                    SizeBytes = 0,
                    LastModified = DateTime.Now
                });
                continue; // Don't recurse into matched keys — the entire subtree is a match
            }

            // Recurse into this subkey to look deeper
            try
            {
                using var subKey = parentKey.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                // Also check string values in this key for matches
                if (CheckValuesForMatch(subKey, searchTerms))
                {
                    results.Add(new JunkItem
                    {
                        Path = fullPath,
                        Description = $"Registry key with values referencing uninstalled program",
                        Type = JunkType.OrphanedRegistryKey,
                        SizeBytes = 0,
                        LastModified = DateTime.Now
                    });
                    continue;
                }

                ScanKeyRecursive(subKey, searchTerms, results, fullPath,
                    depth + 1, progress, ct);
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Checks if any string values in the given key match the search terms.
    /// </summary>
    private static bool CheckValuesForMatch(RegistryKey key, HashSet<string> searchTerms)
    {
        try
        {
            foreach (var valueName in key.GetValueNames())
            {
                var value = key.GetValue(valueName);
                if (value is not string strValue) continue;

                if (searchTerms.Any(term =>
                    strValue.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Builds a set of normalized search terms from the program name and publisher.
    /// </summary>
    private static HashSet<string> BuildSearchTerms(string programName, string publisher)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Add the full program name
        if (!string.IsNullOrWhiteSpace(programName))
        {
            terms.Add(programName);

            // Also add individual words that are at least 4 chars (avoid noise like "the", "pro")
            foreach (var word in programName.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 4)
                    terms.Add(word);
            }
        }

        // Add publisher name
        if (!string.IsNullOrWhiteSpace(publisher))
        {
            terms.Add(publisher);

            foreach (var word in publisher.Split([' ', '-', '_', '.', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 4)
                    terms.Add(word);
            }
        }

        // Remove common noise terms
        terms.Remove("Windows");
        terms.Remove("Microsoft");
        terms.Remove("Update");
        terms.Remove("Version");
        terms.Remove("Corporation");
        terms.Remove("Software");
        terms.Remove("Technologies");

        return terms;
    }

    /// <summary>
    /// Exports a registry key to a .reg backup file before deletion.
    /// </summary>
    public static async Task<string?> BackupRegistryKeyAsync(string keyPath)
    {
        try
        {
            var backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AuraClean", "Backups");
            Directory.CreateDirectory(backupDir);

            var fileName = $"reg_backup_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.reg";
            var backupPath = Path.Combine(backupDir, fileName);

            // Use reg.exe export for reliable .reg file generation
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "reg.exe",
                    Arguments = $"export \"{keyPath}\" \"{backupPath}\" /y",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync();

            return process.ExitCode == 0 ? backupPath : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes a registry key after backing it up.
    /// </summary>
    public static async Task<(bool Success, string Message)> DeleteRegistryKeyAsync(string keyPath)
    {
        // Back up first
        var backup = await BackupRegistryKeyAsync(keyPath);

        try
        {
            // Parse the key path to determine hive and subkey
            var (hive, subKeyPath) = ParseKeyPath(keyPath);
            if (hive == null || subKeyPath == null)
                return (false, "Invalid registry key path.");

            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Default);
            baseKey.DeleteSubKeyTree(subKeyPath, throwOnMissingSubKey: false);

            var msg = backup != null
                ? $"Deleted. Backup saved to: {backup}"
                : "Deleted (backup failed).";

            return (true, msg);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to delete registry key: {ex.Message}");
        }
    }

    private static (RegistryHive? Hive, string? SubKey) ParseKeyPath(string keyPath)
    {
        if (keyPath.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) ||
            keyPath.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
        {
            var idx = keyPath.IndexOf('\\') + 1;
            return (RegistryHive.CurrentUser, keyPath[idx..]);
        }

        if (keyPath.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase) ||
            keyPath.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
        {
            var idx = keyPath.IndexOf('\\') + 1;

            // Handle display labels like "HKLM (64-bit)\\"
            var subKey = keyPath[idx..];
            if (subKey.StartsWith("("))
            {
                var nextSlash = subKey.IndexOf('\\');
                if (nextSlash >= 0) subKey = subKey[(nextSlash + 1)..];
            }

            return (RegistryHive.LocalMachine, subKey);
        }

        return (null, null);
    }
}
