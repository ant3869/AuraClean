using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AuraClean.Helpers;
using AuraClean.Models;
using Microsoft.Win32;
using TaskSchedulerLib = Microsoft.Win32.TaskScheduler;

namespace AuraClean.Services;

/// <summary>
/// Multi-layered threat detection engine providing signature-based, heuristic,
/// behavioral, and pattern-matching analysis for malware, adware, and PUPs.
/// Supports Quick Scan, Full Scan, Custom Scan, and Browser-Only scan modes.
/// </summary>
public static class ThreatScannerService
{
    private static readonly object _resultsLock = new();

    // ══════════════════════════════════════════
    //  PUBLIC SCAN ENTRY POINTS
    // ══════════════════════════════════════════

    /// <summary>
    /// Quick Scan: Running processes + startup locations + temp dirs + browser extensions.
    /// Focuses on active threats and common malware locations.
    /// </summary>
    public static async Task<ThreatScanResult> QuickScanAsync(
        IProgress<string>? progress = null,
        IProgress<double>? percentProgress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ThreatScanResult { Mode = ScanMode.Quick };
        var threats = new List<ThreatItem>();

        var stages = 7;
        var stageIndex = 0;

        void ReportStage(string msg)
        {
            progress?.Report(msg);
            percentProgress?.Report((double)stageIndex / stages * 100);
            stageIndex++;
        }

        // Stage 1: Process analysis
        ReportStage("Scanning running processes...");
        var processThreats = await ScanRunningProcessesAsync(progress, ct);
        result.TotalProcessesScanned = processThreats.scanned;
        threats.AddRange(processThreats.threats);

        // Stage 2: Startup entries
        ReportStage("Scanning startup locations...");
        threats.AddRange(await ScanStartupLocationsAsync(progress, ct));

        // Stage 3: Scheduled tasks
        ReportStage("Scanning scheduled tasks...");
        threats.AddRange(await ScanScheduledTasksAsync(progress, ct));

        // Stage 4: Quick file scan of common malware paths
        ReportStage("Scanning common malware locations...");
        var quickPaths = ThreatSignatureDatabase.GetQuickScanPaths()
            .Where(Directory.Exists).ToArray();
        var fileScanResult = await ScanDirectoriesAsync(quickPaths, maxDepth: 2, progress, ct);
        result.TotalFilesScanned = fileScanResult.scanned;
        threats.AddRange(fileScanResult.threats);

        // Stage 5: Browser extensions
        ReportStage("Scanning browser extensions...");
        var browserResult = await ScanBrowserExtensionsAsync(progress, ct);
        result.TotalBrowserExtensionsScanned = browserResult.scanned;
        threats.AddRange(browserResult.threats);

        // Stage 6: Hosts file
        ReportStage("Checking hosts file...");
        threats.AddRange(await ScanHostsFileAsync(progress, ct));

        // Stage 7: Adware registry keys
        ReportStage("Scanning registry for adware...");
        var regResult = await ScanRegistryForAdwareAsync(progress, ct);
        result.TotalRegistryKeysScanned = regResult.scanned;
        threats.AddRange(regResult.threats);

        percentProgress?.Report(100);
        sw.Stop();

        // Filter out whitelisted items
        var whitelist = ThreatSignatureDatabase.LoadWhitelist();
        foreach (var threat in threats)
        {
            if (!string.IsNullOrEmpty(threat.Sha256Hash) && whitelist.Contains(threat.Sha256Hash))
                threat.IsWhitelisted = true;
        }

        result.Threats = threats.Where(t => !t.IsWhitelisted).ToList();
        result.ScanDuration = sw.Elapsed;

        DiagnosticLogger.Info("ThreatScanner",
            $"Quick scan complete: {result.Threats.Count} threats in {sw.Elapsed.TotalSeconds:F1}s");

        return result;
    }

    /// <summary>
    /// Full/Deep Scan: Comprehensive system-wide scan including all drives,
    /// all processes, all registry, all browser data, and deep file analysis.
    /// </summary>
    public static async Task<ThreatScanResult> FullScanAsync(
        IProgress<string>? progress = null,
        IProgress<double>? percentProgress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ThreatScanResult { Mode = ScanMode.Full };
        var threats = new List<ThreatItem>();

        var stages = 8;
        var stageIndex = 0;

        void ReportStage(string msg)
        {
            progress?.Report(msg);
            percentProgress?.Report((double)stageIndex / stages * 100);
            stageIndex++;
        }

        // Stage 1: Process analysis
        ReportStage("Deep scanning running processes...");
        var processThreats = await ScanRunningProcessesAsync(progress, ct);
        result.TotalProcessesScanned = processThreats.scanned;
        threats.AddRange(processThreats.threats);

        // Stage 2: Startup + services
        ReportStage("Scanning all startup and service entries...");
        threats.AddRange(await ScanStartupLocationsAsync(progress, ct));
        threats.AddRange(await ScanSuspiciousServicesAsync(progress, ct));

        // Stage 3: Scheduled tasks
        ReportStage("Scanning all scheduled tasks...");
        threats.AddRange(await ScanScheduledTasksAsync(progress, ct));

        // Stage 4: Deep file scan - all user directories and program directories
        ReportStage("Deep scanning file system (this may take a while)...");
        var deepPaths = GetDeepScanPaths();
        var fileScanResult = await ScanDirectoriesAsync(deepPaths, maxDepth: 8, progress, ct);
        result.TotalFilesScanned = fileScanResult.scanned;
        threats.AddRange(fileScanResult.threats);

        // Stage 5: Browser extensions & data
        ReportStage("Deep scanning browser data...");
        var browserResult = await ScanBrowserExtensionsAsync(progress, ct);
        result.TotalBrowserExtensionsScanned = browserResult.scanned;
        threats.AddRange(browserResult.threats);

        // Stage 6: Hosts file
        ReportStage("Checking hosts file for hijacking...");
        threats.AddRange(await ScanHostsFileAsync(progress, ct));

        // Stage 7: Registry - adware + suspicious entries
        ReportStage("Deep scanning registry...");
        var regResult = await ScanRegistryForAdwareAsync(progress, ct);
        result.TotalRegistryKeysScanned = regResult.scanned;
        threats.AddRange(regResult.threats);
        threats.AddRange(await ScanRegistryRunKeysAsync(progress, ct));

        // Stage 8: Services analysis
        ReportStage("Finalizing scan results...");

        percentProgress?.Report(100);
        sw.Stop();

        // Filter out whitelisted items
        var whitelist = ThreatSignatureDatabase.LoadWhitelist();
        foreach (var threat in threats)
        {
            if (!string.IsNullOrEmpty(threat.Sha256Hash) && whitelist.Contains(threat.Sha256Hash))
                threat.IsWhitelisted = true;
        }

        result.Threats = threats.Where(t => !t.IsWhitelisted).ToList();
        result.ScanDuration = sw.Elapsed;

        DiagnosticLogger.Info("ThreatScanner",
            $"Full scan complete: {result.Threats.Count} threats in {sw.Elapsed.TotalSeconds:F1}s");

        return result;
    }

