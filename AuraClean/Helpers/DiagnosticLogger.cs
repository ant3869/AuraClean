using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace AuraClean.Helpers;

/// <summary>
/// Lightweight diagnostic logger for AuraClean.
/// Writes to Debug output and to a daily log file under %LocalAppData%\AuraClean\Logs.
/// Uses a buffered write queue to reduce disk I/O under heavy logging load.
/// Thread-safe for use from multiple async operations.
/// </summary>
public static class DiagnosticLogger
{
    /// <summary>Directory that holds daily logs, crash logs, and cleanup audits.</summary>
    public static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AuraClean", "Logs");

    /// <summary>Crash log written synchronously for unhandled exceptions.</summary>
    public static readonly string CrashLogPath = Path.Combine(LogDirectory, "crash.log");

    private static readonly ConcurrentQueue<string> _buffer = new();
    private static readonly object _flushLock = new();
    private static int _pendingCount;
    private const int FlushThreshold = 5;
    private const long MaxCrashLogBytes = 2 * 1024 * 1024;

    static DiagnosticLogger()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    private static string CurrentLogFilePath =>
        Path.Combine(LogDirectory, $"AuraClean_{DateTime.Now:yyyy-MM-dd}.log");

    /// <summary>
    /// Logs a warning-level message with optional exception details.
    /// Used to replace bare catch blocks that would otherwise swallow exceptions.
    /// </summary>
    public static void Warn(string source, string message, Exception? ex = null)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] WARN [{source}] {message}";
        if (ex != null)
            entry += $" | {ex.GetType().Name}: {ex.Message}";

        Debug.WriteLine(entry);
        BufferWrite(entry);
    }

    /// <summary>
    /// Logs an error-level message with exception details.
    /// </summary>
    public static void Error(string source, string message, Exception ex)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] ERROR [{source}] {message} | {ex.GetType().Name}: {ex.Message}";
        Debug.WriteLine(entry);
        BufferWrite(entry);
    }

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    public static void Info(string source, string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] INFO [{source}] {message}";
        Debug.WriteLine(entry);
        BufferWrite(entry);
    }

    /// <summary>
    /// Writes an unhandled-exception report immediately (bypassing the buffer) so the
    /// details survive even if the process terminates right afterwards.
    /// </summary>
    public static void Crash(string source, Exception ex)
    {
        Flush();

        try
        {
            Directory.CreateDirectory(LogDirectory);

            var info = new FileInfo(CrashLogPath);
            if (info.Exists && info.Length > MaxCrashLogBytes)
                File.Move(CrashLogPath, CrashLogPath + ".old", overwrite: true);

            lock (_flushLock)
            {
                File.AppendAllText(CrashLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception logEx)
        {
            Debug.WriteLine($"[AuraClean] Failed to write crash log: {logEx.Message}");
        }
    }

    /// <summary>
    /// Forces all buffered log entries to be written to disk.
    /// Call on application exit to ensure no logs are lost.
    /// </summary>
    public static void Flush()
    {
        FlushBuffer();
    }

    /// <summary>
    /// Deletes daily logs and cleanup audits older than the given age.
    /// Keeps the crash log so post-mortem information is never lost silently.
    /// </summary>
    public static void PruneOldLogs(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
                return;

            var cutoff = DateTime.Now - maxAge;
            foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log"))
            {
                if (file.Equals(CrashLogPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"[AuraClean] Could not prune log {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[AuraClean] Log pruning failed: {ex.Message}");
        }
    }

    private static void BufferWrite(string entry)
    {
        _buffer.Enqueue(entry);
        var count = Interlocked.Increment(ref _pendingCount);
        if (count >= FlushThreshold)
            FlushBuffer();
    }

    private static void FlushBuffer()
    {
        if (_buffer.IsEmpty) return;

        lock (_flushLock)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                using var writer = new StreamWriter(CurrentLogFilePath, append: true);
                while (_buffer.TryDequeue(out var entry))
                {
                    writer.WriteLine(entry);
                    Interlocked.Decrement(ref _pendingCount);
                }
            }
            catch
            {
                // Last resort — can't write logs. Clear buffer to prevent memory growth.
                while (_buffer.TryDequeue(out _))
                    Interlocked.Decrement(ref _pendingCount);
            }
        }
    }
}
