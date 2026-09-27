using AuraClean.Helpers;
using Microsoft.Win32;
using System.Management;

namespace AuraClean.Services;

/// <summary>
/// Creates System Restore Points via WMI before destructive operations.
/// Requires administrator privileges and that System Restore is enabled.
/// </summary>
public static class RestorePointService
{
    private const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyValueName = "SystemRestorePointCreationFrequency";

    /// <summary>
    /// Creates a system restore point with the given description.
    /// </summary>
    /// <param name="description">Description for the restore point.</param>
    /// <returns>True if the restore point was created successfully.</returns>
    public static Task<(bool Success, string Message)> CreateRestorePointAsync(
        string description = "AuraClean Pre-Cleanup") => Task.Run(() =>
    {
        try
        {
            if (!IsSystemRestoreEnabled())
            {
                return (false, "System Restore is disabled on this machine. " +
                               "Enable it in System Properties → System Protection.");
            }

            // Windows refuses a second restore point within 24 hours by default. The limit is
            // lifted only for this call and the user's original setting is put back afterwards.
            var previousFrequency = AllowFrequentRestorePoints();
            try
            {
                return CreateRestorePointCore(Truncate(description, 256));
            }
            finally
            {
                RestoreFrequencySetting(previousFrequency);
            }
        }
        catch (ManagementException ex)
        {
            return (false, $"WMI error creating restore point: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Error creating restore point: {ex.Message}");
        }
    });

    private static (bool Success, string Message) CreateRestorePointCore(string description)
    {
        var scope = new ManagementScope(@"\\.\root\default");
        scope.Connect();

        using var restoreClass = new ManagementClass(scope,
            new ManagementPath("SystemRestore"), new ObjectGetOptions());

        using var inParams = restoreClass.GetMethodParameters("CreateRestorePoint");
        inParams["Description"] = description;
        inParams["RestorePointType"] = 12;  // MODIFY_SETTINGS
        inParams["EventType"] = 100;         // BEGIN_SYSTEM_CHANGE

        using var outParams = restoreClass.InvokeMethod("CreateRestorePoint", inParams, null);

        var returnValue = Convert.ToUInt32(outParams?["ReturnValue"] ?? 1u);
        return returnValue == 0
            ? (true, $"Restore point '{description}' created successfully.")
            : (false, $"Failed to create restore point. Return code: {returnValue}");
    }

    /// <summary>
    /// Checks whether System Restore is enabled on the system drive.
    /// </summary>
    private static bool IsSystemRestoreEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SystemRestoreKey);
            if (key == null) return false;

            // RPSessionInterval is 0 when System Restore is turned off.
            var value = key.GetValue("RPSessionInterval");
            return value is not int intVal || intVal != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            DiagnosticLogger.Warn("RestorePoint", "Could not read System Restore state", ex);
            return true; // Let WMI report the real state.
        }
    }

    /// <summary>
    /// Sets the creation-frequency limit to 0 and returns the previous value (null when unset).
    /// </summary>
    private static object? AllowFrequentRestorePoints()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(SystemRestoreKey);
            var previous = key?.GetValue(FrequencyValueName);
            key?.SetValue(FrequencyValueName, 0, RegistryValueKind.DWord);
            return previous;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            DiagnosticLogger.Warn("RestorePoint", "Could not lift the restore point frequency limit", ex);
            return null;
        }
    }

    private static void RestoreFrequencySetting(object? previous)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SystemRestoreKey, writable: true);
            if (key == null)
                return;

            if (previous is int previousValue)
                key.SetValue(FrequencyValueName, previousValue, RegistryValueKind.DWord);
            else
                key.DeleteValue(FrequencyValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            DiagnosticLogger.Warn("RestorePoint", "Could not restore the restore point frequency setting", ex);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