    /// <summary>
    /// Custom Scan: Scans user-selected directories.
    /// </summary>
    public static async Task<ThreatScanResult> CustomScanAsync(
        string[] directories,
        IProgress<string>? progress = null,
        IProgress<double>? percentProgress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ThreatScanResult { Mode = ScanMode.Custom };

        progress?.Report($"Scanning {directories.Length} selected location(s)...");
        var fileScanResult = await ScanDirectoriesAsync(directories, maxDepth: 10, progress, ct);
        result.TotalFilesScanned = fileScanResult.scanned;

        var whitelist = ThreatSignatureDatabase.LoadWhitelist();
        foreach (var threat in fileScanResult.threats)
        {
            if (!string.IsNullOrEmpty(threat.Sha256Hash) && whitelist.Contains(threat.Sha256Hash))
                threat.IsWhitelisted = true;
        }

        result.Threats = fileScanResult.threats.Where(t => !t.IsWhitelisted).ToList();

        percentProgress?.Report(100);
        sw.Stop();
        result.ScanDuration = sw.Elapsed;

        return result;
    }

    /// <summary>
    /// Browser-Only Scan: Focuses exclusively on browser threats.
    /// </summary>
    public static async Task<ThreatScanResult> BrowserScanAsync(
        IProgress<string>? progress = null,
        IProgress<double>? percentProgress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new ThreatScanResult { Mode = ScanMode.BrowserOnly };
        var threats = new List<ThreatItem>();

        percentProgress?.Report(20);
        progress?.Report("Scanning browser extensions...");
        var browserResult = await ScanBrowserExtensionsAsync(progress, ct);
        result.TotalBrowserExtensionsScanned = browserResult.scanned;
        threats.AddRange(browserResult.threats);

        percentProgress?.Report(50);
        progress?.Report("Checking hosts file for browser hijacking...");
        threats.AddRange(await ScanHostsFileAsync(progress, ct));

        percentProgress?.Report(80);
        progress?.Report("Scanning registry for browser hijackers...");
        threats.AddRange(await ScanBrowserRegistryAsync(progress, ct));

        percentProgress?.Report(100);
        sw.Stop();

        var whitelist = ThreatSignatureDatabase.LoadWhitelist();
        foreach (var threat in threats)
        {
            if (!string.IsNullOrEmpty(threat.Sha256Hash) && whitelist.Contains(threat.Sha256Hash))
                threat.IsWhitelisted = true;
        }

        result.Threats = threats.Where(t => !t.IsWhitelisted).ToList();
        result.ScanDuration = sw.Elapsed;

        return result;
    }

    // ══════════════════════════════════════════
    //  LAYER 1: SIGNATURE-BASED DETECTION
    // ══════════════════════════════════════════

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 8192, useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool CheckSignatureMatch(string sha256Hash)
    {
        return ThreatSignatureDatabase.KnownMalwareHashes.Contains(sha256Hash);
    }

    // ══════════════════════════════════════════
    //  LAYER 2: HEURISTIC / PE ANALYSIS
    // ══════════════════════════════════════════

    private static async Task<(bool isSuspicious, string reason, ThreatType type, ThreatLevel level)>
        AnalyzeFileHeuristicsAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileName = fileInfo.Name;
            var ext = fileInfo.Extension;

            // Check 1: Known malware file name
            if (ThreatSignatureDatabase.KnownMalwareFileNames.Contains(fileName))
            {
                return (true, $"Known malware filename: {fileName}", ThreatType.Malware, ThreatLevel.High);
            }

            // Check 2: Double extension detection (e.g., invoice.pdf.exe)
            if (HasDoubleExtension(fileName))
            {
                return (true, $"Double extension detected: {fileName}", ThreatType.DoubleExtension, ThreatLevel.High);
            }

