using AuraClean.Helpers;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuraClean.Services;

/// <summary>
/// Tracks all cleanup operations performed by AuraClean with persistent JSON storage.
/// Each operation is logged with timestamp, type, item count, bytes freed, and details.
/// </summary>
public static class CleanupHistoryService
{
    private static readonly string HistoryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AuraClean", "History");

    private static readonly string HistoryFile = Path.Combine(HistoryDir, "cleanup_history.json");

    private static readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Raised after a record is added or the history is cleared.</summary>
    public static event Action? HistoryChanged;

    /// <summary>
    /// Convenience wrapper that honors the "Log cleanup operations" setting and skips
    /// empty operations. Never throws.
    /// </summary>
    public static void Record(CleanupOperationType type, int itemCount, long bytesFreed, string details)
    {
        try
        {
            if (!SettingsService.Load().LogCleanupOperations || itemCount <= 0)
                return;

            LogOperation(new CleanupRecord
            {
                OperationType = type,
                ItemCount = itemCount,
                BytesFreed = Math.Max(0, bytesFreed),
                Details = details
            });
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("CleanupHistoryService", "Failed to record operation", ex);
        }
    }

    /// <summary>
    /// Records a new cleanup operation to the history log.
    /// </summary>
    public static void LogOperation(CleanupRecord record)
    {
        try
        {
            lock (_lock)
            {
                var history = LoadHistoryUnlocked();
                history.Records.Insert(0, record); // newest first

                var settings = SettingsService.Load();
                int max = settings.MaxHistoryEntries > 0 ? settings.MaxHistoryEntries : 500;
                if (history.Records.Count > max)
                    history.Records.RemoveRange(max, history.Records.Count - max);

                SaveHistoryUnlocked(history);
            }

            DiagnosticLogger.Info("CleanupHistoryService",
                $"Logged {record.OperationType}: {record.ItemCount} items, {FormatHelper.FormatBytes(record.BytesFreed)}");
            HistoryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("CleanupHistoryService", "Failed to log cleanup operation", ex);
        }
    }

    /// <summary>
    /// Loads the full cleanup history from disk.
    /// </summary>
    public static CleanupHistory LoadHistory()
    {
        lock (_lock)
            return LoadHistoryUnlocked();
    }

    /// <summary>
    /// Clears all history records. Returns false when the file could not be written.
    /// </summary>
    public static bool ClearHistory()
    {
        try
        {
            lock (_lock)
                SaveHistoryUnlocked(new CleanupHistory());

            DiagnosticLogger.Info("CleanupHistoryService", "History cleared");
            HistoryChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("CleanupHistoryService", "Failed to clear history", ex);
            return false;
        }
    }

    /// <summary>
    /// Gets summary statistics across all history.
    /// </summary>
    public static HistorySummary GetSummary() => BuildSummary(LoadHistory().Records);

