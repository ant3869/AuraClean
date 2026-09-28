using AuraClean.Helpers;
using Microsoft.Win32;
using System.IO;
using System.Security.Principal;
using TaskSchedulerLib = Microsoft.Win32.TaskScheduler;

namespace AuraClean.Services;

/// <summary>
/// Registers AuraClean to start when the current user signs in.
/// AuraClean's manifest requires elevation, and Windows silently refuses to launch elevated
/// programs from the Run key, so a logon-triggered scheduled task with the highest run level
/// is used instead.
/// </summary>
public static class LaunchAtLogonService
{
    private const string TaskName = "AuraClean_LaunchAtLogon";
    private const string LegacyRunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValueName = "AuraClean";

    /// <summary>Command-line switch passed when AuraClean is launched at sign-in.</summary>
    public const string MinimizedArgument = "--minimized";

    private static readonly object _lock = new();

    /// <summary>
    /// Creates or removes the logon task so it matches <paramref name="enable"/>.
    /// Never throws; failures are logged.
    /// </summary>
    public static void Apply(bool enable)
    {
        lock (_lock)
        {
            RemoveLegacyRunEntry();

            try
            {
                using var ts = new TaskSchedulerLib.TaskService();
                var existing = ts.GetTask(TaskName);

                if (!enable)
                {
                    if (existing != null)
                        ts.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);
                    existing?.Dispose();
                    return;
                }

                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                {
                    existing?.Dispose();
                    DiagnosticLogger.Warn("LaunchAtLogon", "Cannot locate the AuraClean executable; logon task not created.");
                    return;
                }

                if (existing != null && IsUpToDate(existing, exePath))
                {
                    existing.Dispose();
                    return;
                }

                existing?.Dispose();

                var userId = WindowsIdentity.GetCurrent().Name;
                var td = ts.NewTask();
                td.RegistrationInfo.Description = "Starts AuraClean minimized when you sign in.";
                td.Principal.UserId = userId;
                td.Principal.LogonType = TaskSchedulerLib.TaskLogonType.InteractiveToken;
                td.Principal.RunLevel = TaskSchedulerLib.TaskRunLevel.Highest;
                td.Triggers.Add(new TaskSchedulerLib.LogonTrigger
                {
                    UserId = userId,
                    Delay = TimeSpan.FromSeconds(30)
                });
                td.Actions.Add(new TaskSchedulerLib.ExecAction(exePath, MinimizedArgument, Path.GetDirectoryName(exePath)));
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.ExecutionTimeLimit = TimeSpan.Zero;
                td.Settings.MultipleInstances = TaskSchedulerLib.TaskInstancesPolicy.IgnoreNew;

                ts.RootFolder.RegisterTaskDefinition(TaskName, td);
                DiagnosticLogger.Info("LaunchAtLogon", "Logon task registered.");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("LaunchAtLogon", $"Failed to {(enable ? "register" : "remove")} logon task", ex);
            }
        }
    }

    private static bool IsUpToDate(TaskSchedulerLib.Task task, string exePath)
    {
        try
        {
            return task.Enabled &&
                   task.Definition.Principal.RunLevel == TaskSchedulerLib.TaskRunLevel.Highest &&
                   task.Definition.Actions.OfType<TaskSchedulerLib.ExecAction>().Any(a =>
                       string.Equals(a.Path, exePath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("LaunchAtLogon", "Could not inspect existing logon task", ex);
            return false;
        }
    }

    /// <summary>
    /// Earlier versions wrote a Run-key entry that Windows never honored for an elevated app.
    /// </summary>
    private static void RemoveLegacyRunEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: true);
            if (key?.GetValue(LegacyRunValueName) != null)
                key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("LaunchAtLogon", "Failed to remove legacy Run entry", ex);
        }
    }
}
