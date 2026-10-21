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
        List<string> Errors,
        IReadOnlyList<DamagedFile>? DamagedFiles = null);

    /// <summary>Why a file ended up damaged (overwrite began) without being confirmed deleted.</summary>
    public enum DamageKind
    {
        /// <summary>Cancelled while the overwrite was running; the file is partly overwritten and still present.</summary>
        CancelledDuringOverwrite,
        /// <summary>An I/O error interrupted the overwrite; the file may be partly overwritten and is still present.</summary>
        OverwriteFailed,
        /// <summary>Fully overwritten, but the rename or delete failed: contents destroyed, file still on disk.</summary>
        NotDeleted,
    }

    /// <summary>
    /// A file whose overwrite began but whose deletion was not confirmed. <see cref="RemainsAs"/> is the random name
    /// it was renamed to when the rename succeeded and the delete then failed (null: still at <see cref="Path"/>).
    /// </summary>
    public sealed record DamagedFile(string Path, string? RemainsAs, DamageKind Kind);

    /// <summary>
    /// Thrown when shredding is cancelled. Still an <see cref="OperationCanceledException"/> (existing callers
    /// keep working) but reports what actually happened, so the UI can be honest about it.
    /// </summary>
    public sealed class ShredCancelledException : OperationCanceledException
    {
        public ShredCancelledException(
            IReadOnlyList<string> shreddedPaths, long bytesOverwritten, IReadOnlyList<DamagedFile> damagedFiles, CancellationToken ct)
            : base("Shredding was cancelled.", ct)
        {
            ShreddedPaths = shreddedPaths;
            BytesOverwritten = bytesOverwritten;
            DamagedFiles = damagedFiles;
        }

        /// <summary>Files that were fully overwritten and deleted before the cancel.</summary>
        public IReadOnlyList<string> ShreddedPaths { get; }

        public long BytesOverwritten { get; }

        /// <summary>
        /// Every file whose overwrite began without a confirmed delete — failures earlier in the run as well as the
        /// file being overwritten when the cancel arrived. Empty only when no file was modified and left behind.
        /// </summary>
        public IReadOnlyList<DamagedFile> DamagedFiles { get; }

        /// <summary>The file whose overwrite was running when the cancel arrived, or null.</summary>
        public string? PartiallyOverwrittenFile =>
            DamagedFiles.FirstOrDefault(d => d.Kind == DamageKind.CancelledDuringOverwrite)?.Path;
    }

    /// <summary>Tracks whether the first write to the current file has started.</summary>
    internal sealed class OverwriteProgress { public volatile bool Started; }

    /// <summary>
    /// The three file operations the shred loop performs. Internal seam so tests can inject overwrite, rename and
    /// delete failures deterministically; production always uses <see cref="DefaultOperations"/>.
    /// </summary>
    internal interface IShredFileOperations
    {
        Task OverwriteAsync(string path, long fileSize, ShredAlgorithm algorithm, CancellationToken ct, OverwriteProgress progress);
        void Move(string source, string destination);
        void Delete(string path);
    }

    private sealed class DefaultOperations : IShredFileOperations
    {
        public Task OverwriteAsync(string path, long fileSize, ShredAlgorithm algorithm, CancellationToken ct, OverwriteProgress progress)
            => OverwriteFileAsync(path, fileSize, algorithm, ct, progress);
        public void Move(string source, string destination) => File.Move(source, destination);
        public void Delete(string path) => File.Delete(path);
    }

    internal static IShredFileOperations Operations { get; set; } = new DefaultOperations();

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
        var shreddedPaths = new List<string>();
        var damaged = new List<DamagedFile>();

        for (int i = 0; i < filePaths.Count; i++)
        {
            // Cancelled between files: nothing is half-done now, but earlier failures may have left damaged files.
            if (ct.IsCancellationRequested)
                throw new ShredCancelledException(shreddedPaths, totalBytes, damaged, ct);
            var path = filePaths[i];
            var fileName = Path.GetFileName(path);
            progress?.Report((i + 1, filePaths.Count, fileName));

            var overwrite = new OverwriteProgress();
            // Where the already-overwritten file is now, until its deletion is confirmed.
            string? residual = null;
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

                await Operations.OverwriteAsync(path, fileSize, algorithm, ct, overwrite);
                residual = path;
                totalBytes += fileSize;

                // Rename to a random name before deleting to obscure the original file name.
                string randomName = Path.Combine(
                    Path.GetDirectoryName(path)!,
                    Path.GetRandomFileName());
                try
                {
                    Operations.Move(path, randomName);
                    residual = randomName;
                    Operations.Delete(randomName);
                }
                catch (Exception renameEx) when (renameEx is IOException or UnauthorizedAccessException)
                {
                    Operations.Delete(File.Exists(randomName) ? randomName : path);
                }

                residual = null;
                shredded++;
                shreddedPaths.Add(path);
                DiagnosticLogger.Info("FileShredder", $"Shredded: {path} ({FormatHelper.FormatBytes(fileSize)})");
            }
            catch (OperationCanceledException)
            {
                // Cancellation is not a per-file failure; surface it even for the last file. If the overwrite had
                // begun, the file is partly destroyed but still present: say so instead of implying nothing changed.
                if (overwrite.Started)
                    damaged.Add(new DamagedFile(path, RemainsAs: null, DamageKind.CancelledDuringOverwrite));
                throw new ShredCancelledException(shreddedPaths, totalBytes, damaged, ct);
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{path}: {ex.Message}");
                DiagnosticLogger.Warn("FileShredder", $"Failed to shred: {path}", ex);

                // The overwrite began but the file is not confirmed deleted: remember it (and where it is now) so a
                // later cancel, or the final summary, cannot claim the file is intact or unchanged.
                if (overwrite.Started)
                {
                    damaged.Add(residual == null
                        ? new DamagedFile(path, RemainsAs: null, DamageKind.OverwriteFailed)
                        : new DamagedFile(path, residual == path ? null : residual, DamageKind.NotDeleted));
                }
            }
        }

        return new ShredResult(shredded, failed, totalBytes, errors, damaged);
    }

    /// <summary>
    /// Overwrites file content according to the selected algorithm.
    /// </summary>
    private static async Task OverwriteFileAsync(
        string path, long fileSize, ShredAlgorithm algorithm, CancellationToken ct, OverwriteProgress progress)
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

                // Mark BEFORE the first write: a cancel arriving mid-write may already have changed bytes.
                progress.Started = true;
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
