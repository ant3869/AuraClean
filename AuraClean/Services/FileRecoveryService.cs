using AuraClean.Helpers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AuraClean.Services;

/// <summary>
/// File Recovery Service — scans the Recycle Bin for recently deleted files
/// and allows restoring them to their original locations.
/// Uses the Windows Shell COM interfaces to enumerate Recycle Bin items.
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

        /// <summary>Internal path within Recycle Bin ($Recycle.Bin)</summary>
        public string RecycleBinPath { get; set; } = string.Empty;

        public string FormattedSize => SizeBytes switch
        {
            0 => "0 B",
            < 1024 => $"{SizeBytes} B",
            < 1_048_576 => $"{SizeBytes / 1024.0:F1} KB",
            < 1_073_741_824 => $"{SizeBytes / 1_048_576.0:F1} MB",
            _ => $"{SizeBytes / 1_073_741_824.0:F2} GB"
        };

        public string DeletedDateDisplay => DeletedDate.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>
    /// Scans the Windows Recycle Bin for deleted items using PowerShell
    /// to access Shell.Application COM object.
    /// </summary>
    public static async Task<List<RecoverableFile>> ScanRecycleBinAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var files = new List<RecoverableFile>();

        progress?.Report("Scanning Recycle Bin...");

        try
        {
            await Task.Run(() =>
            {
                // Use Shell32 Namespace to enumerate Recycle Bin (namespace 10 = Recycle Bin)
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return;

                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null) return;

                try
                {
                    // 10 = Recycle Bin special folder
                    dynamic? recycleBin = shell.NameSpace(10);
                    if (recycleBin == null) return;

                    dynamic? items = recycleBin.Items();
                    if (items == null) return;

                    int count = items.Count;
                    progress?.Report($"Found {count} items in Recycle Bin...");

                    for (int i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();

                        try
                        {
                            dynamic? item = items.Item(i);
                            if (item == null) continue;

                            string name = item.Name?.ToString() ?? "";
                            string path = item.Path?.ToString() ?? "";
                            long size = 0;
                            bool isFolder = item.IsFolder;
                            DateTime deletedDate = DateTime.MinValue;

                            try { size = item.Size; } catch { }

                            // Column 2 = "Date deleted" (locale-dependent)
                            try
                            {
                                string? dateStr = recycleBin.GetDetailsOf(item, 2)?.ToString();
                                // Handle non-breaking spaces and Unicode LTR/RTL marks
                                if (!string.IsNullOrWhiteSpace(dateStr))
                                {
                                    dateStr = dateStr.Replace("\u200E", "").Replace("\u200F", "")
                                                     .Replace('\u00A0', ' ').Trim();
                                    DateTime.TryParse(dateStr, out deletedDate);
                                }
                            }
                            catch { }

                            // Column 1 = "Original Location"
                            string originalLocation = "";
                            try
                            {
                                originalLocation = recycleBin.GetDetailsOf(item, 1)?.ToString() ?? "";
                            }
                            catch { }

                            var originalPath = string.IsNullOrEmpty(originalLocation)
                                ? name
                                : Path.Combine(originalLocation, name);

                            files.Add(new RecoverableFile
                            {
                                FileName = name,
                                OriginalPath = originalPath,
                                RecycleBinPath = path,
                                SizeBytes = size,
                                DeletedDate = deletedDate,
                                IsFolder = isFolder,
                                FileType = isFolder ? "Folder" : Path.GetExtension(name).TrimStart('.').ToUpperInvariant()
                            });

                            if ((i + 1) % 100 == 0)
                                progress?.Report($"Processed {i + 1} of {count} items...");
                        }
                        catch { }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(shell);
                }
            }, ct);

            progress?.Report($"Found {files.Count} recoverable items.");
            DiagnosticLogger.Info("FileRecovery", $"Scan found {files.Count} items in Recycle Bin");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("FileRecovery", "Failed to scan Recycle Bin", ex);
            progress?.Report($"Error scanning Recycle Bin: {ex.Message}");
        }

        return files.OrderByDescending(f => f.DeletedDate).ToList();
    }

    /// <summary>
    /// Restores a file from the Recycle Bin to its original location
    /// by using the Shell MoveHere verb.
    /// </summary>
    public static async Task<bool> RestoreFileAsync(RecoverableFile file, IProgress<string>? progress = null)
    {
        if (string.IsNullOrEmpty(file.RecycleBinPath))
            return false;

        progress?.Report($"Restoring {file.FileName}...");

        try
        {
            return await Task.Run(() =>
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return false;

                dynamic? shell = Activator.CreateInstance(shellType);
                if (shell == null) return false;

                try
                {
                    dynamic? recycleBin = shell.NameSpace(10);
                    if (recycleBin == null) return false;

                    dynamic? items = recycleBin.Items();
                    if (items == null) return false;

                    for (int i = 0; i < items.Count; i++)
                    {
                        dynamic? item = items.Item(i);
                        if (item?.Path?.ToString() == file.RecycleBinPath)
                        {
                            // Restore original location directory
                            var origDir = Path.GetDirectoryName(file.OriginalPath);
                            if (!string.IsNullOrEmpty(origDir) && !Directory.Exists(origDir))
                            {
                                Directory.CreateDirectory(origDir);
                            }

                            // Get the original folder namespace and move the item back
                            if (!string.IsNullOrEmpty(origDir))
                            {
                                dynamic? destFolder = shell.NameSpace(origDir);
                                if (destFolder != null)
                                {
                                    // 4 = no UI, 16 = respond "yes to all"
                                    destFolder.MoveHere(item, 4 | 16);
                                    DiagnosticLogger.Info("FileRecovery",
                                        $"Restored: {file.FileName} to {file.OriginalPath}");
                                    return true;
                                }
                            }

                            break;
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(shell);
                }

                return false;
            });
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("FileRecovery", $"Failed to restore {file.FileName}", ex);
            progress?.Report($"Failed to restore {file.FileName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Restores multiple files in batch.
    /// </summary>
    public static async Task<(int Success, int Failed)> RestoreFilesAsync(
        IEnumerable<RecoverableFile> files,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        int success = 0, failed = 0;
        var fileList = files.ToList();

        for (int i = 0; i < fileList.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = fileList[i];
            progress?.Report($"Restoring {i + 1}/{fileList.Count}: {file.FileName}");

            if (await RestoreFileAsync(file, progress))
                success++;
            else
                failed++;
        }

        progress?.Report($"Restore complete: {success} restored, {failed} failed.");
        return (success, failed);
    }
}
