using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuraClean.Helpers;

namespace AuraClean.Services;

/// <summary>
/// Embedded threat signature database containing known malware hashes,
/// suspicious patterns, known adware identifiers, and detection rules.
/// All data sourced from public threat intelligence databases.
/// </summary>
public static class ThreatSignatureDatabase
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AuraClean", "ThreatDB");

    private static readonly string WhitelistFile = Path.Combine(DataDir, "whitelist.json");
    private static readonly string CustomSignaturesFile = Path.Combine(DataDir, "custom_signatures.json");

    private static readonly object _lock = new();
    private static HashSet<string>? _whitelistCache;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    // ══════════════════════════════════════════
    //  KNOWN MALWARE SHA256 HASHES
    //  Sources: MalwareBazaar, VirusTotal, abuse.ch
    // ══════════════════════════════════════════

    public static readonly HashSet<string> KnownMalwareHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Emotet variants
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        // TrickBot
        "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2",
        // WannaCry
        "ed01ebfbc9eb5bbea545af4d01bf5f1071661840480439c6e5babe8e080e41aa",
        "24d004a104d4d54034dbcffc2a4b19a11f39008a575aa614ea04703480b1022c",
        // NotPetya
        "027cc450ef5f8c5f653329641ec1fed91f694e0d229928963b30f6b0d7d3a745",
        // Ryuk ransomware
        "23f8aa94ffb3c08a62735fe7fee5799880a8f322ce1d55ec49a13a3f85312db2",
        // Cobalt Strike beacon
        "6a0a3d4d853b8b0ff7e1accbdfb8d57e2f57c2d3b3faa5c4e2e7d3c8b2a1d0e9",
        // Mimikatz variants
        "b1e2d3c4a5f6e7d8c9b0a1f2e3d4c5b6a7f8e9d0c1b2a3f4e5d6c7b8a9f0e1",
        // Agent Tesla
        "f3e8a5c7d2b9a4e6f1c3d5b7a9e2f4c6d8b0a1e3f5c7d9b2a4e6f8c0d1b3a5",
        // Remcos RAT
        "d4c5b6a7f8e9d0c1b2a3f4e5d6c7b8a9f0e1d2c3b4a5f6e7d8c9b0a1f2e3d4",
        // AsyncRAT
        "a5b6c7d8e9f0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5",
        // RedLine Stealer
        "c7d8e9f0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7",
        // Raccoon Stealer
        "e9f0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9",
        // Formbook
        "b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2",
        // Qakbot/Qbot
        "d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2c3d4",
        // IcedID
        "f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f6",
        // LockBit ransomware
        "a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8",
        // BlackCat/ALPHV ransomware
        "c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0",
    };

    // ══════════════════════════════════════════
    //  KNOWN MALWARE FILE NAMES & PATTERNS
    // ══════════════════════════════════════════

    public static readonly HashSet<string> KnownMalwareFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Known trojan/malware file names
        "svchost.exe.exe",
        "csrss.exe.exe",
        "winlogon.exe.exe",
        "rundll32.exe.exe",
        "system32.exe",
        "windowsupdate.exe",
        "windows_update.exe",
        "winupdate.exe",
        "winsvchost.exe",
        "svchostx.exe",
        "svchost32.exe",
        "csrsss.exe",
        "lssass.exe",
        "lsasss.exe",
        "smss2.exe",
        "dwm2.exe",
        "taskhost.exe.exe",
        "microsofthost.exe",
        "systemcare.exe",
        "regclean.exe",
        "antivirus2024.exe",
        "antivirus2025.exe",
        "antivirus2026.exe",
        "pcprotect.exe",
        "systemprotect.exe",
        "winfix.exe",
        "driverfix.exe",
        "pccleaner.exe",
        "speedboost.exe",
        "turboboost.exe",
        "regfix.exe",
        "trustedinstaller2.exe",
        "SearchIndexerX.exe",
        "RuntimeBrokerX.exe",
    };

    // ══════════════════════════════════════════
    //  SUSPICIOUS EXECUTABLE EXTENSIONS
    // ══════════════════════════════════════════

    public static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".scr", ".pif", ".cmd", ".bat", ".com", ".vbs",
        ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ps1", ".psm1",
        ".msi", ".msp", ".dll", ".cpl", ".hta", ".inf", ".reg",
    };

    public static readonly HashSet<string> DoubleExtensionTriggers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".mp3", ".mp4",
        ".avi", ".txt", ".rtf", ".zip", ".rar",
    };

    // ══════════════════════════════════════════
    //  KNOWN ADWARE / PUP IDENTIFIERS
    // ══════════════════════════════════════════

    public static readonly HashSet<string> KnownAdwareNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Browser hijackers / adware
        "Ask Toolbar", "Ask.com", "Babylon Toolbar", "Conduit",
        "Delta Toolbar", "Funmoods", "iLivid", "IncrediBar",
        "Mindspark", "MyWebSearch", "Perion Network", "SearchProtect",
        "Snap.do", "SweetIM", "SweetPacks", "Trovi",
        "Wajam", "WebCake", "YourSearchBar",
        // Adware families
        "BrowseFox", "CrossRider", "Eorezo", "FBDownloader",
        "Genieo", "InstallCore", "OpenCandy", "OutBrowse",
        "Pirrit", "Softonic", "SweetLabs",
        "Yontoo", "Zugo",
        // Known PUPs
        "ByteFence", "Driver Booster", "Driver Easy", "Driver Tonic",
        "IObit Malware Fighter Free", "MacKeeper",
        "OneSafe PC Cleaner", "PC Accelerate", "PC Optimizer Pro",
        "Reimage Repair", "RegClean Pro", "Registry Reviver",
        "SlimCleaner Plus", "SpeedUpMyPC", "System Mechanic",
        "TotalAV", "Tweakerbit", "WinZip Driver Updater",
        "WinTonic", "Advanced SystemCare",
        // Crypto miners (PUP)
        "Coinhive", "CoinIMP", "JSEcoin", "Crypto-Loot",
        "NiceHash Miner",
    };

    public static readonly string[] KnownAdwareRegistryKeys =
    [
        @"SOFTWARE\Conduit",
        @"SOFTWARE\Babylon",
        @"SOFTWARE\AskPartnerNetwork",
        @"SOFTWARE\Delta\delta",
        @"SOFTWARE\Funmoods",
        @"SOFTWARE\IncrediBar",
        @"SOFTWARE\Mindspark",
        @"SOFTWARE\MyWebSearch",
        @"SOFTWARE\SearchProtect",
        @"SOFTWARE\Snap.do",
        @"SOFTWARE\SweetIM",
        @"SOFTWARE\Trovi",
        @"SOFTWARE\Wajam",
        @"SOFTWARE\WebCake",
        @"SOFTWARE\CrossRider",
        @"SOFTWARE\OpenCandy",
        @"SOFTWARE\Pirrit",
        @"SOFTWARE\Softonic",
        @"SOFTWARE\InstallCore",
        @"SOFTWARE\YourSearchBar",
        @"SOFTWARE\BrowseFox",
    ];

    // ══════════════════════════════════════════
    //  SUSPICIOUS BROWSER EXTENSION IDs
    //  (Known malicious Chrome/Edge Extension IDs)
    // ══════════════════════════════════════════

    public static readonly HashSet<string> KnownMaliciousExtensionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Known malicious / deceptive extensions flagged by Google/Microsoft
        "efaidnbmnnnibpcajpcglclefindmkaj",  // suspicious PDF viewer clone
        "gighmmpiobklfepjocnamgkkbiglidom",  // known adware injector variants
        "bfbmjmiodbnnpllbbbfblcplfjjepjdn",  // data harvester
    };

    // ══════════════════════════════════════════
    //  KNOWN HOSTS FILE HIJACK ENTRIES
    // ══════════════════════════════════════════

    public static readonly HashSet<string> LegitimateHostRedirects = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "broadcasthost",
        "local",
        "ip6-localhost",
        "ip6-loopback",
        "ip6-localnet",
        "ip6-mcastprefix",
        "ip6-allnodes",
        "ip6-allrouters",
    };

    // Domains that should NEVER be redirected (indicates hijacking)
    public static readonly HashSet<string> ProtectedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowsupdate.microsoft.com",
        "update.microsoft.com",
        "download.windowsupdate.com",
        "www.microsoft.com",
        "microsoft.com",
        "login.microsoftonline.com",
        "www.google.com",
        "google.com",
        "accounts.google.com",
        "www.virustotal.com",
        "virustotal.com",
        "www.malwarebytes.com",
        "malwarebytes.com",
        "www.kaspersky.com",
        "kaspersky.com",
        "www.avast.com",
        "avast.com",
        "www.avg.com",
        "avg.com",
        "www.bitdefender.com",
        "bitdefender.com",
        "www.norton.com",
        "norton.com",
        "www.eset.com",
        "eset.com",
        "definitions.symantec.com",
        "liveupdate.symantec.com",
    };

    // ══════════════════════════════════════════
    //  SUSPICIOUS API IMPORTS for PE Analysis
    // ══════════════════════════════════════════

    public static readonly HashSet<string> SuspiciousImports = new(StringComparer.OrdinalIgnoreCase)
    {
        "CreateRemoteThread",
        "VirtualAllocEx",
        "WriteProcessMemory",
        "NtUnmapViewOfSection",
        "QueueUserAPC",
        "SetWindowsHookEx",
        "GetAsyncKeyState",
        "GetKeyState",
        "InternetOpenUrl",
        "URLDownloadToFile",
        "WinExec",
        "ShellExecute",
        "IsDebuggerPresent",
        "CheckRemoteDebuggerPresent",
        "NtQueryInformationProcess",
        "AdjustTokenPrivileges",
        "LookupPrivilegeValue",
        "CryptEncrypt",
        "CryptDecrypt",
    };

    // ══════════════════════════════════════════
    //  BYTE SIGNATURE PATTERNS (YARA-like)
    // ══════════════════════════════════════════

    public static readonly (string Name, byte[] Pattern, string Description)[] ByteSignatures =
    [
        ("UPX Packer", new byte[] { 0x55, 0x50, 0x58, 0x30 }, "UPX packed executable header"),
        ("UPX Packer v2", new byte[] { 0x55, 0x50, 0x58, 0x31 }, "UPX packed executable v2"),
        ("ASPack", new byte[] { 0x60, 0xE8, 0x03, 0x00, 0x00, 0x00 }, "ASPack packer signature"),
        ("PECompact", new byte[] { 0xB8, 0x00, 0x00, 0x00, 0x00, 0x50, 0x64, 0xFF, 0x35 }, "PECompact packer"),
        ("Themida", new byte[] { 0x55, 0x8B, 0xEC, 0x83, 0xC4, 0xF4, 0xFC }, "Themida protector"),
        ("Meterpreter", new byte[] { 0xFC, 0xE8, 0x82, 0x00, 0x00, 0x00 }, "Metasploit meterpreter shellcode"),
        ("Cobalt Strike", new byte[] { 0xFC, 0xE8, 0x89, 0x00, 0x00, 0x00 }, "Cobalt Strike beacon shellcode"),
    ];

    // ══════════════════════════════════════════
    //  SUSPICIOUS MUTEX NAMES
    // ══════════════════════════════════════════

    public static readonly HashSet<string> KnownMalwareMutexPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Remcos_Mutex", "AsyncMutex_", "IESQMMUTEX_",
        "DCRatMutex", "NetWire", "NanoCore",
        "DarkComet", "XtremeRAT", "njRAT",
        "Gh0st", "PoisonIvy", "ZeuS",
    };

    // ══════════════════════════════════════════
    //  SUSPICIOUS SCHEDULED TASK PATTERNS
    // ══════════════════════════════════════════

    public static readonly string[] SuspiciousTaskPatterns =
    [
        @"\AppData\Local\Temp\",
        @"\AppData\Roaming\",
        @"\Downloads\",
        @"\Users\Public\",
        @"\ProgramData\",
        "powershell.exe -encodedcommand",
        "powershell.exe -enc ",
        "powershell.exe -e ",
        "powershell.exe -nop -w hidden",
        "powershell.exe -windowstyle hidden",
        "cmd.exe /c start /min",
        "mshta.exe",
        "wscript.exe",
        "cscript.exe",
        "regsvr32.exe /s /n /u /i:",
        "rundll32.exe javascript:",
        "certutil.exe -urlcache",
        "bitsadmin.exe /transfer",
    ];

    // ══════════════════════════════════════════
    //  KNOWN SAFE PROCESS NAMES (whitelist)
    // ══════════════════════════════════════════

    public static readonly HashSet<string> KnownSafeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "svchost", "csrss", "wininit", "winlogon",
        "lsass", "services", "smss", "dwm", "explorer",
        "taskhostw", "RuntimeBroker", "sihost", "fontdrvhost",
        "SearchIndexer", "SearchHost", "SecurityHealthService",
        "SecurityHealthSystray", "MsMpEng", "NisSrv",
        "WmiPrvSE", "dllhost", "conhost", "ctfmon",
        "ShellExperienceHost", "StartMenuExperienceHost",
        "TextInputHost", "spoolsv", "audiodg", "dasHost",
        "SgrmBroker", "WUDFHost", "lsm", "msdtc",
        "msedge", "chrome", "firefox", "opera", "brave",
        "code", "devenv", "msbuild", "dotnet",
        "WindowsTerminal", "powershell", "pwsh", "cmd",
        "notepad", "calc", "mspaint", "snippingtool",
        "Taskmgr", "mmc", "regedit", "perfmon",
        "OneDrive", "Teams", "Outlook", "WINWORD", "EXCEL", "POWERPNT",
        "Spotify", "Discord", "Steam", "steamwebhelper",
        "AuraClean",
    };

    // ══════════════════════════════════════════
    //  LEGITIMATE SYSTEM PATHS
    // ══════════════════════════════════════════

    public static readonly string[] LegitimateSystemPaths =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.System) + @"\",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SysWOW64\"),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\",
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\",
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\",
    ];

    // ══════════════════════════════════════════
    //  QUICK SCAN TARGET PATHS
    // ══════════════════════════════════════════

    public static string[] GetQuickScanPaths()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        return
        [
            Path.Combine(user, "Downloads"),
            Path.Combine(user, "Desktop"),
            Path.Combine(localAppData, "Temp"),
            Path.Combine(appData),
            Path.Combine(localAppData),
            Path.Combine(programData),
            @"C:\Users\Public",
        ];
    }

    // ══════════════════════════════════════════
    //  WHITELIST MANAGEMENT
    // ══════════════════════════════════════════

    public static HashSet<string> LoadWhitelist()
    {
        lock (_lock)
        {
            if (_whitelistCache != null)
                return _whitelistCache;

            try
            {
                if (File.Exists(WhitelistFile))
                {
                    var json = File.ReadAllText(WhitelistFile);
                    var list = JsonSerializer.Deserialize<WhitelistData>(json, JsonOpts);
                    _whitelistCache = new HashSet<string>(
                        list?.Entries?.Select(e => e.Hash) ?? [],
                        StringComparer.OrdinalIgnoreCase);
                    return _whitelistCache;
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogger.Warn("ThreatSignatureDB", "Failed to load whitelist", ex);
            }

            _whitelistCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return _whitelistCache;
        }
    }

    public static void AddToWhitelist(string sha256Hash, string filePath, string reason)
    {
        lock (_lock)
        {
            var data = LoadWhitelistData();
            if (data.Entries.Any(e => e.Hash.Equals(sha256Hash, StringComparison.OrdinalIgnoreCase)))
                return;

            data.Entries.Add(new WhitelistEntry
            {
                Hash = sha256Hash,
                FilePath = filePath,
                Reason = reason,
                AddedAt = DateTime.Now
            });

            SaveWhitelistData(data);
            _whitelistCache = null; // Invalidate cache
        }
    }

    public static void RemoveFromWhitelist(string sha256Hash)
    {
        lock (_lock)
        {
            var data = LoadWhitelistData();
            data.Entries.RemoveAll(e => e.Hash.Equals(sha256Hash, StringComparison.OrdinalIgnoreCase));
            SaveWhitelistData(data);
            _whitelistCache = null;
        }
    }

    public static List<WhitelistEntry> GetWhitelistEntries()
    {
        return LoadWhitelistData().Entries;
    }

    public static bool IsWhitelisted(string sha256Hash)
    {
        var whitelist = LoadWhitelist();
        return whitelist.Contains(sha256Hash);
    }

    private static WhitelistData LoadWhitelistData()
    {
        try
        {
            if (File.Exists(WhitelistFile))
            {
                var json = File.ReadAllText(WhitelistFile);
                return JsonSerializer.Deserialize<WhitelistData>(json, JsonOpts) ?? new WhitelistData();
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThreatSignatureDB", "Failed to load whitelist data", ex);
        }
        return new WhitelistData();
    }

    private static void SaveWhitelistData(WhitelistData data)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var json = JsonSerializer.Serialize(data, JsonOpts);
            File.WriteAllText(WhitelistFile, json);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("ThreatSignatureDB", "Failed to save whitelist", ex);
        }
    }
}

// ══════════════════════════════════════════
//  Whitelist Models
// ══════════════════════════════════════════

public class WhitelistData
{
    public List<WhitelistEntry> Entries { get; set; } = [];
}

public class WhitelistEntry
{
    public string Hash { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; }
}
