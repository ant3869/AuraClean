using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AuraClean.Models;
using AuraClean.Services;
using AuraClean.ViewModels;
using AuraClean.Views;

namespace TestFeatures;

/// <summary>
/// Selection-integrity coverage for the System Cleaner result list: review-only / high-risk
/// categories (WinSxS, Windows.old, ...) must only become selected by an explicit user action,
/// never through collection refreshes, bindings, virtualization or re-analysis.
/// Uses synthetic JunkItems only — nothing is scanned or deleted.
/// </summary>
public static class CleanerSelectionIntegrityTests
{
    private static int _pass;
    private static int _fail;
    private static volatile string _step = "start";

    private const long WinSxSBytes = 8_120_000_000;
    private const long WindowsOldBytes = 5_000_000_000;

    public static (int Pass, int Fail) Run()
    {
        _pass = 0;
        _fail = 0;

        Section("Default policy: review-only categories are not pre-selected", TestDefaultPolicy);
        Section("Category sync never selects an unselected review-only item", TestCategorySyncDoesNotSelect);
        Section("High-risk policy table (WinSxS, Windows.old)", TestPolicyTable);
        Section("Select All skips high-risk rows but respects deliberate choices", TestSelectAllSkipsHighRisk);
        Section("Category-level bulk selection skips high-risk rows", TestCategoryLevelBulk);
        Section("Deliberate high-risk selection keeps count/size/label accurate", TestDeliberateSelectionKeepsTotalsAccurate);
        Section("Selection counts and sizes always equal the real selection (with an exclusion in force)",
            () => WithSettings(new AppSettings { CleanerExcludedPaths = [@"C:\Temp\f0.tmp"] }, TestCountsReflectSelection));
        Section("High-risk cleanup always requires an unwaivable confirmation", TestHighRiskCleanupRequiresConfirmation);
        Section("Quick Clean stays strictly low-risk", TestQuickCleanStaysLowRisk);
        Section("Real CleanerView: layout, scrolling and virtualization keep WinSxS unselected", TestViewKeepsWinSxSUnselected);

        Console.WriteLine($"  Selection integrity: {_pass} passed, {_fail} failed");
        return (_pass, _fail);
    }

