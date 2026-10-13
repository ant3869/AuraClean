using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AuraClean.Services;

namespace TestFeatures;

/// <summary>
/// Regression tests for the Oct 2026 quality pass: honest memory dry-run reporting,
/// and cancellation support for the System Cleaner analyze scan and the browser scan.
/// </summary>
public static class QualityPassTests
{
    private static int _pass;
    private static int _fail;

    public static int Run() => RunAll().Fail;

    public static (int Pass, int Fail) RunAll()
    {
        _pass = 0;
        _fail = 0;

        Section("Memory dry run reports honest values", TestMemoryDryRunHonesty);
        SectionAsync("System Cleaner analyze honors cancellation", TestAnalyzeCancellation).GetAwaiter().GetResult();
        SectionAsync("Browser scan honors cancellation", TestBrowserScanCancellation).GetAwaiter().GetResult();

        return (_pass, _fail);
    }

    private static void Section(string name, Action test)
    {
        Console.WriteLine($"═══ QUALITY PASS: {name} ═══");
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

    private static async Task SectionAsync(string name, Func<Task> test)
    {
        Console.WriteLine($"═══ QUALITY PASS: {name} ═══");
        try
        {
            await test();
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

    private static void TestMemoryDryRunHonesty()
    {
        var totalProcessCount = Process.GetProcesses().Length;

        var result = MemoryManagerService.BoostMemoryAsync(
            purgeStandbyList: true, dryRun: true).GetAwaiter().GetResult();

        Check(result.MemoryFreedBytes == 0,
            $"Dry run frees 0 bytes — no fabricated estimate (got {result.MemoryFreedBytes})");
        Check(!result.StandbyListPurged,
            "Dry run never claims the standby list was purged (it never calls into the real purge path)");
        Check(result.ProcessesTrimmed > 0,
            $"Dry run reports a nonzero eligible-process count ({result.ProcessesTrimmed})");
        Check(result.ProcessesTrimmed < totalProcessCount,
            $"Dry run's eligible count ({result.ProcessesTrimmed}) excludes protected processes, " +
            $"not the full process count ({totalProcessCount}) the old fabricated path claimed");
        Check(result.WorkingSetBefore == result.WorkingSetAfter,
            "Dry run leaves the reported working-set before/after identical (no simulated reduction)");
    }

    private static async Task TestAnalyzeCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await FileCleanerService.AnalyzeSystemJunkAsync(ct: cts.Token);
            Check(false, "A pre-cancelled token should throw before returning");
        }
        catch (OperationCanceledException)
        {
            Check(true, "AnalyzeSystemJunkAsync honors a pre-cancelled token (CleanerViewModel.AnalyzeAsync now wires this through)");
        }
    }

    private static async Task TestBrowserScanCancellation()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "AuraClean_BrowserScanCancel_Test");
        try
        {
            Directory.CreateDirectory(testDir);
            var fakeBrowser = new BrowserCleanerService.BrowserProfile("TestBrowser", testDir);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            try
            {
                await BrowserCleanerService.ScanBrowserAsync(fakeBrowser, ct: cts.Token);
                Check(false, "A pre-cancelled token should throw before returning");
            }
            catch (OperationCanceledException)
            {
                Check(true, "ScanBrowserAsync honors a pre-cancelled token (BrowserCleanerViewModel.ScanBrowsersAsync now wires this through)");
            }
        }
        finally
        {
            try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); } catch { }
        }
    }
}
