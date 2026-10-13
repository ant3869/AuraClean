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
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore";
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
            if (IsSystemRestoreDisabledByPolicy())
            {
                return (false, "System Restore is disabled by Group Policy on this machine.");
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
            : (false, $"Failed to create restore point (code {returnValue}). This usually means System " +
                      "Restore is turned off for this drive — check System Properties → System Protection.");
    }

    /// <summary>
    /// Checks the one System Restore "off" signal that is actually documented and certain: the
    /// Group Policy "Turn off System Restore" setting. There is no reliable single registry flag
    /// for the common end-user case (System Protection toggled off per-drive in Control Panel) —
    /// that state lives in VSS/COM, not the registry — so this deliberately does not try to guess
    /// it; the real WMI creation call below reports that case on its own via its return code.
    /// </summary>
    internal static bool IsSystemRestoreDisabledByPolicy()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyKey);
            if (key == null) return false;

            bool Flag(string name) => key.GetValue(name) is int v && v != 0;
            return Flag("DisableSR") || Flag("DisableConfig");
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            DiagnosticLogger.Warn("RestorePoint", "Could not read System Restore policy state", ex);
            return false; // Unknown — let the real WMI call decide.
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
