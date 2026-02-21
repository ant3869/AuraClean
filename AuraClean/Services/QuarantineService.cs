using AuraClean.Helpers;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuraClean.Services;

/// <summary>
/// Manages a file quarantine system — instead of permanently deleting suspicious or
/// cleaned files, they can be moved to a quarantine folder with metadata for later
/// restoration or permanent purge.
/// Storage: %LocalAppData%\AuraClean\Quarantine\
/// </summary>
public static class QuarantineService
{
    private static readonly string QuarantineDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AuraClean", "Quarantine");

    private static readonly string ManifestFile = Path.Combine(QuarantineDir, "manifest.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Moves a file to quarantine, preserving its original path for later restoration.
    /// Returns the quarantine entry on success.
    /// </summary>
    public static async Task<QuarantineEntry?> QuarantineFileAsync(
        string originalPath,
        string reason,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(originalPath))
            {
                DiagnosticLogger.Warn("QuarantineService", $"File not found: {originalPath}");
                return null;
            }

            Directory.CreateDirectory(QuarantineDir);

            var fileInfo = new FileInfo(originalPath);
            var entryId = Guid.NewGuid().ToString("N")[..12];
            var storedName = $"{entryId}_{Path.GetFileName(originalPath)}";
            var storedPath = Path.Combine(QuarantineDir, storedName);

            progress?.Report($"Quarantining: {Path.GetFileName(originalPath)}");

            // Move the file
            await Task.Run(() => File.Move(originalPath, storedPath), ct);

            var entry = new QuarantineEntry
            {
                Id = entryId,
                OriginalPath = originalPath,
                StoredFileName = storedName,
                Reason = reason,
                QuarantinedAt = DateTime.Now,
                FileSizeBytes = fileInfo.Length,
                OriginalLastModified = fileInfo.LastWriteTime
            };

            // Update manifest
            var manifest = LoadManifest();
            manifest.Entries.Add(entry);
            SaveManifest(manifest);

            DiagnosticLogger.Info("QuarantineService",
                $"Quarantined: {originalPath} → {storedName} ({FormatHelper.FormatBytes(fileInfo.Length)})");

            return entry;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("QuarantineService", $"Failed to quarantine {originalPath}", ex);
            return null;
        }
    }

    /// <summary>
    /// Quarantines multiple files at once.
    /// </summary>
    public static async Task<List<QuarantineEntry>> QuarantineFilesAsync(
        IEnumerable<string> paths,
        string reason,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<QuarantineEntry>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var entry = await QuarantineFileAsync(path, reason, progress, ct);
            if (entry != null)
                results.Add(entry);
        }
        return results;
    }

    /// <summary>
    /// Restores a quarantined file to its original location.
    /// Creates parent directories if needed.
    /// </summary>
    public static async Task<bool> RestoreFileAsync(
        string entryId,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            var manifest = LoadManifest();
            var entry = manifest.Entries.FirstOrDefault(e => e.Id == entryId);
            if (entry == null)
            {
                DiagnosticLogger.Warn("QuarantineService", $"Entry not found: {entryId}");
                return false;
            }

            var storedPath = Path.Combine(QuarantineDir, entry.StoredFileName);
            if (!File.Exists(storedPath))
            {
                DiagnosticLogger.Warn("QuarantineService", $"Quarantined file missing: {storedPath}");
                manifest.Entries.Remove(entry);
                SaveManifest(manifest);
                return false;
            }

            // Ensure parent directory exists
            var parentDir = Path.GetDirectoryName(entry.OriginalPath);
            if (!string.IsNullOrEmpty(parentDir))
                Directory.CreateDirectory(parentDir);

            progress?.Report($"Restoring: {Path.GetFileName(entry.OriginalPath)}");

            // Handle conflict: if a file already exists at the original path
            if (File.Exists(entry.OriginalPath))
            {
                var backupPath = entry.OriginalPath + ".aura_backup";
                await Task.Run(() => File.Move(entry.OriginalPath, backupPath, overwrite: true), ct);
            }

            await Task.Run(() => File.Move(storedPath, entry.OriginalPath), ct);

            manifest.Entries.Remove(entry);
            SaveManifest(manifest);

            DiagnosticLogger.Info("QuarantineService", $"Restored: {entry.OriginalPath}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("QuarantineService", $"Failed to restore {entryId}", ex);
            return false;
        }
    }

    /// <summary>
    /// Permanently deletes a quarantined file.
    /// </summary>
    public static async Task<bool> PurgeFileAsync(
        string entryId,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            var manifest = LoadManifest();
            var entry = manifest.Entries.FirstOrDefault(e => e.Id == entryId);
            if (entry == null) return false;

            var storedPath = Path.Combine(QuarantineDir, entry.StoredFileName);

            progress?.Report($"Purging: {Path.GetFileName(entry.OriginalPath)}");

            if (File.Exists(storedPath))
                await Task.Run(() => File.Delete(storedPath), ct);

            manifest.Entries.Remove(entry);
            SaveManifest(manifest);

            DiagnosticLogger.Info("QuarantineService", $"Purged: {entry.OriginalPath}");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("QuarantineService", $"Failed to purge {entryId}", ex);
            return false;
        }
    }

    /// <summary>
    /// Purges all quarantined files that have exceeded the retention period.
    /// </summary>
    public static async Task<int> PurgeExpiredAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var settings = SettingsService.Load();
        var retentionDays = settings.QuarantineRetentionDays;
        var cutoff = DateTime.Now.AddDays(-retentionDays);

        var manifest = LoadManifest();
        var expired = manifest.Entries.Where(e => e.QuarantinedAt < cutoff).ToList();

        int purged = 0;
        foreach (var entry in expired)
        {
            ct.ThrowIfCancellationRequested();
            if (await PurgeFileAsync(entry.Id, progress, ct))
                purged++;
        }

        if (purged > 0)
            DiagnosticLogger.Info("QuarantineService", $"Auto-purged {purged} expired items (>{retentionDays}d)");

        return purged;
    }

    /// <summary>
    /// Loads all quarantine entries.
    /// </summary>
    public static List<QuarantineEntry> GetAllEntries()
    {
        return LoadManifest().Entries;
    }

    /// <summary>
    /// Gets quarantine statistics.
    /// </summary>
    public static QuarantineStats GetStats()
    {
        var entries = GetAllEntries();
        return new QuarantineStats
        {
            TotalItems = entries.Count,
            TotalSizeBytes = entries.Sum(e => e.FileSizeBytes),
            OldestEntry = entries.Count > 0 ? entries.Min(e => e.QuarantinedAt) : null,
            NewestEntry = entries.Count > 0 ? entries.Max(e => e.QuarantinedAt) : null
        };
    }

    /// <summary>
    /// Returns the quarantine storage directory path.
    /// </summary>
    public static string GetQuarantineDirectory() => QuarantineDir;

    // ── Manifest I/O ──

    private static QuarantineManifest LoadManifest()
    {
        try
        {
            if (File.Exists(ManifestFile))
            {
                var json = File.ReadAllText(ManifestFile);
                return JsonSerializer.Deserialize<QuarantineManifest>(json, JsonOptions)
                       ?? new QuarantineManifest();
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("QuarantineService", "Failed to load manifest", ex);
        }

        return new QuarantineManifest();
    }

    private static void SaveManifest(QuarantineManifest manifest)
    {
        Directory.CreateDirectory(QuarantineDir);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(ManifestFile, json);
    }
}