    private static void Section(string name, Action test)
    {
        Console.WriteLine($"═══ SELECTION: {name} ═══");
        try { WithSettings(new AppSettings(), test); }
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

    /// <summary>
    /// Runs <paramref name="test"/> with SettingsService serving <paramref name="settings"/> from
    /// memory, so exclusions (JunkCategory/CleanerExcludeStore) never come from the machine's real
    /// AuraClean configuration. Test-only: swaps the private cache, nothing is read from or written
    /// to disk, and the previous cache is restored afterwards. Production code is untouched.
    /// </summary>
    private static void WithSettings(AppSettings settings, Action test)
    {
        var type = typeof(SettingsService);
        var cache = type.GetField("_cached", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SettingsService._cached not found; update the test seam.");
        var gate = type.GetField("_lock", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        object previous;
        lock (gate) { previous = cache.GetValue(null); cache.SetValue(null, settings); }
        try { test(); }
        finally { lock (gate) { cache.SetValue(null, previous); } }
    }

    /// <summary>Mirrors CleanerViewModel.AnalyzeAsync: default policy, exclusions, then group.</summary>
    private static ObservableCollection<JunkCategory> BuildCategories(List<JunkItem> items, bool advanced)
    {
        CleanupModePolicy.ApplyDefaultSelection(items, new AppSettings(), advanced);
        foreach (var item in items)
            if (CleanerExcludeStore.IsExcluded(item.Path))
                item.IsSelected = false;
        var grouped = items.GroupBy(i => i.Category).OrderBy(g => g.Key).Select(g =>
        {
            var cat = new JunkCategory { Name = g.Key };
            foreach (var item in g.OrderByDescending(i => i.SizeBytes))
                cat.Items.Add(item);
            cat.SyncSelectionState();
            return cat;
        });
        return new ObservableCollection<JunkCategory>(grouped);
    }

    private static List<JunkItem> Scenario(int tempCount = 40)
    {
        var items = new List<JunkItem>
        {
            new() { Type = JunkType.WinSxS, Path = @"C:\AuraSynthetic\WinSxS", Description = "Component Store", SizeBytes = WinSxSBytes },
            new() { Type = JunkType.WindowsOld, Path = @"C:\AuraSynthetic\Windows.old", Description = "Windows.old", SizeBytes = WindowsOldBytes },
            new() { Type = JunkType.CrashDump, Path = @"C:\dump.dmp", Description = "Crash dump", SizeBytes = 12_000_000 },
        };
        for (int i = 0; i < tempCount; i++)
            items.Add(new JunkItem { Type = JunkType.TempFile, Path = $@"C:\Temp\f{i}.tmp", Description = $"Temp {i}", SizeBytes = 1_000_000 });
        return items;
    }

    private static void TestDefaultPolicy()
    {
        var items = Scenario();
        var cats = BuildCategories(items, advanced: true);
        Check(!items.First(i => i.Type == JunkType.WinSxS).IsSelected, "WinSxS is unselected by default in Advanced mode");
        Check(!items.First(i => i.Type == JunkType.WindowsOld).IsSelected, "Windows.old is unselected by default in Advanced mode");
        Check(items.Where(i => i.Type == JunkType.TempFile).All(i => i.IsSelected), "Low-risk temp files are selected by default");
        Check(!cats.First(c => c.Name.Contains("WinSxS")).IsAllSelected, "The WinSxS group header reflects 'not all selected'");
    }

    private static void TestCategorySyncDoesNotSelect()
    {
        var items = Scenario();
        var cats = BuildCategories(items, advanced: true);
        var winsxs = items.First(i => i.Type == JunkType.WinSxS);
        var winCat = cats.First(c => c.Name.Contains("WinSxS"));

        winCat.Items.Add(new JunkItem { Type = JunkType.WinSxS, Path = @"C:\x", SizeBytes = 1, IsSelected = false });
        winCat.Items.RemoveAt(winCat.Items.Count - 1);
        winCat.SyncSelectionState();
        winsxs.SizeBytes += 10;
        foreach (var c in cats) c.SyncSelectionState();

        Check(!winsxs.IsSelected, "Adding/removing rows and re-syncing the group leaves WinSxS unselected");
        Check(!winCat.IsAllSelected, "The group header stays unchecked");
    }

    private static void TestSelectAllSkipsHighRisk()
    {
        var items = Scenario();
        var cats = BuildCategories(items, advanced: true);
        var vm = new CleanerViewModel { Categories = cats, HasResults = true, TotalJunkCount = items.Count, IsAdvancedMode = true };
        var winsxs = items.First(i => i.Type == JunkType.WinSxS);
        var windowsOld = items.First(i => i.Type == JunkType.WindowsOld);

        int before = items.Count(i => i.IsSelected);
        Check(vm.SmartCleanLabel.Contains($"{before} ITEM"), $"Label count matches the real selection before Select All ({vm.SmartCleanLabel})");

        vm.SelectAllCommand.Execute(null);
        Check(!winsxs.IsSelected && !windowsOld.IsSelected, "Select All does NOT select WinSxS or Windows.old, even in Advanced mode");
        Check(items.Count(i => i.IsSelected) == before, "Select All changes nothing for already-selected routine rows (count unchanged)");
        Check(vm.SmartCleanLabel.Contains($"{before} ITEM") && !vm.SmartCleanLabel.Contains("GB"), $"Label and size exclude high-risk rows ({vm.SmartCleanLabel})");

        // Routine rows deselected by hand are re-selected by Select All; high-risk rows still are not.
        foreach (var t in items.Where(i => i.Type == JunkType.TempFile).Take(5)) t.IsSelected = false;
        vm.SelectAllCommand.Execute(null);
        Check(items.Count(i => i.IsSelected) == before, "Select All restores routine rows but still skips high-risk rows");

        // A deliberate per-row choice survives a later Select All...
        winsxs.IsSelected = true;
        vm.SelectAllCommand.Execute(null);
        Check(winsxs.IsSelected, "A deliberately selected WinSxS row is not silently undone by Select All");
        Check(!windowsOld.IsSelected, "...and Select All still does not select Windows.old");

        // ...and Deselect All clears everything, including the deliberate choice (mixed-state regression).
        vm.DeselectAllCommand.Execute(null);
        Check(items.All(i => !i.IsSelected), "Deselect All clears every row, including a deliberate high-risk selection");
    }

    private static void TestCategoryLevelBulk()
    {
        var items = Scenario();
        var cats = BuildCategories(items, advanced: true);
        var winCat = cats.First(c => c.Name.Contains("WinSxS"));
        var oldCat = cats.First(c => c.Name.Contains("Windows.old"));
        var tempCat = cats.First(c => c.Name == "Temporary Files");

        winCat.IsAllSelected = true;
        oldCat.IsAllSelected = true;
        Check(items.Where(i => i.IsHighRisk).All(i => !i.IsSelected), "Ticking a high-risk group header selects nothing");
        Check(!winCat.IsAllSelected && !oldCat.IsAllSelected, "The header snaps back to unchecked (nothing bulk-selectable)");
        Check(!winCat.HasBulkSelectableItems && !oldCat.HasBulkSelectableItems, "High-risk-only groups report no bulk-selectable rows (header is disabled in the UI)");
        Check(tempCat.HasBulkSelectableItems, "Routine groups remain bulk-selectable");

        foreach (var t in tempCat.Items) t.IsSelected = false;
        tempCat.IsAllSelected = true;
        Check(tempCat.Items.All(i => i.IsSelected), "A routine group header still selects all its rows");

        var mixed = new JunkCategory { Name = "Mixed" };
        var routine = new JunkItem { Type = JunkType.TempFile, Path = @"C:\AuraSynthetic\m.tmp", SizeBytes = 1, IsSelected = false };
        var risky = new JunkItem { Type = JunkType.WinSxS, Path = @"C:\AuraSynthetic\WinSxS2", SizeBytes = 1, IsSelected = false };
        mixed.Items.Add(routine);
        mixed.Items.Add(risky);
        mixed.IsAllSelected = true;
        Check(routine.IsSelected && !risky.IsSelected, "In a mixed group the header selects the routine row only");
        Check(mixed.IsAllSelected, "The mixed group's header reads checked once every bulk-selectable row is selected");
    }

    private static void TestDeliberateSelectionKeepsTotalsAccurate()
    {
        var items = Scenario();
        var cats = BuildCategories(items, advanced: true);
        var vm = new CleanerViewModel { Categories = cats, HasResults = true, IsAdvancedMode = true };
        var winsxs = items.First(i => i.Type == JunkType.WinSxS);
        int before = items.Count(i => i.IsSelected);
        long beforeBytes = items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);

        winsxs.IsSelected = true;
        var expected = $"CLEAN {before + 1} ITEMS · " + AuraClean.Helpers.FormatHelper.FormatBytes(beforeBytes + WinSxSBytes);
        Check(vm.SmartCleanLabel == expected, $"Deliberately selecting WinSxS adds exactly its row to count and size ('{vm.SmartCleanLabel}')");
        Check(winsxs.IsHighRisk && !string.IsNullOrWhiteSpace(winsxs.RiskNote) && winsxs.RiskNote.Contains("DISM"), "The row carries a plain-language risk note for the UI");
        AssertWinSxSWording(winsxs.RiskNote, "inline row note");
        var temp = items.First(i => i.Type == JunkType.TempFile);
        Check(!temp.IsHighRisk && temp.RiskNote == null, "Routine rows have no risk note");
    }

    private static void TestPolicyTable()
    {
        // The warning promises installed updates stay uninstallable, which is only true without /ResetBase.
        Check(!FileCleanerService.WinSxSCleanupArguments.Contains("/ResetBase", StringComparison.OrdinalIgnoreCase),
            "The DISM arguments AuraClean runs for WinSxS cleanup do NOT contain /ResetBase");
        Check(FileCleanerService.WinSxSCleanupArguments.Contains("/StartComponentCleanup"),
            "The WinSxS cleanup runs /StartComponentCleanup, as the warning describes");
        Check(CleanupModePolicy.DescribeHighRisk(JunkType.WinSxS)!.Contains("does not use /ResetBase"),
            "The user-facing WinSxS warning states that /ResetBase is not used (kept consistent with the command)");

        var highRisk = Enum.GetValues<JunkType>().Where(CleanupModePolicy.IsHighRiskType).ToList();
        Check(highRisk.Count == 2 && highRisk.Contains(JunkType.WinSxS) && highRisk.Contains(JunkType.WindowsOld), "Exactly WinSxS and Windows.old are high-risk");
        Check(Enum.GetValues<JunkType>().All(t => CleanupModePolicy.IsBulkSelectable(t) != CleanupModePolicy.IsHighRiskType(t)), "Bulk-selectable is the exact complement of high-risk");
        Check(Enum.GetValues<JunkType>().All(t => (CleanupModePolicy.DescribeHighRisk(t) != null) == CleanupModePolicy.IsHighRiskType(t)), "Only high-risk types have a risk description");
        Check(Enum.GetValues<JunkType>().Where(CleanupModePolicy.IsHighRiskType).All(t => !CleanupModePolicy.IsNormalModeJunkType(t)), "High-risk types are never Normal-mode junk types");
        Check(CleanupModePolicy.BuildHighRiskWarning(new[] { new JunkItem { Type = JunkType.TempFile } }) == null, "No warning is built for routine items");
        var warning = CleanupModePolicy.BuildHighRiskWarning(new[]
        {
            new JunkItem { Type = JunkType.WinSxS, SizeBytes = 1000 },
            new JunkItem { Type = JunkType.WindowsOld, SizeBytes = 1000 }
        });
        Check(warning != null && warning.Contains("Component Store") && warning.Contains("Windows.old") && warning.Contains("cannot be undone"), "The warning names each category and says it cannot be undone");
    }

    /// <summary>
    /// The WinSxS text must describe the command AuraClean actually runs (DISM /StartComponentCleanup
    /// WITHOUT /ResetBase): previous component versions are deleted immediately (no 30-day grace),
    /// but installed updates can still be uninstalled.
    /// </summary>
    private static void AssertWinSxSWording(string text, string where)
    {
        Check(text != null && text.Contains("/StartComponentCleanup"), $"WinSxS wording ({where}) names the command that is actually run");
        Check(text != null && text.Contains("immediately deletes previous versions of updated components") && text.Contains("30-day grace period"),
            $"WinSxS wording ({where}) describes immediate removal of previous component versions");
        Check(text != null && text.Contains("does not use /ResetBase") && text.Contains("installed updates can still be uninstalled"),
            $"WinSxS wording ({where}) says installed updates can still be uninstalled (no /ResetBase)");
        Check(text != null && !text.Contains("can no longer be rolled back to those older component versions") && !text.Contains("Updates installed before the cleanup"),
            $"WinSxS wording ({where}) no longer overclaims loss of all earlier update rollback");
    }

    private sealed class ConfirmProbe
    {
        public int Calls;
        public string Message = "";
        public string Title = "";
        public bool AlwaysAsk;

        /// <summary>Always declines, so a regression can never run a real cleanup from these tests.</summary>
        public bool Confirm(string message, string title, bool alwaysAsk)
        {
            Calls++;
            Message = message;
            Title = title;
            AlwaysAsk = alwaysAsk;
            return false;
        }
    }

    private static (CleanerViewModel Vm, List<JunkItem> Items, ConfirmProbe Probe) ConfirmFixture(bool advanced = true)
    {
        var items = Scenario();
        var cats = BuildCategories(items, advanced: true);
        var probe = new ConfirmProbe();
        var vm = new CleanerViewModel { Categories = cats, HasResults = true, IsAdvancedMode = advanced, ConfirmAction = probe.Confirm };
        return (vm, items, probe);
    }

    private static void TestHighRiskCleanupRequiresConfirmation()
    {
        // Confirmation dialogs are DISABLED in settings: high-risk cleanup must still always ask.
        var quiet = new AppSettings { ShowConfirmationDialogs = false };
        WithSettings(quiet, () =>
        {
            var (vm, items, probe) = ConfirmFixture();
            var winsxs = items.First(i => i.Type == JunkType.WinSxS);
            var windowsOld = items.First(i => i.Type == JunkType.WindowsOld);
            winsxs.IsSelected = true;
            windowsOld.IsSelected = true;
            int rowsBefore = vm.Categories.Sum(c => c.Items.Count);

            vm.CleanSelectedCommand.ExecuteAsync(null).GetAwaiter().GetResult();

            Check(probe.Calls == 1, "Cleaning with high-risk rows selected asks for confirmation exactly once");
            Check(probe.AlwaysAsk, "...and requests an unconditional prompt (the 'show dialogs' setting cannot waive it)");
            Check(probe.Message.Contains("Component Store (WinSxS)") && probe.Message.Contains("Windows.old"), "The prompt identifies both high-risk categories");
            Check(probe.Message.Contains("DISM") && probe.Message.Contains("previous version of Windows") && probe.Message.Contains("cannot be undone"), "The prompt explains the consequences, including loss of rollback");
            AssertWinSxSWording(probe.Message, "mandatory confirmation");
            Check(probe.Title == "Confirm advanced system cleanup", "The prompt is titled as advanced system cleanup");
            Check(vm.StatusMessage == "Cleanup cancelled.", "Declining cancels the cleanup");
            Check(vm.Categories.Sum(c => c.Items.Count) == rowsBefore && winsxs.IsSelected && windowsOld.IsSelected, "Declining changes nothing: rows and selection are untouched");

            // Stale state: the warning is rebuilt from the live selection on every run.
            winsxs.IsSelected = false;
            windowsOld.IsSelected = false;
            vm.CleanSelectedCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(probe.Calls == 2 && !probe.Message.Contains("advanced system maintenance"), "After deselecting them, the next run carries no high-risk warning (no stale state)");
        });

        WithSettings(quiet, () =>
        {
            var (vm, items, probe) = ConfirmFixture(advanced: false);
            items.First(i => i.Type == JunkType.WinSxS).IsSelected = true;
            vm.CleanSelectedCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(probe.Calls == 1 && !probe.Message.Contains("Component Store"), "In Normal mode a selected WinSxS row is never part of the cleanup or its prompt");
        });

        WithSettings(new AppSettings { CleanerExcludedPaths = [@"C:\AuraSynthetic\Windows.old"] }, () =>
        {
            var (vm, items, probe) = ConfirmFixture();
            items.First(i => i.Type == JunkType.WinSxS).IsSelected = true;
            items.First(i => i.Type == JunkType.WindowsOld).IsSelected = true;
            vm.CleanSelectedCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(probe.Message.Contains("Component Store") && !probe.Message.Contains("previous version of Windows"),
                "An excluded high-risk path is dropped from the cleanup and its warning");
        });
    }

    private static void TestQuickCleanStaysLowRisk()
    {
        WithSettings(new AppSettings { ShowConfirmationDialogs = false }, () =>
        {
            var (vm, items, probe) = ConfirmFixture();
            items.First(i => i.Type == JunkType.WinSxS).IsSelected = true;
            items.First(i => i.Type == JunkType.WindowsOld).IsSelected = true;

            vm.CleanLowRiskAsync().GetAwaiter().GetResult();

            Check(probe.Calls == 1 && probe.AlwaysAsk, "Quick Clean always asks, even with dialogs disabled");
            Check(!probe.Message.Contains("Component Store") && !probe.Message.Contains("Windows.old") && !probe.Message.Contains("advanced system"),
                "Quick Clean never includes WinSxS or Windows.old, even if they were selected");
            Check(probe.Title == "Confirm destructive action", "Quick Clean uses the routine prompt, not the advanced-system prompt");
        });
    }

    private static void TestCountsReflectSelection()
    {
        var items = Scenario();
        var cats = BuildCategories(items, advanced: true);
        var vm = new CleanerViewModel { Categories = cats, HasResults = true, IsAdvancedMode = true };
        var excluded = items.First(i => i.Path == @"C:\Temp\f0.tmp");

        void AssertLabel(string state)
        {
            var selected = items.Where(i => i.IsSelected).ToList();
            var expected = $"CLEAN {selected.Count} ITEM{(selected.Count == 1 ? "" : "S")} · " +
                           AuraClean.Helpers.FormatHelper.FormatBytes(selected.Sum(i => i.SizeBytes));
            Check(vm.SmartCleanLabel == expected, $"{state}: label '{vm.SmartCleanLabel}' equals real selection '{expected}'");
        }

        Check(!excluded.IsSelected, "An excluded path is deselected by the analysis step (production exclusion handling intact)");
        AssertLabel("After analysis (41 low-risk rows, 1 excluded)");

        vm.SelectAllCommand.Execute(null);
        Check(!excluded.IsSelected, "Select All still skips the excluded path");
        AssertLabel("After Select All");

        var one = items.First(i => i.Type == JunkType.CrashDump);
        one.IsSelected = false;
        AssertLabel("After deselecting one row");

        vm.DeselectAllCommand.Execute(null);
        Check(vm.SmartCleanLabel == "Select items to clean", "With nothing selected the label asks for a selection");

        one.IsSelected = true;
        AssertLabel("Single selected row uses the singular form");
    }

    // ── Real view in an off-screen window (needs an interactive desktop; skipped otherwise) ──
    private static void TestViewKeepsWinSxSUnselected()
    {
        Exception failure = null;
        string skipReason = null;
        var thread = new Thread(() =>
        {
            try { skipReason = RunViewScenario(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            Check(false, $"View fixture timed out at step '{_step}'");
            return;
        }

        if (failure != null)
            throw failure;
        if (skipReason != null)
            Console.WriteLine($"  SKIP: {skipReason}");
    }

    /// <summary>Synchronously re-runs layout so virtualized containers are realized/recycled.</summary>
    private static void Pump(Dispatcher d)
    {
        d.Invoke(() => _window?.UpdateLayout(), DispatcherPriority.Send);
    }

    private static Window _window;

    private static string RunViewScenario()
    {
        if (!Environment.UserInteractive)
            return "no interactive desktop available";

        if (Application.Current == null)
        {
            // Load App.xaml's resource dictionaries (the generated Main normally does this).
            var app = new AuraClean.App();
            app.InitializeComponent();
        }
        var items = Scenario(tempCount: 400);
        var cats = BuildCategories(items, advanced: true);
        var vm = new CleanerViewModel { Categories = cats, HasResults = true, TotalJunkCount = items.Count };
        var winsxs = items.First(i => i.Type == JunkType.WinSxS);
        var windowsOld = items.First(i => i.Type == JunkType.WindowsOld);

        _step = "create view";
        var view = new CleanerView { DataContext = vm };
        var window = new Window
        {
            Content = view, Width = 1100, Height = 700,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            ShowInTaskbar = false, ShowActivated = false
        };

        _window = window;
        try
        {
            _step = "show";
            window.Show();
            _step = "show returned; pumping";
            var d = window.Dispatcher;
            Pump(d);
            _step = "first pump done";
            string labelBefore = vm.SmartCleanLabel;
            Check(!winsxs.IsSelected, "After first layout WinSxS is still unselected");

            _step = "scroll";
            foreach (var sv in Descendants<ScrollViewer>(view).ToList())
            {
                sv.ScrollToVerticalOffset(sv.ScrollableHeight);
                Pump(d);
                sv.ScrollToVerticalOffset(0);
                Pump(d);
            }
            _step = "expand/collapse";
            foreach (var ex in Descendants<Expander>(view).ToList())
            {
                ex.IsExpanded = false; Pump(d);
                ex.IsExpanded = true; Pump(d);
            }
            _step = "refresh";
            vm.Categories = new ObservableCollection<JunkCategory>(cats);
            Pump(d);
            view.DataContext = null; Pump(d);
            view.DataContext = vm; Pump(d);

            Check(!winsxs.IsSelected, "After scroll, collapse/expand, refresh and DataContext re-attach WinSxS is unselected");
            Check(!windowsOld.IsSelected, "Windows.old is also unchanged");

            // UI Automation support (Priority 3): the WPF peers exist and carry the accessible names,
            // so an empty tree from an outside client is not caused by missing automation support.
            var names = new List<string>();
            CollectPeerNames(UIElementAutomationPeer.CreatePeerForElement(view), names, 0);
            Check(names.Count > 20, $"CleanerView exposes a populated automation peer tree ({names.Count} named peers)");
            Check(names.Contains("Select all in Component Store (WinSxS)"), "The WinSxS group checkbox has its own accessible name");
            Check(names.Any(n => n.StartsWith("Select Temp ")), "Item checkboxes expose a per-row accessible name ('Select <description>'), not one identical name for every row");
            Check(vm.SmartCleanLabel == labelBefore, $"The Clean button label did not change ('{labelBefore}' -> '{vm.SmartCleanLabel}')");
        }
        finally
        {
            window.Close();
        }
        return null;
    }

    private static void CollectPeerNames(AutomationPeer peer, List<string> names, int depth)
    {
        if (peer == null || depth > 40) return;
        var name = peer.GetName();
        if (!string.IsNullOrEmpty(name)) names.Add(name);
        foreach (var child in peer.GetChildren() ?? new List<AutomationPeer>())
            CollectPeerNames(child, names, depth + 1);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var g in Descendants<T>(child)) yield return g;
        }
    }
}
