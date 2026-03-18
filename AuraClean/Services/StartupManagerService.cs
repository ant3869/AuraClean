using AuraClean.Helpers;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Management;

namespace AuraClean.Services;

/// <summary>
/// Manages Windows startup programs — enumerate, enable/disable, and measure boot impact.
/// Reads from Registry Run keys, shell:startup folder, and Task Scheduler autostart tasks.
/// </summary>
public static partial class StartupManagerService
{
    /// <summary>
    /// Represents a single startup entry from any source.
    /// </summary>
    public partial class StartupEntry : ObservableObject
    {
        [ObservableProperty] private string _name = string.Empty;
        [ObservableProperty] private string _command = string.Empty;
        [ObservableProperty] private string _location = string.Empty;
        [ObservableProperty] private string _publisher = string.Empty;
        [ObservableProperty] private bool _isEnabled = true;
        [ObservableProperty] private StartupSource _source;
        [ObservableProperty] private StartupImpact _impact = StartupImpact.Unknown;
        [ObservableProperty] private string _filePath = string.Empty;
        [ObservableProperty] private long _fileSizeBytes;
        [ObservableProperty] private bool _isSelected;

        public string FormattedSize => FormatHelper.FormatBytes(FileSizeBytes);
        public string ImpactLabel => Impact switch
        {
            StartupImpact.High => "High",
            StartupImpact.Medium => "Medium",
            StartupImpact.Low => "Low",
            StartupImpact.None => "None",
            _ => "Unknown"
        };

        /// <summary>The registry key or folder from which this entry was read.</summary>
        public string RegistryPath { get; set; } = string.Empty;
        /// <summary>The registry value name (for registry entries).</summary>
        public string RegistryValueName { get; set; } = string.Empty;
        /// <summary>Registry hive for toggling.</summary>
        public RegistryHive RegistryHive { get; set; }
        /// <summary>Registry view for toggling.</summary>
        public RegistryView RegistryView { get; set; } = RegistryView.Default;
    }

    public enum StartupSource
    {
        RegistryCurrentUser,
        RegistryLocalMachine,
        StartupFolder,
        TaskScheduler
    }

    public enum StartupImpact
    {
        Unknown,
        None,
        Low,
        Medium,
        High
    }