    /// <summary>Builds summary statistics for an in-memory record list.</summary>
    public static HistorySummary BuildSummary(IReadOnlyList<CleanupRecord> records) => new()
    {
        TotalOperations = records.Count,
        TotalBytesFreed = records.Sum(r => r.BytesFreed),
        TotalItemsCleaned = records.Sum(r => r.ItemCount),
        FirstOperation = records.Count > 0 ? records.Min(r => r.Timestamp) : null,
        LastOperation = records.Count > 0 ? records.Max(r => r.Timestamp) : null,
        OperationsByType = records
            .GroupBy(r => r.OperationType)
            .ToDictionary(g => g.Key, g => g.Count()),
        BytesByType = records
            .GroupBy(r => r.OperationType)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.BytesFreed))
    };

    /// <summary>
    /// Exports history as a human-readable text report.
    /// </summary>
    public static string ExportAsText()
    {
        var history = LoadHistory();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════");
        sb.AppendLine("  AuraClean — Cleanup History Report");
        sb.AppendLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("═══════════════════════════════════════════");
        sb.AppendLine();

        var summary = BuildSummary(history.Records);
        sb.AppendLine($"Total operations:  {summary.TotalOperations}");
        sb.AppendLine($"Total space freed: {FormatHelper.FormatBytes(summary.TotalBytesFreed)}");
        sb.AppendLine($"Total items:       {summary.TotalItemsCleaned}");
        sb.AppendLine();

        foreach (var record in history.Records)
        {
            sb.AppendLine($"[{record.Timestamp:yyyy-MM-dd HH:mm:ss}] {record.OperationType.ToDisplayString()}");
            sb.AppendLine($"  Items: {record.ItemCount}  |  Freed: {FormatHelper.FormatBytes(record.BytesFreed)}");
            if (!string.IsNullOrWhiteSpace(record.Details))
                sb.AppendLine($"  Details: {record.Details}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns the history storage directory path.
    /// </summary>
    public static string GetHistoryDirectory() => HistoryDir;

    private static CleanupHistory LoadHistoryUnlocked()
    {
        try
        {
            if (File.Exists(HistoryFile))
            {
                var json = File.ReadAllText(HistoryFile);
                var history = JsonSerializer.Deserialize<CleanupHistory>(json, JsonOptions) ?? new CleanupHistory();
                history.Records ??= [];
                history.Records.RemoveAll(r => r == null);
                foreach (var record in history.Records)
                    record.Details ??= string.Empty;
                return history;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("CleanupHistoryService", "Failed to load history", ex);
        }

        return new CleanupHistory();
    }

    private static void SaveHistoryUnlocked(CleanupHistory history)
    {
        Directory.CreateDirectory(HistoryDir);
        var json = JsonSerializer.Serialize(history, JsonOptions);
        var tempFile = HistoryFile + ".tmp";
        File.WriteAllText(tempFile, json);
        File.Move(tempFile, HistoryFile, overwrite: true);
    }
}

// ══════════════════════════════════════════════════
//  Models
// ══════════════════════════════════════════════════

public class CleanupHistory
{
    public List<CleanupRecord> Records { get; set; } = [];
}

public class CleanupRecord
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public CleanupOperationType OperationType { get; set; }
    public int ItemCount { get; set; }
    public long BytesFreed { get; set; }
    public string Details { get; set; } = string.Empty;
    public bool WasDryRun { get; set; }

    /// <summary>
    /// Display-friendly description of the operation.
    /// </summary>
    public string Summary => $"{OperationType.ToDisplayString()} — {ItemCount} items, {FormatHelper.FormatBytes(BytesFreed)}";
    public string TimestampDisplay => Timestamp.ToString("MMM dd, yyyy  HH:mm");
}

public enum CleanupOperationType
{
    SystemClean,
    BrowserClean,
    RegistryClean,
    Uninstall,
    DuplicateRemoval,
    LargeFileRemoval,
    MemoryBoost,
    FileShred,
    QuarantinePurge,
    ThreatQuarantine,
    ThreatDelete,
    ManualQuarantine,
    EmptyFolderRemoval,
    QuarantineRestore
}

public static class CleanupOperationTypeExtensions
{
    public static string ToDisplayString(this CleanupOperationType type) => type switch
    {
        CleanupOperationType.SystemClean => "System Cleanup",
        CleanupOperationType.BrowserClean => "Browser Privacy Clean",
        CleanupOperationType.RegistryClean => "Registry Cleanup",
        CleanupOperationType.Uninstall => "Program Uninstall",
        CleanupOperationType.DuplicateRemoval => "Duplicate Removal",
        CleanupOperationType.LargeFileRemoval => "Large File Removal",
        CleanupOperationType.MemoryBoost => "RAM Boost",
        CleanupOperationType.FileShred => "Secure Shred",
        CleanupOperationType.ThreatQuarantine => "Threat Quarantine",
        CleanupOperationType.ThreatDelete => "Threat Delete",
        CleanupOperationType.QuarantinePurge => "Quarantine Purge",
        CleanupOperationType.ManualQuarantine => "Manual Quarantine",
        CleanupOperationType.EmptyFolderRemoval => "Empty Folder Removal",
        CleanupOperationType.QuarantineRestore => "Quarantine Restore",
        _ => type.ToString()
    };

    public static string ToIconKind(this CleanupOperationType type) => type switch
    {
        CleanupOperationType.SystemClean => "Broom",
        CleanupOperationType.BrowserClean => "Web",
        CleanupOperationType.RegistryClean => "DatabaseCog",
        CleanupOperationType.Uninstall => "DeleteForever",
        CleanupOperationType.DuplicateRemoval => "ContentDuplicate",
        CleanupOperationType.LargeFileRemoval => "FileFind",
        CleanupOperationType.MemoryBoost => "Memory",
        CleanupOperationType.FileShred => "ShieldLock",
        CleanupOperationType.QuarantinePurge => "ShieldRemove",
        CleanupOperationType.ThreatQuarantine => "ShieldAlert",
        CleanupOperationType.ThreatDelete => "DeleteForever",
        CleanupOperationType.ManualQuarantine => "ShieldLock",
        CleanupOperationType.EmptyFolderRemoval => "FolderRemove",
        CleanupOperationType.QuarantineRestore => "Restore",
        _ => "ClipboardList"
    };
}

public class HistorySummary
{
    public int TotalOperations { get; set; }
    public long TotalBytesFreed { get; set; }
    public int TotalItemsCleaned { get; set; }
    public DateTime? FirstOperation { get; set; }
    public DateTime? LastOperation { get; set; }
    public Dictionary<CleanupOperationType, int> OperationsByType { get; set; } = [];
    public Dictionary<CleanupOperationType, long> BytesByType { get; set; } = [];
}