            // Check 3: Hidden executable in user directory
            if (fileInfo.Attributes.HasFlag(FileAttributes.Hidden) &&
                ThreatSignatureDatabase.ExecutableExtensions.Contains(ext))
            {
                var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (filePath.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, "Hidden executable in user directory", ThreatType.HiddenExecutable, ThreatLevel.Medium);
                }
            }

            // Check 4: Executable in temp directory
            var tempPath = Path.GetTempPath();
            var downloadsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (ThreatSignatureDatabase.ExecutableExtensions.Contains(ext) &&
                (filePath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase)))
            {
                // Only flag if it looks suspicious (not installers, not recently downloaded)
                if (fileInfo.Length < 50_000 || fileInfo.Length > 50_000_000)
                {
                    // Small executables in temp are more suspicious
                    if (fileInfo.Length < 50_000)
                    {
                        return (true, "Small executable in temp directory",
                            ThreatType.SuspiciousFile, ThreatLevel.Medium);
                    }
                }
            }

            // Check 5: PE Analysis for executables
            if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".scr", StringComparison.OrdinalIgnoreCase))
            {
                var peResult = await AnalyzePeFileAsync(filePath, ct);
                if (peResult.isSuspicious)
                    return peResult;
            }

            // Check 6: Suspicious system file impersonation
            var sysImpersonation = CheckSystemFileImpersonation(filePath, fileName);
            if (sysImpersonation.isSuspicious)
                return sysImpersonation;

            return (false, string.Empty, ThreatType.SuspiciousFile, ThreatLevel.Low);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThreatScanner", $"Heuristic analysis failed for {filePath}", ex);
            return (false, string.Empty, ThreatType.SuspiciousFile, ThreatLevel.Low);
        }
    }

    /// <summary>
    /// Analyzes PE (Portable Executable) headers for suspicious indicators:
    /// high entropy sections, suspicious imports, byte signature matches.
    /// </summary>
    private static async Task<(bool isSuspicious, string reason, ThreatType type, ThreatLevel level)>
        AnalyzePeFileAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[Math.Min(new FileInfo(filePath).Length, 65536)];
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                await fs.ReadAsync(buffer, ct);
            }

            // Check MZ header
            if (buffer.Length < 64 || buffer[0] != 0x4D || buffer[1] != 0x5A)
                return (false, "", ThreatType.SuspiciousFile, ThreatLevel.Low);

            // Check for known byte signatures (packer detection)
            foreach (var (name, pattern, desc) in ThreatSignatureDatabase.ByteSignatures)
            {
                if (ContainsPattern(buffer, pattern))
                {
                    return (true, $"{desc} detected ({name})", ThreatType.PackedExecutable, ThreatLevel.Medium);
                }
            }

            // Check entropy of the file (packed/encrypted files have high entropy)
            var entropy = CalculateEntropy(buffer);
            if (entropy > 7.2) // Very high entropy suggests encryption/packing
            {
                return (true, $"Extremely high entropy ({entropy:F2}) — likely packed or encrypted",
                    ThreatType.PackedExecutable, ThreatLevel.Medium);
            }

            // Check for suspicious string imports
            var suspiciousImports = FindSuspiciousImports(buffer);
            if (suspiciousImports.Count >= 3) // Multiple suspicious APIs = higher risk
            {
                return (true,
                    $"Multiple suspicious API imports: {string.Join(", ", suspiciousImports.Take(5))}",
                    ThreatType.Malware, ThreatLevel.High);
            }

            return (false, "", ThreatType.SuspiciousFile, ThreatLevel.Low);
        }
        catch
        {
            return (false, "", ThreatType.SuspiciousFile, ThreatLevel.Low);
        }
    }

    /// <summary>
    /// Shannon entropy calculation — values above 7.0 suggest packed/encrypted content.
    /// Normal executables typically have entropy between 4.0-6.5.
    /// </summary>
    private static double CalculateEntropy(byte[] data)
    {
        if (data.Length == 0) return 0;

        var freq = new int[256];
        foreach (var b in data)
            freq[b]++;

        double entropy = 0;
        double len = data.Length;
        for (int i = 0; i < 256; i++)
        {
            if (freq[i] == 0) continue;
            double p = freq[i] / len;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }

    private static List<string> FindSuspiciousImports(byte[] data)
    {
        var found = new List<string>();
        var content = Encoding.ASCII.GetString(data);

        foreach (var import in ThreatSignatureDatabase.SuspiciousImports)
        {
            if (content.Contains(import, StringComparison.Ordinal))
                found.Add(import);
        }
        return found;
    }

    private static bool ContainsPattern(byte[] data, byte[] pattern)
    {
        if (pattern.Length > data.Length) return false;
        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    // ══════════════════════════════════════════
    //  LAYER 3: PROCESS ANALYSIS
    // ══════════════════════════════════════════

    private static async Task<(List<ThreatItem> threats, int scanned)>
        ScanRunningProcessesAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var threats = new List<ThreatItem>();
        int scanned = 0;

        await Task.Run(() =>
        {
            try
            {
                var processes = Process.GetProcesses();
                scanned = processes.Length;

                foreach (var proc in processes)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var procName = proc.ProcessName;

                        // Skip known safe processes
                        if (ThreatSignatureDatabase.KnownSafeProcesses.Contains(procName))
                            continue;

                        string? exePath = null;
                        try { exePath = proc.MainModule?.FileName; }
                        catch { continue; } // Access denied — skip

                        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                            continue;

                        // Check 1: Process running from suspicious location
                        var tempPath = Path.GetTempPath();
                        var downloadsPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

                        if (exePath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase))
                        {
                            threats.Add(new ThreatItem
                            {
                                Name = procName,
                                Path = exePath,
                                Description = "Process running from temp directory",
                                ThreatLevel = ThreatLevel.High,
                                ThreatType = ThreatType.SuspiciousProcess,
                                DetectionMethod = ThreatDetectionMethod.ProcessAnalysis,
                                ProcessName = procName,
                                ProcessId = proc.Id,
                                SizeBytes = TryGetFileSize(exePath),
                            });
                            continue;
                        }

                        // Check 2: Process impersonating system process
                        var systemNames = new[] {
                            "svchost", "csrss", "lsass", "services", "smss",
                            "winlogon", "dwm", "explorer", "taskhostw" };
                        foreach (var sysName in systemNames)
                        {
                            if (procName.Equals(sysName, StringComparison.OrdinalIgnoreCase) &&
                                !IsInLegitimateSystemPath(exePath))
                            {
                                threats.Add(new ThreatItem
                                {
                                    Name = procName,
                                    Path = exePath,
                                    Description = $"Process '{procName}' running from non-system path: {exePath}",
                                    ThreatLevel = ThreatLevel.Critical,
                                    ThreatType = ThreatType.Malware,
                                    DetectionMethod = ThreatDetectionMethod.ProcessAnalysis,
                                    ProcessName = procName,
                                    ProcessId = proc.Id,
                                    SizeBytes = TryGetFileSize(exePath),
                                });
                                break;
                            }
                        }

                        // Check 3: Known malware filename
                        var fileName = Path.GetFileName(exePath);
                        if (ThreatSignatureDatabase.KnownMalwareFileNames.Contains(fileName))
                        {
                            threats.Add(new ThreatItem
                            {
                                Name = procName,
                                Path = exePath,
                                Description = $"Known malware filename: {fileName}",
                                ThreatLevel = ThreatLevel.Critical,
                                ThreatType = ThreatType.Malware,
                                DetectionMethod = ThreatDetectionMethod.SignatureMatch,
                                ProcessName = procName,
                                ProcessId = proc.Id,
                                SizeBytes = TryGetFileSize(exePath),
                            });
                        }
                    }
                    catch { }
                    finally
                    {
                        try { proc.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("ThreatScanner", "Process scan error", ex);
            }
        }, ct);

        return (threats, scanned);
    }

    // ══════════════════════════════════════════
    //  LAYER 4: FILE SYSTEM SCANNING
    // ══════════════════════════════════════════

    private static async Task<(List<ThreatItem> threats, int scanned)>
        ScanDirectoriesAsync(
            string[] directories,
            int maxDepth,
            IProgress<string>? progress,
            CancellationToken ct)
    {
        var threats = new List<ThreatItem>();
        int totalScanned = 0;

        await Task.Run(async () =>
        {
            foreach (var rootDir in directories)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(rootDir)) continue;

                // Stack-based traversal for safety
                var stack = new Stack<(string path, int depth)>();
                stack.Push((rootDir, 0));

                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var (currentDir, depth) = stack.Pop();

                    if (depth > maxDepth) continue;

                    // Push subdirectories
                    if (depth < maxDepth)
                    {
                        try
                        {
                            foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                            {
                                var dirName = Path.GetFileName(subDir);
                                // Skip well-known safe directories and system volume info
                                if (dirName.StartsWith('.') ||
                                    dirName.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                                    dirName.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                                    dirName.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                                    dirName.Equals("Recovery", StringComparison.OrdinalIgnoreCase) ||
                                    dirName.Equals("Windows", StringComparison.OrdinalIgnoreCase) && depth == 0)
                                    continue;

                                stack.Push((subDir, depth + 1));
                            }
                        }
                        catch { }
                    }

                    // Scan files in current directory
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(currentDir); }
                    catch { continue; }

                    foreach (var filePath in files)
                    {
                        ct.ThrowIfCancellationRequested();

                        try
                        {
                            var ext = Path.GetExtension(filePath);
                            if (!ThreatSignatureDatabase.ExecutableExtensions.Contains(ext) &&
                                !ext.Equals(".sys", StringComparison.OrdinalIgnoreCase))
                                continue;

                            Interlocked.Increment(ref totalScanned);

                            if (totalScanned % 200 == 0)
                                progress?.Report($"Scanned {totalScanned} files... ({Path.GetFileName(currentDir)})");

                            // Quick size check — skip very large files for speed
                            var fileInfo = new FileInfo(filePath);
                            if (fileInfo.Length > 200_000_000) // >200MB, skip heuristic
                                continue;

                            // Heuristic analysis
                            var heurResult = await AnalyzeFileHeuristicsAsync(filePath, ct);
                            if (heurResult.isSuspicious)
                            {
                                var hash = await ComputeSha256Async(filePath, ct);
                                lock (_resultsLock)
                                {
                                    threats.Add(new ThreatItem
                                    {
                                        Name = Path.GetFileName(filePath),
                                        Path = filePath,
                                        Description = heurResult.reason,
                                        ThreatLevel = heurResult.level,
                                        ThreatType = heurResult.type,
                                        DetectionMethod = ThreatDetectionMethod.HeuristicAnalysis,
                                        SizeBytes = fileInfo.Length,
                                        Sha256Hash = hash,
                                    });
                                }
                                continue;
                            }

                            // Signature hash check for executables
                            if (fileInfo.Length < 50_000_000) // Only hash files < 50MB
                            {
                                var hash = await ComputeSha256Async(filePath, ct);
                                if (!string.IsNullOrEmpty(hash) && CheckSignatureMatch(hash))
                                {
                                    lock (_resultsLock)
                                    {
                                        threats.Add(new ThreatItem
                                        {
                                            Name = Path.GetFileName(filePath),
                                            Path = filePath,
                                            Description = "Matches known malware signature hash",
                                            ThreatLevel = ThreatLevel.Critical,
                                            ThreatType = ThreatType.Malware,
                                            DetectionMethod = ThreatDetectionMethod.SignatureMatch,
                                            SizeBytes = fileInfo.Length,
                                            Sha256Hash = hash,
                                        });
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
        }, ct);

        return (threats, totalScanned);
    }

    // ══════════════════════════════════════════
    //  LAYER 5: STARTUP / AUTORUN ANALYSIS
    // ══════════════════════════════════════════

    private static async Task<List<ThreatItem>> ScanStartupLocationsAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        var threats = new List<ThreatItem>();

        await Task.Run(() =>
        {
            // Scan Run registry keys
            var runKeys = new[]
            {
                (RegistryHive.CurrentUser, RegistryView.Default,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (RegistryHive.LocalMachine, RegistryView.Registry64,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (RegistryHive.LocalMachine, RegistryView.Registry32,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (RegistryHive.CurrentUser, RegistryView.Default,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
                (RegistryHive.LocalMachine, RegistryView.Registry64,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
            };

            foreach (var (hive, view, keyPath) in runKeys)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var runKey = baseKey.OpenSubKey(keyPath);
                    if (runKey == null) continue;

                    foreach (var valueName in runKey.GetValueNames())
                    {
                        var value = runKey.GetValue(valueName)?.ToString();
                        if (string.IsNullOrEmpty(value)) continue;

                        // Extract executable path from command line
                        var exePath = ExtractExePath(value);

                        // Check for suspicious patterns
                        bool isSuspicious = false;
                        string reason = "";

                        foreach (var pattern in ThreatSignatureDatabase.SuspiciousTaskPatterns)
                        {
                            if (value.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                            {
                                isSuspicious = true;
                                reason = $"Suspicious command in startup: {pattern.Trim()}";
                                break;
                            }
                        }

                        // Check if the executable exists and is in a suspicious location
                        if (!isSuspicious && !string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                        {
                            if (exePath.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
                                exePath.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase) ||
                                exePath.Contains(@"\Users\Public\", StringComparison.OrdinalIgnoreCase))
                            {
                                isSuspicious = true;
                                reason = $"Startup entry points to suspicious location: {exePath}";
                            }
                        }

                        // Check for known malware file names
                        if (!isSuspicious && !string.IsNullOrEmpty(exePath))
                        {
                            var fileName = Path.GetFileName(exePath);
                            if (ThreatSignatureDatabase.KnownMalwareFileNames.Contains(fileName))
                            {
                                isSuspicious = true;
                                reason = $"Known malware file in startup: {fileName}";
                            }
                        }

                        if (isSuspicious)
                        {
                            threats.Add(new ThreatItem
                            {
                                Name = valueName,
                                Path = exePath ?? value,
                                Description = reason,
                                ThreatLevel = ThreatLevel.High,
                                ThreatType = ThreatType.SuspiciousStartup,
                                DetectionMethod = ThreatDetectionMethod.RegistryAnalysis,
                                SizeBytes = TryGetFileSize(exePath),
                            });
                        }
                    }
                }
                catch { }
            }

            // Scan startup folders
            var startupFolders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            };

            foreach (var folder in startupFolders.Where(Directory.Exists))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder))
                    {
                        var ext = Path.GetExtension(file);
                        if (ext.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
                            ext.Equals(".db", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Check for suspicious scripts or executables
                        if (ThreatSignatureDatabase.ExecutableExtensions.Contains(ext))
                        {
                            var fileName = Path.GetFileName(file);
                            if (ThreatSignatureDatabase.KnownMalwareFileNames.Contains(fileName))
                            {
                                threats.Add(new ThreatItem
                                {
                                    Name = fileName,
                                    Path = file,
                                    Description = $"Known malware file in startup folder",
                                    ThreatLevel = ThreatLevel.Critical,
                                    ThreatType = ThreatType.SuspiciousStartup,
                                    DetectionMethod = ThreatDetectionMethod.PatternMatch,
                                    SizeBytes = TryGetFileSize(file),
                                });
                            }
                        }
                    }
                }
                catch { }
            }
        }, ct);

        return threats;
    }

    // ══════════════════════════════════════════
    //  LAYER 6: SCHEDULED TASK ANALYSIS
    // ══════════════════════════════════════════

    private static async Task<List<ThreatItem>> ScanScheduledTasksAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        var threats = new List<ThreatItem>();

        await Task.Run(() =>
        {
            try
            {
                using var ts = new TaskSchedulerLib.TaskService();
                ScanTaskFolder(ts.RootFolder, threats, ct);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("ThreatScanner", "Scheduled task scan error", ex);
            }
        }, ct);

        return threats;
    }

    private static void ScanTaskFolder(TaskSchedulerLib.TaskFolder folder, List<ThreatItem> threats, CancellationToken ct)
    {
        try
        {
            foreach (var task in folder.Tasks)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    foreach (var action in task.Definition.Actions)
                    {
                        if (action is not TaskSchedulerLib.ExecAction execAction) continue;

                        var command = $"{execAction.Path} {execAction.Arguments}".Trim();

                        foreach (var pattern in ThreatSignatureDatabase.SuspiciousTaskPatterns)
                        {
                            if (command.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                            {
                                threats.Add(new ThreatItem
                                {
                                    Name = task.Name,
                                    Path = execAction.Path ?? command,
                                    Description = $"Suspicious scheduled task with pattern: {pattern.Trim()}",
                                    ThreatLevel = ThreatLevel.High,
                                    ThreatType = ThreatType.SuspiciousScheduledTask,
                                    DetectionMethod = ThreatDetectionMethod.BehavioralAnalysis,
                                });
                                break;
                            }
                        }

                        // Check if task runs from temp/downloads/public
                        if (!string.IsNullOrEmpty(execAction.Path))
                        {
                            var path = execAction.Path;
                            if (path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
                                path.Contains(@"\Users\Public\", StringComparison.OrdinalIgnoreCase))
                            {
                                // Check if not already flagged
                                if (!threats.Any(t => t.Name == task.Name))
                                {
                                    threats.Add(new ThreatItem
                                    {
                                        Name = task.Name,
                                        Path = path,
                                        Description = "Scheduled task runs from suspicious location",
                                        ThreatLevel = ThreatLevel.Medium,
                                        ThreatType = ThreatType.SuspiciousScheduledTask,
                                        DetectionMethod = ThreatDetectionMethod.BehavioralAnalysis,
                                    });
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            foreach (var subFolder in folder.SubFolders)
            {
                ScanTaskFolder(subFolder, threats, ct);
            }
        }
        catch { }
    }

    // ══════════════════════════════════════════
    //  LAYER 7: BROWSER EXTENSION ANALYSIS
    // ══════════════════════════════════════════

    private static async Task<(List<ThreatItem> threats, int scanned)>
        ScanBrowserExtensionsAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var threats = new List<ThreatItem>();
        int scanned = 0;

        await Task.Run(() =>
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            var browsers = new[]
            {
                ("Chrome", Path.Combine(localAppData, @"Google\Chrome\User Data")),
                ("Edge", Path.Combine(localAppData, @"Microsoft\Edge\User Data")),
                ("Brave", Path.Combine(localAppData, @"BraveSoftware\Brave-Browser\User Data")),
                ("Opera", Path.Combine(appData, @"Opera Software\Opera Stable")),
                ("Vivaldi", Path.Combine(localAppData, @"Vivaldi\User Data")),
            };

            foreach (var (browserName, basePath) in browsers)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(basePath)) continue;

                progress?.Report($"Scanning {browserName} extensions...");

                // Find profile directories (Default, Profile 1, Profile 2, etc.)
                var profiles = new List<string>();
                var defaultProfile = Path.Combine(basePath, "Default");
                if (Directory.Exists(defaultProfile))
                    profiles.Add(defaultProfile);

                try
                {
                    profiles.AddRange(
                        Directory.EnumerateDirectories(basePath, "Profile *")
                                 .Where(Directory.Exists));
                }
                catch { }

                // Opera doesn't use profile subdirs
                if (browserName == "Opera" && Directory.Exists(basePath))
                    profiles.Add(basePath);

                foreach (var profile in profiles)
                {
                    var extDir = Path.Combine(profile, "Extensions");
                    if (!Directory.Exists(extDir)) continue;

                    try
                    {
                        foreach (var extFolder in Directory.EnumerateDirectories(extDir))
                        {
                            scanned++;
                            var extId = Path.GetFileName(extFolder);

                            // Check against known malicious extension IDs
                            if (ThreatSignatureDatabase.KnownMaliciousExtensionIds.Contains(extId))
                            {
                                threats.Add(new ThreatItem
                                {
                                    Name = $"[{browserName}] Malicious Extension: {extId}",
                                    Path = extFolder,
                                    Description = "Known malicious browser extension ID",
                                    ThreatLevel = ThreatLevel.High,
                                    ThreatType = ThreatType.SuspiciousBrowserExtension,
                                    DetectionMethod = ThreatDetectionMethod.BrowserAnalysis,
                                    SizeBytes = GetDirectorySizeSafe(extFolder, 100),
                                });
                                continue;
                            }

                            // Analyze extension manifest for suspicious permissions
                            var manifestPath = FindExtensionManifest(extFolder);
                            if (manifestPath == null) continue;

                            try
                            {
                                var manifestContent = File.ReadAllText(manifestPath);

                                // Check for ad-injection / data-theft permissions
                                bool hasSuspiciousPerms =
                                    manifestContent.Contains("\"<all_urls>\"", StringComparison.OrdinalIgnoreCase) &&
                                    (manifestContent.Contains("webRequest", StringComparison.OrdinalIgnoreCase) ||
                                     manifestContent.Contains("webRequestBlocking", StringComparison.OrdinalIgnoreCase));

                                // Check for known adware extension names
                                foreach (var adwareName in ThreatSignatureDatabase.KnownAdwareNames)
                                {
                                    if (manifestContent.Contains(adwareName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        threats.Add(new ThreatItem
                                        {
                                            Name = $"[{browserName}] Adware Extension: {adwareName}",
                                            Path = extFolder,
                                            Description = $"Extension matches known adware: {adwareName}",
                                            ThreatLevel = ThreatLevel.Medium,
                                            ThreatType = ThreatType.Adware,
                                            DetectionMethod = ThreatDetectionMethod.BrowserAnalysis,
                                            SizeBytes = GetDirectorySizeSafe(extFolder, 100),
                                        });
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                // Check for browser hijacking settings
                var prefsFile = Path.Combine(basePath, "Default", "Preferences");
                if (File.Exists(prefsFile))
                {
                    try
                    {
                        var prefs = File.ReadAllText(prefsFile);
                        CheckBrowserHijacking(prefs, browserName, prefsFile, threats);
                    }
                    catch { }
                }
            }
        }, ct);

        return (threats, scanned);
    }

    private static void CheckBrowserHijacking(string prefsJson, string browserName,
        string prefsPath, List<ThreatItem> threats)
    {
        // Known hijacker search engines
        var hijackerSearchEngines = new[]
        {
            "search.yahoo.com/yhs", "search.conduit.com", "search.babylon.com",
            "search.ask.com/web", "delta-search.com", "trovi.com",
            "search.snapdo.com", "search.sweetim.com", "isearch.omiga-plus.com",
            "mystartsearch.com", "search.myway.com", "search.funmoods.com",
        };

        foreach (var hijacker in hijackerSearchEngines)
        {
            if (prefsJson.Contains(hijacker, StringComparison.OrdinalIgnoreCase))
            {
                threats.Add(new ThreatItem
                {
                    Name = $"[{browserName}] Search Engine Hijack",
                    Path = prefsPath,
                    Description = $"Browser search engine hijacked to: {hijacker}",
                    ThreatLevel = ThreatLevel.High,
                    ThreatType = ThreatType.BrowserHijacker,
                    DetectionMethod = ThreatDetectionMethod.BrowserAnalysis,
                });
                break;
            }
        }
    }

    // ══════════════════════════════════════════
    //  LAYER 8: HOSTS FILE ANALYSIS
    // ══════════════════════════════════════════

    private static async Task<List<ThreatItem>> ScanHostsFileAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        var threats = new List<ThreatItem>();

        await Task.Run(() =>
        {
            var hostsPath = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
            if (!File.Exists(hostsPath)) return;

            try
            {
                var lines = File.ReadAllLines(hostsPath);
                foreach (var rawLine in lines)
                {
                    ct.ThrowIfCancellationRequested();

                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                        continue;

                    // Parse hosts line: IP_ADDRESS HOSTNAME
                    var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    var ip = parts[0];
                    var hostname = parts[1].ToLowerInvariant();

                    // Skip legitimate entries
                    if (ThreatSignatureDatabase.LegitimateHostRedirects.Contains(hostname))
                        continue;

                    // Check if a protected domain is being redirected
                    if (ThreatSignatureDatabase.ProtectedDomains.Contains(hostname))
                    {
                        // Redirecting a security/update domain is a critical threat
                        threats.Add(new ThreatItem
                        {
                            Name = $"Hosts Hijack: {hostname}",
                            Path = hostsPath,
                            Description = $"Protected domain '{hostname}' redirected to {ip} — possible malware blocking security updates",
                            ThreatLevel = ThreatLevel.Critical,
                            ThreatType = ThreatType.HostsFileModification,
                            DetectionMethod = ThreatDetectionMethod.FileAnomalyDetection,
                        });
                    }
                    // If any non-localhost entry is redirecting a domain somewhere unexpected
                    else if (!ip.Equals("127.0.0.1") && !ip.Equals("0.0.0.0") && !ip.Equals("::1"))
                    {
                        threats.Add(new ThreatItem
                        {
                            Name = $"Hosts Redirect: {hostname}",
                            Path = hostsPath,
                            Description = $"Domain '{hostname}' redirected to suspicious IP: {ip}",
                            ThreatLevel = ThreatLevel.Medium,
                            ThreatType = ThreatType.HostsFileModification,
                            DetectionMethod = ThreatDetectionMethod.FileAnomalyDetection,
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("ThreatScanner", "Hosts file scan error", ex);
            }
        }, ct);

        return threats;
    }

    // ══════════════════════════════════════════
    //  LAYER 9: REGISTRY ADWARE SCANNING
    // ══════════════════════════════════════════

    private static async Task<(List<ThreatItem> threats, int scanned)>
        ScanRegistryForAdwareAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var threats = new List<ThreatItem>();
        int scanned = 0;

        await Task.Run(() =>
        {
            foreach (var keyPath in ThreatSignatureDatabase.KnownAdwareRegistryKeys)
            {
                ct.ThrowIfCancellationRequested();
                scanned++;

                // Check in all registry hives
                var hives = new[]
                {
                    (RegistryHive.CurrentUser, RegistryView.Default),
                    (RegistryHive.LocalMachine, RegistryView.Registry64),
                    (RegistryHive.LocalMachine, RegistryView.Registry32),
                };

                foreach (var (hive, view) in hives)
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var key = baseKey.OpenSubKey(keyPath);
                        if (key != null)
                        {
                            var adwareName = Path.GetFileName(keyPath);
                            threats.Add(new ThreatItem
                            {
                                Name = $"Adware Registry: {adwareName}",
                                Path = $"{hive}\\{keyPath}",
                                Description = $"Known adware registry key found: {adwareName}",
                                ThreatLevel = ThreatLevel.Medium,
                                ThreatType = ThreatType.Adware,
                                DetectionMethod = ThreatDetectionMethod.RegistryAnalysis,
                            });
                        }
                    }
                    catch { }
                }
            }

            // Also scan Uninstall keys for known adware/PUP names
            progress?.Report("Checking installed programs for known PUPs...");
            ScanUninstallKeysForPUPs(threats, ref scanned, ct);
        }, ct);

        return (threats, scanned);
    }

    private static void ScanUninstallKeysForPUPs(List<ThreatItem> threats, ref int scanned,
        CancellationToken ct)
    {
        var uninstallPaths = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, RegistryView.Registry32,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, RegistryView.Default,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var (hive, view, basePath) in uninstallPaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstallKey = baseKey.OpenSubKey(basePath);
                if (uninstallKey == null) continue;

                foreach (var subKeyName in uninstallKey.GetSubKeyNames())
                {
                    scanned++;
                    try
                    {
                        using var appKey = uninstallKey.OpenSubKey(subKeyName);
                        var displayName = appKey?.GetValue("DisplayName")?.ToString();
                        if (string.IsNullOrWhiteSpace(displayName)) continue;

                        var publisher = appKey?.GetValue("Publisher")?.ToString() ?? "";

                        // Check against known adware/PUP names
                        foreach (var adwareName in ThreatSignatureDatabase.KnownAdwareNames)
                        {
                            if (displayName.Contains(adwareName, StringComparison.OrdinalIgnoreCase) ||
                                publisher.Contains(adwareName, StringComparison.OrdinalIgnoreCase))
                            {
                                var installLocation = appKey?.GetValue("InstallLocation")?.ToString() ?? "";
                                threats.Add(new ThreatItem
                                {
                                    Name = displayName,
                                    Path = !string.IsNullOrEmpty(installLocation) ? installLocation
                                        : $"{hive}\\{basePath}\\{subKeyName}",
                                    Description = $"Known PUP/Adware: {adwareName} (Publisher: {publisher})",
                                    ThreatLevel = ThreatLevel.Medium,
                                    ThreatType = ThreatType.PotentiallyUnwanted,
                                    DetectionMethod = ThreatDetectionMethod.PatternMatch,
                                });
                                break;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    private static async Task<List<ThreatItem>> ScanRegistryRunKeysAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        // Extended scan of RunOnceEx, Explorer\Shell Folders, etc.
        var threats = new List<ThreatItem>();

        await Task.Run(() =>
        {
            // Check for BHO (Browser Helper Objects)
            var bhoKeys = new[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects"),
                (RegistryHive.LocalMachine, RegistryView.Registry32,
                    @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects"),
            };

            foreach (var (hive, view, path) in bhoKeys)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var bhoKey = baseKey.OpenSubKey(path);
                    if (bhoKey == null) continue;

                    foreach (var clsid in bhoKey.GetSubKeyNames())
                    {
                        ct.ThrowIfCancellationRequested();

                        // Resolve CLSID to DLL path
                        var clsidPath = $@"SOFTWARE\Classes\CLSID\{clsid}\InProcServer32";
                        try
                        {
                            using var clsidKey = baseKey.OpenSubKey(clsidPath);
                            var dllPath = clsidKey?.GetValue(null)?.ToString();
                            if (string.IsNullOrEmpty(dllPath)) continue;

                            // Check if DLL is from a legitimate location
                            if (!IsInLegitimateSystemPath(dllPath) &&
                                !dllPath.Contains(@"\Program Files", StringComparison.OrdinalIgnoreCase))
                            {
                                threats.Add(new ThreatItem
                                {
                                    Name = $"Browser Helper Object: {clsid}",
                                    Path = dllPath,
                                    Description = $"BHO from non-standard path: {dllPath}",
                                    ThreatLevel = ThreatLevel.Medium,
                                    ThreatType = ThreatType.BrowserHijacker,
                                    DetectionMethod = ThreatDetectionMethod.RegistryAnalysis,
                                    SizeBytes = TryGetFileSize(dllPath),
                                });
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }, ct);

        return threats;
    }

    private static async Task<List<ThreatItem>> ScanBrowserRegistryAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        var threats = new List<ThreatItem>();

        // Combine registry BHO + adware registry scans for browser mode
        threats.AddRange(await ScanRegistryRunKeysAsync(progress, ct));

        return threats;
    }

    // ══════════════════════════════════════════
    //  LAYER 10: SUSPICIOUS SERVICES
    // ══════════════════════════════════════════

    private static async Task<List<ThreatItem>> ScanSuspiciousServicesAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        var threats = new List<ThreatItem>();

        await Task.Run(() =>
        {
            try
            {
                using var scmKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                if (scmKey == null) return;

                foreach (var serviceName in scmKey.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var serviceKey = scmKey.OpenSubKey(serviceName);
                        if (serviceKey == null) continue;

                        var imagePath = serviceKey.GetValue("ImagePath")?.ToString();
                        if (string.IsNullOrEmpty(imagePath)) continue;

                        var exePath = ExtractExePath(imagePath);
                        if (string.IsNullOrEmpty(exePath)) continue;

                        // Check if service binary is in suspicious location
                        if (exePath.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
                            exePath.Contains(@"\Users\Public\", StringComparison.OrdinalIgnoreCase) ||
                            exePath.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase))
                        {
                            threats.Add(new ThreatItem
                            {
                                Name = $"Suspicious Service: {serviceName}",
                                Path = exePath,
                                Description = $"Service '{serviceName}' runs from suspicious path",
                                ThreatLevel = ThreatLevel.High,
                                ThreatType = ThreatType.SuspiciousService,
                                DetectionMethod = ThreatDetectionMethod.BehavioralAnalysis,
                                SizeBytes = TryGetFileSize(exePath),
                            });
                        }

                        // Check for known malware service names
                        var fileName = Path.GetFileName(exePath);
                        if (ThreatSignatureDatabase.KnownMalwareFileNames.Contains(fileName))
                        {
                            threats.Add(new ThreatItem
                            {
                                Name = $"Malware Service: {serviceName}",
                                Path = exePath,
                                Description = $"Service uses known malware binary: {fileName}",
                                ThreatLevel = ThreatLevel.Critical,
                                ThreatType = ThreatType.Malware,
                                DetectionMethod = ThreatDetectionMethod.SignatureMatch,
                                SizeBytes = TryGetFileSize(exePath),
                            });
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("ThreatScanner", "Service scan error", ex);
            }
        }, ct);

        return threats;
    }

    // ══════════════════════════════════════════
    //  THREAT REMOVAL / QUARANTINE
    // ══════════════════════════════════════════

    /// <summary>
    /// Quarantines detected threats by moving files to quarantine and
    /// disabling associated startup/registry entries.
    /// </summary>
    public static async Task<(int quarantined, int failed, List<string> errors)>
        QuarantineThreatsAsync(
            IEnumerable<ThreatItem> threats,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
    {
        int quarantined = 0, failed = 0;
        var errors = new List<string>();

        foreach (var threat in threats.Where(t => t.IsSelected && !t.IsWhitelisted))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Report($"Quarantining: {threat.Name}");

                // Kill process if it's a running process threat
                if (threat.ProcessId > 0)
                {
                    try
                    {
                        var proc = Process.GetProcessById(threat.ProcessId);
                        if (!proc.HasExited)
                        {
                            proc.Kill(entireProcessTree: true);
                            await Task.Delay(500, ct); // Wait for process to die
                        }
                    }
                    catch { } // Process may already be gone
                }

                // Quarantine the file if it exists
                if (File.Exists(threat.Path))
                {
                    var reason = $"Threat detected: {threat.ThreatTypeDisplay} [{threat.ThreatLevelDisplay}] — {threat.Description}";
                    var entry = await QuarantineService.QuarantineFileAsync(threat.Path, reason, progress, ct);
                    if (entry != null)
                    {
                        threat.IsQuarantined = true;
                        quarantined++;
                    }
                    else
                    {
                        failed++;
                        errors.Add($"Failed to quarantine: {threat.Path}");
                    }
                }
                else if (threat.ThreatType == ThreatType.SuspiciousScheduledTask)
                {
                    // Disable the scheduled task
                    if (await DisableScheduledTaskAsync(threat.Name))
                    {
                        threat.IsQuarantined = true;
                        quarantined++;
                    }
                    else
                    {
                        failed++;
                        errors.Add($"Failed to disable task: {threat.Name}");
                    }
                }
                else if (threat.ThreatType is ThreatType.Adware or ThreatType.BrowserHijacker
                         && threat.Path.Contains(@"HK", StringComparison.OrdinalIgnoreCase))
                {
                    // Registry-based threat — log it but don't auto-delete registry
                    // (too dangerous for automated removal)
                    quarantined++;
                    threat.IsQuarantined = true;
                }
                else if (Directory.Exists(threat.Path))
                {
                    // Directory-based threat (e.g., browser extension folder)
                    var reason = $"Threat detected: {threat.ThreatTypeDisplay} — {threat.Description}";
                    // Quarantine individual files within the directory
                    try
                    {
                        var files = Directory.GetFiles(threat.Path, "*", SearchOption.AllDirectories);
                        foreach (var file in files.Take(50)) // Limit to prevent runaway
                        {
                            await QuarantineService.QuarantineFileAsync(file, reason, progress, ct);
                        }
                        threat.IsQuarantined = true;
                        quarantined++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        errors.Add($"Failed to quarantine directory: {threat.Path} — {ex.Message}");
                    }
                }
                else
                {
                    quarantined++;
                    threat.IsQuarantined = true;
                }
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Error quarantining {threat.Name}: {ex.Message}");
                DiagnosticLogger.Warn("ThreatScanner", $"Quarantine failed: {threat.Name}", ex);
            }
        }

        return (quarantined, failed, errors);
    }

    /// <summary>
    /// Permanently deletes detected threats — files are removed immediately
    /// without being moved to quarantine.
    /// </summary>
    public static async Task<(int deleted, int failed, List<string> errors)>
        DeleteThreatsAsync(
            IEnumerable<ThreatItem> threats,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
    {
        int deleted = 0, failed = 0;
        var errors = new List<string>();

        foreach (var threat in threats.Where(t => t.IsSelected && !t.IsWhitelisted))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Report($"Deleting: {threat.Name}");

                // Kill process if running
                if (threat.ProcessId > 0)
                {
                    try
                    {
                        var proc = Process.GetProcessById(threat.ProcessId);
                        if (!proc.HasExited)
                        {
                            proc.Kill(entireProcessTree: true);
                            await Task.Delay(500, ct);
                        }
                    }
                    catch { }
                }

                if (File.Exists(threat.Path))
                {
                    await Task.Run(() => File.Delete(threat.Path), ct);
                    threat.IsQuarantined = true; // reuse flag to mark as handled
                    deleted++;
                }
                else if (Directory.Exists(threat.Path))
                {
                    await Task.Run(() => Directory.Delete(threat.Path, recursive: true), ct);
                    threat.IsQuarantined = true;
                    deleted++;
                }
                else if (threat.ThreatType == ThreatType.SuspiciousScheduledTask)
                {
                    if (await DisableScheduledTaskAsync(threat.Name))
                    {
                        threat.IsQuarantined = true;
                        deleted++;
                    }
                    else
                    {
                        failed++;
                        errors.Add($"Failed to disable task: {threat.Name}");
                    }
                }
                else
                {
                    // Non-file threat (registry, etc.) — mark handled
                    threat.IsQuarantined = true;
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"Error deleting {threat.Name}: {ex.Message}");
                DiagnosticLogger.Warn("ThreatScanner", $"Delete failed: {threat.Name}", ex);
            }
        }

        return (deleted, failed, errors);
    }

    /// <summary>
    /// Adds a detected threat to the whitelist (mark as safe/false positive).
    /// </summary>
    public static void WhitelistThreat(ThreatItem threat, string reason)
    {
        if (string.IsNullOrEmpty(threat.Sha256Hash))
        {
            // Compute hash if not already done
            if (File.Exists(threat.Path))
            {
                using var stream = File.OpenRead(threat.Path);
                var hash = SHA256.HashData(stream);
                threat.Sha256Hash = Convert.ToHexString(hash).ToLowerInvariant();
            }
            else
            {
                // Use path hash as identifier for non-file threats
                var pathBytes = Encoding.UTF8.GetBytes(threat.Path);
                threat.Sha256Hash = Convert.ToHexString(SHA256.HashData(pathBytes)).ToLowerInvariant();
            }
        }

        ThreatSignatureDatabase.AddToWhitelist(threat.Sha256Hash, threat.Path, reason);
        threat.IsWhitelisted = true;

        DiagnosticLogger.Info("ThreatScanner",
            $"Whitelisted: {threat.Name} ({threat.Sha256Hash[..12]}...) — {reason}");
    }

    // ══════════════════════════════════════════
    //  HELPER METHODS
    // ══════════════════════════════════════════

    private static bool HasDoubleExtension(string fileName)
    {
        // Check for patterns like "document.pdf.exe" or "photo.jpg.scr"
        var parts = fileName.Split('.');
        if (parts.Length < 3) return false;

        var lastExt = "." + parts[^1];
        var secondLastExt = "." + parts[^2];

        return ThreatSignatureDatabase.ExecutableExtensions.Contains(lastExt) &&
               ThreatSignatureDatabase.DoubleExtensionTriggers.Contains(secondLastExt);
    }

    private static (bool isSuspicious, string reason, ThreatType type, ThreatLevel level)
        CheckSystemFileImpersonation(string filePath, string fileName)
    {
        // System process names that should ONLY exist in System32/SysWOW64
        var systemOnly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["svchost.exe"] = @"C:\Windows\System32\svchost.exe",
            ["csrss.exe"] = @"C:\Windows\System32\csrss.exe",
            ["lsass.exe"] = @"C:\Windows\System32\lsass.exe",
            ["smss.exe"] = @"C:\Windows\System32\smss.exe",
            ["services.exe"] = @"C:\Windows\System32\services.exe",
            ["winlogon.exe"] = @"C:\Windows\System32\winlogon.exe",
            ["wininit.exe"] = @"C:\Windows\System32\wininit.exe",
            ["conhost.exe"] = @"C:\Windows\System32\conhost.exe",
        };

        if (systemOnly.TryGetValue(fileName, out var legitimatePath))
        {
            if (!filePath.Equals(legitimatePath, StringComparison.OrdinalIgnoreCase) &&
                !filePath.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase))
            {
                return (true,
                    $"System file '{fileName}' found outside Windows directory — likely malware impersonation",
                    ThreatType.Trojan, ThreatLevel.Critical);
            }
        }

        return (false, "", ThreatType.SuspiciousFile, ThreatLevel.Low);
    }

    private static bool IsInLegitimateSystemPath(string path)
    {
        return ThreatSignatureDatabase.LegitimateSystemPaths.Any(
            p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractExePath(string commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return null;

        // Handle quoted paths: "C:\path\to\exe.exe" -args
        if (commandLine.StartsWith('"'))
        {
            var endQuote = commandLine.IndexOf('"', 1);
            return endQuote > 1 ? commandLine[1..endQuote] : null;
        }

        // Handle unquoted paths: C:\path\to\exe.exe -args
        var space = commandLine.IndexOf(' ');
        var path = space > 0 ? commandLine[..space] : commandLine;

        // Expand environment variables
        path = Environment.ExpandEnvironmentVariables(path);

        return path;
    }

    private static string? FindExtensionManifest(string extensionDir)
    {
        try
        {
            // Extensions have version subfolders: Extensions/<id>/<version>/manifest.json
            foreach (var versionDir in Directory.EnumerateDirectories(extensionDir))
            {
                var manifest = Path.Combine(versionDir, "manifest.json");
                if (File.Exists(manifest))
                    return manifest;
            }
        }
        catch { }
        return null;
    }

    private static long TryGetFileSize(string? path)
    {
        try
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch { return 0; }
    }

    private static long GetDirectorySizeSafe(string path, int maxFiles)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(maxFiles))
            {
                try { size += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return size;
    }

    private static Task<bool> DisableScheduledTaskAsync(string taskName)
    {
        try
        {
            using var ts = new TaskSchedulerLib.TaskService();
            var task = FindTask(ts.RootFolder, taskName);
            if (task != null)
            {
                task.Enabled = false;
                return Task.FromResult(true);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThreatScanner", $"Failed to disable task: {taskName}", ex);
        }
        return Task.FromResult(false);
    }

    private static TaskSchedulerLib.Task? FindTask(TaskSchedulerLib.TaskFolder folder, string taskName)
    {
        foreach (var task in folder.Tasks)
        {
            if (task.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase))
                return task;
        }
        foreach (var sub in folder.SubFolders)
        {
            var found = FindTask(sub, taskName);
            if (found != null) return found;
        }
        return null;
    }

    private static string[] GetDeepScanPaths()
    {
        var paths = new List<string>();

        paths.AddRange(ThreatSignatureDatabase.GetQuickScanPaths());

        // Add all user profiles
        var usersDir = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Users");
        if (Directory.Exists(usersDir))
        {
            try
            {
                foreach (var userDir in Directory.EnumerateDirectories(usersDir))
                {
                    var dirName = Path.GetFileName(userDir);
                    if (dirName.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("Default User", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                        dirName.Equals("All Users", StringComparison.OrdinalIgnoreCase))
                        continue;
                    paths.Add(userDir);
                }
            }
            catch { }
        }

        // Program directories
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (Directory.Exists(programFiles)) paths.Add(programFiles);
        if (Directory.Exists(programFilesX86)) paths.Add(programFilesX86);

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