    /// <summary>Registry paths containing Run/RunOnce entries.</summary>
    private static readonly (RegistryHive Hive, RegistryView View, string SubKey, string Label, StartupSource Source)[] RegistryRunPaths =
    [
        (RegistryHive.CurrentUser, RegistryView.Default, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            "HKCU\\Run", StartupSource.RegistryCurrentUser),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            "HKLM\\Run (64-bit)", StartupSource.RegistryLocalMachine),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            "HKLM\\Run (32-bit)", StartupSource.RegistryLocalMachine),
    ];

    /// <summary>Disabled entries are stored in these keys (moved by Windows).</summary>
    private static readonly string DisabledRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private static readonly string DisabledRunKey32 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

    /// <summary>
    /// Enumerates all Windows startup entries from registry, startup folder, and task scheduler.
    /// </summary>
    public static async Task<List<StartupEntry>> GetStartupEntriesAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var entries = new List<StartupEntry>();

        await Task.Run(() =>
        {
            // 1. Registry Run keys
            progress?.Report("Scanning registry Run keys...");
            foreach (var (hive, view, subKey, label, source) in RegistryRunPaths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var runKey = baseKey.OpenSubKey(subKey);
                    if (runKey == null) continue;

                    foreach (var valueName in runKey.GetValueNames())
                    {
                        var command = runKey.GetValue(valueName) as string;
                        if (string.IsNullOrWhiteSpace(command)) continue;

                        var filePath = ExtractFilePath(command);
                        var entry = new StartupEntry
                        {
                            Name = valueName,
                            Command = command,
                            Location = label,
                            FilePath = filePath,
                            Source = source,
                            IsEnabled = true,
                            RegistryPath = subKey,
                            RegistryValueName = valueName,
                            RegistryHive = hive,
                            RegistryView = view,
                            Publisher = GetFilePublisher(filePath),
                            FileSizeBytes = GetFileSize(filePath),
                            Impact = EstimateImpact(filePath),
                        };

                        // Check if disabled via StartupApproved
                        entry.IsEnabled = !IsDisabledViaStartupApproved(valueName, hive);

                        entries.Add(entry);
                    }
                }
                catch (System.Security.SecurityException) { }
                catch (UnauthorizedAccessException) { }
            }

            // 2. Startup folder
            progress?.Report("Scanning startup folders...");
            ScanStartupFolder(entries,
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "User Startup Folder");
            ScanStartupFolder(entries,
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                "All Users Startup Folder");

            // 3. Task Scheduler (boot/login triggers)
            progress?.Report("Scanning scheduled tasks...");
            ScanScheduledTasks(entries, ct);

        }, ct);

        return entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Enables or disables a startup entry.
    /// For registry entries, uses the StartupApproved mechanism.
    /// For startup folder entries, renames the shortcut.
    /// </summary>
    public static async Task<(bool Success, string Message)> ToggleStartupEntryAsync(
        StartupEntry entry, bool enable)
    {
        return await Task.Run(() =>
        {
            try
            {
                switch (entry.Source)
                {
                    case StartupSource.RegistryCurrentUser:
                    case StartupSource.RegistryLocalMachine:
                        return ToggleRegistryEntry(entry, enable);

                    case StartupSource.StartupFolder:
                        return ToggleStartupFolderEntry(entry, enable);

                    case StartupSource.TaskScheduler:
                        return ToggleScheduledTask(entry, enable);

                    default:
                        return (false, "Unknown startup source.");
                }
            }
            catch (UnauthorizedAccessException)
            {
                return (false, "Administrator privileges required.");
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Deletes a startup entry permanently.
    /// </summary>
    public static async Task<(bool Success, string Message)> DeleteStartupEntryAsync(StartupEntry entry)
    {
        return await Task.Run(() =>
        {
            try
            {
                switch (entry.Source)
                {
                    case StartupSource.RegistryCurrentUser:
                    case StartupSource.RegistryLocalMachine:
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(entry.RegistryHive, entry.RegistryView);
                        using var runKey = baseKey.OpenSubKey(entry.RegistryPath, writable: true);
                        runKey?.DeleteValue(entry.RegistryValueName, throwOnMissingValue: false);

                        // Also remove from StartupApproved
                        RemoveFromStartupApproved(entry.RegistryValueName, entry.RegistryHive);

                        return (true, $"Removed startup entry: {entry.Name}");
                    }

                    case StartupSource.StartupFolder:
                    {
                        if (File.Exists(entry.Command))
                        {
                            File.Delete(entry.Command);
                            return (true, $"Deleted startup shortcut: {entry.Name}");
                        }
                        return (false, "Shortcut file not found.");
                    }

                    case StartupSource.TaskScheduler:
                    {
                        var psi = new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{entry.Command}\" /F")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true
                        };
                        using var proc = Process.Start(psi);
                        proc?.WaitForExit(10000);
                        return proc?.ExitCode == 0
                            ? (true, $"Deleted scheduled task: {entry.Name}")
                            : (false, "Failed to delete scheduled task.");
                    }

                    default:
                        return (false, "Unknown startup source.");
                }
            }
            catch (UnauthorizedAccessException)
            {
                return (false, "Administrator privileges required.");
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}");
            }
        });
    }

    #region Private Helpers

    private static void ScanStartupFolder(List<StartupEntry> entries, string folderPath, string label)
    {
        if (!Directory.Exists(folderPath)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                bool isDisabled = ext == ".disabled";
                var actualName = isDisabled
                    ? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file))
                    : Path.GetFileNameWithoutExtension(file);

                var targetPath = file;
                // If it's a .lnk, resolve the target
                if (ext is ".lnk" or ".disabled")
                {
                    targetPath = ResolveShortcutTarget(file) ?? file;
                }

                entries.Add(new StartupEntry
                {
                    Name = actualName,
                    Command = file,
                    Location = label,
                    FilePath = targetPath,
                    Source = StartupSource.StartupFolder,
                    IsEnabled = !isDisabled,
                    Publisher = GetFilePublisher(targetPath),
                    FileSizeBytes = GetFileSize(targetPath),
                    Impact = EstimateImpact(targetPath),
                });
            }
        }
        catch (Exception ex) { DiagnosticLogger.Warn("StartupManager", "ScanStartupFolder failed", ex); }
    }

    private static void ScanScheduledTasks(List<StartupEntry> entries, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", "/Query /FO CSV /NH /V")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return;

            using var reader = proc.StandardOutput;
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;

                // CSV columns with /V: [0]=HostName, [1]=TaskName, [3]=Status,
                // [8]=Task To Run, [18]=Schedule Type
                var parts = ParseCsvLine(line);
                if (parts.Length < 19) continue;

                var taskName = parts[1].Trim('"');
                var status = parts[3].Trim('"');
                var taskToRun = parts[8].Trim('"');
                var scheduleType = parts[18].Trim('"');

                // Only include logon/boot triggered tasks  
                if (!scheduleType.Contains("logon", StringComparison.OrdinalIgnoreCase) &&
                    !scheduleType.Contains("boot", StringComparison.OrdinalIgnoreCase) &&
                    !scheduleType.Contains("startup", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip system tasks
                if (taskName.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
                    continue;

                var filePath = ExtractFilePath(taskToRun);
                entries.Add(new StartupEntry
                {
                    Name = Path.GetFileName(taskName),
                    Command = taskName,
                    Location = "Task Scheduler",
                    FilePath = filePath,
                    Source = StartupSource.TaskScheduler,
                    IsEnabled = status.Contains("Ready", StringComparison.OrdinalIgnoreCase),
                    Publisher = GetFilePublisher(filePath),
                    FileSizeBytes = GetFileSize(filePath),
                    Impact = StartupImpact.Medium,
                });
            }

            proc.WaitForExit(15000);
        }
        catch (Exception ex) { DiagnosticLogger.Warn("StartupManager", "ScanScheduledTasks failed", ex); }
    }

    private static (bool Success, string Message) ToggleRegistryEntry(StartupEntry entry, bool enable)
    {
        try
        {
            // Use StartupApproved mechanism (same as Task Manager uses)
            var approvedKeyPath = entry.RegistryHive == RegistryHive.CurrentUser
                ? DisabledRunKey
                : (entry.RegistryView == RegistryView.Registry32 ? DisabledRunKey32 : DisabledRunKey);

            var hive = entry.RegistryHive == RegistryHive.CurrentUser
                ? RegistryHive.CurrentUser
                : RegistryHive.LocalMachine;

            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var approvedKey = baseKey.CreateSubKey(approvedKeyPath);

            if (enable)
            {
                // Enable: set bytes to enabled flag (12 bytes with first 3 as 02 00 00...)
                var enabledBytes = new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
                approvedKey?.SetValue(entry.RegistryValueName, enabledBytes, RegistryValueKind.Binary);
            }
            else
            {
                // Disable: set bytes to disabled flag (first 3 as 03 00 00...)
                var disabledBytes = new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
                approvedKey?.SetValue(entry.RegistryValueName, disabledBytes, RegistryValueKind.Binary);
            }

            entry.IsEnabled = enable;
            return (true, $"{entry.Name} {(enable ? "enabled" : "disabled")}.");
        }
        catch (Exception ex)
        {
            return (false, $"Error toggling {entry.Name}: {ex.Message}");
        }
    }

    private static (bool Success, string Message) ToggleStartupFolderEntry(StartupEntry entry, bool enable)
    {
        var path = entry.Command;

        if (enable && path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            var newPath = path[..^".disabled".Length];
            File.Move(path, newPath);
            entry.Command = newPath;
            entry.IsEnabled = true;
            return (true, $"Enabled: {entry.Name}");
        }
        else if (!enable && !path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            var newPath = path + ".disabled";
            File.Move(path, newPath);
            entry.Command = newPath;
            entry.IsEnabled = false;
            return (true, $"Disabled: {entry.Name}");
        }

        return (true, "No change needed.");
    }

    private static (bool Success, string Message) ToggleScheduledTask(StartupEntry entry, bool enable)
    {
        var action = enable ? "/Enable" : "/Disable";
        var psi = new ProcessStartInfo("schtasks.exe", $"/Change /TN \"{entry.Command}\" {action}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };

        using var proc = Process.Start(psi);
        proc?.WaitForExit(10000);

        if (proc?.ExitCode == 0)
        {
            entry.IsEnabled = enable;
            return (true, $"{entry.Name} {(enable ? "enabled" : "disabled")}.");
        }
        return (false, $"Failed to {(enable ? "enable" : "disable")} task.");
    }

    private static bool IsDisabledViaStartupApproved(string valueName, RegistryHive hive)
    {
        try
        {
            var keyPath = hive == RegistryHive.CurrentUser ? DisabledRunKey : DisabledRunKey;
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var approvedKey = baseKey.OpenSubKey(keyPath);

            if (approvedKey?.GetValue(valueName) is byte[] data && data.Length >= 1)
            {
                // 0x03 = disabled, 0x02 = enabled
                return data[0] == 0x03;
            }
        }
        catch (Exception ex) { DiagnosticLogger.Warn("StartupManager", "IsDisabledViaStartupApproved failed", ex); }
        return false;
    }

    private static void RemoveFromStartupApproved(string valueName, RegistryHive hive)
    {
        try
        {
            var keyPath = hive == RegistryHive.CurrentUser ? DisabledRunKey : DisabledRunKey;
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var approvedKey = baseKey.OpenSubKey(keyPath, writable: true);
            approvedKey?.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { DiagnosticLogger.Warn("StartupManager", "RemoveFromStartupApproved failed", ex); }
    }

    private static string ExtractFilePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;
        command = command.Trim();

        // Quoted path
        if (command.StartsWith('"'))
        {
            int closeQuote = command.IndexOf('"', 1);
            if (closeQuote > 0)
                return command[1..closeQuote];
        }

        // Find .exe boundary
        int exeIdx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0)
            return command[..(exeIdx + 4)];

        // Just return the first token
        int spaceIdx = command.IndexOf(' ');
        return spaceIdx > 0 ? command[..spaceIdx] : command;
    }

    private static string GetFilePublisher(string filePath)
    {
        try
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
                return versionInfo.CompanyName ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }

    private static long GetFileSize(string filePath)
    {
        try
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                return new FileInfo(filePath).Length;
        }
        catch { }
        return 0;
    }

    private static StartupImpact EstimateImpact(string filePath)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return StartupImpact.Unknown;

            var size = new FileInfo(filePath).Length;
            return size switch
            {
                > 50 * 1024 * 1024 => StartupImpact.High,     // > 50 MB
                > 10 * 1024 * 1024 => StartupImpact.Medium,   // > 10 MB
                > 1 * 1024 * 1024 => StartupImpact.Low,       // > 1 MB
                _ => StartupImpact.None
            };
        }
        catch { }
        return StartupImpact.Unknown;
    }

    private static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            // Simple .lnk parser — read the target path from the binary shortcut format
            // LNK files have a well-known structure: the target path is embedded after fixed headers
            using var fs = new FileStream(lnkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            // Header: 4 bytes size (76)
            var headerSize = br.ReadUInt32();
            if (headerSize != 0x4C) return null;

            // Skip CLSID (16 bytes)
            br.ReadBytes(16);

            // Flags
            var flags = br.ReadUInt32();
            bool hasLinkTargetIdList = (flags & 0x01) != 0;
            bool hasLinkInfo = (flags & 0x02) != 0;

            // Skip file attributes (4), creation time (8), access time (8), write time (8)
            br.ReadBytes(28);
            // file size (4), icon index (4), show command (4)
            br.ReadBytes(12);
            // hot key (2), reserved (10)
            br.ReadBytes(12);

            // Skip LinkTargetIDList if present
            if (hasLinkTargetIdList)
            {
                var idListSize = br.ReadUInt16();
                br.ReadBytes(idListSize);
            }

            // Read LinkInfo for target path
            if (hasLinkInfo)
            {
                long linkInfoStart = fs.Position;
                var linkInfoSize = br.ReadUInt32();
                var linkInfoHeaderSize = br.ReadUInt32();
                var linkInfoFlags = br.ReadUInt32();

                bool volumeIdAndLocalBasePath = (linkInfoFlags & 0x01) != 0;

                if (volumeIdAndLocalBasePath)
                {
                    var volumeIdOffset = br.ReadUInt32();
                    var localBasePathOffset = br.ReadUInt32();

                    fs.Position = linkInfoStart + localBasePathOffset;
                    var pathBytes = new List<byte>();
                    int b;
                    while ((b = br.ReadByte()) != 0)
                        pathBytes.Add((byte)b);

                    return System.Text.Encoding.Default.GetString(pathBytes.ToArray());
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());

        return [.. result];
    }

    #endregion
}
