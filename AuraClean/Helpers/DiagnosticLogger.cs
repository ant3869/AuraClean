using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace AuraClean.Helpers;

/// <summary>
/// Lightweight diagnostic logger for AuraClean.
/// Writes warnings/errors to Debug output and to a daily log file.
/// Uses a buffered write queue to reduce disk I/O under heavy logging load.
/// Thread-safe for use from multiple async operations.
/// </summary>
public static class DiagnosticLogger
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AuraClean", "Logs");

    private static readonly string LogFilePath = Path.Combine(
        LogDirectory, $"AuraClean_{DateTime.Now:yyyy-MM-dd}.log");

    private static readonly ConcurrentQueue<string> _buffer = new();
    private static readonly object _flushLock = new();
    private static int _pendingCount;
    private const int FlushThreshold = 5;

    /// <summary>
    /// Logs a warning-level message with optional exception details.
    /// Used to replace bare <c>catch { }</c> blocks.
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
    }

    /// <summary>
    /// Forces all buffered log entries to be written to disk.
    /// Call on application exit to ensure no logs are lost.
    /// </summary>
    public static void Flush()
    {
        FlushBuffer();
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
                using var writer = new StreamWriter(LogFilePath, append: true);
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
