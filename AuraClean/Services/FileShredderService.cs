using System.IO;
using System.Security.Cryptography;
using AuraClean.Helpers;

namespace AuraClean.Services;

/// <summary>
/// Provides secure file deletion by overwriting data before removal.
/// Supports multiple overwrite algorithms of varying thoroughness.
/// </summary>
public static class FileShredderService
{
    /// <summary>
    /// The shredding algorithm to use when overwriting file data.
    /// </summary>
    public enum ShredAlgorithm
    {
        /// <summary>Single pass of zeros — fast, sufficient for SSDs.</summary>
        QuickZero,

        /// <summary>Single pass of cryptographically random bytes.</summary>
        Random,

        /// <summary>DoD 5220.22-M: 3 passes (zeros, ones, random) — US Department of Defense standard.</summary>
        DoD3Pass,

        /// <summary>7-pass overwrite (alternating patterns + random) — enhanced security.</summary>
        Enhanced7Pass,
    }

    /// <summary>
    /// Result of a shred operation.
    /// </summary>
    public record ShredResult(
        int FilesShredded,
        int FilesFailed,
        long TotalBytesOverwritten,
        List<string> Errors);

    /// <summary>
    /// Securely shreds the given files using the specified algorithm.
    /// </summary>
    public static async Task<ShredResult> ShredFilesAsync(
        IReadOnlyList<string> filePaths,
        ShredAlgorithm algorithm,
        IProgress<(int current, int total, string fileName)>? progress = null,
        CancellationToken ct = default)
    {
        int shredded = 0;
        int failed = 0;
        long totalBytes = 0;
        var errors = new List<string>();

        for (int i = 0; i < filePaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = filePaths[i];
            var fileName = Path.GetFileName(path);
            progress?.Report((i + 1, filePaths.Count, fileName));

            try
            {
                if (!File.Exists(path))
                {
                    errors.Add($"File not found: {path}");
                    failed++;
                    continue;
                }

                if (!PathSafety.IsSafeToDeleteFile(path, out var reason))
                {
                    errors.Add($"{path}: {reason}");
                    failed++;
                    continue;
                }

                var fileInfo = new FileInfo(path);
                if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // Overwriting through a symlink would destroy the link target instead.
                    errors.Add($"{path}: symbolic links are not shredded.");
                    failed++;
                    continue;
                }

                long fileSize = fileInfo.Length;

                if (fileInfo.IsReadOnly)
                    fileInfo.IsReadOnly = false;

                await OverwriteFileAsync(path, fileSize, algorithm, ct);
                totalBytes += fileSize;

                // Rename to a random name before deleting to obscure the original file name.
                string randomName = Path.Combine(
                    Path.GetDirectoryName(path)!,
                    Path.GetRandomFileName());
                try
                {
                    File.Move(path, randomName);
                    File.Delete(randomName);
                }
                catch (Exception renameEx) when (renameEx is IOException or UnauthorizedAccessException)
                {
                    File.Delete(File.Exists(randomName) ? randomName : path);
                }

                shredded++;
                DiagnosticLogger.Info("FileShredder", $"Shredded: {path} ({FormatHelper.FormatBytes(fileSize)})");
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{path}: {ex.Message}");
                DiagnosticLogger.Warn("FileShredder", $"Failed to shred: {path}", ex);
            }
        }

        return new ShredResult(shredded, failed, totalBytes, errors);
    }

    /// <summary>
    /// Overwrites file content according to the selected algorithm.
    /// </summary>
    private static async Task OverwriteFileAsync(
        string path, long fileSize, ShredAlgorithm algorithm, CancellationToken ct)
    {
        var passes = GetOverwritePasses(algorithm);
        const int bufferSize = 1024 * 1024; // 1 MB write buffer

        // WriteThrough + Flush(true) push every pass to the device instead of letting the
        // OS cache coalesce the passes so that only the last one reaches the disk.
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough | FileOptions.Asynchronous);

        var buffer = new byte[(int)Math.Min(bufferSize, Math.Max(fileSize, 1))];

        foreach (var (type, pattern) in passes)
        {
            ct.ThrowIfCancellationRequested();

            switch (type)
            {
                case PassType.Zeros: Array.Clear(buffer); break;
                case PassType.Ones: Array.Fill(buffer, (byte)0xFF); break;
                case PassType.Pattern: Array.Fill(buffer, pattern); break;
            }

            fs.Position = 0;
            long remaining = fileSize;
            while (remaining > 0)
            {
                int chunkSize = (int)Math.Min(buffer.Length, remaining);
                if (type == PassType.Random)
                    RandomNumberGenerator.Fill(buffer.AsSpan(0, chunkSize));

                await fs.WriteAsync(buffer.AsMemory(0, chunkSize), ct);
                remaining -= chunkSize;
            }

            fs.Flush(flushToDisk: true);
        }

        // Hide the original size before the file is renamed and deleted.
        fs.SetLength(0);
        fs.Flush(flushToDisk: true);
    }

    private enum PassType { Zeros, Ones, Random, Pattern }

    private static List<(PassType type, byte pattern)> GetOverwritePasses(ShredAlgorithm algorithm)
    {
        return algorithm switch
        {
            ShredAlgorithm.QuickZero => [
                (PassType.Zeros, 0)
            ],

            ShredAlgorithm.Random => [
                (PassType.Random, 0)
            ],

            ShredAlgorithm.DoD3Pass => [
                (PassType.Zeros, 0),      // Pass 1: all zeros
                (PassType.Ones, 0),       // Pass 2: all ones
                (PassType.Random, 0),     // Pass 3: random data
            ],

            ShredAlgorithm.Enhanced7Pass => [
                (PassType.Zeros, 0),
                (PassType.Ones, 0),
                (PassType.Random, 0),
                (PassType.Pattern, 0xAA),
                (PassType.Pattern, 0x55),
                (PassType.Random, 0),
                (PassType.Zeros, 0),
            ],

            _ => [(PassType.Zeros, 0)]
        };
    }

    /// <summary>
    /// Gets the number of overwrite passes for a given algorithm.
    /// </summary>
    public static int GetPassCount(ShredAlgorithm algorithm) => algorithm switch
    {
        ShredAlgorithm.QuickZero => 1,
        ShredAlgorithm.Random => 1,
        ShredAlgorithm.DoD3Pass => 3,
        ShredAlgorithm.Enhanced7Pass => 7,
        _ => 1
    };

    /// <summary>
    /// Gets a human-readable description of the algorithm.
    /// </summary>
    public static string GetAlgorithmDescription(ShredAlgorithm algorithm) => algorithm switch
    {
        ShredAlgorithm.QuickZero => "Quick Zero (1 pass) — Overwrites with zeros. On SSDs no overwrite method can guarantee erasure; use full-disk encryption.",
        ShredAlgorithm.Random => "Random (1 pass) — Overwrites with cryptographic random data.",
        ShredAlgorithm.DoD3Pass => "DoD 5220.22-M (3 passes) — US Department of Defense standard.",
        ShredAlgorithm.Enhanced7Pass => "Enhanced (7 passes) — Maximum security with alternating patterns.",
        _ => "Unknown"
    };
}
