using System;
using System.IO;
using AuraClean.Helpers;
using AuraClean.Services;

namespace TestFeatures;

/// <summary>
/// Batch A (backend safety hardening) tests. Pure logic plus a private temp folder;
/// no writes to real settings, quarantine, or system locations.
/// </summary>
public static class SafetyHardeningTests
{
    private static int _pass;
    private static int _fail;

    /// <summary>
    /// Runs all Batch A tests. Returns the failure count (0 = green); the lead wires
    /// this into Program.cs. See <see cref="RunAll"/> for the (pass, fail) breakdown.
    /// </summary>
    public static int Run() => RunAll().Fail;

    public static (int Pass, int Fail) RunAll()
    {
        _pass = 0;
        _fail = 0;

        Section("A1: cleaner file-delete guard", TestFileDeleteGuard);
        Section("A2: settings defensive copy", TestSettingsDefensiveCopy);
        Section("A3: quarantine expiry math", TestQuarantineExpiry);
        Section("A4: force-delete never-kill list", TestNeverKillList);
        Section("A5: defrag drive-letter validation", TestDriveLetterValidation);

        return (_pass, _fail);
    }

    private static void Section(string name, Action test)
    {
        Console.WriteLine($"═══ SAFETY: {name} ═══");
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

    private static void TestFileDeleteGuard()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "AuraSafetyTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            var normal = Path.Combine(sandbox, "normal.tmp");
            File.WriteAllText(normal, "x");
            Check(FileCleanerService.IsSafeToCleanFile(normal, out var reason) && reason == string.Empty,
                "Ordinary temp file is cleanable");

            Check(!FileCleanerService.IsSafeToCleanFile(string.Empty, out reason) && reason.Length > 0,
                "Empty path is rejected with a reason");
            Check(!FileCleanerService.IsSafeToCleanFile("relative\\path.tmp", out reason) && reason.Length > 0,
                "Relative path is rejected with a reason");

            var missing = Path.Combine(sandbox, "does-not-exist.tmp");
            Check(FileCleanerService.IsSafeToCleanFile(missing, out _) ,
                "Missing file passes the guard (nothing to protect; delete fails downstream)");

            // System-attributed file: the core new protection. Staging the attribute can
            // fail on locked-down systems — then the case is skipped, not failed.
            var systemFile = Path.Combine(sandbox, "system.tmp");
            File.WriteAllText(systemFile, "x");
            try
            {
                File.SetAttributes(systemFile, File.GetAttributes(systemFile) | FileAttributes.System);
                Check(!FileCleanerService.IsSafeToCleanFile(systemFile, out reason)
                        && reason.Contains("system", StringComparison.OrdinalIgnoreCase),
                    "System-attributed file is rejected");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Check(true, $"System-attribute staging unsupported here — skipped ({ex.GetType().Name})");
            }
            finally
            {
                try { File.SetAttributes(systemFile, FileAttributes.Normal); } catch { }
            }
        }
        finally
        {
            try { Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }

    private static void TestSettingsDefensiveCopy()
    {
        // Read-only: Load() never writes to disk, and these tests never call Save().
        var a = SettingsService.Load();
        var b = SettingsService.Load();
        Check(!ReferenceEquals(a, b), "Load() returns a new object per call, not the live cache");

        var originalTheme = b.Theme;
        a.Theme = originalTheme == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light;
        a.CleanerExcludedPaths.Add("X:\\safety-probe");
        a.ScheduledCleanupCategories.Add("SafetyProbe");

        var fresh = SettingsService.Load();
        Check(fresh.Theme == originalTheme, "Mutating a copy leaves the cached scalar untouched");
        Check(!fresh.CleanerExcludedPaths.Contains("X:\\safety-probe")
                && !fresh.ScheduledCleanupCategories.Contains("SafetyProbe"),
            "Mutating a copy's lists leaves the cached lists untouched");
        Check(!ReferenceEquals(a.CleanerExcludedPaths, fresh.CleanerExcludedPaths),
            "List references are deep-copied, not shared");
    }

    private static void TestQuarantineExpiry()
    {
        var old = new QuarantineEntry { QuarantinedAt = DateTime.Now.AddDays(-31).AddMinutes(-5) };
        var recent = new QuarantineEntry { QuarantinedAt = DateTime.Now.AddDays(-2).AddMinutes(-5) };
        Check(old.IsExpiredAt(30), "31-day-old entry is expired under a 30-day window");
        Check(!recent.IsExpiredAt(30), "2-day-old entry is not expired under a 30-day window");
        Check(recent.IsExpiredAt(1), "2-day-old entry is expired under a 1-day window");

        Check(old.GetExpiresIn(30) == "Expired", "Expired entry reports 'Expired'");
        Check(recent.GetExpiresIn(30) == "27d remaining", "Multi-day remainder reports 'Nd remaining'");

        var hoursLeft = new QuarantineEntry { QuarantinedAt = DateTime.Now.AddHours(-20) };
        var label = hoursLeft.GetExpiresIn(1);
        Check(label.EndsWith("h remaining", StringComparison.Ordinal), $"Sub-day remainder reports hours ('{label}')");

        // Stamped retention (the GetAllEntries path) drives the bound properties.
        var stamped = new QuarantineEntry { QuarantinedAt = DateTime.Now.AddDays(-31), RetentionDays = 30 };
        Check(stamped.IsExpired, "Stamped entry honors RetentionDays for IsExpired");
        Check(stamped.ExpiresIn == "Expired", "Stamped entry honors RetentionDays for ExpiresIn");
    }

    private static void TestNeverKillList()
    {
        foreach (var office in new[] { "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK" })
            Check(ForceDeleteService.IsNeverKillProcess(office), $"Office process denied: {office}");
        foreach (var db in new[] { "sqlservr", "mysqld", "mariadbd", "postgres", "mongod", "oracle", "db2sysc" })
            Check(ForceDeleteService.IsNeverKillProcess(db), $"DB/writer process denied: {db}");

        Check(ForceDeleteService.IsNeverKillProcess("winword"), "Denylist match is case-insensitive");
        Check(!ForceDeleteService.IsNeverKillProcess("notepad"), "Ordinary process is not denied");
        Check(!ForceDeleteService.IsNeverKillProcess("chrome"), "Browser process is not denied");
        Check(!ForceDeleteService.IsNeverKillProcess(null), "Null name is not denied");
        Check(!ForceDeleteService.IsNeverKillProcess("  "), "Blank name is not denied");
    }

    private static void TestDriveLetterValidation()
    {
        Check(DiskOptimizerService.TryNormalizeDriveLetter("C", out var v1) && v1 == "C:",
            "'C' normalizes to 'C:'");
        Check(DiskOptimizerService.TryNormalizeDriveLetter("c:", out var v2) && v2 == "C:",
            "'c:' normalizes to 'C:' (case-insensitive)");
        Check(DiskOptimizerService.TryNormalizeDriveLetter("D:\\", out var v3) && v3 == "D:",
            "'D:\\' normalizes to 'D:'");
        Check(DiskOptimizerService.TryNormalizeDriveLetter("  e  ", out var v4) && v4 == "E:",
            "Surrounding whitespace is tolerated");

        foreach (var bad in new[] { null, "", "  ", "CC", "1", "C:foo", "C;calc.exe", "C /O", "C:/", "\\\\?\\C:" })
            Check(!DiskOptimizerService.TryNormalizeDriveLetter(bad, out _),
                $"Rejected: '{bad ?? "<null>"}'");
    }
}
