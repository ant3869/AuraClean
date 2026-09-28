using AuraClean.Helpers;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AuraClean.Services;

/// <summary>
/// Advanced Memory Optimizer — "One-Click Boost" RAM cleaner.
/// Uses EmptyWorkingSet via psapi.dll to flush the System Working Set
/// and the NtSetSystemInformation for Standby List purge.
/// </summary>
public static class MemoryManagerService
{
    #region P/Invoke Declarations

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;
    private const string SeProfileSingleProcessPrivilege = "SeProfileSingleProcessPrivilege";

    // Access rights
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_SET_QUOTA = 0x0100;

    // NtSetSystemInformation classes
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;

    #endregion

    /// <summary>
    /// Result snapshot from a memory boost operation.
    /// </summary>
    public record BoostResult(
        long MemoryFreedBytes,
        int ProcessesTrimmed,
        int ProcessesSkipped,
        bool StandbyListPurged,
        long WorkingSetBefore,
        long WorkingSetAfter);

    /// <summary>
    /// Performs a "One-Click Boost": trims working sets of all user processes
    /// and optionally purges the Standby List.
    /// </summary>
    /// <param name="purgeStandbyList">Whether to purge the standby list (requires admin).</param>
    /// <param name="dryRun">If true, calculates potential savings without actually trimming.</param>
    /// <param name="progress">Progress reporter.</param>
    
