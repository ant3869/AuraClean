using AuraClean.Helpers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AuraClean.Services;

/// <summary>
/// "Force Delete" service — handles forceful file deletion and program removal
/// for broken MSI installers. Uses Restart Manager API to identify locking processes
/// and provides "Terminate & Delete" or "Schedule for Boot-Time Deletion" options.
/// </summary>
public static class ForceDeleteService
{
    #region P/Invoke — MoveFileEx for Boot-Time Deletion

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    #endregion

    /// <summary>
    /// Result of a force-delete operation.
    /// </summary>
    public record ForceDeleteResult(
        bool Success,
        string Message,
        ForceDeleteAction ActionTaken,
        List<string> KilledProcesses);

    public enum ForceDeleteAction
    {
        DeletedDirectly,
        TerminatedAndDeleted,
        ScheduledForBootDeletion,
        DryRunOnly,
        Failed
    }

    /// <summary>
    /// Attempts to force-delete a file or directory, handling locks.
    /// </summary>
    /// <param name="path">File or directory to delete.</param>
    /// <param name="terminateLockers">If true, kill processes locking the file.</param>
    /// <param name="scheduleBootDelete">If true and file can't be deleted, schedule for boot-time deletion.</param>
    /// <param name="dryRun">If true, identify lockers and report what would happen.</param>
    /// <param name="progress">Progress reporter.</param>
    public static async Task<ForceDeleteResult> ForceDeleteAsync(
        string path,
        bool terminateLockers = false,
        bool scheduleBootDelete = true,
        bool dryRun = false,
        IProgress<string>? progress = null)
    {
        var killedProcesses = new List<string>();

        try
        {
            var normalized = PathSafety.Normalize(path);
            if (normalized == null)
                return new ForceDeleteResult(false, $"Invalid path: {path}", ForceDeleteAction.Failed, killedProcesses);
            path = normalized;

            bool isDirectory = Directory.Exists(path);
            bool isFile = File.Exists(path);

            if (!isDirectory && !isFile)
                return new ForceDeleteResult(false, $"Path not found: {path}", ForceDeleteAction.Failed, killedProcesses);

            // Hard safety gate: never touch OS folders, profile roots, or known folders.
            string reason;
            bool allowed = isDirectory
                ? PathSafety.IsSafeToDeleteDirectory(path, out reason)
                : PathSafety.IsSafeToDeleteFile(path, out reason);
            if (!allowed)
            {
                DiagnosticLogger.Warn("ForceDelete", $"Blocked deletion of {path}: {reason}");
                return new ForceDeleteResult(false, $"Blocked for safety: {reason}", ForceDeleteAction.Failed, killedProcesses);
            }

            if (dryRun)
            {
                var lockers = await Task.Run(() => isFile
                    ? FileLockDetector.GetLockingProcesses(path)
                    : GetAllLockingProcesses(path));

                string msg = lockers.Count > 0
                    ? $"[DRY RUN] Would need to handle {lockers.Count} locking process(es): {string.Join(", ", lockers)}"
                    : $"[DRY RUN] No locks detected. Would delete {(isDirectory ? "directory" : "file")}: {path}";

                return new ForceDeleteResult(true, msg, ForceDeleteAction.DryRunOnly, killedProcesses);
            }

            // Attempt 1: Direct delete (on background thread to avoid blocking UI)
            progress?.Report($"Attempting direct deletion of {Path.GetFileName(path)}...");
            var directDeleted = await Task.Run(() => TryDirectDelete(path, isDirectory));
            if (directDeleted)
                return new ForceDeleteResult(true, $"Deleted: {path}", ForceDeleteAction.DeletedDirectly, killedProcesses);

            // Attempt 2: Identify lockers and optionally kill them (expensive — on background thread)
            progress?.Report("File is locked. Identifying locking processes...");
            var lockingProcs = await Task.Run(() => isFile
                ? FindLockingProcessDetails(path)
                : FindAllLockingProcessDetails(path));

            if (lockingProcs.Count > 0 && terminateLockers)
            {
                progress?.Report($"Terminating {lockingProcs.Count} locking process(es)...");
                foreach (var (pid, name) in lockingProcs.Where(p => p.Pid > 0))
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        if (IsSystemCriticalProcess(proc))
                            continue;

                        var actualName = proc.ProcessName;
                        if (IsNeverKillProcess(actualName))
                        {
                            DiagnosticLogger.Warn("ForceDelete",
                                $"Refused to terminate {actualName} ({pid}): on the never-kill list (holds user data or a live database).");
                            continue;
                        }

                        proc.Kill(entireProcessTree: true);
                        proc.WaitForExit(5000);
                        killedProcesses.Add(name);
                        DiagnosticLogger.Info("ForceDelete", $"Terminated {actualName} ({pid}) to release {path}.");
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        DiagnosticLogger.Warn("ForceDelete", $"Could not terminate {name} ({pid})", ex);
                    }
                }

                await Task.Delay(500);
                var retryDeleted = await Task.Run(() => TryDirectDelete(path, isDirectory));
                if (retryDeleted)
                    return new ForceDeleteResult(true,
                        $"Terminated {killedProcesses.Count} process(es) and deleted: {path}",
                        ForceDeleteAction.TerminatedAndDeleted, killedProcesses);
            }

