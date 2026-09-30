using System;
using System.Collections.Generic;
using System.Linq;
using AuraClean.Models;
using AuraClean.Services;
using AuraClean.ViewModels;

namespace TestFeatures;

/// <summary>
/// Batch U feature tests: U1 sortable columns + Drive column, U2 deep leftover scan
/// (search-term builder, value-match logic, sort semantics). Pure logic only —
/// no disk, no registry, no UI. Returns the failure count for Program.cs wiring.
/// </summary>
public static class UninstallerBatchTests
{
    private static int _pass;
    private static int _fail;

    public static int Run()
    {
        _pass = 0;
        _fail = 0;

        Section("DriveLetter parsing", TestDriveLetter);
        Section("InstallDateParsed parsing", TestInstallDateParsed);
        Section("Sort semantics", TestSortSemantics);
        Section("Search-term builder", TestSearchTerms);
        Section("Startup/value matching", TestValueMatching);

        Console.WriteLine($"  Batch U: {_pass} passed, {_fail} failed");
        return _fail;
    }

    private static void Section(string name, Action test)
    {
        Console.WriteLine($"═══ BATCH U: {name} ═══");
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

    private static void TestDriveLetter()
    {
        Check(new InstalledProgram { InstallLocation = @"C:\Program Files\App" }.DriveLetter == "C:",
            "InstallLocation root yields C:");
        Check(new InstalledProgram { InstallLocation = @"d:\apps\tool" }.DriveLetter == "D:",
            "Drive letter is uppercased");
        Check(new InstalledProgram { InstallLocation = "", DisplayIcon = @"""E:\App\app.exe"",0" }.DriveLetter == "E:",
            "DisplayIcon with quoted path + icon index falls back to E:");
        Check(new InstalledProgram { InstallLocation = "", DisplayIcon = @"F:\App\app.exe,0" }.DriveLetter == "F:",
            "DisplayIcon with unquoted icon index falls back to F:");
        Check(new InstalledProgram().DriveLetter == string.Empty,
            "Empty paths yield empty drive");
        Check(new InstalledProgram { InstallLocation = "relative\\path" }.DriveLetter == string.Empty,
            "Relative path yields empty drive");
        Check(new InstalledProgram { InstallLocation = @"\\server\share\app" }.DriveLetter == string.Empty,
            "UNC path yields empty drive (no drive letter)");
    }

    private static void TestInstallDateParsed()
    {
        Check(new InstalledProgram { InstallDate = "20240115" }.InstallDateParsed == new DateTime(2024, 1, 15),
            "yyyyMMdd parses");
        Check(new InstalledProgram { InstallDate = "2024-01-15" }.InstallDateParsed == new DateTime(2024, 1, 15),
            "yyyy-MM-dd parses");
        Check(new InstalledProgram { InstallDate = "01/15/2024" }.InstallDateParsed == new DateTime(2024, 1, 15),
            "MM/dd/yyyy parses");
        Check(new InstalledProgram { InstallDate = "not a date" }.InstallDateParsed == null,
            "Garbage date yields null");
        Check(new InstalledProgram { InstallDate = "" }.InstallDateParsed == null,
            "Empty date yields null");
    }

    private static UninstallerViewModel SortVm(UninstallerSortColumn column, bool ascending)
    {
        var vm = new UninstallerViewModel();
        // ApplySort reads these; use reflection-free path via Sort command state.
        // The VM exposes SortColumn/SortAscending as observable properties.
        vm.SortColumn = column;
        vm.SortAscending = ascending;
        return vm;
    }

    private static void TestSortSemantics()
    {
        var programs = new List<InstalledProgram>
        {
            new() { DisplayName = "Bravo", Publisher = "Zeta", EstimatedSizeKB = 100, InstallDate = "20240101", InstallLocation = @"D:\Bravo" },
            new() { DisplayName = "Alpha", Publisher = "Alpha", EstimatedSizeKB = 0, InstallDate = "garbage", InstallLocation = @"C:\Alpha" },
            new() { DisplayName = "Charlie", Publisher = "Mid", EstimatedSizeKB = 50, InstallDate = "20230601", InstallLocation = @"C:\Charlie" },
        };

        // Default: Name ascending.
        var vm = new UninstallerViewModel();
        Check(vm.SortColumn == UninstallerSortColumn.Name && vm.SortAscending,
            "Default sort is Name ascending");
        var names = vm.ApplySort(programs).Select(p => p.DisplayName).ToList();
        Check(names.SequenceEqual(["Alpha", "Bravo", "Charlie"]),
            $"Name ascending orders Alpha,Bravo,Charlie (got {string.Join(",", names)})");

        // Size ascending: unknown (0) LAST.
        var sizeAsc = SortVm(UninstallerSortColumn.Size, ascending: true).ApplySort(programs).ToList();
        Check(sizeAsc[^1].DisplayName == "Alpha",
            $"Unknown size sorts last ascending (got {string.Join(",", sizeAsc.Select(p => p.DisplayName))})");
        Check(sizeAsc[0].DisplayName == "Charlie" && sizeAsc[1].DisplayName == "Bravo",
            "Known sizes order 50KB before 100KB ascending");

        // Size descending: unknown STILL last.
        var sizeDesc = SortVm(UninstallerSortColumn.Size, ascending: false).ApplySort(programs).ToList();
        Check(sizeDesc[^1].DisplayName == "Alpha",
            $"Unknown size sorts last descending (got {string.Join(",", sizeDesc.Select(p => p.DisplayName))})");
        Check(sizeDesc[0].DisplayName == "Bravo" && sizeDesc[1].DisplayName == "Charlie",
            "Known sizes order 100KB before 50KB descending");

        // Date ascending: bad dates LAST.
        var dateAsc = SortVm(UninstallerSortColumn.Installed, ascending: true).ApplySort(programs).ToList();
        Check(dateAsc[^1].DisplayName == "Alpha",
            $"Bad date sorts last ascending (got {string.Join(",", dateAsc.Select(p => p.DisplayName))})");
        Check(dateAsc[0].DisplayName == "Charlie" && dateAsc[1].DisplayName == "Bravo",
            "Dates order 2023 before 2024 ascending");

        // Date descending: bad dates STILL last.
        var dateDesc = SortVm(UninstallerSortColumn.Installed, ascending: false).ApplySort(programs).ToList();
        Check(dateDesc[^1].DisplayName == "Alpha",
            $"Bad date sorts last descending (got {string.Join(",", dateDesc.Select(p => p.DisplayName))})");

        // Drive ascending groups C before D.
        var driveAsc = SortVm(UninstallerSortColumn.Drive, ascending: true).ApplySort(programs).ToList();
        Check(driveAsc[0].DriveLetter == "C:" && driveAsc[^1].DriveLetter == "D:",
            $"Drive ascending groups C before D (got {string.Join(",", driveAsc.Select(p => p.DriveLetter))})");

        // Publisher descending reverses.
        var pubDesc = SortVm(UninstallerSortColumn.Publisher, ascending: false).ApplySort(programs).ToList();
        Check(pubDesc[0].DisplayName == "Bravo" && pubDesc[^1].DisplayName == "Alpha",
            "Publisher descending orders Zeta first, Alpha last");
    }

    private static void TestSearchTerms()
    {
        var terms = UninstallerService.BuildRemnantDirectorySearchTerms("Google Chrome", "Google LLC");
        Check(terms.Contains("googlechrome"), "Full product name is searchable");
        Check(terms.Contains("chrome"), "Product-specific word is searchable");
        Check(!terms.Contains("google"), "Publisher-only vendor term is not searchable");
        Check(UninstallerService.MatchesAnySearchTerm("Google Chrome", terms),
            "Full product folder matches via verbatim hit");
        Check(UninstallerService.MatchesAnySearchTerm("Chrome Updater", terms),
            "Name containing a ≥4-char term segment matches");
        Check(!UninstallerService.MatchesAnySearchTerm("Mozilla Firefox", terms),
            "Unrelated name does not match");
        Check(!UninstallerService.MatchesAnySearchTerm("", terms),
            "Empty name does not match");
    }

    private static void TestValueMatching()
    {
        var terms = UninstallerService.BuildRemnantDirectorySearchTerms("Acme Photo Editor", "Acme Inc");

        Check(RegistryScannerService.IsTraceValueMatch(
                @"C:\Program Files\Acme Photo Editor\editor.exe", terms,
                installPathRequired: false, installDir: null),
            "Value data containing the product path matches");
        Check(RegistryScannerService.IsTraceValueMatch(
                "AcmePhotoEditorStartup", terms,
                installPathRequired: false, installDir: null),
            "Value name containing a ≥4-char term segment matches");
        Check(!RegistryScannerService.IsTraceValueMatch(
                "Windows Defender", terms,
                installPathRequired: false, installDir: null),
            "Unrelated value does not match");

        // Services rule: name match alone is NOT enough without the install path.
        Check(!RegistryScannerService.IsTraceValueMatch(
                @"C:\Windows\System32\acmephotoeditor.exe", terms,
                installPathRequired: true, installDir: @"C:\Program Files\Acme"),
            "Services hit without install-path substring is rejected");
        Check(RegistryScannerService.IsTraceValueMatch(
                @"""C:\Program Files\Acme Photo Editor\svc.exe"" -run", terms,
                installPathRequired: true, installDir: @"C:\Program Files\Acme Photo Editor"),
            "Services hit with install-path substring is accepted");

        // Protected shared keys stay protected.
        Check(RegistryScannerService.IsProtectedKey(@"Software\Microsoft\Windows\CurrentVersion\Run"),
            "Run key itself stays protected from deletion");
        Check(RegistryScannerService.IsProtectedKey(@"Software\Microsoft"),
            "Shallow shared branches stay protected from deletion");

        // P1: value-level hits name the value, never the shared parent.
        var (vHive, vView, vSub, vName) = RegistryScannerService.ParseKeyPath(
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run:value=AcmeUpdater");
        Check(vHive == Microsoft.Win32.RegistryHive.CurrentUser && vName == "AcmeUpdater" &&
              vSub == @"Software\Microsoft\Windows\CurrentVersion\Run",
            "Value-suffixed path parses to parent key + value name");
        Check(RegistryScannerService.IsProtectedKey(vSub!),
            "Value hit's parent stays protected (key delete still refused)");

        // P1: shared-word substring must NOT qualify an Uninstall entry on its own.
        // "Visual Studio Code" terms matching an installed "Visual Studio" edition
        // must not offer that edition's key for deletion.
        Check(!UninstallerService.BuildRemnantDirectorySearchTerms("Visual Studio Code", "Microsoft")
                .SetEquals(UninstallerService.BuildRemnantDirectorySearchTerms("Visual Studio", "Microsoft")),
            "VS Code vs VS terms recorded (sanity: distinct products)");

        // Round 3 P1: boundary-aware install-dir check — "Acme Tools" is NOT under "Acme".
        Check(RegistryScannerService.IsSameOrUnderDirectory(
                @"C:\Program Files\Acme\bin\app.exe", @"C:\Program Files\Acme"),
            "Same-dir child path qualifies");
        Check(RegistryScannerService.IsSameOrUnderDirectory(
                @"C:\Program Files\Acme", @"C:\Program Files\Acme"),
            "Same directory qualifies");
        Check(!RegistryScannerService.IsSameOrUnderDirectory(
                @"C:\Program Files\Acme Tools\bin\app.exe", @"C:\Program Files\Acme"),
            "Name-prefix sibling directory does NOT qualify");
        Check(RegistryScannerService.IsSameOrUnderDirectory(
                "\"C:\\Program Files\\Acme\\svc.exe\" -run", @"C:\Program Files\Acme"),
            "Quoted path with args still qualifies");
        Check(!RegistryScannerService.IsSameOrUnderDirectory(
                "\"C:\\Program Files\\Acme Tools\\svc.exe\" -run", @"C:\Program Files\Acme"),
            "Quoted sibling path with args does NOT qualify");

        // Round 3 P1: 32-bit view survives the backup-path round-trip.
        var (bHive, bView, bSub, bVal) = RegistryScannerService.ParseKeyPath(
            @"HKLM (32-bit)\Software\Microsoft\Windows\CurrentVersion\Run:value=AcmeUpdater");
        Check(bHive == Microsoft.Win32.RegistryHive.LocalMachine &&
              bView == Microsoft.Win32.RegistryView.Registry32 &&
              bVal == "AcmeUpdater",
            "32-bit value path parses with view + value name intact");
    }
}
