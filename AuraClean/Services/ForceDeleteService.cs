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
            bool isDirectory = Directory.Exists(path);
            bool isFile = File.Exists(path);

            if (!isDirectory && !isFile)
                return new ForceDeleteResult(false, $"Path not found: {path}", ForceDeleteAction.Failed, killedProcesses);

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
                foreach (var (pid, name) in lockingProcs)
                {
                    try
                    {
                        var proc = Process.GetProcessById(pid);
                        if (!IsSystemCriticalProcess(proc))
                        {
                            proc.Kill();
                            proc.WaitForExit(5000);
                            killedProcesses.Add(name);
                        }
                    }
                    catch { }
                }

                // Retry delete after killing
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
                var bootResult = await Task.Run(() =>
                {
                    if (isFile)
                    {
                        return MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);
                    }
                    else
                    {
                        return ScheduleDirectoryForBootDeletion(path);
                    }
                });

                if (bootResult)
                    return new ForceDeleteResult(true,
                        isFile
                            ? $"Scheduled for deletion on next reboot: {path}"
                            : $"Entire directory scheduled for boot-time deletion: {path}",
                        ForceDeleteAction.ScheduledForBootDeletion, killedProcesses);
            }

            return new ForceDeleteResult(false,
                $"Unable to delete {path}. Try running as Administrator or reboot first.",
                ForceDeleteAction.Failed, killedProcesses);
        }
        catch (Exception ex)
        {
            return new ForceDeleteResult(false, $"Error: {ex.Message}", ForceDeleteAction.Failed, killedProcesses);
        }
    }

    /// <summary>
    /// Force-uninstalls a program by removing its registry entries, files, and directories.
    /// For programs with broken/missing MSI installers.
    /// </summary>
    /// <param name="program">The program to force-uninstall.</param>
    /// <param name="dryRun">If true, just reports what would be removed.</param>
    /// <param name="progress">Progress reporter.</param>
    public static async Task<ForceUninstallResult> ForceUninstallAsync(
        Models.InstalledProgram program,
        bool dryRun = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var result = new ForceUninstallResult { ProgramName = program.DisplayName };

        try
        {
            // Step 1: Remove install directory if known
            if (!string.IsNullOrWhiteSpace(program.InstallLocation) && Directory.Exists(program.InstallLocation))
            {
                progress?.Report($"Processing install directory: {program.InstallLocation}...");
                if (dryRun)
                {
                    result.DirectoriesIdentified.Add(program.InstallLocation);
                    result.TotalSizeBytes += await Task.Run(() =>
                        UninstallerService.GetDirectorySize(program.InstallLocation));
                }
                else
                {
                    var delResult = await ForceDeleteAsync(program.InstallLocation,
                        terminateLockers: true, scheduleBootDelete: true, progress: progress);
                    result.FilesDeleted += delResult.Success ? 1 : 0;
                    result.KilledProcesses.AddRange(delResult.KilledProcesses);
                }
            }

            // Step 2: Scan for and remove remnant directories
            progress?.Report("Scanning for remnant files...");
            var remnants = await UninstallerService.PostUninstallScanAsync(program, progress, ct);
            foreach (var remnant in remnants)
            {
                ct.ThrowIfCancellationRequested();
                if (remnant.Type == Models.JunkType.OrphanedRegistryKey)
                {
                    if (dryRun)
                    {
                        result.RegistryKeysIdentified.Add(remnant.Path);
                    }
                    else
                    {
                        var (ok, _) = await RegistryScannerService.DeleteRegistryKeyAsync(remnant.Path);
                        if (ok) result.RegistryKeysRemoved++;
                    }
                }
                else
                {
                    if (dryRun)
                    {
                        result.DirectoriesIdentified.Add(remnant.Path);
                        result.TotalSizeBytes += remnant.SizeBytes;
                    }
                    else
                    {
                        var delResult = await ForceDeleteAsync(remnant.Path,
                            terminateLockers: true, scheduleBootDelete: true, progress: progress);
                        if (delResult.Success) result.FilesDeleted++;
                    }
                }
            }

            // Step 3: Remove the Uninstall registry entry itself
            if (!string.IsNullOrWhiteSpace(program.RegistryKeyPath))
            {
                progress?.Report("Removing registry uninstall entry...");
                if (dryRun)
                {
                    result.RegistryKeysIdentified.Add(program.RegistryKeyPath);
                }
                else
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(
                            program.RegistryKeyPath.StartsWith("SOFTWARE\\Microsoft")
                                ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                            program.RegistryView);
                        var parentPath = Path.GetDirectoryName(program.RegistryKeyPath)?.Replace('/', '\\');
                        var keyName = Path.GetFileName(program.RegistryKeyPath);
                        if (parentPath != null && keyName != null)
                        {
                            using var parentKey = baseKey.OpenSubKey(parentPath, writable: true);
                            parentKey?.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
                            result.RegistryKeysRemoved++;
                        }
                    }
                    catch { }
                }
            }

            result.Success = true;
            result.Message = dryRun
                ? $"[DRY RUN] Would remove: {result.DirectoriesIdentified.Count} dirs, " +
                  $"{result.RegistryKeysIdentified.Count} registry keys ({FormatHelper.FormatBytes(result.TotalSizeBytes)})"
                : $"Force uninstalled: {result.FilesDeleted} items deleted, " +
                  $"{result.RegistryKeysRemoved} registry keys removed.";
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
                catch { }
                finally { proc.Dispose(); }
            }
        }
        catch { }

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
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Take(100))
            {
                all.AddRange(FindLockingProcessDetails(file));
            }
        }
        catch { }
        return all.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> GetAllLockingProcesses(string directory)
    {
        return FindAllLockingProcessDetails(directory).Select(x => x.Name).Distinct().ToList();
    }

    private static bool IsSystemCriticalProcess(Process proc)
    {
        try
        {
            var name = proc.ProcessName.ToLowerInvariant();
            return name is "system" or "idle" or "csrss" or "wininit"
                or "services" or "lsass" or "svchost" or "dwm"
                or "winlogon" or "smss" or "explorer";
        }
        catch { return true; }
    }

    private static bool ScheduleDirectoryForBootDeletion(string directory)
    {
        bool allOk = true;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (!MoveFileEx(file, null, MOVEFILE_DELAY_UNTIL_REBOOT))
                    allOk = false;
            }

            // Schedule directories (leaf first)
            var dirs = Directory.GetDirectories(directory, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length);
            foreach (var dir in dirs)
            {
                MoveFileEx(dir, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            }

            MoveFileEx(directory, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        }
        catch
        {
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
    }
}
