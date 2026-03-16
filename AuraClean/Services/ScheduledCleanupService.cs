using AuraClean.Helpers;
using System.Diagnostics;
using System.IO;

namespace AuraClean.Services;

/// <summary>
/// Manages a Windows Task Scheduler entry for automatic AuraClean cleanups.
/// Uses schtasks.exe for task creation/deletion (consistent with StartupManagerService).
/// </summary>
public static class ScheduledCleanupService
{
    private const string TaskName = "AuraClean_ScheduledCleanup";

    /// <summary>
    /// Applies the scheduled cleanup task based on current settings.
    /// Creates or removes the Windows scheduled task as needed.
    /// </summary>
    public static async Task ApplyScheduleAsync()
    {
        var settings = SettingsService.Load();

        if (!settings.ScheduledCleanupEnabled)
        {
            await RemoveScheduledTaskAsync();
            return;
        }

        await CreateScheduledTaskAsync(
            settings.ScheduledCleanupFrequency,
            settings.ScheduledCleanupTime,
            settings.ScheduledCleanupDayOfWeek);
    }

    /// <summary>
    /// Creates a Windows scheduled task that runs AuraClean with /autoclean flag.
    /// </summary>
    private static async Task CreateScheduledTaskAsync(string frequency, string time, int dayOfWeek)
    {
        // Remove existing task first
        await RemoveScheduledTaskAsync();

        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            DiagnosticLogger.Info("ScheduledCleanup", "Cannot locate AuraClean executable.");
            return;
        }

        // Build schtasks arguments based on frequency
        var schedType = frequency.ToUpperInvariant() switch
        {
            "DAILY" => "DAILY",
            "MONTHLY" => "MONTHLY",
            _ => "WEEKLY"
        };

        var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\" /autoclean\" /SC {schedType} /ST {time} /F /RL LIMITED";

        // For weekly, add the day
        if (schedType == "WEEKLY")
        {
            var dayName = dayOfWeek switch
            {
                1 => "MON",
                2 => "TUE",
                3 => "WED",
                4 => "THU",
                5 => "FRI",
                6 => "SAT",
                7 => "SUN",
                _ => "MON"
            };
            args += $" /D {dayName}";
        }

        var (success, output) = await RunSchtasksAsync(args);
        if (success)
            DiagnosticLogger.Info("ScheduledCleanup", $"Scheduled task created: {frequency} at {time}");
        else
            DiagnosticLogger.Info("ScheduledCleanup", $"Failed to create task: {output}");
    }

    /// <summary>
    /// Removes the AuraClean scheduled task if it exists.
    /// </summary>
    private static async Task RemoveScheduledTaskAsync()
    {
        var (success, _) = await RunSchtasksAsync($"/Delete /TN \"{TaskName}\" /F");
        if (success)
            DiagnosticLogger.Info("ScheduledCleanup", "Scheduled task removed.");
    }

    /// <summary>
    /// Checks whether the scheduled task currently exists.
    /// </summary>
    public static async Task<bool> IsTaskRegisteredAsync()
    {
        var (success, _) = await RunSchtasksAsync($"/Query /TN \"{TaskName}\"");
        return success;
    }

    private static async Task<(bool Success, string Output)> RunSchtasksAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Failed to start schtasks.exe");

            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            return (proc.ExitCode == 0, string.IsNullOrEmpty(error) ? output : error);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error("ScheduledCleanup", $"schtasks error: {ex.Message}", ex);
            return (false, ex.Message);
        }
    }
}
