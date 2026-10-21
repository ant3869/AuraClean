using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using AuraClean.ViewModels;
using AuraClean.Views;

namespace TestFeatures;

/// <summary>
/// Regression coverage for the final UX corrections found by driving the real app:
/// shredder cancellation reporting, Large File Finder folder display + minimum-size validation,
/// Uninstaller header/data alignment, and test log isolation. Synthetic data only.
/// </summary>
public static class UxCorrectionsTests
{
    private static int _pass;
    private static int _fail;
    private static volatile string _step = "start";

    public static (int Pass, int Fail) Run()
    {
        _pass = 0;
        _fail = 0;

        Section("Test runs keep their logs out of the user's real log folder", TestLogIsolation);
        SectionAsync("Shredder: cancel before any change is reported as untouched", TestCancelBeforeModification).GetAwaiter().GetResult();
        SectionAsync("Shredder: cancel mid-overwrite flags the partially overwritten file", TestCancelMidOverwrite).GetAwaiter().GetResult();
        SectionAsync("Shredder: overwrite/rename/delete failures followed by cancel are never reported as unchanged", TestFaultsThenCancel).GetAwaiter().GetResult();
        Section("Shredder: cancellation message is honest about partial damage", TestCancellationMessages);
        Section("Large File Finder: minimum size is validated with friendly messages", TestMinimumSizeValidation);
        Section("Large File Finder: a browsed folder is shown and invalid input never starts a scan", TestFolderDisplayAndScanGate);
        Section("Uninstaller: column headers line up with their data", TestUninstallerAlignment);

        Console.WriteLine($"  UX corrections: {_pass} passed, {_fail} failed");
        return (_pass, _fail);
    }

