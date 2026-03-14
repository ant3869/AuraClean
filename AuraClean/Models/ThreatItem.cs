using CommunityToolkit.Mvvm.ComponentModel;

namespace AuraClean.Models;

public enum ThreatLevel
{
    Low,
    Medium,
    High,
    Critical
}

public enum ThreatType
{
    Malware,
    Adware,
    PotentiallyUnwanted,
    BrowserHijacker,
    Trojan,
    Worm,
    Rootkit,
    Spyware,
    Ransomware,
    Miner,
    SuspiciousFile,
    SuspiciousProcess,
    SuspiciousStartup,
    SuspiciousScheduledTask,
    SuspiciousService,
    HostsFileModification,
    SuspiciousBrowserExtension,
    PackedExecutable,
    DoubleExtension,
    HiddenExecutable
}

public enum ThreatDetectionMethod
{
    SignatureMatch,
    HeuristicAnalysis,
    PatternMatch,
    BehavioralAnalysis,
    EntropyAnalysis,
    ProcessAnalysis,
    RegistryAnalysis,
    FileAnomalyDetection,
    BrowserAnalysis
}

public partial class ThreatItem : ObservableObject
{
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private ThreatLevel _threatLevel;
    [ObservableProperty] private ThreatType _threatType;
    [ObservableProperty] private ThreatDetectionMethod _detectionMethod;
    [ObservableProperty] private long _sizeBytes;
    [ObservableProperty] private string _sha256Hash = string.Empty;
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private int _processId;
    [ObservableProperty] private bool _isWhitelisted;
    [ObservableProperty] private bool _isQuarantined;
    [ObservableProperty] private DateTime _detectedAt = DateTime.Now;

    public string ThreatLevelDisplay => ThreatLevel switch
    {
        ThreatLevel.Low => "Low",
        ThreatLevel.Medium => "Medium",
        ThreatLevel.High => "High",
        ThreatLevel.Critical => "Critical",
        _ => "Unknown"
    };

    public string ThreatTypeDisplay => ThreatType switch
    {
        ThreatType.Malware => "Malware",
        ThreatType.Adware => "Adware",
        ThreatType.PotentiallyUnwanted => "PUP",
        ThreatType.BrowserHijacker => "Browser Hijacker",
        ThreatType.Trojan => "Trojan",
        ThreatType.Worm => "Worm",
        ThreatType.Rootkit => "Rootkit",
        ThreatType.Spyware => "Spyware",
        ThreatType.Ransomware => "Ransomware",
        ThreatType.Miner => "Crypto Miner",
        ThreatType.SuspiciousFile => "Suspicious File",
        ThreatType.SuspiciousProcess => "Suspicious Process",
        ThreatType.SuspiciousStartup => "Suspicious Startup",
        ThreatType.SuspiciousScheduledTask => "Suspicious Task",
        ThreatType.SuspiciousService => "Suspicious Service",
        ThreatType.HostsFileModification => "Hosts Modification",
        ThreatType.SuspiciousBrowserExtension => "Suspicious Extension",
        ThreatType.PackedExecutable => "Packed Executable",
        ThreatType.DoubleExtension => "Double Extension",
        ThreatType.HiddenExecutable => "Hidden Executable",
        _ => "Unknown"
    };

    public string DetectionMethodDisplay => DetectionMethod switch
    {
        ThreatDetectionMethod.SignatureMatch => "Signature",
        ThreatDetectionMethod.HeuristicAnalysis => "Heuristic",
        ThreatDetectionMethod.PatternMatch => "Pattern",
        ThreatDetectionMethod.BehavioralAnalysis => "Behavioral",
        ThreatDetectionMethod.EntropyAnalysis => "Entropy",
        ThreatDetectionMethod.ProcessAnalysis => "Process",
        ThreatDetectionMethod.RegistryAnalysis => "Registry",
        ThreatDetectionMethod.FileAnomalyDetection => "Anomaly",
        ThreatDetectionMethod.BrowserAnalysis => "Browser",
        _ => "Unknown"
    };

    public string FormattedSize => SizeBytes switch
    {
        0 => "",
        < 1024 => $"{SizeBytes} B",
        < 1_048_576 => $"{SizeBytes / 1024.0:F1} KB",
        < 1_073_741_824 => $"{SizeBytes / 1_048_576.0:F1} MB",
        _ => $"{SizeBytes / 1_073_741_824.0:F2} GB"
    };

    public string CategoryDisplay => ThreatType switch
    {
        ThreatType.Malware or ThreatType.Trojan or ThreatType.Worm
            or ThreatType.Rootkit or ThreatType.Ransomware => "Malware Threats",
        ThreatType.Adware or ThreatType.BrowserHijacker
            or ThreatType.SuspiciousBrowserExtension => "Adware & Browser Threats",
        ThreatType.Spyware or ThreatType.Miner => "Spyware & Miners",
        ThreatType.PotentiallyUnwanted => "Potentially Unwanted Programs",
        ThreatType.SuspiciousProcess or ThreatType.SuspiciousStartup
            or ThreatType.SuspiciousScheduledTask or ThreatType.SuspiciousService => "Suspicious Activity",
        ThreatType.HostsFileModification => "System Modifications",
        _ => "File Anomalies"
    };
}

public enum ScanMode
{
    Quick,
    Full,
    Custom,
    BrowserOnly
}

public class ThreatScanResult
{
    public List<ThreatItem> Threats { get; set; } = [];
    public int TotalFilesScanned { get; set; }
    public int TotalProcessesScanned { get; set; }
    public int TotalRegistryKeysScanned { get; set; }
    public int TotalBrowserExtensionsScanned { get; set; }
    public TimeSpan ScanDuration { get; set; }
    public ScanMode Mode { get; set; }
    public DateTime ScanDate { get; set; } = DateTime.Now;

    public int CriticalCount => Threats.Count(t => t.ThreatLevel == ThreatLevel.Critical);
    public int HighCount => Threats.Count(t => t.ThreatLevel == ThreatLevel.High);
    public int MediumCount => Threats.Count(t => t.ThreatLevel == ThreatLevel.Medium);
    public int LowCount => Threats.Count(t => t.ThreatLevel == ThreatLevel.Low);
    public bool IsClean => Threats.Count == 0;
}
