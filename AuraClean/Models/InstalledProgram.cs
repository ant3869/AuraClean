using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using System.Globalization;
using System.IO;

namespace AuraClean.Models;

/// <summary>
/// Represents a program installed on the system, sourced from the Windows Uninstall registry keys.
/// </summary>
public partial class InstalledProgram : ObservableObject
{
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _displayVersion = string.Empty;
    [ObservableProperty] private string _publisher = string.Empty;
    [ObservableProperty] private string _installLocation = string.Empty;
    [ObservableProperty] private string _uninstallString = string.Empty;
    [ObservableProperty] private string _quietUninstallString = string.Empty;
    [ObservableProperty] private string _displayIcon = string.Empty;
    [ObservableProperty] private string _installDate = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedSize))]
    private long _estimatedSizeKB;
    [ObservableProperty] private bool _isWindowsInstaller;
    [ObservableProperty] private bool _isSelected;

    /// <summary>The registry key path this entry was read from (for post-uninstall cleanup).</summary>
    public string RegistryKeyPath { get; set; } = string.Empty;

    /// <summary>Registry view (32-bit vs 64-bit) that this entry belongs to.</summary>
    public RegistryView RegistryView { get; set; }

    /// <summary>Registry hive that this entry belongs to.</summary>
    public RegistryHive RegistryHive { get; set; } = RegistryHive.LocalMachine;

    public string FormattedSize =>
        EstimatedSizeKB switch
        {
            <= 0 => "Unknown",
            < 1024 => $"{EstimatedSizeKB} KB",
            < 1_048_576 => $"{EstimatedSizeKB / 1024.0:F1} MB",
            _ => $"{EstimatedSizeKB / 1_048_576.0:F2} GB"
        };

    /// <summary>
    /// Drive holding the program (e.g. "C:"), derived from the install-location root,
    /// else the display-icon path root, else empty. Pure getter, never throws.
    /// </summary>
    public string DriveLetter =>
        GetDriveRoot(InstallLocation) ?? GetDriveRoot(DisplayIcon) ?? string.Empty;

    /// <summary>
    /// The InstallDate registry string parsed as yyyyMMdd / yyyy-MM-dd / MM/dd/yyyy,
    /// else null. Pure getter, never throws.
    /// </summary>
    public DateTime? InstallDateParsed
    {
        get
        {
            if (string.IsNullOrWhiteSpace(InstallDate))
                return null;

            string[] formats = ["yyyyMMdd", "yyyy-MM-dd", "MM/dd/yyyy"];
            return DateTime.TryParseExact(InstallDate.Trim(), formats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
        }
    }

    private static string? GetDriveRoot(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            var trimmed = raw.Trim();

            // DisplayIcon often carries icon-index args: "C:\App\app.exe",0
            if (trimmed.StartsWith('"'))
            {
                int close = trimmed.IndexOf('"', 1);
                if (close <= 1)
                    return null;
                trimmed = trimmed[1..close];
            }
            else
            {
                int comma = trimmed.IndexOf(',');
                if (comma >= 0)
                    trimmed = trimmed[..comma];
                trimmed = trimmed.Trim().Trim('"');
            }

            if (!Path.IsPathFullyQualified(trimmed))
                return null;

            // Drive-letter paths only (C:\…). UNC shares have no drive letter.
            if (trimmed.Length < 2 || trimmed[1] != ':')
                return null;

            return char.ToUpperInvariant(trimmed[0]) + ":";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
                                   PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
