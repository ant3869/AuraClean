using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using AuraClean.Helpers;
using Microsoft.Win32;

namespace AuraClean.Services;

/// <summary>
/// Collects detailed system hardware and software information via WMI and .NET APIs.
/// </summary>
public static class SystemInfoService
{
    /// <summary>WMI query timeout to prevent indefinite hangs.</summary>
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A single piece of system information displayed in the info grid.
    /// </summary>
    public record InfoEntry(string Category, string Label, string Value, string Icon = "Information");

    /// <summary>
    /// Creates a ManagementObjectSearcher with a timeout to prevent WMI hangs.
    /// </summary>
    private static ManagementObjectSearcher CreateSearcher(string query)
    {
        var scope = new ManagementScope(@"\\.\root\cimv2");
        var options = new System.Management.EnumerationOptions { Timeout = WmiTimeout };
        return new ManagementObjectSearcher(scope, new ObjectQuery(query), options);
    }

    /// <summary>
    /// Collects all system information on a background thread.
    /// </summary>
    public static async Task<List<InfoEntry>> CollectAllAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var entries = new List<InfoEntry>();

        await Task.Run(() =>
        {
            // ── Operating System ──
            progress?.Report("Reading OS information...");
            entries.AddRange(GetOsInfo());
            ct.ThrowIfCancellationRequested();

            // ── Processor ──
            progress?.Report("Reading CPU information...");
            entries.AddRange(GetCpuInfo());
            ct.ThrowIfCancellationRequested();

            // ── Memory ──
            progress?.Report("Reading memory information...");
            entries.AddRange(GetMemoryInfo());
            ct.ThrowIfCancellationRequested();

            // ── Graphics ──
            progress?.Report("Reading GPU information...");
            entries.AddRange(GetGpuInfo());
            ct.ThrowIfCancellationRequested();

            // ── Storage ──
            progress?.Report("Reading storage information...");
            entries.AddRange(GetStorageInfo());
            ct.ThrowIfCancellationRequested();

            // ── Network ──
            progress?.Report("Reading network information...");
            entries.AddRange(GetNetworkInfo());
            ct.ThrowIfCancellationRequested();

            // ── Motherboard ──
            progress?.Report("Reading motherboard information...");
            entries.AddRange(GetMotherboardInfo());
            ct.ThrowIfCancellationRequested();

            // ── .NET / Runtime ──
            progress?.Report("Reading runtime information...");
            entries.AddRange(GetRuntimeInfo());

        }, ct);

