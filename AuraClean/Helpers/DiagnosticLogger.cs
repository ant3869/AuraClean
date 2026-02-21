using System.Diagnostics;
using System.IO;

namespace AuraClean.Helpers;

/// <summary>
/// Lightweight diagnostic logger for AuraClean.
/// Writes warnings/errors to Debug output and optionally to a log file.
/// Replaces bare <c>catch { }</c> blocks with observable diagnostics.
/// </summary>
public static class DiagnosticLogger
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AuraClean", "Logs");

    private static readonly string LogFilePath = Path.Combine(
        LogDirectory, $"AuraClean_{DateTime.Now:yyyy-MM-dd}.log");

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
        WriteToFile(entry);
    }

    /// <summary>
    /// Logs an error-level message with exception details.
    /// </summary>
    public static void Error(string source, string message, Exception ex)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] ERROR [{source}] {message} | {ex.GetType().Name}: {ex.Message}";
        Debug.WriteLine(entry);
        WriteToFile(entry);
    }

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    public static void Info(string source, string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] INFO [{source}] {message}";
        Debug.WriteLine(entry);
    }

    private static void WriteToFile(string entry)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogFilePath, entry + Environment.NewLine);
        }
        catch
        {
            // Last resort — can't even write logs. Swallow silently.
        }
    }
}
