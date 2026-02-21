namespace AuraClean.Helpers;

/// <summary>
/// Shared formatting utilities used across the application.
/// Eliminates the FormatBytes duplication in every service and ViewModel.
/// </summary>
public static class FormatHelper
{
    /// <summary>
    /// Converts a byte count to a human-readable string (e.g., "2.3 GB").
    /// </summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        < 0 => "0 B",
        0 => "0 B",
        < 1024 => $"{bytes} B",
        < 1_048_576 => $"{bytes / 1024.0:F1} KB",
        < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
        < 1_099_511_627_776 => $"{bytes / 1_073_741_824.0:F2} GB",
        _ => $"{bytes / 1_099_511_627_776.0:F2} TB"
    };

    /// <summary>
    /// Formats a timespan as a human-readable duration (e.g., "2d 5h 32m").
    /// </summary>
    public static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1)
            return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    /// <summary>
    /// Formats a count with K/M suffix for large numbers (e.g., "1.2K", "3.4M").
    /// </summary>
    public static string FormatCount(long count) => count switch
    {
        < 1000 => count.ToString("N0"),
        < 1_000_000 => $"{count / 1000.0:F1}K",
        _ => $"{count / 1_000_000.0:F1}M"
    };
}
