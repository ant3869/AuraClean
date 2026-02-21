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

            // Snapshot current RAM usage
            var memBefore = GC.GetGCMemoryInfo();
            var processes = Process.GetProcesses();
            progress?.Report($"Analyzing {processes.Length} processes...");

            // Calculate pre-boost working set
            foreach (var proc in processes)
            {
                try
                {
                    totalWorkingSetBefore += proc.WorkingSet64;
                }
                catch { }
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
                    catch
                    {
                        skipped++;
                    }
                }

                // Phase 2: Purge Standby List (requires admin/SeProfileSingleProcessPrivilege)
                if (purgeStandbyList)
                {
                    progress?.Report("Purging standby memory list...");
                    try
                    {
                        int command = MemoryPurgeStandbyList;
                        int result = NtSetSystemInformation(
                            SystemMemoryListInformation, ref command, sizeof(int));
                        standbyPurged = (result >= 0); // NT_SUCCESS
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
                    catch { }
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
                    catch { }
                }

                totalWorkingSetAfter = totalWorkingSetBefore - reclaimable;
                trimmed = processes.Length;
                standbyPurged = purgeStandbyList;
            }

            // Dispose process snapshots
            foreach (var proc in processes)
            {
                try { proc.Dispose(); } catch { }
            }

            long freed = totalWorkingSetBefore - totalWorkingSetAfter;
            if (freed < 0) freed = 0;

            return new BoostResult(
                freed, trimmed, skipped, standbyPurged,
                totalWorkingSetBefore, totalWorkingSetAfter);
        }, ct);
    }

    /// <summary>
    /// Gets current system memory statistics (async — enumerates all processes on a background thread).
    /// </summary>
    public static async Task<MemorySnapshot> GetMemorySnapshotAsync()
    {
        return await Task.Run(() =>
        {
            var gcInfo = GC.GetGCMemoryInfo();
            long totalPhysical = gcInfo.TotalAvailableMemoryBytes;

            long usedByProcesses = 0;
            foreach (var proc in Process.GetProcesses())
            {
                try { usedByProcesses += proc.WorkingSet64; }
                catch { }
                finally { proc.Dispose(); }
            }

            return new MemorySnapshot(
                TotalPhysicalBytes: totalPhysical,
                UsedBytes: usedByProcesses,
                AvailableBytes: totalPhysical - usedByProcesses,
                UsagePercent: totalPhysical > 0
                    ? (double)usedByProcesses / totalPhysical * 100.0 : 0);
        });
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