            // Attempt 3: Schedule for boot-time deletion
            if (scheduleBootDelete)
            {
                progress?.Report("Scheduling for boot-time deletion...");
                var bootResult = await Task.Run(() => isFile
                    ? MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT)
                    : ScheduleDirectoryForBootDeletion(path));

                if (bootResult)
                    return new ForceDeleteResult(true,
                        isFile
                            ? $"Scheduled for deletion on next reboot: {path}"
                            : $"Remaining files scheduled for deletion on next reboot: {path}",
                        ForceDeleteAction.ScheduledForBootDeletion, killedProcesses);
            }

            return new ForceDeleteResult(false,
                $"Unable to delete {path}. Close programs using it or restart Windows and try again.",
                ForceDeleteAction.Failed, killedProcesses);
        }
        catch (Exception ex)
        {
            return new ForceDeleteResult(false, $"Error: {ex.Message}", ForceDeleteAction.Failed, killedProcesses);
        }
    }

    /// <summary>
    /// Force-uninstalls a program whose own uninstaller is broken: removes its install directory
    /// (only when it passes the safety checks) and its Uninstall registry entry.
    /// Leftover files and registry keys are NOT deleted automatically — the caller should run a
    /// post-uninstall scan so the user can review them.
    /// </summary>
    public static async Task<ForceUninstallResult> ForceUninstallAsync(
        Models.InstalledProgram program,
        bool dryRun = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var result = new ForceUninstallResult { ProgramName = program.DisplayName };

        try
        {
            ct.ThrowIfCancellationRequested();

            // Step 1: Remove install directory if known and safe.
            var installDir = PathSafety.Normalize(program.InstallLocation);
            if (installDir != null && Directory.Exists(installDir))
            {
                if (!PathSafety.IsSafeToDeleteDirectory(installDir, out var reason))
                {
                    result.Warnings.Add($"Install folder skipped: {reason}");
                    DiagnosticLogger.Warn("ForceDelete", $"Install location of {program.DisplayName} rejected: {reason}");
                }
                else
                {
                    progress?.Report($"Processing install directory: {installDir}...");
                    if (dryRun)
                    {
                        result.DirectoriesIdentified.Add(installDir);
                        result.TotalSizeBytes += await Task.Run(() => UninstallerService.GetDirectorySize(installDir), ct);
                    }
                    else
                    {
                        var delResult = await ForceDeleteAsync(installDir,
                            terminateLockers: true, scheduleBootDelete: true, progress: progress);
                        if (delResult.Success) result.FilesDeleted++;
                        else result.Warnings.Add(delResult.Message);
                        result.KilledProcesses.AddRange(delResult.KilledProcesses);
                    }
                }
            }

            ct.ThrowIfCancellationRequested();

            // Step 2: Remove the Uninstall registry entry itself (with a verified backup).
            if (!string.IsNullOrWhiteSpace(program.RegistryKeyPath))
            {
                var hiveLabel = program.RegistryHive == RegistryHive.CurrentUser
                    ? "HKCU"
                    : program.RegistryView == RegistryView.Registry32 ? "HKLM (32-bit)" : "HKLM (64-bit)";
                var displayKey = $"{hiveLabel}\\{program.RegistryKeyPath}";

                progress?.Report("Removing registry uninstall entry...");
                if (dryRun)
                {
                    result.RegistryKeysIdentified.Add(displayKey);
                }
                else
                {
                    var (ok, message) = await RegistryScannerService.DeleteRegistryKeyAsync(displayKey);
                    if (ok) result.RegistryKeysRemoved++;
                    else result.Warnings.Add($"Uninstall entry not removed: {message}");
                }
            }

            result.Success = dryRun || result.FilesDeleted > 0 || result.RegistryKeysRemoved > 0;
            result.Message = dryRun
                ? $"[DRY RUN] Would remove: {result.DirectoriesIdentified.Count} folder(s), " +
                  $"{result.RegistryKeysIdentified.Count} registry key(s) ({FormatHelper.FormatBytes(result.TotalSizeBytes)})."
                : $"Force uninstall: {result.FilesDeleted} folder(s) removed, " +
                  $"{result.RegistryKeysRemoved} registry key(s) removed." +
                  (result.Warnings.Count > 0 ? $" {result.Warnings.Count} warning(s): {result.Warnings[0]}" : string.Empty);
        }
        catch (OperationCanceledException)
        {
            result.Success = false;
            result.Message = "Force uninstall cancelled.";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Force uninstall error: {ex.Message}";
        }

        return result;
    }

    #region Private Helpers

    private static bool TryDirectDelete(string path, bool isDirectory)
    {
        try
        {
            if (isDirectory)
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<(int Pid, string Name)> FindLockingProcessDetails(string filePath)
    {
        var result = new List<(int Pid, string Name)>();
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    // Check if process has the file in its modules
                    foreach (ProcessModule module in proc.Modules)
                    {
                        if (module.FileName?.Equals(filePath, StringComparison.OrdinalIgnoreCase) == true)
                        {
                            result.Add((proc.Id, proc.ProcessName));
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn("ForceDelete", $"Failed to inspect modules of PID {proc.Id} ({proc.ProcessName})", ex);
                }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ForceDelete", $"Locking-process scan failed for {filePath}", ex);
        }

        // Also use Restart Manager
        var rmLockers = FileLockDetector.GetLockingProcesses(filePath);
        // Merge without duplicates
        foreach (var name in rmLockers)
        {
            if (!result.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                result.Add((0, name));
        }

        return result;
    }

    private static List<(int Pid, string Name)> FindAllLockingProcessDetails(string directory)
    {
        var all = new List<(int Pid, string Name)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", PathSafety.RecursiveNoReparse).Take(100))
            {
                all.AddRange(FindLockingProcessDetails(file));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("ForceDelete", $"Lock scan failed for {directory}", ex);
        }
        return all.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> GetAllLockingProcesses(string directory)
    {
        return FindAllLockingProcessDetails(directory).Select(x => x.Name).Distinct().ToList();
    }

    /// <summary>
    /// Processes that must never be terminated: killing them loses unsaved user documents
    /// (Office) or risks corrupting live databases and indexes (DB/writer engines).
    /// Compared case-insensitively against Process.ProcessName (no .exe extension).
    /// </summary>
    internal static readonly HashSet<string> NeverKillProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // Office + document editors (unsaved user data).
        "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "MSACCESS", "ONENOTE", "MSPUB", "VISIO", "WINPROJ",
        // Database / index writers (corruption risk).
        "sqlservr", "mysqld", "mariadbd", "postgres", "mongod", "oracle", "db2sysc", "firebird",
        "elasticsearch", "redis-server", "influxd",
    };

    internal static bool IsNeverKillProcess(string? processName) =>
        !string.IsNullOrWhiteSpace(processName) && NeverKillProcesses.Contains(processName);

    private static bool IsSystemCriticalProcess(Process proc)
    {
        try
        {
            if (proc.Id == Environment.ProcessId || proc.Id <= 4 || proc.SessionId == 0)
                return true;

            var name = proc.ProcessName.ToLowerInvariant();
            return name is "system" or "idle" or "registry" or "csrss" or "wininit"
                or "services" or "lsass" or "svchost" or "dwm" or "fontdrvhost"
                or "winlogon" or "smss" or "explorer" or "msmpeng" or "sihost"
                or "ctfmon" or "taskhostw" or "runtimebroker";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    private static bool ScheduleDirectoryForBootDeletion(string directory)
    {
        bool allOk = true;
        try
        {
            // Never descend into junctions/symlinks: their targets are not ours to delete.
            foreach (var file in Directory.EnumerateFiles(directory, "*", PathSafety.RecursiveNoReparse))
            {
                if (!MoveFileEx(file, null, MOVEFILE_DELAY_UNTIL_REBOOT))
                    allOk = false;
            }

            // Schedule directories (leaf first); a reparse point itself is removed, not its target.
            var dirs = Directory.GetDirectories(directory, "*", PathSafety.RecursiveNoReparse)
                .OrderByDescending(d => d.Length);
            foreach (var dir in dirs)
                MoveFileEx(dir, null, MOVEFILE_DELAY_UNTIL_REBOOT);

            MoveFileEx(directory, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("ForceDelete", $"Boot-time deletion scheduling failed for {directory}", ex);
            allOk = false;
        }
        return allOk;
    }



    #endregion

    /// <summary>Result of a force uninstall operation.</summary>
    public class ForceUninstallResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string ProgramName { get; set; } = string.Empty;
        public int FilesDeleted { get; set; }
        public int RegistryKeysRemoved { get; set; }
        public long TotalSizeBytes { get; set; }
        public List<string> DirectoriesIdentified { get; set; } = [];
        public List<string> RegistryKeysIdentified { get; set; } = [];
        public List<string> KilledProcesses { get; set; } = [];
        public List<string> Warnings { get; set; } = [];
    }
}