// ══════════════════════════════════════════════════
//  Models
// ══════════════════════════════════════════════════

public class QuarantineManifest
{
    public List<QuarantineEntry> Entries { get; set; } = [];
}

public class QuarantineEntry
{
    public string Id { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime QuarantinedAt { get; set; }
    public long FileSizeBytes { get; set; }
    public DateTime OriginalLastModified { get; set; }

    // Display helpers
    public string FileName => Path.GetFileName(OriginalPath);
    public string ParentFolder => Path.GetDirectoryName(OriginalPath) ?? string.Empty;
    public string QuarantinedAtDisplay => QuarantinedAt.ToString("MMM dd, yyyy  HH:mm");
    public string SizeDisplay => Helpers.FormatHelper.FormatBytes(FileSizeBytes);
    public bool IsExpired
    {
        get
        {
            var settings = SettingsService.Load();
            return QuarantinedAt.AddDays(settings.QuarantineRetentionDays) < DateTime.Now;
        }
    }

    public string ExpiresIn
    {
        get
        {
            var settings = SettingsService.Load();
            var expiry = QuarantinedAt.AddDays(settings.QuarantineRetentionDays);
            var remaining = expiry - DateTime.Now;
            if (remaining.TotalDays < 0) return "Expired";
            if (remaining.TotalDays < 1) return $"{remaining.Hours}h remaining";
            return $"{(int)remaining.TotalDays}d remaining";
        }
    }
}

public class QuarantineStats
{
    public int TotalItems { get; set; }
    public long TotalSizeBytes { get; set; }
    public DateTime? OldestEntry { get; set; }
    public DateTime? NewestEntry { get; set; }
}
