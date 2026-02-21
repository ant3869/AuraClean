using AuraClean.Helpers;
using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuraClean.Services;

/// <summary>
/// "Shadow" Installation Monitor — Snapshot service that records "Before" and "After"
/// state of the Windows Registry and Program Files during software installation.
/// Generates a log file for 100% surgical removal of the installed program.
/// </summary>
public static class InstallMonitorService
{
    private static readonly string SnapshotDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AuraClean", "Snapshots");

    /// <summary>
    /// Represents a full system snapshot (registry + file system state).
    /// </summary>
    public class SystemSnapshot
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Label { get; set; } = string.Empty;

        /// <summary>Registry key paths → value names.</summary>
        public Dictionary<string, List<string>> RegistryEntries { get; set; } = [];

        /// <summary>Full file paths with sizes.</summary>
        public Dictionary<string, long> FileEntries { get; set; } = [];

        /// <summary>Directory paths.</summary>
        public HashSet<string> DirectoryEntries { get; set; } = [];
    }

    /// <summary>
    /// Represents the delta between two snapshots: everything that was added.
    /// </summary>
    public class SnapshotDelta
    {
        public string ProgramLabel { get; set; } = string.Empty;
        public DateTime InstalledAt { get; set; }
        public List<string> NewRegistryKeys { get; set; } = [];
        public List<string> NewRegistryValues { get; set; } = [];
        public List<FileChange> NewFiles { get; set; } = [];
        public List<string> NewDirectories { get; set; } = [];
        public long TotalNewFileSizeBytes { get; set; }
    }

    public class FileChange
    {
        public string Path { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
    }

    /// <summary>Registry roots to monitor.</summary>
    private static readonly (RegistryHive Hive, RegistryView View, string SubKey, string Label)[] MonitoredRegistryPaths =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE", "HKLM\\SOFTWARE"),
        (RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE", "HKCU\\SOFTWARE"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services", "HKLM\\Services"),
    ];

    /// <summary>File system roots to monitor.</summary>
    private static readonly string[] MonitoredDirectories =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    ];

    /// <summary>
    /// Takes a "Before" snapshot of registry and file system state.
    /// </summary>
    /// <param name="label">A label for this snapshot (e.g., program name being installed).</param>
    /// <param name="dryRun">If true, returns the snapshot but does not persist it.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Snapshot ID (filename) that can be used to compare later.</returns>
    public static async Task<(string SnapshotId, SystemSnapshot Snapshot)> TakeSnapshotAsync(
        string label,
        bool dryRun = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var snapshot = new SystemSnapshot { Label = label };

        await Task.Run(() =>
        {
            // Phase 1: Snapshot registry keys
            progress?.Report("Capturing registry state...");
            foreach (var (hive, view, subKey, regLabel) in MonitoredRegistryPaths)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Scanning {regLabel}...");
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var rootKey = baseKey.OpenSubKey(subKey);
                    if (rootKey != null)
                    {
                        SnapshotRegistryRecursive(rootKey, $"{regLabel}", snapshot.RegistryEntries, 0, 4, ct);
                    }
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }

            // Phase 2: Snapshot file system
            progress?.Report("Capturing file system state...");
            foreach (var dir in MonitoredDirectories.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Scanning {dir}...");
                try
                {
                    SnapshotFileSystemRecursive(dir, snapshot, 0, 3, ct);
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }
            }
        }, ct);

        // Generate ID and save
        string snapshotId = $"{SanitizeFileName(label)}_{snapshot.Timestamp:yyyyMMdd_HHmmss}";

        if (!dryRun)
        {
            Directory.CreateDirectory(SnapshotDir);
            var filePath = Path.Combine(SnapshotDir, $"{snapshotId}.json");
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            await File.WriteAllTextAsync(filePath, json, ct);
        }

        progress?.Report($"Snapshot '{label}' captured: {snapshot.RegistryEntries.Count} registry paths, {snapshot.FileEntries.Count} files.");
        return (snapshotId, snapshot);
    }

    /// <summary>
    /// Compares a "Before" snapshot with the current system state (the "After") and generates a delta log.
    /// </summary>
    /// <param name="snapshotId">The snapshot ID returned from TakeSnapshotAsync.</param>
    /// <param name="dryRun">If true, generates the report but does not persist the log.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The delta between the before snapshot and the current state.</returns>
    public static async Task<SnapshotDelta> CompareAndGenerateDeltaAsync(
        string snapshotId,
        bool dryRun = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // Load the "Before" snapshot
        var snapshotPath = Path.Combine(SnapshotDir, $"{snapshotId}.json");
        if (!File.Exists(snapshotPath))
            throw new FileNotFoundException($"Snapshot '{snapshotId}' not found.", snapshotPath);

        var json = await File.ReadAllTextAsync(snapshotPath, ct);
        var before = JsonSerializer.Deserialize<SystemSnapshot>(json)
            ?? throw new InvalidOperationException("Failed to deserialize snapshot.");

        // Take the "After" snapshot (current state)
        progress?.Report("Capturing current state for comparison...");
        var (_, after) = await TakeSnapshotAsync(before.Label + " (After)", dryRun: true, progress, ct);

        // Compute delta
        progress?.Report("Computing differences...");
        var delta = new SnapshotDelta
        {
            ProgramLabel = before.Label,
            InstalledAt = DateTime.Now
        };

        // New registry keys
        foreach (var (key, values) in after.RegistryEntries)
        {
            ct.ThrowIfCancellationRequested();
            if (!before.RegistryEntries.ContainsKey(key))
            {
                delta.NewRegistryKeys.Add(key);
                foreach (var v in values)
                    delta.NewRegistryValues.Add($"{key}\\{v}");
            }
            else
            {
                // Check for new values within existing keys
                var beforeValues = before.RegistryEntries[key];
                foreach (var v in values.Except(beforeValues))
                    delta.NewRegistryValues.Add($"{key}\\{v}");
            }
        }

        // New files
        foreach (var (path, size) in after.FileEntries)
        {
            ct.ThrowIfCancellationRequested();
            if (!before.FileEntries.ContainsKey(path))
            {
                delta.NewFiles.Add(new FileChange { Path = path, SizeBytes = size });
                delta.TotalNewFileSizeBytes += size;
            }
        }

        // New directories
        foreach (var dir in after.DirectoryEntries.Except(before.DirectoryEntries))
        {
            delta.NewDirectories.Add(dir);
        }

        // Persist delta log
        if (!dryRun)
        {
            Directory.CreateDirectory(SnapshotDir);
            var logPath = Path.Combine(SnapshotDir, $"{snapshotId}_delta.json");
            var deltaJson = JsonSerializer.Serialize(delta, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(logPath, deltaJson, ct);

            // Also write a human-readable text report
            var reportPath = Path.Combine(SnapshotDir, $"{snapshotId}_report.txt");
            await WriteHumanReadableReportAsync(reportPath, delta, ct);
        }

        progress?.Report($"Delta: {delta.NewRegistryKeys.Count} new reg keys, {delta.NewFiles.Count} new files ({FormatHelper.FormatBytes(delta.TotalNewFileSizeBytes)}).");
        return delta;
    }

    /// <summary>
    /// Lists all existing snapshots with their metadata.
    /// </summary>
    public static async Task<List<(string Id, string Label, DateTime Timestamp, bool HasDelta)>> ListSnapshotsAsync()
    {
        var results = new List<(string Id, string Label, DateTime Timestamp, bool HasDelta)>();
        if (!Directory.Exists(SnapshotDir)) return results;

        await Task.Run(() =>
        {
            foreach (var file in Directory.GetFiles(SnapshotDir, "*.json"))
            {
                if (file.EndsWith("_delta.json")) continue;

                try
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    var json = File.ReadAllText(file);
                    var snap = JsonSerializer.Deserialize<SystemSnapshot>(json);
                    if (snap != null)
                    {
                        bool hasDelta = File.Exists(Path.Combine(SnapshotDir, $"{id}_delta.json"));
                        results.Add((id, snap.Label, snap.Timestamp, hasDelta));
                    }
                }
                catch (Exception ex) { DiagnosticLogger.Warn("InstallMonitor", $"Failed to load snapshot: {file}", ex); }
            }
        });

        return results.OrderByDescending(r => r.Timestamp).ToList();
    }

    /// <summary>
    /// Deletes a snapshot and its delta files.
    /// </summary>
    public static void DeleteSnapshot(string snapshotId)
    {
        var basePath = Path.Combine(SnapshotDir, snapshotId);
        TryDelete($"{basePath}.json");
        TryDelete($"{basePath}_delta.json");
        TryDelete($"{basePath}_report.txt");
    }

    #region Private Helpers

    private static void SnapshotRegistryRecursive(
        RegistryKey parentKey, string path,
        Dictionary<string, List<string>> entries,
        int depth, int maxDepth, CancellationToken ct)
    {
        if (depth >= maxDepth) return;

        try
        {
            var valueNames = parentKey.GetValueNames();
            if (valueNames.Length > 0)
                entries[path] = [.. valueNames];

            foreach (var sub in parentKey.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var subKey = parentKey.OpenSubKey(sub);
                    if (subKey != null)
                        SnapshotRegistryRecursive(subKey, $"{path}\\{sub}", entries, depth + 1, maxDepth, ct);
                }
                catch { }
            }
        }
        catch (Exception ex) { DiagnosticLogger.Warn("InstallMonitor", $"Registry snapshot failed at: {path}", ex); }
    }

    private static void SnapshotFileSystemRecursive(
        string directory, SystemSnapshot snapshot,
        int depth, int maxDepth, CancellationToken ct)
    {
        if (depth >= maxDepth) return;

        try
        {
            snapshot.DirectoryEntries.Add(directory);

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(file);
                    snapshot.FileEntries[file] = fi.Length;
                }
                catch { }
            }

            foreach (var dir in Directory.EnumerateDirectories(directory))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    SnapshotFileSystemRecursive(dir, snapshot, depth + 1, maxDepth, ct);
                }
                catch { }
            }
        }
        catch (Exception ex) { DiagnosticLogger.Warn("InstallMonitor", $"Filesystem snapshot failed at: {directory}", ex); }
    }

    private static async Task WriteHumanReadableReportAsync(
        string path, SnapshotDelta delta, CancellationToken ct)
    {
        using var writer = new StreamWriter(path);
        await writer.WriteLineAsync($"═══ AuraClean Installation Monitor Report ═══");
        await writer.WriteLineAsync($"Program: {delta.ProgramLabel}");
        await writer.WriteLineAsync($"Installed: {delta.InstalledAt:yyyy-MM-dd HH:mm:ss}");
        await writer.WriteLineAsync($"Total new file size: {FormatHelper.FormatBytes(delta.TotalNewFileSizeBytes)}");
        await writer.WriteLineAsync();

        await writer.WriteLineAsync($"── New Registry Keys ({delta.NewRegistryKeys.Count}) ──");
        foreach (var key in delta.NewRegistryKeys)
            await writer.WriteLineAsync($"  [REG] {key}");

        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"── New Registry Values ({delta.NewRegistryValues.Count}) ──");
        foreach (var val in delta.NewRegistryValues.Take(200)) // cap for readability
            await writer.WriteLineAsync($"  [VAL] {val}");

        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"── New Directories ({delta.NewDirectories.Count}) ──");
        foreach (var dir in delta.NewDirectories)
            await writer.WriteLineAsync($"  [DIR] {dir}");

        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"── New Files ({delta.NewFiles.Count}) ──");
        foreach (var file in delta.NewFiles)
            await writer.WriteLineAsync($"  [{FormatHelper.FormatBytes(file.SizeBytes),10}] {file.Path}");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }



    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    #endregion
}
