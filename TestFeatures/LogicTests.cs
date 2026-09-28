using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;

namespace TestFeatures;

/// <summary>
/// Pure-logic tests with no dependency on the Windows desktop runtime or the registry; the only
/// file-system access is a private temp folder. They can run on any OS against the built AuraClean assembly.
/// </summary>
public static class LogicTests
{
    private static int _pass;
    private static int _fail;

    public static (int Pass, int Fail) RunAll()
    {
        _pass = 0;
        _fail = 0;

        Section("DISM reclaimable parsing", TestDismParsing);
        Section("Hosts file editing", TestHostsEditing);
        Section("Hosts backup naming", TestHostsBackupNaming);
        Section("Registry key protection", TestRegistryKeyProtection);
        Section("Recycle Bin $I parsing", TestRecycleBinParsing);
        Section("winget output parsing", TestWingetParsing);
        Section("Settings validation", TestSettingsNormalization);
        Section("Command-line options", TestStartupOptions);
        Section("Cleanup selection policy", TestCleanupPolicy);
        Section("Duplicate keep/delete exclusivity", TestDuplicateEntryExclusivity);
        Section("Threat signature data", TestSignatureData);

        return (_pass, _fail);
    }

    private static void Section(string name, Action test)
    {
        Console.WriteLine($"═══ LOGIC: {name} ═══");
        try
        {
            test();
        }
        catch (Exception ex)
        {
            Check(false, $"Unexpected exception: {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine();
    }

    private static void Check(bool condition, string message)
    {
        Console.ForegroundColor = condition ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}: {message}");
        Console.ResetColor();
        if (condition) _pass++; else _fail++;
    }

    private static void TestDismParsing()
    {
        Check(FileCleanerService.ParseDismSize("Cache and Temporary Data : 1.23 GB") == (long)(1.23 * 1_073_741_824),
            "GB value parsed");
        Check(FileCleanerService.ParseDismSize("Backups and Disabled Features : 456 MB") == 456L * 1_048_576,
            "MB value parsed");
        Check(FileCleanerService.ParseDismSize("Cache and Temporary Data : 0 bytes") == 0,
            "'0 bytes' parsed as zero");
        Check(FileCleanerService.ParseDismSize("Backups and Disabled Features : 2,50 GB") == (long)(2.5 * 1_073_741_824),
            "Comma decimal separator supported");
        Check(FileCleanerService.ParseDismSize("Something : not-a-number GB") == 0, "Malformed size returns 0");
        Check(FileCleanerService.ParseDismSize("No colon here") == 0, "Missing colon returns 0");

        const string recommended = """
            Component Store (WinSxS) information:

            Windows Explorer Reported Size of Component Store : 8.01 GB
            Actual Size of Component Store : 7.73 GB
                Shared with Windows : 5.93 GB
                Backups and Disabled Features : 1.50 GB
                Cache and Temporary Data : 512.00 MB
            Date of Last Cleanup : 2026-01-01 10:00:00
            Number of Reclaimable Packages : 2
            Component Store Cleanup Recommended : Yes
            """;
        Check(FileCleanerService.ParseDismReclaimable(recommended) == (long)(1.5 * 1_073_741_824) + 512L * 1_048_576,
            "Reclaimable = backups + cache when cleanup is recommended");

        var notRecommended = recommended.Replace("Recommended : Yes", "Recommended : No");
        Check(FileCleanerService.ParseDismReclaimable(notRecommended) == 0,
            "Nothing reclaimable when DISM does not recommend cleanup");
        Check(FileCleanerService.ParseDismReclaimable(string.Empty) == 0, "Empty output returns 0");
    }

    private static void TestHostsEditing()
    {
        const string hosts = "# comment\r\n127.0.0.1 localhost\r\n10.0.0.5 update.microsoft.com\r\n" +
                             "0.0.0.0 ads.example.com tracker.example.com # blockers\r\n";

        var (single, removedSingle) = HostsFileEditor.RemoveHostEntries(hosts, "update.microsoft.com");
        Check(removedSingle == 1, "One mapping removed");
        Check(!single.Contains("update.microsoft.com"), "Hijacked host no longer present");
        Check(single.Contains("127.0.0.1 localhost") && single.Contains("# comment"), "Other lines preserved");
        Check(single.Contains("\r\n"), "CRLF line endings preserved");

        var (multi, removedMulti) = HostsFileEditor.RemoveHostEntries(hosts, "ads.example.com");
        Check(removedMulti == 1, "Host removed from a multi-host line");
        Check(multi.Contains("tracker.example.com") && multi.Contains("# blockers"),
            "Remaining hosts and trailing comment kept");

        var (unchanged, removedNone) = HostsFileEditor.RemoveHostEntries(hosts, "absent.example.com");
        Check(removedNone == 0 && unchanged == hosts, "Absent host leaves the file byte-for-byte identical");

        var (commented, removedCommented) = HostsFileEditor.RemoveHostEntries("# 10.0.0.1 evil.com\n", "evil.com");
        Check(removedCommented == 0 && commented.Contains("evil.com"), "Commented-out mappings are ignored");
    }

    private static void TestHostsBackupNaming()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AuraCleanTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var hostsPath = Path.Combine(dir, "hosts");
            File.WriteAllText(hostsPath, "original");
            var first = HostsFileEditor.CreateBackup(hostsPath);

            File.WriteAllText(hostsPath, "edited");
            var backups = new List<string> { first };
            for (int i = 0; i < 5; i++)
                backups.Add(HostsFileEditor.CreateBackup(hostsPath));

            Check(backups.Distinct(StringComparer.OrdinalIgnoreCase).Count() == backups.Count,
                "Back-to-back backups get distinct names");
            Check(File.ReadAllText(first) == "original", "First backup still holds the original mappings");
            Check(backups.Skip(1).All(b => File.ReadAllText(b) == "edited"), "Later backups hold the edited file");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void TestRegistryKeyProtection()
    {
        Check(RegistryScannerService.IsProtectedKey(@"Software"), "Software root is protected");
        Check(RegistryScannerService.IsProtectedKey(@"Software\Microsoft"), "Software\\Microsoft is protected");
        Check(RegistryScannerService.IsProtectedKey(@"Software\Microsoft\Windows\CurrentVersion\Run"),
            "Run key is protected");
        Check(RegistryScannerService.IsProtectedKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
            "32-bit Run key is protected");
        Check(!RegistryScannerService.IsProtectedKey(@"Software\VideoLAN"), "Vendor key is not protected");
        Check(!RegistryScannerService.IsProtectedKey(
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}"),
            "Individual uninstall entry is deletable");

        var (hive, view, sub) = RegistryScannerService.ParseKeyPath(@"HKLM (32-bit)\Software\Vendor");
        Check(hive == Microsoft.Win32.RegistryHive.LocalMachine && view == Microsoft.Win32.RegistryView.Registry32 &&
              sub == @"Software\Vendor", "HKLM (32-bit) display path parsed");

        var (hkcu, _, hkcuSub) = RegistryScannerService.ParseKeyPath(@"HKCU\Software\Vendor\App");
        Check(hkcu == Microsoft.Win32.RegistryHive.CurrentUser && hkcuSub == @"Software\Vendor\App", "HKCU path parsed");

        var (bad, _, _) = RegistryScannerService.ParseKeyPath("CurrentConfig\\Foo");
        Check(bad == null, "Unsupported hive rejected");
    }

    private static void TestRecycleBinParsing()
    {
        const string original = @"C:\Users\me\Documents\report.docx";
        var deleted = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        var v2 = new List<byte>();
        v2.AddRange(BitConverter.GetBytes(2L));
        v2.AddRange(BitConverter.GetBytes(12345L));
        v2.AddRange(BitConverter.GetBytes(deleted.ToFileTimeUtc()));
        v2.AddRange(BitConverter.GetBytes(original.Length + 1));
        v2.AddRange(Encoding.Unicode.GetBytes(original + "\0"));

        Check(FileRecoveryService.TryParseRecycleBinInfo(v2.ToArray(), out var path2, out var size2, out var when2) &&
              path2 == original && size2 == 12345 && when2 == deleted, "Version 2 record parsed");

        var v1 = new byte[24 + 520];
        BitConverter.GetBytes(1L).CopyTo(v1, 0);
        BitConverter.GetBytes(99L).CopyTo(v1, 8);
        BitConverter.GetBytes(deleted.ToFileTimeUtc()).CopyTo(v1, 16);
        Encoding.Unicode.GetBytes(original).CopyTo(v1, 24);

        Check(FileRecoveryService.TryParseRecycleBinInfo(v1, out var path1, out var size1, out _) &&
              path1 == original && size1 == 99, "Version 1 record parsed");

        Check(!FileRecoveryService.TryParseRecycleBinInfo(new byte[10], out _, out _, out _), "Truncated record rejected");

        var corrupt = v2.ToArray();
        BitConverter.GetBytes(10_000).CopyTo(corrupt, 24);
        Check(!FileRecoveryService.TryParseRecycleBinInfo(corrupt, out _, out _, out _),
            "Record whose length field overruns the buffer is rejected");
    }

    private static void TestWingetParsing()
    {
        const string output =
            "   - \r   \\ \r" +
            "Name                           Id                         Version      Available    Source\n" +
            "-----------------------------------------------------------------------------------------\n" +
            "Mozilla Firefox (x64 en-US)    Mozilla.Firefox            128.0        129.0        winget\n" +
            "Notepad++ (64-bit x64)         Notepad++.Notepad++        8.6.0        8.7.7        winget\n" +
            "2 upgrades available.\n";

        var parsed = SoftwareUpdaterService.ParseWingetUpgradeOutput(output);
        Check(parsed.Count == 2, $"Two upgrades parsed (got {parsed.Count})");
        Check(parsed.Any(p => p.Id == "Mozilla.Firefox" && p.AvailableVersion == "129.0"), "Firefox row parsed");
        Check(parsed.Any(p => p.Id == "Notepad++.Notepad++"), "Id with '+' characters accepted");

        Check(SoftwareUpdaterService.IsValidPackageId("Microsoft.VisualStudioCode"), "Normal id valid");
        Check(!SoftwareUpdaterService.IsValidPackageId("Microsoft.VisualStudio…"), "Truncated id rejected");
        Check(!SoftwareUpdaterService.IsValidPackageId("bad id\" && calc"), "Id with spaces/quotes rejected");
        Check(SoftwareUpdaterService.ParseWingetUpgradeOutput("No installed package found.").Count == 0,
            "Output without a table yields no results");
    }

    private static void TestSettingsNormalization()
    {
        var settings = new AppSettings
        {
            AbandonedFileDaysThreshold = -5,
            QuarantineRetentionDays = 0,
            MaxHistoryEntries = -1,
            ScheduledCleanupDayOfWeek = 42,
            ScheduledCleanupFrequency = "hourly",
            ScheduledCleanupTime = "25:99",
            DefaultShredAlgorithm = "Gutmann35",
            DefaultLargeFileSizeMb = 0
        };
        settings.Normalize();

        Check(settings.AbandonedFileDaysThreshold >= 30, "Abandoned-file threshold clamped");
        Check(settings.QuarantineRetentionDays >= 1, "Retention days clamped");
        Check(settings.MaxHistoryEntries >= 10, "History size clamped");
        Check(settings.ScheduledCleanupDayOfWeek is >= 1 and <= 7, "Day of week clamped");
        Check(settings.ScheduledCleanupFrequency == "Weekly", "Unknown frequency falls back to Weekly");
        Check(settings.ScheduledCleanupTime == "03:00", "Invalid time falls back to 03:00");
        Check(settings.DefaultShredAlgorithm == "DoD3Pass", "Unknown algorithm falls back to DoD3Pass");
        Check(settings.DefaultLargeFileSizeMb >= 1, "Large-file threshold clamped");

        var valid = new AppSettings { ScheduledCleanupTime = "7:05", ScheduledCleanupFrequency = "daily" };
        valid.Normalize();
        Check(valid.ScheduledCleanupTime == "07:05", "Single-digit hour normalized to HH:mm");
        Check(valid.ScheduledCleanupFrequency == "Daily", "Frequency casing normalized");
    }

    private static void TestStartupOptions()
    {
        var auto = StartupOptions.Parse(["/autoclean"]);
        Check(auto.AutoClean && !auto.StartMinimized, "/autoclean recognised");

        var minimized = StartupOptions.Parse(["--minimized"]);
        Check(minimized.StartMinimized && !minimized.AutoClean, "--minimized recognised");

        var deep = StartupOptions.Parse(["--deep-uninstall", "\"C:\\Program Files\\App\\app.exe\""]);
        Check(deep.DeepUninstallTarget == @"C:\Program Files\App\app.exe", "--deep-uninstall target captured and unquoted");

        var dangling = StartupOptions.Parse(["--deep-uninstall"]);
        Check(dangling.DeepUninstallTarget == null, "Missing deep-uninstall target ignored");
    }

    private static void TestCleanupPolicy()
    {
        var settings = new AppSettings { CleanTempFiles = false, CleanRecycleBin = true };

        Check(!CleanupModePolicy.IsSelectedByDefault(JunkType.TempFile, settings, isAdvancedMode: false),
            "Normal mode honors a disabled category");
        Check(!CleanupModePolicy.IsSelectedByDefault(JunkType.RecycleBin, settings, isAdvancedMode: false),
            "Recycle Bin never auto-selected in Normal mode");
        Check(CleanupModePolicy.IsSelectedByDefault(JunkType.RecycleBin, settings, isAdvancedMode: true),
            "Recycle Bin follows the setting in Advanced mode");
        Check(!CleanupModePolicy.IsSelectedByDefault(JunkType.WinSxS, settings, isAdvancedMode: true),
            "WinSxS is review-only");
        Check(!CleanupModePolicy.IsSelectedByDefault(JunkType.BrowserTracking, settings, isAdvancedMode: true),
            "Browser site data is review-only");
        Check(CleanupModePolicy.IsSelectedByDefault(JunkType.Prefetch, settings, isAdvancedMode: false),
            "Enabled low-risk category is selected");
    }

    private static void TestDuplicateEntryExclusivity()
    {
        var entry = new DuplicateFinderService.DuplicateFileEntry { IsKeep = true };
        entry.IsSelected = true;
        Check(entry.IsSelected && !entry.IsKeep, "Selecting a kept copy for deletion clears Keep");

        entry.IsKeep = true;
        Check(entry.IsKeep && !entry.IsSelected, "Marking Keep clears the delete selection");

        var group = new DuplicateFinderService.DuplicateGroup { FileSize = 100, Files = [entry] };
        Check(group.WastedBytes == 0, "Single-file group wastes nothing (no negative sizes)");
    }

    private static void TestSignatureData()
    {
        Check(!ThreatSignatureDatabase.KnownMalwareHashes.Contains(ThreatSignatureDatabase.EmptyFileSha256),
            "Empty-file hash is not a malware signature");
        Check(ThreatSignatureDatabase.KnownMalwareHashes.All(h => h.Length == 64 && h.All(Uri.IsHexDigit)),
            "Every signature is a well-formed SHA-256");
        Check(!ThreatSignatureDatabase.KnownMaliciousExtensionIds.Contains("efaidnbmnnnibpcajpcglclefindmkaj"),
            "Adobe Acrobat extension is not flagged");
        Check(!ThreatSignatureDatabase.KnownMaliciousExtensionIds.Contains("gighmmpiobklfepjocnamgkkbiglidom"),
            "AdBlock extension is not flagged");
        Check(!ThreatSignatureDatabase.SuspiciousTaskPatterns.Any(p =>
                p.Contains(@"\AppData\Roaming\", StringComparison.OrdinalIgnoreCase)),
            "Ordinary AppData\\Roaming autoruns are not suspicious");
    }
}
