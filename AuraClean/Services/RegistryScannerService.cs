using AuraClean.Helpers;
using AuraClean.Models;
using Microsoft.Win32;
using System.IO;

namespace AuraClean.Services;

/// <summary>
/// Scans the Windows registry for keys left behind after uninstalls.
/// Pass 1 (<see cref="ScanForOrphanedKeysAsync"/>) only reports keys whose
/// <em>name</em> matches the uninstalled product: a key that merely contains a value
/// referencing the product (e.g. the shared Run key) is never flagged, because deleting
/// it would destroy unrelated entries. Pass 3 (<see cref="ScanForProgramTracesAsync"/>)
/// additionally reports value-level traces (Run values, services, tasks, …); shared
/// value containers are listed for manual review and stay protected from deletion.
/// </summary>
public static class RegistryScannerService
{
    private const int MAX_DEPTH = 6;
    private const int TRACE_MAX_DEPTH = 8;

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
                catch (System.Security.SecurityException ex) { DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied scanning hive: {label}", ex); }
                catch (UnauthorizedAccessException ex) { DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied scanning hive: {label}", ex); }
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
            catch (System.Security.SecurityException ex) { DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied at: {childDisplay}", ex); }
            catch (UnauthorizedAccessException ex) { DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied at: {childDisplay}", ex); }
            catch (IOException ex) { DiagnosticLogger.Warn("RegistryScanner", $"Registry IO failure at: {childDisplay}", ex); }
        }
    }

    /// <summary>
    /// Scans for value-level registry traces of a (possibly already uninstalled) program:
    /// Run/RunOnce values, the Uninstall key itself, Services/ImagePath, scheduled-task
    /// names, App Paths entries, and MUI cache values. Hits under a program-owned key
    /// (Uninstall entry, service, task, App Paths) point at that key and stay deletable
    /// through the existing backup-then-delete path. Hits inside a shared container
    /// (Run/RunOnce, MUI cache) point at the shared parent key and name the value(s) in
    /// the description: they are listed for manual review and
    /// <see cref="DeleteRegistryKeyAsync"/> refuses them via the protected-key guard.
    /// Conservative matching: the same ≥4-char segment rule as the directory scan;
    /// Services values additionally require the value data to contain the
    /// install-location substring, never just the name.
    /// </summary>
    public static async Task<List<JunkItem>> ScanForProgramTracesAsync(
        string displayName, string publisher, string? installLocation,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<JunkItem>();
        if (string.IsNullOrWhiteSpace(displayName))
            return results;

        var searchTerms = UninstallerService.BuildRemnantDirectorySearchTerms(displayName, publisher);
        if (searchTerms.Count == 0)
            return results;

        var installDir = Helpers.PathSafety.Normalize(installLocation);

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
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    ScanTraceTargets(baseKey, label, searchTerms, installDir, results, progress, ct);
                }
                catch (System.Security.SecurityException ex) { DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied scanning traces: {label}", ex); }
                catch (UnauthorizedAccessException ex) { DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied scanning traces: {label}", ex); }
            }
        }, ct);

        return results
            .DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanTraceTargets(
        RegistryKey baseKey, string label,
        HashSet<string> searchTerms, string? installDir,
        List<JunkItem> results,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        // Direct targets with a fixed location.
        foreach (var relative in GetTraceTargets(baseKey))
        {
            ct.ThrowIfCancellationRequested();
            string display = $"{label}\\{relative}";
            progress?.Report($"Scanning {display}...");

            try
            {
                using var key = baseKey.OpenSubKey(relative);
                if (key != null)
                    ScanTraceValues(key, display, searchTerms, installDir,
                        requireInstallPath: false, results, hitLabel: "Leftover autostart value");
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied at: {display}", ex);
            }
        }

        // Uninstall subtree: entry key name or any of its string values.
        ScanTraceUninstallSubtree(baseKey, label, searchTerms, installDir, results, ct);

        // Services: ImagePath / value data must contain the install path (name alone is not enough).
        ScanTraceServicesSubtree(baseKey, label, searchTerms, installDir, results, ct);

        // Scheduled tasks.
        ScanTraceTasksSubtree(baseKey, label, searchTerms, installDir, results, ct);

        // App Paths: subkey names matching the product terms.
        ScanTraceAppPathsSubtree(baseKey, label, searchTerms, installDir, results, ct);

        // MUI cache values referencing the program.
        ScanTraceMuiCacheSubtree(baseKey, label, searchTerms, installDir, results, ct);
    }

    private static IEnumerable<string> GetTraceTargets(RegistryKey baseKey)
    {
        yield return @"Software\Microsoft\Windows\CurrentVersion\Run";
        yield return @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

        // HKLM also carries the machine-wide Run/RunOnce under WOW6432Node.
        if (baseKey.Name.Contains("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
        {
            yield return @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
            yield return @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce";
        }
    }

    private static void ScanTraceUninstallSubtree(
        RegistryKey baseKey, string label,
        HashSet<string> searchTerms, string? installDir,
        List<JunkItem> results, CancellationToken ct)
    {
        const string relative = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        RegistryKey? uninstallKey = null;
        try { uninstallKey = baseKey.OpenSubKey(relative); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied at: {label}\\{relative}", ex);
            return;
        }

        if (uninstallKey == null)
            return;

        using (uninstallKey)
        {
            string[] entryNames;
            try { entryNames = uninstallKey.GetSubKeyNames(); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return;
            }

            foreach (var entry in entryNames)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var entryKey = uninstallKey.OpenSubKey(entry);
                    if (entryKey == null)
                        continue;

                    if (IsTraceValueMatch(entry, searchTerms, installPathRequired: false, installDir: null))
                    {
                        TryAddTraceHit(results, $"{label}\\{relative}\\{entry}",
                            $"Leftover uninstall entry: {entry}");
                        continue;
                    }

                    foreach (var value in GetStringValues(entryKey))
                    {
                        if (IsTraceValueMatch(value.Data, searchTerms, installPathRequired: false, installDir: null))
                        {
                            TryAddTraceHit(results, $"{label}\\{relative}\\{entry}",
                                $"Leftover uninstall entry: {entry} (value {value.Name})");
                            break;
                        }
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied at: {label}\\{relative}\\{entry}", ex);
                }
            }
        }
    }

    private static void ScanTraceServicesSubtree(
        RegistryKey baseKey, string label,
        HashSet<string> searchTerms, string? installDir,
        List<JunkItem> results, CancellationToken ct)
    {
        const string relative = @"System\CurrentControlSet\Services";
        RegistryKey? servicesKey = null;
        try { servicesKey = baseKey.OpenSubKey(relative); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied at: {label}\\{relative}", ex);
            return;
        }

        if (servicesKey == null)
            return;

        using (servicesKey)
        {
            string[] names;
            try { names = servicesKey.GetSubKeyNames(); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return;
            }

            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var serviceKey = servicesKey.OpenSubKey(name);
                    if (serviceKey == null)
                        continue;

                    foreach (var value in GetStringValues(serviceKey))
                    {
                        // False-positive-prone area: install-path substring is mandatory.
                        if (IsTraceValueMatch(value.Data, searchTerms, installPathRequired: true, installDir) &&
                            PathContainsInstallDir(value.Data, installDir))
                        {
                            TryAddTraceHit(results, $"{label}\\{relative}\\{name}",
                                $"Leftover service trace: {name} (value {value.Name})");
                            break;
                        }
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied at: {label}\\{relative}\\{name}", ex);
                }
            }
        }
    }

    private static void ScanTraceTasksSubtree(
        RegistryKey baseKey, string label,
        HashSet<string> searchTerms, string? installDir,
        List<JunkItem> results, CancellationToken ct)
    {
        const string relative = @"Software\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache\Tree";
        RegistryKey? treeKey = null;
        try { treeKey = baseKey.OpenSubKey(relative); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied at: {label}\\{relative}", ex);
            return;
        }

        if (treeKey == null)
            return;

        using (treeKey)
        {
            ScanTraceTreeNames(treeKey, $"{label}\\{relative}", relative,
                searchTerms, installDir, results, depth: 0, ct);
        }
    }

    private static void ScanTraceTreeNames(
        RegistryKey parent, string displayPath, string relativePath,
        HashSet<string> searchTerms, string? installDir,
        List<JunkItem> results, int depth, CancellationToken ct)
    {
        if (depth > TRACE_MAX_DEPTH)
            return;

        string[] names;
        try { names = parent.GetSubKeyNames(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var childDisplay = $"{displayPath}\\{name}";
            var childRelative = $"{relativePath}\\{name}";

            if (IsTraceValueMatch(name, searchTerms, installPathRequired: false, installDir: null) &&
                !IsProtectedKey(childRelative))
            {
                TryAddTraceHit(results, childDisplay,
                    $"Leftover scheduled task: {name}");
                continue;
            }

            try
            {
                using var child = parent.OpenSubKey(name);
                if (child == null)
                    continue;
                ScanTraceTreeNames(child, childDisplay, childRelative,
                    searchTerms, installDir, results, depth + 1, ct);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied at: {childDisplay}", ex);
            }
        }
    }

    private static void ScanTraceAppPathsSubtree(
        RegistryKey baseKey, string label,
        HashSet<string> searchTerms, string? installDir,
        List<JunkItem> results, CancellationToken ct)
    {
        const string relative = @"Software\Microsoft\Windows\CurrentVersion\App Paths";
        RegistryKey? appPathsKey = null;
        try { appPathsKey = baseKey.OpenSubKey(relative); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied at: {label}\\{relative}", ex);
            return;
        }

        if (appPathsKey == null)
            return;

        using (appPathsKey)
        {
            string[] names;
            try { names = appPathsKey.GetSubKeyNames(); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return;
            }

            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsTraceValueMatch(name, searchTerms, installPathRequired: false, installDir: null))
                    continue;

                TryAddTraceHit(results, $"{label}\\{relative}\\{name}",
                    $"Leftover App Paths entry: {name}");
            }
        }
    }