    private static void Section(string name, Action test)
    {
        Console.WriteLine($"═══ UX: {name} ═══");
        try { test(); }
        catch (Exception ex) { Check(false, $"Unexpected exception: {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine();
    }

    private static async Task SectionAsync(string name, Func<Task> test)
    {
        Console.WriteLine($"═══ UX: {name} ═══");
        try { await test(); }
        catch (Exception ex) { Check(false, $"Unexpected exception: {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine();
    }

    private static void Check(bool condition, string message)
    {
        Console.ForegroundColor = condition ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}: {message}");
        Console.ResetColor();
        if (condition) _pass++; else _fail++;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AuraUxTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── log isolation ─────────────────────────────────────────────────────────────────────────────
    private static void TestLogIsolation()
    {
        var realLogs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuraClean", "Logs");

        Check(!string.Equals(DiagnosticLogger.LogDirectory, realLogs, StringComparison.OrdinalIgnoreCase),
            "The runner's log folder is not the user's real AuraClean log folder");
        Check(DiagnosticLogger.CrashLogPath.StartsWith(DiagnosticLogger.LogDirectory, StringComparison.OrdinalIgnoreCase),
            "crash.log lives in the isolated folder");
        Check(DiagnosticLogger.LogDirectory.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            "...which is a temp folder");

        var realCrash = Path.Combine(realLogs, "crash.log");
        var before = File.Exists(realCrash) ? new FileInfo(realCrash).Length : -1;
        DiagnosticLogger.Crash("UxCorrectionsTests isolation probe", new InvalidOperationException("synthetic"));
        var after = File.Exists(realCrash) ? new FileInfo(realCrash).Length : -1;
        Check(before == after, "Writing a crash entry during tests leaves the real crash.log untouched");
        Check(File.Exists(DiagnosticLogger.CrashLogPath), "The entry landed in the isolated crash.log");
    }

    // ── shredder cancellation ─────────────────────────────────────────────────────────────────────
    private sealed class SyncProgress(Action<int> onFile) : IProgress<(int current, int total, string fileName)>
    {
        public void Report((int current, int total, string fileName) value) => onFile(value.current);
    }

    private static async Task TestCancelBeforeModification()
    {
        var dir = NewTempDir();
        try
        {
            var a = Path.Combine(dir, "a.bin");
            var b = Path.Combine(dir, "b.bin");
            File.WriteAllBytes(a, Enumerable.Repeat((byte)0xAB, 4096).ToArray());
            File.WriteAllBytes(b, Enumerable.Repeat((byte)0xAB, 4096).ToArray());

            using var cts = new CancellationTokenSource();
            // Cancel the moment the second file is announced, before its first write.
            var progress = new SyncProgress(n => { if (n == 2) cts.Cancel(); });

            FileShredderService.ShredCancelledException caught = null;
            try
            {
                await FileShredderService.ShredFilesAsync([a, b], FileShredderService.ShredAlgorithm.DoD3Pass, progress, cts.Token);
            }
            catch (FileShredderService.ShredCancelledException ex) { caught = ex; }

            Check(caught != null, "Cancellation surfaces as ShredCancelledException");
            Check(caught is OperationCanceledException, "...which is still an OperationCanceledException, so existing handlers keep working");
            Check(caught != null && caught.ShreddedPaths.SequenceEqual(new[] { a }), "It reports the file that was fully shredded before the cancel");
            Check(caught != null && caught.PartiallyOverwrittenFile == null && caught.DamagedFiles.Count == 0, "A cancel before the next file's first write reports no partially overwritten or damaged file");
            Check(!File.Exists(a), "The completed file is gone");
            Check(File.Exists(b) && File.ReadAllBytes(b).All(x => x == 0xAB), "The file that had not started is byte-for-byte untouched");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static async Task TestCancelMidOverwrite()
    {
        var dir = NewTempDir();
        try
        {
            var path = Path.Combine(dir, "big.bin");
            const int size = 192 * 1024 * 1024;
            FileShredderService.ShredCancelledException caught = null;

            for (int attempt = 0; attempt < 3 && caught?.PartiallyOverwrittenFile == null; attempt++)
            {
                using (var fs = File.Create(path))
                {
                    var chunk = Enumerable.Repeat((byte)0xAB, 1024 * 1024).ToArray();
                    for (int i = 0; i < size / chunk.Length; i++) fs.Write(chunk, 0, chunk.Length);
                }

                using var cts = new CancellationTokenSource();
                cts.CancelAfter(100 + attempt * 150);
                caught = null;
                try
                {
                    await FileShredderService.ShredFilesAsync([path], FileShredderService.ShredAlgorithm.Enhanced7Pass, null, cts.Token);
                }
                catch (FileShredderService.ShredCancelledException ex) { caught = ex; }
            }

            Check(caught != null, "Cancelling the only file mid-overwrite still surfaces as a cancellation");
            Check(caught?.PartiallyOverwrittenFile == path, "The file being overwritten when the cancel arrived is reported");
            Check(File.Exists(path), "That file was NOT deleted");
            var head = new byte[4096];
            using (var fs = File.OpenRead(path)) fs.ReadExactly(head);
            Check(head.Any(x => x != 0xAB), "...and its contents really were partly overwritten (the report matches the disk)");
            Check(caught?.ShreddedPaths.Count == 0, "No file is reported as fully shredded");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── fault injection: overwrite / rename / delete failures, then cancellation ───────────────────
    private sealed class FaultyOps : FileShredderService.IShredFileOperations
    {
        private readonly FileShredderService.IShredFileOperations _real = FileShredderService.Operations;
        private readonly Dictionary<string, string> _renamedFrom = new();

        public Func<string, bool> OverwriteFailsBeforeAnyWrite = _ => false;
        public Func<string, bool> OverwriteFailsAfterFirstWrite = _ => false;
        public Func<string, bool> CancelledDuringOverwrite = _ => false;
        public Func<string, bool> RenameFails = _ => false;
        public Func<string, bool> DeleteFails = _ => false; // keyed by the ORIGINAL path, even after a rename

        public async Task OverwriteAsync(string path, long size, FileShredderService.ShredAlgorithm alg, CancellationToken ct, FileShredderService.OverwriteProgress progress)
        {
            if (OverwriteFailsBeforeAnyWrite(path)) throw new IOException("injected: cannot open for writing");
            if (OverwriteFailsAfterFirstWrite(path))
            {
                await _real.OverwriteAsync(path, size, alg, CancellationToken.None, progress); // really overwrites first
                progress.Started = true;
                throw new IOException("injected: disk error during overwrite");
            }
            if (CancelledDuringOverwrite(path))
            {
                progress.Started = true;
                throw new OperationCanceledException(ct);
            }
            await _real.OverwriteAsync(path, size, alg, ct, progress);
        }

        public void Move(string source, string destination)
        {
            if (RenameFails(source)) throw new IOException("injected: rename failed");
            _real.Move(source, destination);
            _renamedFrom[destination] = source;
        }

        public void Delete(string path)
        {
            var original = _renamedFrom.TryGetValue(path, out var o) ? o : path;
            if (DeleteFails(original)) throw new UnauthorizedAccessException("injected: delete failed");
            _real.Delete(path);
        }
    }

    private static string[] MakeFiles(string dir, int count)
    {
        var files = new string[count];
        for (int i = 0; i < count; i++)
        {
            files[i] = Path.Combine(dir, $"f{i + 1}.bin");
            File.WriteAllBytes(files[i], Enumerable.Repeat((byte)0xAB, 4096).ToArray());
        }
        return files;
    }

    /// <summary>Shreds <paramref name="files"/> with injected faults, cancelling when file number <paramref name="cancelAt"/> starts.</summary>
    private static async Task<(FileShredderService.ShredCancelledException Cancelled, FileShredderService.ShredResult Result)> RunWithFaults(
        FaultyOps ops, string[] files, int cancelAt)
    {
        var previous = FileShredderService.Operations;
        FileShredderService.Operations = ops;
        try
        {
            using var cts = new CancellationTokenSource();
            var progress = new SyncProgress(n => { if (n == cancelAt) cts.Cancel(); });
            try
            {
                var result = await FileShredderService.ShredFilesAsync(files, FileShredderService.ShredAlgorithm.QuickZero, progress, cts.Token);
                return (null, result);
            }
            catch (FileShredderService.ShredCancelledException ex) { return (ex, null); }
        }
        finally { FileShredderService.Operations = previous; }
    }

    private static async Task TestFaultsThenCancel()
    {
        var dir = NewTempDir();
        try
        {
            // 1. Overwrite fails after it began (file 1), then cancel on file 2.
            var f = MakeFiles(dir, 3);
            var (c1, _) = await RunWithFaults(new FaultyOps { OverwriteFailsAfterFirstWrite = p => p == f[0] }, f, cancelAt: 2);
            var m1 = FileShredderViewModel.DescribeCancellation(c1);
            Check(c1 is OperationCanceledException, "A cancel after a failed overwrite is still an OperationCanceledException");
            Check(c1.DamagedFiles.Count == 1 && c1.DamagedFiles[0].Path == f[0] && c1.DamagedFiles[0].Kind == FileShredderService.DamageKind.OverwriteFailed,
                "An overwrite that failed after starting is remembered as damaged when a later cancel arrives");
            Check(File.Exists(f[0]) && new FileInfo(f[0]).Length != 4096, "...and that file really was modified (truncated by the overwrite) and is still on disk");
            Check(m1.Contains("f1.bin") && m1.Contains("no longer intact") && !m1.Contains("Nothing was overwritten") && !m1.Contains("remaining files were not changed"),
                $"The message names it and does not claim nothing changed ('{m1}')");

            // 2. Rename fails and the fallback delete fails too: file stays at its path, contents destroyed.
            f = MakeFiles(dir, 3);
            var (c2, _) = await RunWithFaults(new FaultyOps { RenameFails = p => p == f[0], DeleteFails = p => p == f[0] }, f, cancelAt: 2);
            Check(c2.DamagedFiles.Count == 1 && c2.DamagedFiles[0].Path == f[0] && c2.DamagedFiles[0].Kind == FileShredderService.DamageKind.NotDeleted && c2.DamagedFiles[0].RemainsAs == null,
                "A rename failure followed by a delete failure is remembered as overwritten-but-not-deleted at the original path");
            var m2 = FileShredderViewModel.DescribeCancellation(c2);
            Check(m2.Contains("f1.bin") && m2.Contains("could not be deleted") && m2.Contains("still on disk") && !m2.Contains("Nothing was overwritten"),
                $"The message says it is destroyed but still on disk ('{m2}')");

            // 3. Rename succeeds, then delete fails: the file now lives under a random name, and that name is tracked.
            f = MakeFiles(dir, 3);
            var (c3, _) = await RunWithFaults(new FaultyOps { DeleteFails = p => p == f[0] }, f, cancelAt: 2);
            var d3 = c3.DamagedFiles.SingleOrDefault();
            Check(d3 != null && d3.Kind == FileShredderService.DamageKind.NotDeleted && d3.RemainsAs != null && d3.RemainsAs != f[0],
                "A rename that succeeded before the delete failed keeps track of the renamed path");
            Check(d3 != null && !File.Exists(f[0]) && File.Exists(d3.RemainsAs), "...which is where the file actually is (the original path is gone)");
            var m3 = FileShredderViewModel.DescribeCancellation(c3);
            Check(m3.Contains(Path.GetFileName(d3.RemainsAs)) && m3.Contains("still exists as"), $"The message tells the user the new name ('{m3}')");
            Check(File.ReadAllBytes(d3.RemainsAs).All(x => x != 0xAB), "The renamed file's original contents are gone");

            // 4. A failure BEFORE any write is not damage: a later cancel may truthfully say nothing was changed.
            f = MakeFiles(dir, 3);
            var (c4, _) = await RunWithFaults(new FaultyOps { OverwriteFailsBeforeAnyWrite = p => p == f[0] }, f, cancelAt: 2);
            Check(c4.DamagedFiles.Count == 0 && File.ReadAllBytes(f[0]).All(x => x == 0xAB), "A failure before any write leaves the file untouched and is not reported as damage");
            Check(FileShredderViewModel.DescribeCancellation(c4).Contains("Nothing was overwritten"), "...so the cancel message may say nothing was overwritten");

            // 5. Earlier damaged file + cancel mid-overwrite of the next: both are reported.
            f = MakeFiles(dir, 3);
            var (c5, _) = await RunWithFaults(new FaultyOps { DeleteFails = p => p == f[0], CancelledDuringOverwrite = p => p == f[1] }, f, cancelAt: 2);
            Check(c5.DamagedFiles.Count == 2 && c5.DamagedFiles[0].Path == f[0] && c5.DamagedFiles[1].Path == f[1] &&
                  c5.DamagedFiles[1].Kind == FileShredderService.DamageKind.CancelledDuringOverwrite, "An earlier damaged file and the file cancelled mid-overwrite are both reported");
            Check(c5.PartiallyOverwrittenFile == f[1], "PartiallyOverwrittenFile still identifies the file being overwritten at the cancel");
            var m5 = FileShredderViewModel.DescribeCancellation(c5);
            Check(m5.Contains("f1.bin") && m5.Contains("f2.bin") && !m5.Contains("Nothing was overwritten"), $"The message names both files ('{m5}')");
            Check(File.Exists(f[2]) && File.ReadAllBytes(f[2]).All(x => x == 0xAB), "The file that was never reached is untouched");

            // 6. Without any cancel, the normal result carries the same information.
            f = MakeFiles(dir, 2);
            var (c6, r6) = await RunWithFaults(new FaultyOps { DeleteFails = p => p == f[0] }, f, cancelAt: int.MaxValue);
            Check(c6 == null && r6 != null && r6.FilesFailed == 1 && r6.FilesShredded == 1, "Without a cancel, one file fails and the other is shredded as before");
            Check(r6.DamagedFiles is { Count: 1 } && r6.DamagedFiles[0].Path == f[0], "The normal result lists the overwritten-but-not-deleted file");
            Check(FileShredderViewModel.DescribeDamaged(r6.DamagedFiles).Contains("destroyed"), "...and the summary says its contents are destroyed");

            // 7. Normal shredding through the real operations is unchanged.
            f = MakeFiles(dir, 2);
            var plain = await FileShredderService.ShredFilesAsync(f, FileShredderService.ShredAlgorithm.QuickZero);
            Check(plain.FilesShredded == 2 && plain.FilesFailed == 0 && plain.DamagedFiles!.Count == 0 && !File.Exists(f[0]) && !File.Exists(f[1]),
                "Normal shredding still deletes the files and reports no damaged files");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static FileShredderService.ShredCancelledException Cancelled(string[] shredded, params FileShredderService.DamagedFile[] damaged)
        => new(shredded, 10, damaged, CancellationToken.None);

    private static void TestCancellationMessages()
    {
        var untouched = Cancelled(new string[0]);
        var some = Cancelled(new[] { "one.bin", "two.bin" });
        var partial = Cancelled(new[] { "one.bin" },
            new FileShredderService.DamagedFile("big.bin", null, FileShredderService.DamageKind.CancelledDuringOverwrite));

        var m0 = FileShredderViewModel.DescribeCancellation(untouched);
        var m1 = FileShredderViewModel.DescribeCancellation(some);
        var m2 = FileShredderViewModel.DescribeCancellation(partial);

        Check(m0.Contains("before any file was changed") && m0.Contains("Nothing was overwritten"), $"Cancel before any change says nothing was overwritten ('{m0}')");
        Check(m1.Contains("2 files fully shredded") && m1.Contains("remaining files were not changed"), $"Cancel between files reports the count and that the rest is untouched ('{m1}')");
        Check(m2.Contains("big.bin") && m2.Contains("partly destroyed") && m2.Contains("NOT deleted") && m2.Contains("no longer intact"),
            $"Cancel mid-overwrite names the file and says it is partly destroyed, not deleted and no longer intact ('{m2}')");
        Check(!m2.Contains("recover", StringComparison.OrdinalIgnoreCase) && !m2.Contains("safe", StringComparison.OrdinalIgnoreCase),
            "The partial-damage message never implies the data is recoverable or safe");
        Check(!m0.Contains("partly") && !m1.Contains("partly"), "Clean cancellations don't claim any partial damage");

        var many = Cancelled(new string[0],
            Enumerable.Range(1, 5).Select(i => new FileShredderService.DamagedFile($"f{i}.bin", null, FileShredderService.DamageKind.NotDeleted)).ToArray());
        var mm = FileShredderViewModel.DescribeCancellation(many);
        Check(mm.Contains("f1.bin") && mm.Contains("f3.bin") && !mm.Contains("f4.bin") && mm.Contains("2 more file(s)"), $"Many damaged files are summarised, not silently dropped ('{mm}')");
        Check(!mm.Contains("Nothing was overwritten") && !mm.Contains("remaining files were not changed"), "A run with damaged files never claims nothing changed");
    }

    // ── Large File Finder ─────────────────────────────────────────────────────────────────────────
    private static void TestMinimumSizeValidation()
    {
        int mb;
        Check(LargeFileFinderViewModel.ValidateMinimumSize("100", out mb) == null && mb == 100, "'100' is valid");
        Check(LargeFileFinderViewModel.ValidateMinimumSize("  250 ", out mb) == null && mb == 250, "Surrounding spaces are ignored");
        Check(LargeFileFinderViewModel.ValidateMinimumSize("1", out mb) == null && mb == 1, "The minimum of 1 MB is valid");
        Check(LargeFileFinderViewModel.ValidateMinimumSize("1048576", out mb) == null && mb == 1_048_576, "The maximum (1 TB) is valid");

        foreach (var bad in new[] { "", "   ", "abc", "10.5", "-5", "1e3", @"C:\Users\x", "１２" })
        {
            var msg = LargeFileFinderViewModel.ValidateMinimumSize(bad, out mb);
            Check(msg != null && mb == 0, $"'{bad}' is rejected with a message ('{msg}')");
        }
        Check(LargeFileFinderViewModel.ValidateMinimumSize("0", out _) == "The minimum size must be at least 1 MB.", "Zero is rejected: at least 1 MB");
        Check(LargeFileFinderViewModel.ValidateMinimumSize("99999999999", out _).StartsWith("Too large"), "Huge values are rejected as too large");
        Check(new[] { "", "abc", "0", "99999999999" }.Select(t => LargeFileFinderViewModel.ValidateMinimumSize(t, out _))
                .All(m => !m.Contains("converted") && !m.Contains("Value ''")),
            "No message is a raw framework conversion error");
    }

    private static void TestFolderDisplayAndScanGate()
    {
        var dir = NewTempDir();
        try
        {
            var vm = new LargeFileFinderViewModel();
            vm.UseFolder(dir);
            Check(vm.ScanPath == dir, "Browsing sets the scan path");
            Check(vm.SelectedDrive != null && vm.SelectedDrive.IsFolder && vm.SelectedDrive.Display.Contains(dir),
                $"The chosen folder is shown in the location box ('{vm.SelectedDrive?.Display}')");
            Check(vm.Drives.Count(d => d.IsFolder) == 1 && vm.Drives[0].IsFolder, "It is listed once, at the top");

            var dir2 = NewTempDir();
            try
            {
                vm.UseFolder(dir2);
                Check(vm.Drives.Count(d => d.IsFolder) == 1 && vm.SelectedDrive.Path == dir2, "Browsing again replaces the previous folder entry");
            }
            finally { try { Directory.Delete(dir2, true); } catch { } }

            var drive = vm.Drives.First(d => !d.IsFolder);
            vm.UseFolder(drive.Path);
            Check(vm.SelectedDrive == drive && vm.Drives.Count(d => d.IsFolder) == 1, "Choosing a drive path selects the existing drive entry instead of duplicating it");

            // Typing invalid text blocks the scan.
            vm.UseFolder(dir);
            vm.MinimumSizeText = "123";
            vm.MinimumSizeText = "abc";
            Check(vm.MinSizeError.Length > 0 && vm.MinimumSizeMB == 123, "Invalid text shows a message and does not change the accepted size");
            vm.ScanCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(vm.StatusMessage.StartsWith("Can't scan yet") && !vm.IsBusy && !vm.HasResults,
                $"Scanning with invalid input is refused before any scan starts ('{vm.StatusMessage}')");

            vm.MinimumSizeText = "250";
            Check(vm.MinSizeError.Length == 0 && vm.MinimumSizeMB == 250, "Valid text clears the message and updates the size");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── Uninstaller alignment (real view, off-screen) ─────────────────────────────────────────────
    private static void TestUninstallerAlignment()
    {
        var skip = WpfTestHost.EnsureStarted();
        if (skip != null)
        {
            Console.WriteLine($"  SKIP: {skip}");
            return;
        }

        try { WpfTestHost.Invoke(RunUninstallerScenario, TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { Check(false, $"Uninstaller fixture timed out at step '{_step}'"); }
    }

    private static T FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var inner = FindFirst<T>(child);
            if (inner != null) return inner;
        }
        return null;
    }

    private static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var list = new List<T>();
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) list.Add(t);
            list.AddRange(FindAll<T>(child));
        }
        return list;
    }

    private static void RunUninstallerScenario()
    {
        _step = "create";
        var vm = new UninstallerViewModel
        {
            FilteredPrograms = new ObservableCollection<InstalledProgram>
            {
                new() { DisplayName = "Sample App One", Publisher = "Contoso Ltd", DisplayVersion = "1.2.3", EstimatedSizeKB = 5600, InstallDate = "20260101" },
                new() { DisplayName = "Sample App Two", Publisher = "Fabrikam", DisplayVersion = "24.1.0.166", EstimatedSizeKB = 3_500_000, InstallDate = "20250330" },
            }
        };
        var view = new UninstallerView { DataContext = vm };
        var window = new Window
        {
            Content = view, Width = 1400, Height = 800,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            ShowInTaskbar = false, ShowActivated = false
        };

        try
        {
            _step = "show";
            window.Show();
            window.UpdateLayout();

            var list = FindFirst<ListView>(view);
            Check(list != null, "The program list is present");
            double Left(FrameworkElement e) => e.TransformToAncestor(list).Transform(new Point(0, 0)).X;

            var headers = FindAll<GridViewColumnHeader>(list)
                .Where(h => h.Column != null).OrderBy(h => Left(h)).ToList();
            var row = FindFirst<ListViewItem>(list);
            var cells = FindAll<TextBlock>(row).Where(t => !string.IsNullOrEmpty(t.Text)).ToList();

            Check(headers.Count == 7, $"All seven columns have a header, including Drive ({headers.Count})");

            // Column 1..6 = name, publisher, version, size, installed, drive; compare left edges of the text.
            string[] expected = ["Sample App One", "Contoso Ltd", "1.2.3", null, null, null];
            var headerText = headers.Skip(1).Select(h => FindFirst<TextBlock>(h)).ToList();

            int aligned = 0, compared = 0;
            for (int i = 0; i < Math.Min(headerText.Count, expected.Length); i++)
            {
                if (expected[i] == null || headerText[i] == null) continue;
                var data = cells.FirstOrDefault(c => c.Text == expected[i]);
                if (data == null) continue;
                compared++;
                var delta = Math.Abs(Left(headerText[i]) - Left(data));
                if (delta <= 6) aligned++;
                else Console.WriteLine($"    column {i + 1}: header '{headerText[i].Text}' x={Left(headerText[i]):F1} vs data '{data.Text}' x={Left(data):F1}");
            }
            Check(compared == 3, $"Header and data text were found for the name, publisher and version columns ({compared})");
            Check(aligned == compared, $"Every compared header's text starts within 6 DIP of its data ({aligned}/{compared} aligned)");

            Check(headers.Skip(1).All(h => { var b = FindFirst<Button>(h); return b == null || b.ActualWidth <= h.ActualWidth + 0.5; }),
                "No header button is wider than its column (headers are not clipped)");
        }
        finally
        {
            window.Close();
        }
    }
}
