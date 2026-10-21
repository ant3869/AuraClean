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
            Check(caught != null && caught.PartiallyOverwrittenFile == null, "A cancel before the next file's first write reports no partially overwritten file");
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

    private static void TestCancellationMessages()
    {
        var untouched = new FileShredderService.ShredCancelledException(new string[0], 0, null, CancellationToken.None);
        var some = new FileShredderService.ShredCancelledException(new[] { @"C:\x\one.bin", @"C:\x\two.bin" }, 10, null, CancellationToken.None);
        var partial = new FileShredderService.ShredCancelledException(new[] { @"C:\x\one.bin" }, 10, @"C:\x\big.bin", CancellationToken.None);

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
