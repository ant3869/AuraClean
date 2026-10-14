using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AuraClean.Models;
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
        SectionAsync("File Shredder honors cancellation (now wired to a Cancel button)", TestFileShredderCancellation).GetAwaiter().GetResult();
        SectionAsync("Uninstaller leftover scan honors cancellation (now wired to a Cancel button)", TestUninstallerLeftoverScanCancellation).GetAwaiter().GetResult();
        SectionAsync("ForceDeleteService dry run is honest and protected paths stay refused", TestForceDeleteServiceSafety).GetAwaiter().GetResult();

        Section("Dashboard junk tile never claims 'Nothing to clean' without a completed scan", TestJunkTileStates);
        Section("Quick Clean confirmation cannot be disabled by the dialog setting", TestQuickCleanPromptGate);
        Section("Counts use correct singular/plural wording", TestPluralization);

        return (_pass, _fail);
    }

    private static void TestJunkTileStates()
    {
        Check(MainViewModel.FormatJunkTile(true, false, false, "") == "Scanning…", "While analyzing the tile says Scanning…, not 'Nothing to clean'");
        Check(MainViewModel.FormatJunkTile(false, false, false, "") == "Not scanned yet", "Never scanned / cancelled / failed analysis is 'Not scanned yet'");
        Check(MainViewModel.FormatJunkTile(false, false, true, "") == "Nothing to clean", "A completed analysis with zero results is 'Nothing to clean'");
        Check(MainViewModel.FormatJunkTile(false, true, true, "9.3 GB") == "9.3 GB", "Results show the reclaimable size");

        var vm = new CleanerViewModel();
        Check(!vm.HasCompletedAnalysis, "A fresh cleaner has not completed an analysis");
    }

    private static void TestQuickCleanPromptGate()
    {
        Check(SafetyPromptService.RequiresPrompt(showConfirmationDialogsSetting: false, alwaysAsk: true), "alwaysAsk prompts even when dialogs are disabled in settings");
        Check(!SafetyPromptService.RequiresPrompt(false, false), "Normal flows still honor the disabled-dialogs setting");
        Check(SafetyPromptService.RequiresPrompt(true, false), "Normal flows prompt when dialogs are enabled");
    }

    private static void TestPluralization()
    {
        Check(AuraClean.Helpers.FormatHelper.Pluralize(1, "item") == "1 item", "1 item is singular");
        Check(AuraClean.Helpers.FormatHelper.Pluralize(0, "item") == "0 items", "0 items is plural");
        Check(AuraClean.Helpers.FormatHelper.Pluralize(1234, "item") == 1234L.ToString("N0") + " items", "Large counts use the current culture's grouping and are plural");

        var cat = new JunkCategory { Name = "Test" };
        cat.Items.Add(new JunkItem { Path = @"C:\x", SizeBytes = 1 });
        Check(cat.ItemCountLabel == "1 item", "Category header reads '1 item', not '1 items'");
        cat.Items.Add(new JunkItem { Path = @"C:\y", SizeBytes = 1 });
        Check(cat.ItemCountLabel == "2 items", "Category header pluralizes at 2");
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

    private static async Task TestFileShredderCancellation()
    {
        // Regression for a Phase 2/3 UX gap: FileShredderViewModel.ShredAllAsync never created a
        // CancellationTokenSource and the view had no Cancel button, so a multi-pass destructive
        // overwrite could not be aborted once started — unlike every other long operation in the
        // app. The service itself already accepted a token; this proves it honors a pre-cancelled
        // one, the same mechanism the ViewModel's new Cancel button now triggers.
        var testDir = Path.Combine(Path.GetTempPath(), "AuraClean_ShredCancel_Test");
        try
        {
            Directory.CreateDirectory(testDir);
            var path = Path.Combine(testDir, "secret.bin");
            File.WriteAllBytes(path, new byte[64 * 1024]);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            try
            {
                await FileShredderService.ShredFilesAsync(
                    [path], FileShredderService.ShredAlgorithm.Enhanced7Pass, progress: null, ct: cts.Token);
                Check(false, "A pre-cancelled token should throw before shredding anything");
            }
            catch (OperationCanceledException)
            {
                Check(true, "ShredFilesAsync honors a pre-cancelled token (FileShredderViewModel now wires this to a Cancel button)");
            }

            Check(File.Exists(path), "The file is untouched when shredding is cancelled before it starts");

            // Codex finding on PR #16: cancelling while the ONLY/last file is mid-overwrite used to be
            // swallowed by the per-file catch and reported as "1 failed" instead of cancellation.
            var bigPath = Path.Combine(testDir, "big.bin");
            File.WriteAllBytes(bigPath, new byte[32 * 1024 * 1024]);
            using var midCts = new CancellationTokenSource();
            var midProgress = new Progress<(int current, int total, string fileName)>(_ => midCts.CancelAfter(5));
            try
            {
                var midResult = await FileShredderService.ShredFilesAsync(
                    [bigPath], FileShredderService.ShredAlgorithm.Enhanced7Pass, midProgress, midCts.Token);
                Check(false, $"Cancelling mid-overwrite of the last file must throw, not return ({midResult.FilesFailed} failed)");
            }
            catch (OperationCanceledException)
            {
                Check(true, "Cancellation during the last file's overwrite propagates instead of being counted as a failure");
            }
        }
        finally
        {
            try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); } catch { }
        }
    }

    private static async Task TestUninstallerLeftoverScanCancellation()
    {
        // Regression for a Phase 2/3 UX gap: UninstallerViewModel had zero cancellation anywhere,
        // including its longest operation (the post-uninstall leftover scan across the registry
        // and file system), despite already having an IsScanning flag scoped correctly for it. The
        // service accepted a token already; this proves it honors a pre-cancelled one, the same
        // mechanism the ViewModel's new Cancel button now triggers via DeepScanAsync.
        var fakeProgram = new InstalledProgram
        {
            DisplayName = "AuraClean_NonExistent_Test_Program_XYZ",
            Publisher = "AuraClean Test Suite"
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            await UninstallerService.PostUninstallScanAsync(fakeProgram, progress: null, ct: cts.Token);
            Check(false, "A pre-cancelled token should throw before scanning anything");
        }
        catch (OperationCanceledException)
        {
            Check(true, "PostUninstallScanAsync honors a pre-cancelled token (UninstallerViewModel now wires this to a Cancel button)");
        }
    }

    private static async Task TestForceDeleteServiceSafety()
    {
        // Regression for the Phase 2/3 feature gap named explicitly in the brief: ForceDeleteService
        // was only reachable internally via the Uninstaller's broken-uninstaller fallback, with no
        // UI path for the far more common case of a locked file in Large File Finder. Large File
        // Finder's new "Force Delete" button calls this same service method directly, so this
        // proves (a) its dry run is honest — it inspects without deleting or killing anything — and
        // (b) the protected-path safety gate that guards a normal delete still guards this one.
        var testDir = Path.Combine(Path.GetTempPath(), "AuraClean_ForceDelete_Test");
        try
        {
            Directory.CreateDirectory(testDir);
            var testFile = Path.Combine(testDir, "unlocked.txt");
            File.WriteAllText(testFile, "test content");

            var dryRunResult = await ForceDeleteService.ForceDeleteAsync(
                testFile, terminateLockers: true, scheduleBootDelete: true, dryRun: true);

            Check(dryRunResult.Success, "Dry run reports success (it only inspects, never fails)");
            Check(dryRunResult.ActionTaken == ForceDeleteService.ForceDeleteAction.DryRunOnly,
                $"Dry run takes no real action (got {dryRunResult.ActionTaken})");
            Check(File.Exists(testFile), "Dry run never actually deletes the file");
            Check(dryRunResult.KilledProcesses.Count == 0, "Dry run never terminates processes");

            var systemFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "kernel32.dll");
            var blockedResult = await ForceDeleteService.ForceDeleteAsync(
                systemFile, terminateLockers: true, scheduleBootDelete: true, dryRun: false);

            Check(!blockedResult.Success, "Force delete refuses a protected system file, exactly like a normal delete");
            Check(blockedResult.ActionTaken == ForceDeleteService.ForceDeleteAction.Failed,
                $"The refusal is reported as Failed, not silently ignored (got {blockedResult.ActionTaken})");
            Check(File.Exists(systemFile), "The protected system file is untouched");
        }
        finally
        {
            try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); } catch { }
        }
    }
}
