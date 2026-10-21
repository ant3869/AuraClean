using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using AuraClean.Views;

namespace TestFeatures;

/// <summary>
/// Sidebar navigation state across sections. The radios live under different section panels; without one
/// explicit group an old selection stayed checked, so re-selecting it through UI Automation raised no
/// Checked event and the previous page stayed on screen.
/// The window is constructed but never shown, so startup maintenance (leftover recovery, quarantine purge)
/// never runs; nothing is scanned or deleted.
/// </summary>
public static class SidebarNavigationTests
{
    private static int _pass;
    private static int _fail;
    private static volatile string _step = "start";

    private static readonly (string Key, string Radio)[] Pages =
    [
        ("Dashboard", "NavDashboard"),
        ("Cleaner", "NavCleaner"),
        ("StorageMap", "NavStorage"),
        ("Shredder", "NavShredder"),
        ("SystemInfo", "NavSystemInfo"),
        ("Settings", "NavSettings"),
    ];

    public static (int Pass, int Fail) Run()
    {
        _pass = 0;
        _fail = 0;

        Console.WriteLine("═══ SIDEBAR: navigation is mutually exclusive across sections (mouse/keyboard command + UI Automation) ═══");
        var skip = WpfTestHost.EnsureStarted();
        if (skip != null)
            Console.WriteLine($"  SKIP: {skip}");
        else
        {
            try { WpfTestHost.Invoke(() => RunScenario(), TimeSpan.FromSeconds(90)); }
            catch (TimeoutException) { Check(false, $"Sidebar fixture timed out at step '{_step}'"); }
            catch (Exception ex) { Check(false, $"Unexpected exception: {ex.GetType().Name}: {ex.Message} | {ex.InnerException?.Message}"); }
        }
        Console.WriteLine();

        Console.WriteLine($"  Sidebar navigation: {_pass} passed, {_fail} failed");
        return (_pass, _fail);
    }

    private static void Check(bool condition, string message)
    {
        Console.ForegroundColor = condition ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}: {message}");
        Console.ResetColor();
        if (condition) _pass++; else _fail++;
    }

    private static string CurrentView(MainWindow window) =>
        ((AuraClean.ViewModels.MainViewModel)window.DataContext).CurrentViewName;

    private static IEnumerable<RadioButton> AllNavRadios(MainWindow window) =>
        Pages.Select(p => (RadioButton)window.FindName(p.Radio));

    private static int CheckedCount(MainWindow window) => AllNavRadios(window).Count(r => r.IsChecked == true);

    /// <summary>What a mouse click or Space key does: runs the radio's bound navigation command.</summary>
    private static void ClickOrKeyboard(RadioButton radio) => radio.Command.Execute(radio.CommandParameter);

    /// <summary>What Narrator / voice control does: UI Automation SelectionItem.Select().</summary>
    private static void SelectViaUia(RadioButton radio)
    {
        var peer = new RadioButtonAutomationPeer(radio);
        ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).Select();
    }

    private static void RunScenario()
    {
        _step = "window";
        var window = new MainWindow();
        try
        {
            var radios = Pages.ToDictionary(p => p.Key, p => (RadioButton)window.FindName(p.Radio));
            Check(radios.Values.All(r => r != null), "All sampled sidebar radios exist");

            _step = "mouse/keyboard path across sections";
            ClickOrKeyboard(radios["Cleaner"]);
            Check(CurrentView(window) == "Cleaner" && radios["Cleaner"].IsChecked == true, "Command navigation (mouse/Space) opens the Cleaner");
            ClickOrKeyboard(radios["StorageMap"]);
            Check(CurrentView(window) == "StorageMap" && radios["StorageMap"].IsChecked == true, "...then Storage Map in a different section");
            Check(radios["Cleaner"].IsChecked != true, "The Cleaner radio is unchecked once Storage Map is open (no stale selection)");
            Check(CheckedCount(window) == 1, "Exactly one sidebar item is checked at any time");

            _step = "UIA return to a previously visited page";
            SelectViaUia(radios["Cleaner"]);
            Check(CurrentView(window) == "Cleaner", "UI Automation Select on the previously visited Cleaner navigates back to it");
            Check(CheckedCount(window) == 1 && radios["Cleaner"].IsChecked == true, "...and the selection styling follows (one checked item)");

            _step = "UIA across every section";
            foreach (var (key, name) in Pages)
            {
                SelectViaUia(radios[key]);
                bool ok = CurrentView(window) == key && CheckedCount(window) == 1 && radios[key].IsChecked == true;
                Check(ok, $"UI Automation Select on '{name}' shows '{key}' with a single checked item");
            }

            _step = "round trip mixing input methods";
            string[] route = ["Settings", "Cleaner", "SystemInfo", "StorageMap", "Dashboard", "Shredder", "Cleaner", "Settings"];
            bool routeOk = true;
            for (int i = 0; i < route.Length; i++)
            {
                var radio = radios[route[i]];
                if (i % 2 == 0) ClickOrKeyboard(radio); else SelectViaUia(radio);
                routeOk &= CurrentView(window) == route[i] && CheckedCount(window) == 1 && radio.IsChecked == true;
            }
            Check(routeOk, "Alternating mouse/keyboard and UI Automation across sections and back always shows the right page");

            _step = "re-select current";
            SelectViaUia(radios["Settings"]);
            Check(CurrentView(window) == "Settings" && CheckedCount(window) == 1, "Selecting the already-open page changes nothing");
        }
        finally
        {
            // Dispose the tray icon and close without the minimize-to-tray interception.
            typeof(MainWindow).GetMethod("DisposeTrayIcon", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(window, null);
            typeof(MainWindow).GetField("_forceClose", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(window, true);
            window.Close();
        }
    }
}
