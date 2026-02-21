using System.Management;

namespace AuraClean.Services;

/// <summary>
/// Creates System Restore Points via WMI before destructive operations.
/// Requires administrator privileges and that System Restore is enabled.
/// </summary>
public static class RestorePointService
{
    /// <summary>
    /// Creates a system restore point with the given description.
    /// </summary>
    /// <param name="description">Description for the restore point.</param>
    /// <returns>True if the restore point was created successfully.</returns>
    public static async Task<(bool Success, string Message)> CreateRestorePointAsync(
        string description = "AuraClean Pre-Cleanup")
    {
        return await Task.Run(() =>
        {
            try
            {
                // Check if System Restore is enabled
                if (!IsSystemRestoreEnabled())
                {
                    return (false, "System Restore is disabled on this machine. " +
                                   "Enable it in System Properties → System Protection.");
                }

                // Allow frequent restore point creation (override 24-hour default)
                AllowFrequentRestorePoints();

                var scope = new ManagementScope(@"\\.\root\default");
                scope.Connect();

                using var restoreClass = new ManagementClass(scope,
                    new ManagementPath("SystemRestore"), new ObjectGetOptions());

                using var inParams = restoreClass.GetMethodParameters("CreateRestorePoint");
                inParams["Description"] = description;
                inParams["RestorePointType"] = 12;  // MODIFY_SETTINGS
                inParams["EventType"] = 100;         // BEGIN_SYSTEM_CHANGE

                using var outParams = restoreClass.InvokeMethod("CreateRestorePoint", inParams, null);

                var returnValue = (uint)(outParams?["ReturnValue"] ?? 1u);
                if (returnValue == 0)
                {
                    return (true, $"Restore point '{description}' created successfully.");
                }

                return (false, $"Failed to create restore point. Return code: {returnValue}");
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
    }

    /// <summary>
    /// Checks whether System Restore is enabled on the system drive.
    /// </summary>
    private static bool IsSystemRestoreEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            if (key == null) return false;

            var value = key.GetValue("RPSessionInterval");
            // If RPSessionInterval is 0, System Restore is disabled
            return value is not int intVal || intVal != 0;
        }
        catch
        {
            return true; // Assume enabled if we can't check
        }
    }

    /// <summary>
    /// Sets the registry value to allow creating restore points more frequently than every 24 hours.
    /// </summary>
    private static void AllowFrequentRestorePoints()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            key?.SetValue("SystemRestorePointCreationFrequency", 0,
                Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch
        {
            // Non-critical — just means we might hit the 24-hour limit
        }
    }
}
