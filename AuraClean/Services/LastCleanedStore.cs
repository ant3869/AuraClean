using AuraClean.Helpers;
using System.Globalization;
using System.IO;

namespace AuraClean.Services;

/// <summary>
/// Persists small dashboard facts (last successful cleanup, previous health score) in
/// %LocalAppData%\AuraClean so they survive restarts and are shared with scheduled runs.
/// </summary>
public static class LastCleanedStore
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuraClean");

    private static readonly string LastCleanedFile = Path.Combine(DataDir, "last_cleaned.txt");
    private static readonly string HealthScoreFile = Path.Combine(DataDir, "prev_health_score.txt");

    public static DateTime? Load()
    {
        try
        {
            if (!File.Exists(LastCleanedFile))
                return null;

            var text = File.ReadAllText(LastCleanedFile).Trim();
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ||
                DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out value))
            {
                return value > DateTime.Now ? DateTime.Now : value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("LastCleanedStore", "Failed to read last-cleaned date", ex);
        }

        return null;
    }

    public static void Save(DateTime value)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(LastCleanedFile, value.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("LastCleanedStore", "Failed to persist last-cleaned date", ex);
        }
    }

    public static int? LoadHealthScore()
    {
        try
        {
            if (File.Exists(HealthScoreFile) &&
                int.TryParse(File.ReadAllText(HealthScoreFile).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var score))
            {
                return Math.Clamp(score, 0, 100);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("LastCleanedStore", "Failed to read previous health score", ex);
        }

        return null;
    }

    public static void SaveHealthScore(int score)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(HealthScoreFile, Math.Clamp(score, 0, 100).ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("LastCleanedStore", "Failed to persist health score", ex);
        }
    }
}
