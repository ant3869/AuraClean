using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AuraClean.Services;
using AuraClean.ViewModels;

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
        SectionAsync("Browser scan cancellation interrupts directory-size walks", TestBrowserScanCancellationDuringDirectoryWalk).GetAwaiter().GetResult();
        Section("Memory result labels freeze at completion time, not the live Dry Run toggle", TestMemoryResultLabelsFreezeAtCompletion);
        Section("Restore point policy check reads the documented signal", TestRestorePointPolicyCheck);

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

    private static void TestRestorePointPolicyCheck()
    {
        // Ground-truthed live on this machine via `reg query` (read-only): no
        // HKLM\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore key exists here, so the
        // policy check must report "not disabled" rather than guessing from the old, undocumented
        // RPSessionInterval heuristic (which read 1, not 0, despite System Restore being actively
        // enabled and in use — 387 existing restore points on this machine).
        var disabledByPolicy = RestorePointService.IsSystemRestoreDisabledByPolicy();
        Check(!disabledByPolicy,
            "No Group Policy SystemRestore key present on this machine, so the check correctly reports 'not disabled'");
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

    private static async Task TestBrowserScanCancellationDuringDirectoryWalk()
    {
        // Regresses a PR #15 review finding: GetDirectorySize had no cancellation check at all,
        // so a token cancelled mid-scan (after the per-profile check, before the next one) was
        // silently ignored until the next profile directory — on a single-profile scan, that meant
        // cancellation never took effect. Cancelling from inside the progress callback fires after
        // the outer per-profile ct.ThrowIfCancellationRequested() but before the Cache directory is
        // measured, so only a cancellation check inside GetDirectorySize itself can catch it.
        var testDir = Path.Combine(Path.GetTempPath(), "AuraClean_BrowserScanCancel_MidWalk_Test");
        try
        {
            Directory.CreateDirectory(testDir);
            var cacheDir = Path.Combine(testDir, "Cache");
            Directory.CreateDirectory(cacheDir);
            for (int i = 0; i < 5; i++)
                File.WriteAllText(Path.Combine(cacheDir, $"entry{i}.tmp"), "x");

            var fakeBrowser = new BrowserCleanerService.BrowserProfile("TestBrowser", testDir);

            using var cts = new CancellationTokenSource();
            var progress = new Progress<string>(_ => cts.Cancel());

            try
            {
                await BrowserCleanerService.ScanBrowserAsync(fakeBrowser, progress: progress, ct: cts.Token);
                Check(false, "Cancelling during the directory-size walk should throw before the scan completes");
            }
            catch (OperationCanceledException)
            {
                Check(true, "GetDirectorySize now checks the token per-file, so cancellation mid-walk is honored " +
                    "instead of silently finishing the scan");
            }
        }
        finally
        {
            try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); } catch { }
        }
    }

    private static void TestMemoryResultLabelsFreezeAtCompletion()
    {
        // Regresses a PR #15 review finding: FreedLabel/TrimmedLabel were computed from the live
        // IsDryRun toggle, so flipping it after a result was displayed silently relabeled an
        // already-completed real boost as a preview (or vice versa). They must instead reflect the
        // mode captured at the moment that result was produced (ResultWasDryRun).
        var vm = new MemoryViewModel { IsDryRun = false };
        vm.ResultWasDryRun = vm.IsDryRun; // simulates what BoostMemoryAsync captures at completion
        vm.HasBoostResult = true;

        Check(vm.FreedLabel == "Memory Freed",
            $"A completed real boost shows the real-run label (got '{vm.FreedLabel}')");
        Check(vm.TrimmedLabel == "Processes trimmed",
            $"A completed real boost shows the real-run trimmed label (got '{vm.TrimmedLabel}')");

        vm.IsDryRun = true; // toggling the live switch afterward must not relabel the displayed result

        Check(vm.FreedLabel == "Memory Freed",
            $"Toggling Dry Run after completion must not relabel the already-displayed real result (got '{vm.FreedLabel}')");
        Check(vm.TrimmedLabel == "Processes trimmed",
            $"Toggling Dry Run after completion must not relabel the already-displayed trimmed count (got '{vm.TrimmedLabel}')");
    }
}
