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
        Section("Select All is the explicit path that selects WinSxS, and counts stay accurate", TestSelectAllIsExplicitAndAccurate);
        Section("Selection counts and sizes always equal the real selection (with an exclusion in force)",
            () => WithSettings(new AppSettings { CleanerExcludedPaths = [@"C:\Temp\f0.tmp"] }, TestCountsReflectSelection));
        Section("A low-risk-only run never targets a selected WinSxS item", TestLowRiskFilter);
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
            new() { Type = JunkType.WinSxS, Path = @"C:\WINDOWS\WinSxS", Description = "Component Store", SizeBytes = WinSxSBytes },
            new() { Type = JunkType.WindowsOld, Path = @"C:\Windows.old", Description = "Windows.old", SizeBytes = WindowsOldBytes },
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

    private static void TestSelectAllIsExplicitAndAccurate()
    {
        var items = Scenario();
        var cats = BuildCategories(items, advanced: true);
        var vm = new CleanerViewModel { Categories = cats, HasResults = true, TotalJunkCount = items.Count };

        int before = items.Count(i => i.IsSelected);
        long beforeBytes = items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
        Check(vm.SmartCleanLabel.Contains($"{before} ITEM"), $"Label count matches the real selection before Select All ({vm.SmartCleanLabel})");

        vm.SelectAllCommand.Execute(null);

        int after = items.Count(i => i.IsSelected);
        long afterBytes = items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
        Check(items.First(i => i.Type == JunkType.WinSxS).IsSelected, "Explicit Select All does select WinSxS (designed behavior)");
        Check(after == before + 2 && afterBytes - beforeBytes == WinSxSBytes + WindowsOldBytes,
            "Select All adds exactly the unselected review-only rows (WinSxS + Windows.old) to count and size");
        Check(vm.SmartCleanLabel.Contains($"{after} ITEM"), $"Label follows the real selection after Select All ({vm.SmartCleanLabel})");

        vm.DeselectAllCommand.Execute(null);
        Check(items.All(i => !i.IsSelected), "Deselect All clears everything");
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

    private static void TestLowRiskFilter()
    {
        var items = Scenario();
        var cats = BuildCategories(items, advanced: true);
        var vm = new CleanerViewModel { Categories = cats, HasResults = true };
        vm.SelectAllCommand.Execute(null);

        var targeted = cats.SelectMany(c => c.Items)
            .Where(i => i.IsSelected)
            .Where(i => CleanupModePolicy.IsNormalModeJunkType(i.Type))
            .ToList();
        Check(targeted.Count > 0 && targeted.All(i => i.Type is not (JunkType.WinSxS or JunkType.WindowsOld)),
            "Even with everything selected, the low-risk filter excludes WinSxS and Windows.old");
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
            Check(names.Contains("Select Cleanup Item"), "Item checkboxes expose an accessible name");
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