    /// <param name="ct">Cancellation token.</param>
    public static async Task<BoostResult> BoostMemoryAsync(
        bool purgeStandbyList = true,
        bool dryRun = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            long totalWorkingSetBefore = 0;
            long totalWorkingSetAfter = 0;
            int trimmed = 0;
            int skipped = 0;
            bool standbyPurged = false;

            var processes = Process.GetProcesses();
            try
            {
            progress?.Report($"Analyzing {processes.Length} processes...");

            // Calculate pre-boost working set
            foreach (var proc in processes)
            {
                try
                {
                    totalWorkingSetBefore += proc.WorkingSet64;
                }
                catch (Exception ex) { DiagnosticLogger.Warn("MemoryManager", $"Failed to read WorkingSet64 for PID {proc.Id}", ex); }
            }

            if (!dryRun)
            {
                // Phase 1: Trim working sets of all accessible processes
                progress?.Report("Trimming process working sets...");
                foreach (var proc in processes)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        // Skip critical system processes
                        if (IsProtectedProcess(proc)) { skipped++; continue; }

                        var handle = OpenProcess(
                            PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA,
                            false, proc.Id);

                        if (handle == IntPtr.Zero) { skipped++; continue; }

                        try
                        {
                            if (EmptyWorkingSet(handle))
                                trimmed++;
                            else
                                skipped++;
                        }
                        finally
                        {
                            CloseHandle(handle);
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Warn("MemoryManager", $"Failed to trim process {proc.Id}", ex);
                        skipped++;
                    }
                }

                // Phase 2: Purge Standby List (requires admin/SeProfileSingleProcessPrivilege)
                if (purgeStandbyList)
                {
                    progress?.Report("Purging standby memory list...");
                    try
                    {
                        // The purge is refused with STATUS_PRIVILEGE_NOT_HELD unless this
                        // privilege is explicitly enabled on the (elevated) process token.
                        if (TryEnablePrivilege(SeProfileSingleProcessPrivilege))
                        {
                            int command = MemoryPurgeStandbyList;
                            int result = NtSetSystemInformation(
                                SystemMemoryListInformation, ref command, sizeof(int));
                            standbyPurged = result >= 0; // NT_SUCCESS
                            if (!standbyPurged)
                                DiagnosticLogger.Warn("MemoryManager", $"Standby purge returned NTSTATUS 0x{result:X8}");
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Warn("MemoryManager", "Standby list purge failed", ex);
                        standbyPurged = false;
                    }
                }

                // Allow a moment for OS to reclaim
                Thread.Sleep(500);

                // Re-measure
                progress?.Report("Measuring results...");
                var postProcesses = Process.GetProcesses();
                foreach (var proc in postProcesses)
                {
                    try { totalWorkingSetAfter += proc.WorkingSet64; }
                    catch (Exception ex) { DiagnosticLogger.Warn("MemoryManager", $"Failed to read post-boost WorkingSet64", ex); }
                    finally { proc.Dispose(); }
                }
            }
            else
            {
                // Dry-run: estimate ~30% of non-protected working set could be freed
                long reclaimable = 0;
                foreach (var proc in processes)
                {
                    try
                    {
                        if (!IsProtectedProcess(proc))
                            reclaimable += (long)(proc.WorkingSet64 * 0.3);
                    }
                    catch (Exception ex) { DiagnosticLogger.Warn("MemoryManager", "Failed to estimate reclaimable memory", ex); }
                }

                totalWorkingSetAfter = totalWorkingSetBefore - reclaimable;
                trimmed = processes.Length;
                standbyPurged = purgeStandbyList;
            }

            }
            finally
            {
                // Dispose process snapshots — guaranteed cleanup even if exception occurs mid-loop
                foreach (var proc in processes)
                {
                    try { proc.Dispose(); } catch { /* Disposal failure is truly ignorable */ }
                }
            }

            long freed = totalWorkingSetBefore - totalWorkingSetAfter;
            if (freed < 0) freed = 0;

            return new BoostResult(
                freed, trimmed, skipped, standbyPurged,
                totalWorkingSetBefore, totalWorkingSetAfter);
        }, ct);
    }

    /// <summary>
    /// Gets current system memory statistics on a background thread.
    /// </summary>
    public static Task<MemorySnapshot> GetMemorySnapshotAsync() => Task.Run(GetMemorySnapshot);

    /// <summary>
    /// Gets current physical memory statistics. Returns zeros if the query fails.
    /// </summary>
    public static MemorySnapshot GetMemorySnapshot()
    {
        var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref memStatus))
        {
            DiagnosticLogger.Warn("MemoryManager", $"GlobalMemoryStatusEx failed (error {Marshal.GetLastWin32Error()})");
            return new MemorySnapshot(0, 0, 0, 0);
        }

        long totalPhysical = (long)memStatus.ullTotalPhys;
        long availablePhysical = (long)memStatus.ullAvailPhys;
        long usedPhysical = totalPhysical - availablePhysical;

        return new MemorySnapshot(
            TotalPhysicalBytes: totalPhysical,
            UsedBytes: usedPhysical,
            AvailableBytes: availablePhysical,
            UsagePercent: totalPhysical > 0
                ? (double)usedPhysical / totalPhysical * 100.0 : 0);
    }

    /// <summary>Enables a privilege on the current process token. Returns true on success.</summary>
    private static bool TryEnablePrivilege(string privilegeName)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            DiagnosticLogger.Warn("MemoryManager", $"OpenProcessToken failed (error {Marshal.GetLastWin32Error()})");
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
                return false;

            var privileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            };

            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                return false;

            // AdjustTokenPrivileges "succeeds" even when the privilege is not held.
            if (Marshal.GetLastWin32Error() == ERROR_NOT_ALL_ASSIGNED)
            {
                DiagnosticLogger.Warn("MemoryManager", $"{privilegeName} is not held by this account.");
                return false;
            }

            return true;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    /// <summary>
    /// Determines if a process should not be trimmed (system-critical processes).
    /// </summary>
    private static bool IsProtectedProcess(Process proc)
    {
        try
        {
            var name = proc.ProcessName.ToLowerInvariant();
            return name is "system" or "idle" or "registry" or "smss"
                or "csrss" or "wininit" or "services" or "lsass"
                or "svchost" or "dwm" or "explorer" or "winlogon"
                or "fontdrvhost" or "auraclean";
        }
        catch
        {
            return true; // If we can't read the name, skip it
        }
    }

    /// <summary>Snapshot of system memory state.</summary>
    public record MemorySnapshot(
        long TotalPhysicalBytes,
        long UsedBytes,
        long AvailableBytes,
        double UsagePercent);
}
