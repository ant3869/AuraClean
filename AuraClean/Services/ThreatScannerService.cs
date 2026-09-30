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
        await Task.Run(() => FinalizeResult(result, threats, sw), ct);

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
        await Task.Run(() => FinalizeResult(result, threats, sw), ct);

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

        percentProgress?.Report(100);
        await Task.Run(() => FinalizeResult(result, fileScanResult.threats, sw), ct);

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
        await Task.Run(() => FinalizeResult(result, threats, sw), ct);

        return result;
    }

    /// <summary>
    /// Removes whitelisted and duplicate detections and applies the default selection policy:
    /// only high-confidence findings (signature matches, Critical items, double-extension files)
    /// start selected. Heuristic hits must be opted in by the user because they can be wrong.
    /// </summary>
    private static void FinalizeResult(ThreatScanResult result, List<ThreatItem> threats, Stopwatch sw)
    {
        sw.Stop();
        var whitelist = ThreatSignatureDatabase.LoadWhitelist();
        var finalThreats = new List<ThreatItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var threat in threats)
        {
            if (string.IsNullOrEmpty(threat.Sha256Hash))
                threat.Sha256Hash = ComputeIdentityHash(threat);

            if (whitelist.Contains(threat.Sha256Hash))
            {
                threat.IsWhitelisted = true;
                continue;
            }

            var dedupeKey = $"{threat.ThreatType}|{threat.Path}|{threat.RegistryValueName}|{threat.HostsEntryHostName}|{threat.TaskPath}";
            if (!seen.Add(dedupeKey))
                continue;

            bool highConfidence = threat.DetectionMethod == ThreatDetectionMethod.SignatureMatch ||
                                  threat.ThreatLevel == ThreatLevel.Critical ||
                                  threat.ThreatType == ThreatType.DoubleExtension;
            threat.IsSelected = highConfidence;
            threat.RemediationNote = DescribeRemediation(threat);
            if (!highConfidence && string.IsNullOrEmpty(threat.RemediationNote))
                threat.RemediationNote = "Heuristic finding — review before acting.";

            finalThreats.Add(threat);
        }

        result.Threats = finalThreats;
        result.ScanDuration = sw.Elapsed;
    }

    /// <summary>
    /// Stable identity used for whitelisting: the file's content hash when the threat is a file,
    /// otherwise a hash of the entry's location (registry value, task, hosts entry, folder).
    /// </summary>
    internal static string ComputeIdentityHash(ThreatItem threat)
    {
        if (!string.IsNullOrEmpty(threat.Path) && File.Exists(threat.Path) &&
            string.IsNullOrEmpty(threat.HostsEntryHostName) && string.IsNullOrEmpty(threat.TaskPath))
        {
            try
            {
                using var stream = new FileStream(threat.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through to the location-based identity.
                DiagnosticLogger.Warn("ThreatScanner", $"Hash unreadable, using location identity: {threat.Path}", ex);
            }
        }

        var identity = $"{threat.ThreatType}|{threat.Path}|{threat.RegistryKeyPath}|{threat.RegistryValueName}|{threat.HostsEntryHostName}|{threat.TaskPath}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToLowerInvariant()))).ToLowerInvariant();
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
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThreatScanner", $"Hash failed, treating as unknown: {filePath}", ex);
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

            // Check 5: PE Analysis for executables. Validly signed binaries are skipped:
            // packers, high entropy, and injection APIs are all common in legitimate signed software.
            if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".scr", StringComparison.OrdinalIgnoreCase))
            {
                // The signature check is comparatively expensive, so it only runs for files the
                // PE heuristics already consider suspicious.
                var peResult = await AnalyzePeFileAsync(filePath, ct);
                if (peResult.isSuspicious && !AuthenticodeHelper.IsSignedAndTrusted(filePath))
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
            var length = new FileInfo(filePath).Length;
            if (length < 64)
                return (false, "", ThreatType.SuspiciousFile, ThreatLevel.Low);

            var buffer = new byte[Math.Min(length, 65536)];
            int read;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                read = await fs.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
            }
            if (read < buffer.Length)
                Array.Resize(ref buffer, read);

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
            if (suspiciousImports.Count >= ThreatSignatureDatabase.SuspiciousImportThreshold)
            {
                return (true,
                    $"Unsigned file importing process-injection APIs: {string.Join(", ", suspiciousImports.Take(5))}",
                    ThreatType.SuspiciousFile, ThreatLevel.High);
            }

            return (false, "", ThreatType.SuspiciousFile, ThreatLevel.Low);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThreatScanner", $"Heuristic analysis failed for {filePath}", ex);
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
            // Import names are NUL-terminated in the import table; requiring the terminator
            // avoids matching longer, unrelated identifiers.
            if (content.Contains(import + "\0", StringComparison.Ordinal))
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
                        catch (Exception ex)
                        {
                            // Access denied — skip
                            DiagnosticLogger.Warn("ThreatScanner", $"Skipped process with unreadable MainModule: PID {proc.Id} ({proc.ProcessName})", ex);
                            continue;
                        }

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
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Warn("ThreatScanner", "Failed to inspect process", ex);
                    }
                    finally
                    {
                        try { proc.Dispose(); } catch (Exception ex) { DiagnosticLogger.Warn("ThreatScanner", $"Process dispose failed for PID {proc.Id}", ex); }
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
                            foreach (var subDir in Directory.EnumerateDirectories(currentDir, "*", PathSafety.TopLevelNoReparse))
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
                        catch (Exception ex)
                        {
                            DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable scan directory: {currentDir}", ex);
                        }
                    }

                    // Scan files in current directory
                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(currentDir); }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable scan directory: {currentDir}", ex);
                        continue;
                    }

                    // Filter to scannable files, then process in parallel
                    var candidateFiles = files
                        .Where(f =>
                        {
                            var ext = Path.GetExtension(f);
                            return ThreatSignatureDatabase.ExecutableExtensions.Contains(ext) ||
                                   ext.Equals(".sys", StringComparison.OrdinalIgnoreCase);
                        })
                        .ToList();

                    var parallelOptions = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 4,
                        CancellationToken = ct
                    };

                    await Parallel.ForEachAsync(candidateFiles, parallelOptions, async (filePath, token) =>
                    {
                        try
                        {
                            Interlocked.Increment(ref totalScanned);

                            if (totalScanned % 200 == 0)
                                progress?.Report($"Scanned {totalScanned} files... ({Path.GetFileName(currentDir)})");

                            // Quick size check — skip very large files for speed
                            var fileInfo = new FileInfo(filePath);
                            if (fileInfo.Length > 200_000_000) // >200MB, skip heuristic
                                return;

                            // Heuristic analysis
                            var heurResult = await AnalyzeFileHeuristicsAsync(filePath, token);
                            if (heurResult.isSuspicious)
                            {
                                var hash = await ComputeSha256Async(filePath, token);
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
                                return;
                            }

                            // Signature hash check for executables (empty files have no meaningful hash)
                            if (fileInfo.Length > 0 && fileInfo.Length < 50_000_000)
                            {
                                var hash = await ComputeSha256Async(filePath, token);
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
                        catch (Exception ex)
                        {
                            DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable file during scan: {filePath}", ex);
                        }
                    }); // end Parallel.ForEachAsync per-directory candidates

                    } // end while (stack has dirs)
                } // end foreach rootDir
            } // end Task.Run directory traversal
            , ct);

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
                (RegistryHive.CurrentUser, RegistryView.Default, "HKCU",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM (64-bit)",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM (32-bit)",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (RegistryHive.CurrentUser, RegistryView.Default, "HKCU",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
                (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM (64-bit)",
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
            };

            foreach (var (hive, view, hiveLabel, keyPath) in runKeys)
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
                                RegistryKeyPath = $"{hiveLabel}\\{keyPath}",
                                RegistryValueName = valueName,
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable startup registry key: {hiveLabel}\\{keyPath}", ex);
                }
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
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable startup folder: {folder}", ex);
                }
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
                                    TaskPath = task.Path,
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
                                if (!threats.Any(t => t.TaskPath == task.Path))
                                {
                                    threats.Add(new ThreatItem
                                    {
                                        Name = task.Name,
                                        Path = path,
                                        TaskPath = task.Path,
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
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable scheduled task: {task.Path}", ex);
                }
            }

            foreach (var subFolder in folder.SubFolders)
            {
                ScanTaskFolder(subFolder, threats, ct);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThreatScanner", $"Failed scanning task folder: {folder.Path}", ex);
        }
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
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn("ThreatScanner", $"Failed enumerating {browserName} profiles in {basePath}", ex);
                }

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
                            catch (Exception ex)
                            {
                                DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable {browserName} extension manifest: {manifestPath}", ex);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable {browserName} extension folder: {extDir}", ex);
                    }
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
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable {browserName} Preferences file: {prefsFile}", ex);
                    }
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
                            HostsEntryHostName = hostname,
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
                            HostsEntryHostName = hostname,
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
                    (RegistryHive.CurrentUser, RegistryView.Default, "HKCU"),
                    (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM (64-bit)"),
                    (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM (32-bit)"),
                };

                foreach (var (hive, view, hiveLabel) in hives)
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
                                Path = $"{hiveLabel}\\{keyPath}",
                                Description = $"Known adware registry key found: {adwareName}",
                                ThreatLevel = ThreatLevel.Medium,
                                ThreatType = ThreatType.Adware,
                                DetectionMethod = ThreatDetectionMethod.RegistryAnalysis,
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable adware key {hiveLabel}\\{keyPath}", ex);
                    }
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
                                        : $"{(hive == RegistryHive.CurrentUser ? "HKCU" : view == RegistryView.Registry32 ? "HKLM (32-bit)" : "HKLM (64-bit)")}\\{basePath}\\{subKeyName}",
                                    Description = $"Known PUP/Adware: {adwareName} (Publisher: {publisher})",
                                    ThreatLevel = ThreatLevel.Medium,
                                    ThreatType = ThreatType.PotentiallyUnwanted,
                                    DetectionMethod = ThreatDetectionMethod.PatternMatch,
                                });
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable uninstall entry '{subKeyName}'", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("ThreatScanner", $"Failed scanning uninstall hive: {basePath}", ex);
            }
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
                        catch (Exception ex)
                        {
                            DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable BHO entry '{clsid}'", ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn("ThreatScanner", $"Failed scanning BHO hive: {hive} ({path})", ex);
                }
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
                    catch (Exception ex)
                    {
                        DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable service entry: {serviceName}", ex);
                    }
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

    /// <summary>Outcome of a quarantine or delete request.</summary>
    public sealed record ThreatActionResult(int Handled, int Failed, int ManualActionRequired, List<string> Messages);

    private enum RemediationKind
    {
        DisableScheduledTask,
        RemoveHostsEntry,
        RemoveAutorunValue,
        DeleteRegistryKey,
        FileTarget,
        DirectoryTarget,
        ManualOnly
    }

    private static RemediationKind Classify(ThreatItem threat, out string manualReason)
    {
        manualReason = string.Empty;

        if (threat.ThreatType == ThreatType.SuspiciousScheduledTask)
            return RemediationKind.DisableScheduledTask;

        if (threat.ThreatType == ThreatType.HostsFileModification)
        {
            if (!string.IsNullOrEmpty(threat.HostsEntryHostName))
                return RemediationKind.RemoveHostsEntry;
            manualReason = "Edit the hosts file manually to remove the redirect.";
            return RemediationKind.ManualOnly;
        }

        if (!string.IsNullOrEmpty(threat.RegistryKeyPath) && !string.IsNullOrEmpty(threat.RegistryValueName))
            return RemediationKind.RemoveAutorunValue;

        if (threat.ThreatType == ThreatType.BrowserHijacker &&
            Path.GetFileName(threat.Path).Equals("Preferences", StringComparison.OrdinalIgnoreCase))
        {
            manualReason = "Reset the search engine in the browser's settings (the profile file is not modified).";
            return RemediationKind.ManualOnly;
        }

        if (threat.Path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase) ||
            threat.Path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase))
        {
            return threat.ThreatType is ThreatType.Adware or ThreatType.BrowserHijacker
                ? RemediationKind.DeleteRegistryKey
                : ManualWith("Remove this registry entry with the Uninstaller or manually.", out manualReason);
        }

        if (threat.ThreatType == ThreatType.PotentiallyUnwanted)
        {
            manualReason = "Remove this program with the Uninstaller so its own uninstaller runs.";
            return RemediationKind.ManualOnly;
        }

        if (File.Exists(threat.Path))
        {
            if (!PathSafety.IsSafeToDeleteFile(threat.Path, out var reason))
            {
                manualReason = $"{reason} It will not be moved.";
                return RemediationKind.ManualOnly;
            }
            return RemediationKind.FileTarget;
        }

        if (Directory.Exists(threat.Path))
        {
            if (!PathSafety.IsSafeToDeleteDirectory(threat.Path, out var reason))
            {
                manualReason = reason;
                return RemediationKind.ManualOnly;
            }
            return RemediationKind.DirectoryTarget;
        }

        manualReason = "The item no longer exists or cannot be removed automatically.";
        return RemediationKind.ManualOnly;

        static RemediationKind ManualWith(string reason, out string note)
        {
            note = reason;
            return RemediationKind.ManualOnly;
        }
    }

    /// <summary>Short explanation of what quarantine/delete will do for this item.</summary>
    private static string DescribeRemediation(ThreatItem threat) => Classify(threat, out var manual) switch
    {
        RemediationKind.DisableScheduledTask => "The scheduled task will be disabled.",
        RemediationKind.RemoveHostsEntry => "Only this hosts-file line will be removed (a backup is kept).",
        RemediationKind.RemoveAutorunValue => "The startup entry will be removed; the program file is quarantined when it is not a Windows component.",
        RemediationKind.DeleteRegistryKey => "The registry key will be deleted after a .reg backup.",
        RemediationKind.DirectoryTarget => "The folder's files will be moved to quarantine.",
        RemediationKind.FileTarget => string.Empty,
        _ => manual
    };

    /// <summary>
    /// Quarantines detected threats: files and folders are moved to quarantine, scheduled tasks
    /// are disabled, hosts redirects and autorun values are removed. Items that cannot be handled
    /// safely are reported as needing manual action instead of being silently marked handled.
    /// </summary>
    public static Task<ThreatActionResult> QuarantineThreatsAsync(
        IEnumerable<ThreatItem> threats,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        RemediateAsync(threats, permanentDelete: false, progress, ct);

    /// <summary>
    /// Permanently deletes detected threats. Same safety rules as quarantine; files are removed
    /// immediately instead of being moved.
    /// </summary>
    public static Task<ThreatActionResult> DeleteThreatsAsync(
        IEnumerable<ThreatItem> threats,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        RemediateAsync(threats, permanentDelete: true, progress, ct);

    private static async Task<ThreatActionResult> RemediateAsync(
        IEnumerable<ThreatItem> threats, bool permanentDelete,
        IProgress<string>? progress, CancellationToken ct)
    {
        int handled = 0, failed = 0, manual = 0;
        var messages = new List<string>();

        foreach (var threat in threats.Where(t => t.IsSelected && !t.IsWhitelisted && !t.IsQuarantined).ToList())
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"{(permanentDelete ? "Removing" : "Quarantining")}: {threat.Name}");

            try
            {
                var kind = Classify(threat, out var manualReason);
                if (kind == RemediationKind.ManualOnly)
                {
                    manual++;
                    threat.RemediationNote = manualReason;
                    messages.Add($"{threat.Name}: {manualReason}");
                    continue;
                }

                if (kind is RemediationKind.FileTarget or RemediationKind.RemoveAutorunValue)
                    await TerminateThreatProcessAsync(threat, ct);

                var (ok, message) = kind switch
                {
                    RemediationKind.DisableScheduledTask => await DisableScheduledTaskAsync(threat),
                    RemediationKind.RemoveHostsEntry => await Task.Run(() => HostsFileEditor.RemoveEntries(threat.HostsEntryHostName), ct),
                    RemediationKind.RemoveAutorunValue => await RemoveAutorunAsync(threat, permanentDelete, progress, ct),
                    RemediationKind.DeleteRegistryKey => await RegistryScannerService.DeleteRegistryKeyAsync(threat.Path),
                    RemediationKind.FileTarget => await HandleFileAsync(threat, threat.Path, permanentDelete, progress, ct),
                    RemediationKind.DirectoryTarget => await HandleDirectoryAsync(threat, permanentDelete, progress, ct),
                    _ => (false, "Unsupported item.")
                };

                if (ok)
                {
                    threat.IsQuarantined = true;
                    handled++;
                }
                else
                {
                    failed++;
                    messages.Add($"{threat.Name}: {message}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"{threat.Name}: {ex.Message}");
                DiagnosticLogger.Warn("ThreatScanner", $"Remediation failed: {threat.Name}", ex);
            }
        }

        return new ThreatActionResult(handled, failed, manual, messages);
    }

    /// <summary>
    /// Terminates the running process of a threat, but only if the PID still belongs to the same
    /// executable (PIDs are reused) and the executable is not a Windows component.
    /// </summary>
    private static async Task TerminateThreatProcessAsync(ThreatItem threat, CancellationToken ct)
    {
        if (threat.ProcessId <= 0 || threat.ProcessId == Environment.ProcessId)
            return;

        try
        {
            using var proc = Process.GetProcessById(threat.ProcessId);
            if (proc.HasExited)
                return;

            string? exePath = null;
            try { exePath = proc.MainModule?.FileName; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                DiagnosticLogger.Warn("ThreatScanner", $"Could not read MainModule for PID {threat.ProcessId}", ex);
            }

            bool sameExecutable = exePath != null &&
                                  string.Equals(exePath, threat.Path, StringComparison.OrdinalIgnoreCase);
            if (!sameExecutable || PathSafety.IsWithinWindowsDirectory(exePath!))
            {
                DiagnosticLogger.Warn("ThreatScanner",
                    $"Not terminating PID {threat.ProcessId}: it no longer matches {threat.Path} or is a Windows component.");
                return;
            }

            proc.Kill(entireProcessTree: true);
            await proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        catch (ArgumentException ex)
        {
            // Process already exited.
            DiagnosticLogger.Warn("ThreatScanner", $"Process already exited, skipping termination for PID {threat.ProcessId}", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            DiagnosticLogger.Warn("ThreatScanner", $"Could not terminate PID {threat.ProcessId}", ex);
        }
    }

    private static async Task<(bool Ok, string Message)> HandleFileAsync(
        ThreatItem threat, string path, bool permanentDelete, IProgress<string>? progress, CancellationToken ct)
    {
        if (!PathSafety.IsSafeToDeleteFile(path, out var reason))
            return (false, reason);

        if (permanentDelete)
        {
            await Task.Run(() =>
            {
                var info = new FileInfo(path);
                if (info.IsReadOnly) info.IsReadOnly = false;
                info.Delete();
            }, ct);
            return (true, "Deleted.");
        }

        var entry = await QuarantineService.QuarantineFileAsync(path, BuildQuarantineReason(threat), progress, ct);
        return entry != null ? (true, "Quarantined.") : (false, $"Could not quarantine {path} (it may be in use).");
    }

    private static async Task<(bool Ok, string Message)> HandleDirectoryAsync(
        ThreatItem threat, bool permanentDelete, IProgress<string>? progress, CancellationToken ct)
    {
        if (!PathSafety.IsSafeToDeleteDirectory(threat.Path, out var reason))
            return (false, reason);

        if (permanentDelete)
        {
            await Task.Run(() => Directory.Delete(threat.Path, recursive: true), ct);
            return (true, "Deleted.");
        }

        const int maxFiles = 5000;
        var files = await Task.Run(() =>
            Directory.EnumerateFiles(threat.Path, "*", PathSafety.RecursiveNoReparse).Take(maxFiles + 1).ToList(), ct);
        if (files.Count > maxFiles)
            return (false, $"Folder contains more than {maxFiles:N0} files; remove it manually.");

        int moved = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (await QuarantineService.QuarantineFileAsync(file, BuildQuarantineReason(threat), progress, ct) != null)
                moved++;
        }

        if (moved < files.Count)
            return (false, $"Quarantined {moved} of {files.Count} file(s); the rest are in use.");

        try
        {
            RemoveEmptyDirectories(threat.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("ThreatScanner", $"Folder emptied but not removed: {threat.Path}", ex);
        }

        return (true, "Quarantined.");
    }

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", PathSafety.RecursiveNoReparse)
                     .OrderByDescending(d => d.Length).ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }

        if (!Directory.EnumerateFileSystemEntries(root).Any())
            Directory.Delete(root);
    }

    /// <summary>
    /// Removes a malicious Run/RunOnce value (after exporting the key) and quarantines or deletes
    /// the program it launched — unless that program is a Windows component such as powershell.exe.
    /// </summary>
    private static async Task<(bool Ok, string Message)> RemoveAutorunAsync(
        ThreatItem threat, bool permanentDelete, IProgress<string>? progress, CancellationToken ct)
    {
        var (hive, view, subKey, _) = RegistryScannerService.ParseKeyPath(threat.RegistryKeyPath);
        if (hive == null || subKey == null)
            return (false, "Invalid startup registry location.");

        var backup = await RegistryScannerService.BackupRegistryKeyAsync(threat.RegistryKeyPath);
        if (backup == null)
            return (false, "Could not back up the startup key; nothing was changed.");

        using (var baseKey = RegistryKey.OpenBaseKey(hive.Value, view))
        using (var runKey = baseKey.OpenSubKey(subKey, writable: true))
        {
            runKey?.DeleteValue(threat.RegistryValueName, throwOnMissingValue: false);
        }

        DiagnosticLogger.Info("ThreatScanner",
            $"Removed autorun value '{threat.RegistryValueName}' from {threat.RegistryKeyPath} (backup: {backup})");

        if (!string.IsNullOrEmpty(threat.Path) && File.Exists(threat.Path) &&
            PathSafety.IsSafeToDeleteFile(threat.Path, out _))
        {
            var (fileOk, fileMessage) = await HandleFileAsync(threat, threat.Path, permanentDelete, progress, ct);
            if (!fileOk)
                return (true, $"Startup entry removed; program file kept: {fileMessage}");
        }

        return (true, "Startup entry removed.");
    }

    private static string BuildQuarantineReason(ThreatItem threat) =>
        $"Threat detected: {threat.ThreatTypeDisplay} [{threat.ThreatLevelDisplay}] — {threat.Description}";

    /// <summary>
    /// Adds a detected threat to the whitelist (mark as safe/false positive).
    /// </summary>
    public static void WhitelistThreat(ThreatItem threat, string reason)
    {
        if (string.IsNullOrEmpty(threat.Sha256Hash))
            threat.Sha256Hash = ComputeIdentityHash(threat);

        ThreatSignatureDatabase.AddToWhitelist(threat.Sha256Hash, threat.Path, reason);
        threat.IsWhitelisted = true;

        DiagnosticLogger.Info("ThreatScanner",
            $"Whitelisted: {threat.Name} ({threat.Sha256Hash[..Math.Min(12, threat.Sha256Hash.Length)]}...) — {reason}");
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
        var sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var winDir = Path.GetDirectoryName(sys32) ?? @"C:\Windows";

        var systemOnly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["svchost.exe"] = Path.Combine(sys32, "svchost.exe"),
            ["csrss.exe"] = Path.Combine(sys32, "csrss.exe"),
            ["lsass.exe"] = Path.Combine(sys32, "lsass.exe"),
            ["smss.exe"] = Path.Combine(sys32, "smss.exe"),
            ["services.exe"] = Path.Combine(sys32, "services.exe"),
            ["winlogon.exe"] = Path.Combine(sys32, "winlogon.exe"),
            ["wininit.exe"] = Path.Combine(sys32, "wininit.exe"),
            ["conhost.exe"] = Path.Combine(sys32, "conhost.exe"),
        };

        if (systemOnly.TryGetValue(fileName, out var legitimatePath))
        {
            if (!filePath.Equals(legitimatePath, StringComparison.OrdinalIgnoreCase) &&
                !filePath.StartsWith(winDir + @"\", StringComparison.OrdinalIgnoreCase))
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
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThreatScanner", $"Failed enumerating extension dir: {extensionDir}", ex);
        }
        return null;
    }

    private static long TryGetFileSize(string? path)
    {
        try
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThreatScanner", $"Failed reading file size: {path}", ex);
            return 0;
        }
    }

    private static long GetDirectorySizeSafe(string path, int maxFiles)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(maxFiles))
            {
                try { size += new FileInfo(file).Length; }
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn("ThreatScanner", $"Skipped unreadable file during size calc: {file}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThreatScanner", $"Failed directory size calc: {path}", ex);
        }
        return size;
    }

    private static Task<(bool Ok, string Message)> DisableScheduledTaskAsync(ThreatItem threat) => Task.Run(() =>
    {
        try
        {
            using var ts = new TaskSchedulerLib.TaskService();
            var task = !string.IsNullOrEmpty(threat.TaskPath)
                ? ts.GetTask(threat.TaskPath)
                : FindTask(ts.RootFolder, threat.Name);

            if (task == null)
                return (false, "Scheduled task not found.");

            using (task)
            {
                task.Enabled = false;
                return (true, "Scheduled task disabled.");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThreatScanner", $"Failed to disable task: {threat.TaskPath}", ex);
            return (false, $"Could not disable the task: {ex.Message}");
        }
    });

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
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("ThreatScanner", $"Failed enumerating user profiles in {usersDir}", ex);
            }
        }

        // Program directories
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (Directory.Exists(programFiles)) paths.Add(programFiles);
        if (Directory.Exists(programFilesX86)) paths.Add(programFilesX86);

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