    private static void ScanTraceMuiCacheSubtree(
        RegistryKey baseKey, string label,
        HashSet<string> searchTerms, string? installDir,
        List<JunkItem> results, CancellationToken ct)
    {
        const string relative = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
        RegistryKey? muiKey = null;
        try { muiKey = baseKey.OpenSubKey(relative); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticLogger.Warn("RegistryScanner", $"Registry access denied at: {label}\\{relative}", ex);
            return;
        }

        if (muiKey == null)
            return;

        using (muiKey)
        {
            ct.ThrowIfCancellationRequested();
            ScanTraceValues(muiKey, $"{label}\\{relative}",
                searchTerms, installDir, requireInstallPath: false, results,
                hitLabel: "Leftover display-name trace");
        }
    }

    /// <summary>
    /// Collects the matching values under a shared container key into a single review
    /// hit: the Path stays the shared parent (protected from deletion), the value
    /// names go in the description for manual removal.
    /// </summary>
    private static void ScanTraceValues(
        RegistryKey key, string displayPath,
        HashSet<string> searchTerms, string? installDir,
        bool requireInstallPath,
        List<JunkItem> results,
        string hitLabel)
    {
        var matched = new List<string>();
        foreach (var value in GetStringValues(key))
        {
            if (IsTraceValueMatch(value.Name, searchTerms, requireInstallPath, installDir) ||
                IsTraceValueMatch(value.Data, searchTerms, requireInstallPath, installDir))
            {
                matched.Add(value.Name);
            }
        }

        if (matched.Count == 0)
            return;

        var description = matched.Count == 1
            ? $"{hitLabel}: {matched[0]} (shared key — remove the value manually)"
            : $"{hitLabel}s: {string.Join(", ", matched.Take(5))}" +
              (matched.Count > 5 ? $" (+{matched.Count - 5} more)" : string.Empty) +
              " (shared key — remove the values manually)";

        results.Add(new JunkItem
        {
            Path = displayPath,
            Description = description,
            Type = JunkType.OrphanedRegistryKey,
            SizeBytes = 0,
            LastModified = DateTime.Now,
            IsSelected = false,
            LockingProcess = "Review before deleting (shared key — remove the value manually)"
        });
    }

