using System.IO;
using System.Runtime.InteropServices;

namespace AuraClean.Services;

/// <summary>
/// Detects file locks using the Windows Restart Manager API.
/// Identifies which processes hold locks on specific files.
/// </summary>
public static class FileLockDetector
{
    #region Restart Manager P/Invoke

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle,
        uint nFiles, string[] rgsFileNames,
        uint nApplications, [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle,
        out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    private const int ERROR_MORE_DATA = 234;

    #endregion

    /// <summary>
    /// Checks whether a file is currently locked by any process.
    /// Falls back to a simple File.Open test if the Restart Manager call fails.
    /// </summary>
    public static bool IsLocked(string filePath)
    {
        try
        {
            var processes = GetLockingProcesses(filePath);
            return processes.Count > 0;
        }
        catch
        {
            // Fallback: try-open approach
            return IsLockedSimple(filePath);
        }
    }

    /// <summary>
    /// Returns a list of process names that currently hold a lock on the given file.
    /// </summary>
    public static List<string> GetLockingProcesses(string filePath)
    {
        var result = new List<string>();

        int status = RmStartSession(out uint sessionHandle, 0,
            Guid.NewGuid().ToString("N")[..32]);
        if (status != 0) return result;

        try
        {
            string[] resources = [filePath];
            status = RmRegisterResources(sessionHandle,
                (uint)resources.Length, resources,
                0, null, 0, null);

            if (status != 0) return result;

            uint pnProcInfo = 0;
            uint rebootReasons = 0;

            // First call to determine buffer size
            status = RmGetList(sessionHandle, out uint pnProcInfoNeeded,
                ref pnProcInfo, null, ref rebootReasons);

            if (status == ERROR_MORE_DATA && pnProcInfoNeeded > 0)
            {
                var processInfo = new RM_PROCESS_INFO[pnProcInfoNeeded];
                pnProcInfo = pnProcInfoNeeded;

                status = RmGetList(sessionHandle, out pnProcInfoNeeded,
                    ref pnProcInfo, processInfo, ref rebootReasons);

                if (status == 0)
                {
                    for (int i = 0; i < pnProcInfo; i++)
                    {
                        string name = processInfo[i].strAppName;
                        if (!string.IsNullOrEmpty(name))
                            result.Add(name);
                    }
                }
            }
        }
        finally
        {
            RmEndSession(sessionHandle);
        }

        return result;
    }

    /// <summary>
    /// Simple fallback: attempts to open the file exclusively to test if it's locked.
    /// </summary>
    private static bool IsLockedSimple(string filePath)
    {
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // Access denied is not the same as locked
        }
    }
}
