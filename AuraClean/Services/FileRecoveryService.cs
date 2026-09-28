using AuraClean.Helpers;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace AuraClean.Services;

/// <summary>
/// File Recovery Service — lists the current user's Recycle Bin items and restores them to
/// their original locations. Reads the Recycle Bin's own "$I" metadata records directly, which
/// gives exact original paths, sizes, and deletion times regardless of the Windows language.
/// </summary>
public static class FileRecoveryService
{
    public class RecoverableFile
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public DateTime DeletedDate { get; set; }
        public string FileType { get; set; } = string.Empty;
        public bool IsFolder { get; set; }

        /// <summary>The "$R…" payload inside $Recycle.Bin.</summary>
        public string RecycleBinPath { get; set; } = string.Empty;

        /// <summary>The "$I…" metadata record that describes the payload.</summary>
        public string MetadataPath { get; set; } = string.Empty;

        public string FormattedSize => FormatHelper.FormatBytes(SizeBytes);

        public string DeletedDateDisplay => DeletedDate.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>
    /// Scans every fixed drive's Recycle Bin for the current user.
    /// </summary>
    public static async Task<List<RecoverableFile>> ScanRecycleBinAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Scanning Recycle Bin...");

        var files = await Task.Run(() =>
        {
            var results = new List<RecoverableFile>();
            string? sid;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                sid = identity.User?.Value;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                DiagnosticLogger.Warn("FileRecovery", "Could not resolve current user SID", ex);
                return results;
            }

            if (sid == null)
                return results;

            foreach (var drive in DriveInfo.GetDrives())
            {
                ct.ThrowIfCancellationRequested();

                bool usable;
                try { usable = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable; }
                catch (IOException) { usable = false; }
                if (!usable)
                    continue;

                var binDir = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
                if (!Directory.Exists(binDir))
                    continue;

                progress?.Report($"Reading Recycle Bin on {drive.Name}...");

                IEnumerable<string> metadataFiles;
                try
                {
                    metadataFiles = Directory.EnumerateFiles(binDir, "$I*", PathSafety.TopLevelNoReparse).ToList();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLogger.Warn("FileRecovery", $"Could not read {binDir}", ex);
                    continue;
                }

                foreach (var metadataPath in metadataFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = TryReadItem(metadataPath);
                    if (item != null)
                        results.Add(item);
                }
            }

            return results;
        }, ct);

        progress?.Report($"Found {files.Count} recoverable items.");
        DiagnosticLogger.Info("FileRecovery", $"Scan found {files.Count} items in Recycle Bin");
        return files.OrderByDescending(f => f.DeletedDate).ToList();
    }

    private static RecoverableFile? TryReadItem(string metadataPath)
    {
        try
        {
            var info = new FileInfo(metadataPath);
            if (info.Length is < 24 or > 65536)
                return null;

            var data = File.ReadAllBytes(metadataPath);
            if (!TryParseRecycleBinInfo(data, out var originalPath, out var size, out var deletedUtc))
                return null;

            var payloadName = "$R" + info.Name[2..];
            var payloadPath = Path.Combine(info.DirectoryName!, payloadName);
            bool isFolder = Directory.Exists(payloadPath);
            if (!isFolder && !File.Exists(payloadPath))
                return null; // Orphaned metadata — nothing to restore.

            var fileName = Path.GetFileName(originalPath);
            return new RecoverableFile
            {
                FileName = string.IsNullOrEmpty(fileName) ? originalPath : fileName,
                OriginalPath = originalPath,
                RecycleBinPath = payloadPath,
                MetadataPath = metadataPath,
                SizeBytes = size,
                DeletedDate = deletedUtc.ToLocalTime(),
                IsFolder = isFolder,
                FileType = isFolder ? "Folder" : Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant()
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DiagnosticLogger.Warn("FileRecovery", $"Skipped unreadable Recycle Bin record {metadataPath}", ex);
            return null;
        }
    }

    /// <summary>
    /// Parses a Recycle Bin "$I" record.
    /// Version 1 (Vista–8.1): header, size, FILETIME, fixed 520-byte UTF-16 path.
    /// Version 2 (Windows 10+): header, size, FILETIME, 4-byte char count, UTF-16 path.
    /// </summary>
    internal static bool TryParseRecycleBinInfo(byte[] data, out string originalPath, out long size, out DateTime deletedUtc)
    {
        originalPath = string.Empty;
        size = 0;
        deletedUtc = DateTime.MinValue;

        if (data.Length < 24)
            return false;

        long version = BitConverter.ToInt64(data, 0);
        size = BitConverter.ToInt64(data, 8);
        long fileTime = BitConverter.ToInt64(data, 16);

        try
        {
            deletedUtc = fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;
        }
        catch (ArgumentOutOfRangeException)
        {
            deletedUtc = DateTime.MinValue;
        }

        string raw;
        switch (version)
        {
            case 1:
                if (data.Length < 24 + 2)
                    return false;
                raw = Encoding.Unicode.GetString(data, 24, Math.Min(520, data.Length - 24));
                break;

            case 2:
                if (data.Length < 28)
                    return false;
                int chars = BitConverter.ToInt32(data, 24);
                if (chars <= 0 || 28 + (long)chars * 2 > data.Length)
                    return false;
                raw = Encoding.Unicode.GetString(data, 28, chars * 2);
                break;

            default:
                return false;
        }

        var nul = raw.IndexOf('\0');
        originalPath = (nul >= 0 ? raw[..nul] : raw).Trim();
        return originalPath.Length > 0 && size >= 0;
    }

    /// <summary>
    /// Restores a Recycle Bin item to its original location. Never overwrites: if something now
    /// exists at the original path the item is left in the Recycle Bin and a message explains why.
    /// </summary>
    public static Task<(bool Success, string Message)> RestoreFileAsync(RecoverableFile file) => Task.Run(() =>
    {
        try
        {
            if (string.IsNullOrEmpty(file.RecycleBinPath) || string.IsNullOrEmpty(file.OriginalPath))
                return (false, "Missing Recycle Bin information.");

            bool payloadIsFolder = Directory.Exists(file.RecycleBinPath);
            if (!payloadIsFolder && !File.Exists(file.RecycleBinPath))
                return (false, $"{file.FileName} is no longer in the Recycle Bin.");

            if (File.Exists(file.OriginalPath) || Directory.Exists(file.OriginalPath))
                return (false, $"{file.FileName}: something already exists at {file.OriginalPath}; it was not overwritten.");

            var parent = Path.GetDirectoryName(file.OriginalPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            if (payloadIsFolder)
                Directory.Move(file.RecycleBinPath, file.OriginalPath);
            else
                File.Move(file.RecycleBinPath, file.OriginalPath);

            try
            {
                if (!string.IsNullOrEmpty(file.MetadataPath) && File.Exists(file.MetadataPath))
                    File.Delete(file.MetadataPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLogger.Warn("FileRecovery", $"Restored, but could not remove {file.MetadataPath}", ex);
            }

            DiagnosticLogger.Info("FileRecovery", $"Restored: {file.OriginalPath}");
            return (true, $"Restored {file.FileName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DiagnosticLogger.Error("FileRecovery", $"Failed to restore {file.FileName}", ex);
            return (false, $"{file.FileName}: {ex.Message}");
        }
    });

    /// <summary>
    /// Restores multiple files. Returns the items that were restored and the failure messages.
    /// </summary>
    public static async Task<(List<RecoverableFile> Restored, List<string> Failures)> RestoreFilesAsync(
        IEnumerable<RecoverableFile> files,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var restored = new List<RecoverableFile>();
        var failures = new List<string>();
        var fileList = files.ToList();

        for (int i = 0; i < fileList.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = fileList[i];
            progress?.Report($"Restoring {i + 1}/{fileList.Count}: {file.FileName}");

            var (ok, message) = await RestoreFileAsync(file);
            if (ok) restored.Add(file);
            else failures.Add(message);
        }

        progress?.Report($"Restore complete: {restored.Count} restored, {failures.Count} failed.");
        return (restored, failures);
    }
}