    private static List<(string Name, string Data)> GetStringValues(RegistryKey key)
    {
        string[] valueNames;
        try { valueNames = key.GetValueNames(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }

        var values = new List<(string Name, string Data)>(valueNames.Length);
        foreach (var name in valueNames)
        {
            object? raw;
            try { raw = key.GetValue(name); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            switch (raw)
            {
                case string s:
                    values.Add((name, s));
                    break;
                case string[] multi:
                    values.Add((name, string.Join(" ", multi)));
                    break;
            }
        }

        return values;
    }

    /// <summary>
    /// Conservative value matcher: exact normalized hit, or any ≥4-char letter-bearing
    /// segment of the search terms appearing as a substring of the normalized text.
    /// When <paramref name="installPathRequired"/> is true the match additionally
    /// requires the text to contain the install-location substring.
    /// </summary>
    internal static bool IsTraceValueMatch(
        string? text, HashSet<string> searchTerms,
        bool installPathRequired, string? installDir)
    {
        if (string.IsNullOrWhiteSpace(text) || searchTerms.Count == 0)
            return false;

        var normalized = UninstallerService.NormalizeForTrace(text);
        if (string.IsNullOrEmpty(normalized))
            return false;

        bool nameHit = searchTerms.Contains(normalized) ||
            searchTerms.Any(term => term.Length >= 4 &&
                term.Any(char.IsLetter) &&
                normalized.Contains(term, StringComparison.OrdinalIgnoreCase));

        if (!nameHit)
        {
            // Segment fallback: split the *text* into segments and match each
            // ≥4-char segment against the terms' normalized segments.
            var textSegments = UninstallerService.SplitForTrace(text);
            var termSegments = searchTerms
                .SelectMany(UninstallerService.SplitForTrace)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            nameHit = textSegments.Any(seg =>
                seg.Length >= 4 && seg.Any(char.IsLetter) && termSegments.Contains(seg));
        }

        if (!nameHit)
            return false;

        return !installPathRequired || PathContainsInstallDir(text, installDir);
    }

    private static bool PathContainsInstallDir(string? text, string? installDir)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(installDir))
            return false;

        return text.Contains(installDir, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryAddTraceHit(
        List<JunkItem> results, string parentDisplayPath, string description)
    {
        results.Add(new JunkItem
        {
            Path = parentDisplayPath,
            Description = description,
            Type = JunkType.OrphanedRegistryKey,
            SizeBytes = 0,
            LastModified = DateTime.Now,
            IsSelected = false,
            LockingProcess = "Review before deleting"
        });
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
