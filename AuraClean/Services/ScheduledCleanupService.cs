using AuraClean.Helpers;
using System.IO;
using System.Security.Principal;
using TaskSchedulerLib = Microsoft.Win32.TaskScheduler;

namespace AuraClean.Services;

/// <summary>
/// Manages the Windows Task Scheduler entry for automatic AuraClean cleanups.
/// The task runs with the highest privileges because AuraClean's manifest requires elevation;
/// a limited task would fail to start the executable at all.
/// </summary>
public static class ScheduledCleanupService
{
    private const string TaskName = "AuraClean_ScheduledCleanup";

    /// <summary>Command-line switch that runs a headless cleanup and exits.</summary>
    public const string AutoCleanArgument = "/autoclean";

    /// <summary>
    /// Creates, updates, or removes the scheduled task based on current settings.
    /// </summary>
    public static Task<(bool Success, string Message)> ApplyScheduleAsync()
    {
        var settings = SettingsService.Load();
        return Task.Run(() => settings.ScheduledCleanupEnabled
            ? CreateOrUpdateTask(settings)
            : RemoveTask());
    }

    /// <summary>
    /// Checks whether the scheduled task currently exists.
    /// </summary>
    public static Task<bool> IsTaskRegisteredAsync() => Task.Run(() =>
    {
        try
        {
            using var ts = new TaskSchedulerLib.TaskService();
            using var task = ts.GetTask(TaskName);
            return task != null;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ScheduledCleanup", "Failed to query scheduled task", ex);
            return false;
        }
    });

    private static (bool Success, string Message) CreateOrUpdateTask(AppSettings settings)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            DiagnosticLogger.Warn("ScheduledCleanup", "Cannot locate AuraClean executable.");
            return (false, "Could not locate the AuraClean executable.");
        }

        if (!AppSettings.TryParseScheduleTime(settings.ScheduledCleanupTime, out var timeOfDay))
            return (false, $"'{settings.ScheduledCleanupTime}' is not a valid 24-hour time (HH:mm).");

        try
        {
            using var ts = new TaskSchedulerLib.TaskService();
            var userId = WindowsIdentity.GetCurrent().Name;
            var start = DateTime.Today.Add(timeOfDay);
            if (start <= DateTime.Now)
                start = start.AddDays(1);

            var td = ts.NewTask();
            td.RegistrationInfo.Description = "Runs AuraClean's low-risk cleanup categories on a schedule.";
            td.Principal.UserId = userId;
            td.Principal.LogonType = TaskSchedulerLib.TaskLogonType.InteractiveToken;
            td.Principal.RunLevel = TaskSchedulerLib.TaskRunLevel.Highest;
            td.Triggers.Add(BuildTrigger(settings, start));
            td.Actions.Add(new TaskSchedulerLib.ExecAction(exePath, AutoCleanArgument, Path.GetDirectoryName(exePath)));
            td.Settings.StartWhenAvailable = true;
            td.Settings.DisallowStartIfOnBatteries = true;
            td.Settings.StopIfGoingOnBatteries = true;
            td.Settings.ExecutionTimeLimit = TimeSpan.FromHours(2);
            td.Settings.MultipleInstances = TaskSchedulerLib.TaskInstancesPolicy.IgnoreNew;

            ts.RootFolder.RegisterTaskDefinition(TaskName, td);

            var message = $"Scheduled cleanup set: {settings.ScheduledCleanupFrequency} at {settings.ScheduledCleanupTime}.";
            DiagnosticLogger.Info("ScheduledCleanup", message);
            return (true, message);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ScheduledCleanup", "Failed to register scheduled task", ex);
            return (false, $"Could not create the scheduled task: {ex.Message}");
        }
    }

    private static TaskSchedulerLib.Trigger BuildTrigger(AppSettings settings, DateTime start)
    {
        switch (settings.ScheduledCleanupFrequency.ToUpperInvariant())
        {
            case "DAILY":
                return new TaskSchedulerLib.DailyTrigger { StartBoundary = start, DaysInterval = 1 };

            case "MONTHLY":
                return new TaskSchedulerLib.MonthlyTrigger(1) { StartBoundary = start };

            default:
                var day = settings.ScheduledCleanupDayOfWeek switch
                {
                    2 => TaskSchedulerLib.DaysOfTheWeek.Tuesday,
                    3 => TaskSchedulerLib.DaysOfTheWeek.Wednesday,
                    4 => TaskSchedulerLib.DaysOfTheWeek.Thursday,
                    5 => TaskSchedulerLib.DaysOfTheWeek.Friday,
                    6 => TaskSchedulerLib.DaysOfTheWeek.Saturday,
                    7 => TaskSchedulerLib.DaysOfTheWeek.Sunday,
                    _ => TaskSchedulerLib.DaysOfTheWeek.Monday
                };
                return new TaskSchedulerLib.WeeklyTrigger(day) { StartBoundary = start };
        }
    }

    private static (bool Success, string Message) RemoveTask()
    {
        try
        {
            using var ts = new TaskSchedulerLib.TaskService();
            ts.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);
            return (true, "Scheduled cleanup disabled.");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ScheduledCleanup", "Failed to remove scheduled task", ex);
            return (false, $"Could not remove the scheduled task: {ex.Message}");
        }
    }
}