        return entries;
    }

    private static List<InfoEntry> GetOsInfo()
    {
        var entries = new List<InfoEntry>();
        string cat = "Operating System";

        try
        {
            using var mos = CreateSearcher(
                "SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, " +
                "LastBootUpTime, RegisteredUser, SerialNumber FROM Win32_OperatingSystem");

            foreach (var obj in mos.Get())
            {
                entries.Add(new InfoEntry(cat, "Name", obj["Caption"]?.ToString()?.Trim() ?? "N/A", "MicrosoftWindows"));
                entries.Add(new InfoEntry(cat, "Version", obj["Version"]?.ToString() ?? "N/A", "InformationOutline"));
                entries.Add(new InfoEntry(cat, "Build", obj["BuildNumber"]?.ToString() ?? "N/A", "Wrench"));
                entries.Add(new InfoEntry(cat, "Architecture", obj["OSArchitecture"]?.ToString() ?? "N/A", "Chip"));
                entries.Add(new InfoEntry(cat, "Registered User", obj["RegisteredUser"]?.ToString() ?? "N/A", "Account"));

                if (obj["InstallDate"] is string installStr)
                {
                    var installDate = ManagementDateTimeConverter.ToDateTime(installStr);
                    entries.Add(new InfoEntry(cat, "Install Date", installDate.ToString("yyyy-MM-dd"), "Calendar"));
                }

                if (obj["LastBootUpTime"] is string bootStr)
                {
                    var bootTime = ManagementDateTimeConverter.ToDateTime(bootStr);
                    var uptime = DateTime.Now - bootTime;
                    entries.Add(new InfoEntry(cat, "Last Boot", bootTime.ToString("yyyy-MM-dd HH:mm"), "Clock"));
                    entries.Add(new InfoEntry(cat, "Uptime", FormatHelper.FormatDuration(uptime), "TimerOutline"));
                }
                break;
            }
        }
        catch (Exception ex)
        {
            entries.Add(new InfoEntry(cat, "Error", ex.Message, "AlertCircle"));
        }

        entries.Add(new InfoEntry(cat, "Computer Name", Environment.MachineName, "Monitor"));
        entries.Add(new InfoEntry(cat, "User Name", Environment.UserName, "AccountCircle"));

        return entries;
    }

    private static List<InfoEntry> GetCpuInfo()
    {
        var entries = new List<InfoEntry>();
        string cat = "Processor";

        try
        {
            using var mos = CreateSearcher(
                "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, " +
                "MaxClockSpeed, L2CacheSize, L3CacheSize FROM Win32_Processor");

            int cpuIndex = 0;
            foreach (var obj in mos.Get())
            {
                string prefix = cpuIndex > 0 ? $"CPU {cpuIndex + 1} " : "";
                entries.Add(new InfoEntry(cat, $"{prefix}Name", obj["Name"]?.ToString()?.Trim() ?? "N/A", "Cpu64Bit"));
                entries.Add(new InfoEntry(cat, $"{prefix}Manufacturer", obj["Manufacturer"]?.ToString() ?? "N/A", "Factory"));
                entries.Add(new InfoEntry(cat, $"{prefix}Cores", obj["NumberOfCores"]?.ToString() ?? "N/A", "CircleMultiple"));
                entries.Add(new InfoEntry(cat, $"{prefix}Logical Processors", obj["NumberOfLogicalProcessors"]?.ToString() ?? "N/A", "CircleMultipleOutline"));

                if (obj["MaxClockSpeed"] is uint mhz)
                    entries.Add(new InfoEntry(cat, $"{prefix}Max Speed", $"{mhz / 1000.0:F2} GHz", "SpeedometerMedium"));

                if (obj["L2CacheSize"] is uint l2)
                    entries.Add(new InfoEntry(cat, $"{prefix}L2 Cache", FormatHelper.FormatBytes(l2 * 1024L), "Memory"));

                if (obj["L3CacheSize"] is uint l3)
                    entries.Add(new InfoEntry(cat, $"{prefix}L3 Cache", FormatHelper.FormatBytes(l3 * 1024L), "Memory"));

                cpuIndex++;
            }
        }
        catch (Exception ex)
        {
            entries.Add(new InfoEntry(cat, "Error", ex.Message, "AlertCircle"));
        }

        return entries;
    }

    private static List<InfoEntry> GetMemoryInfo()
    {
        var entries = new List<InfoEntry>();
        string cat = "Memory";

        try
        {
            // Total physical memory via GC
            var gcInfo = GC.GetGCMemoryInfo();
            entries.Add(new InfoEntry(cat, "Total Physical", FormatHelper.FormatBytes(gcInfo.TotalAvailableMemoryBytes), "Memory"));

            // Memory module details
            using var mos = CreateSearcher(
                "SELECT Manufacturer, Capacity, Speed, MemoryType, FormFactor FROM Win32_PhysicalMemory");

            int slotIndex = 0;
            long totalCapacity = 0;
            foreach (var obj in mos.Get())
            {
                slotIndex++;
                long capacity = Convert.ToInt64(obj["Capacity"] ?? 0);
                totalCapacity += capacity;

                entries.Add(new InfoEntry(cat, $"Slot {slotIndex} Size", FormatHelper.FormatBytes(capacity), "ChipOutline"));

                if (obj["Manufacturer"]?.ToString() is string mfg && !string.IsNullOrWhiteSpace(mfg))
                    entries.Add(new InfoEntry(cat, $"Slot {slotIndex} Manufacturer", mfg.Trim(), "Factory"));

                if (obj["Speed"] is uint speed && speed > 0)
                    entries.Add(new InfoEntry(cat, $"Slot {slotIndex} Speed", $"{speed} MHz", "SpeedometerMedium"));
            }

            entries.Add(new InfoEntry(cat, "Total Slots Used", slotIndex.ToString(), "Counter"));
        }
        catch (Exception ex)
        {
            entries.Add(new InfoEntry(cat, "Error", ex.Message, "AlertCircle"));
        }

        return entries;
    }

    private static List<InfoEntry> GetGpuInfo()
    {
        var entries = new List<InfoEntry>();
        string cat = "Graphics";

        try
        {
            using var mos = CreateSearcher(
                "SELECT Name, DriverVersion, AdapterRAM, VideoProcessor, " +
                "CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController");

            int gpuIndex = 0;
            foreach (var obj in mos.Get())
            {
                gpuIndex++;
                string prefix = gpuIndex > 1 ? $"GPU {gpuIndex} " : "";

                entries.Add(new InfoEntry(cat, $"{prefix}Name", obj["Name"]?.ToString() ?? "N/A", "Gpu"));
                entries.Add(new InfoEntry(cat, $"{prefix}Driver Version", obj["DriverVersion"]?.ToString() ?? "N/A", "Update"));

                if (obj["AdapterRAM"] is uint vramBytes && vramBytes > 0)
                    entries.Add(new InfoEntry(cat, $"{prefix}VRAM", FormatHelper.FormatBytes(vramBytes), "Memory"));

                var hRes = obj["CurrentHorizontalResolution"];
                var vRes = obj["CurrentVerticalResolution"];
                if (hRes != null && vRes != null)
                    entries.Add(new InfoEntry(cat, $"{prefix}Resolution", $"{hRes} x {vRes}", "Monitor"));
            }
        }
        catch (Exception ex)
        {
            entries.Add(new InfoEntry(cat, "Error", ex.Message, "AlertCircle"));
        }

        return entries;
    }

    private static List<InfoEntry> GetStorageInfo()
    {
        var entries = new List<InfoEntry>();
        string cat = "Storage";

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;

                    string label = string.IsNullOrEmpty(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                    entries.Add(new InfoEntry(cat, $"{drive.Name} ({label})",
                        $"{FormatHelper.FormatBytes(drive.AvailableFreeSpace)} free of {FormatHelper.FormatBytes(drive.TotalSize)}",
                        "Harddisk"));
                    entries.Add(new InfoEntry(cat, $"{drive.Name} Format", drive.DriveFormat, "Harddisk"));
                    entries.Add(new InfoEntry(cat, $"{drive.Name} Type", drive.DriveType.ToString(), "Harddisk"));
                }
                catch { }
            }

            // Physical disk info via WMI
            using var mos = CreateSearcher(
                "SELECT Model, Size, MediaType, InterfaceType FROM Win32_DiskDrive");
            int diskIdx = 0;
            foreach (var obj in mos.Get())
            {
                diskIdx++;
                string model = obj["Model"]?.ToString() ?? "Unknown";
                long size = Convert.ToInt64(obj["Size"] ?? 0);
                string media = obj["MediaType"]?.ToString() ?? "N/A";
                string iface = obj["InterfaceType"]?.ToString() ?? "N/A";

                entries.Add(new InfoEntry(cat, $"Disk {diskIdx}", $"{model} ({FormatHelper.FormatBytes(size)})", "Harddisk"));
                entries.Add(new InfoEntry(cat, $"Disk {diskIdx} Interface", $"{iface} / {media}", "Connection"));
            }
        }
        catch (Exception ex)
        {
            entries.Add(new InfoEntry(cat, "Error", ex.Message, "AlertCircle"));
        }

        return entries;
    }

    private static List<InfoEntry> GetNetworkInfo()
    {
        var entries = new List<InfoEntry>();
        string cat = "Network";

        try
        {
            using var mos = CreateSearcher(
                "SELECT Name, MACAddress, Speed, NetConnectionStatus FROM Win32_NetworkAdapter " +
                "WHERE PhysicalAdapter = True AND NetConnectionStatus IS NOT NULL");

            foreach (var obj in mos.Get())
            {
                string name = obj["Name"]?.ToString() ?? "Unknown";
                string mac = obj["MACAddress"]?.ToString() ?? "N/A";
                ulong speed = Convert.ToUInt64(obj["Speed"] ?? 0);
                int status = Convert.ToInt32(obj["NetConnectionStatus"] ?? 0);

                string statusStr = status switch
                {
                    0 => "Disconnected",
                    1 => "Connecting",
                    2 => "Connected",
                    3 => "Disconnecting",
                    _ => "Unknown"
                };

                string speedStr = speed switch
                {
                    0 => "N/A",
                    < 1_000_000 => $"{speed / 1000.0:F0} Kbps",
                    < 1_000_000_000 => $"{speed / 1_000_000.0:F0} Mbps",
                    _ => $"{speed / 1_000_000_000.0:F1} Gbps"
                };

                entries.Add(new InfoEntry(cat, name, $"{statusStr} — {speedStr}", "Ethernet"));
                entries.Add(new InfoEntry(cat, $"{name} MAC", mac, "NetworkOutline"));
            }
        }
        catch (Exception ex)
        {
            entries.Add(new InfoEntry(cat, "Error", ex.Message, "AlertCircle"));
        }

        return entries;
    }

    private static List<InfoEntry> GetMotherboardInfo()
    {
        var entries = new List<InfoEntry>();
        string cat = "Motherboard";

        try
        {
            using var mos = CreateSearcher(
                "SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard");

            foreach (var obj in mos.Get())
            {
                entries.Add(new InfoEntry(cat, "Manufacturer", obj["Manufacturer"]?.ToString() ?? "N/A", "DeveloperBoard"));
                entries.Add(new InfoEntry(cat, "Model", obj["Product"]?.ToString() ?? "N/A", "DeveloperBoard"));
                break;
            }

            // BIOS
            using var bios = CreateSearcher(
                "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            foreach (var obj in bios.Get())
            {
                entries.Add(new InfoEntry(cat, "BIOS Vendor", obj["Manufacturer"]?.ToString() ?? "N/A", "Chip"));
                entries.Add(new InfoEntry(cat, "BIOS Version", obj["SMBIOSBIOSVersion"]?.ToString() ?? "N/A", "InformationOutline"));

                if (obj["ReleaseDate"] is string dateStr)
                {
                    try
                    {
                        var date = ManagementDateTimeConverter.ToDateTime(dateStr);
                        entries.Add(new InfoEntry(cat, "BIOS Date", date.ToString("yyyy-MM-dd"), "Calendar"));
                    }
                    catch { }
                }
                break;
            }
        }
        catch (Exception ex)
        {
            entries.Add(new InfoEntry(cat, "Error", ex.Message, "AlertCircle"));
        }

        return entries;
    }

    private static List<InfoEntry> GetRuntimeInfo()
    {
        var entries = new List<InfoEntry>();
        string cat = "Runtime";

        entries.Add(new InfoEntry(cat, ".NET Version", RuntimeInformation.FrameworkDescription, "LanguageCsharp"));
        entries.Add(new InfoEntry(cat, "Runtime ID", RuntimeInformation.RuntimeIdentifier, "Cog"));
        entries.Add(new InfoEntry(cat, "Process Architecture", RuntimeInformation.ProcessArchitecture.ToString(), "Chip"));
        entries.Add(new InfoEntry(cat, "OS Description", RuntimeInformation.OSDescription, "MicrosoftWindows"));

        return entries;
    }

    /// <summary>
    /// Formats all info entries as a plain-text report suitable for copying to clipboard.
    /// </summary>
    public static string FormatAsText(List<InfoEntry> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("═══════════════════════════════════════════");
        sb.AppendLine("        AuraClean — System Information");
        sb.AppendLine($"        Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("═══════════════════════════════════════════");
        sb.AppendLine();

        string? currentCategory = null;
        foreach (var entry in entries)
        {
            if (entry.Category != currentCategory)
            {
                currentCategory = entry.Category;
                sb.AppendLine($"── {currentCategory} ──");
            }
            sb.AppendLine($"  {entry.Label,-30} {entry.Value}");
        }

        return sb.ToString();
    }
}
