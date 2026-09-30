using System;
using System.Collections.Generic;
using System.Linq;
using AuraClean.Models;
using AuraClean.Services;

namespace TestFeatures;

/// <summary>
/// Batch D feature tests: exclusion matching (D1), scheduled-category validation (D2),
/// and history-trend bucketing (D3). Pure logic only — no disk, no UI.
/// Returns the failure count so the lead can wire it into Program.cs.
/// </summary>
public static class FeatureBatchDTests
{
    private static int _pass;
    private static int _fail;

    public static int Run()
    {
        _pass = 0;
        _fail = 0;

        Section("Cleaner exclusion matching", TestIsExcluded);
        Section("Scheduled category validation", TestScheduledCategories);
        Section("History daily trend", TestDailyTrend);
        Section("History CSV export", TestHistoryCsv);

        Console.WriteLine($"  Batch D: {_pass} passed, {_fail} failed");
        return _fail;
    }

    private static void Section(string name, Action test)
    {
        Console.WriteLine($"═══ BATCH D: {name} ═══");
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

    private static void TestIsExcluded()
    {
        var exclusions = new List<string>
        {
            @"C:\Users\me\Documents\keep.docx",
            @"D:\Projects\Secret"
        };

        Check(CleanerExcludeStore.IsExcluded(@"C:\Users\me\Documents\keep.docx", exclusions),
            "Exact file path matches");
        Check(CleanerExcludeStore.IsExcluded(@"c:\users\ME\documents\KEEP.docx", exclusions),
            "Exact file match is case-insensitive");
        Check(CleanerExcludeStore.IsExcluded(@"D:\Projects\Secret\sub\file.tmp", exclusions),
            "Folder entry covers its subtree");
        Check(CleanerExcludeStore.IsExcluded(@"d:\projects\secret", exclusions),
            "Folder entry matches the folder itself, case-insensitively");
        Check(CleanerExcludeStore.IsExcluded(@"D:/Projects/Secret/nested/deep.log", exclusions),
            "Forward slashes are normalized");
        Check(CleanerExcludeStore.IsExcluded(@"D:\Projects\Secret\", exclusions),
            "Trailing separator is tolerated");

        Check(!CleanerExcludeStore.IsExcluded(@"D:\Projects\Secret2\file.tmp", exclusions),
            "Sibling with a shared prefix is NOT excluded");
        Check(!CleanerExcludeStore.IsExcluded(@"D:\Projects\Other\file.tmp", exclusions),
            "Unrelated path is not excluded");
        Check(!CleanerExcludeStore.IsExcluded(@"C:\Users\me\Documents\other.docx", exclusions),
            "Sibling file of an excluded file is not excluded");

        Check(!CleanerExcludeStore.IsExcluded(null, exclusions), "Null path is not excluded");
        Check(!CleanerExcludeStore.IsExcluded("   ", exclusions), "Blank path is not excluded");
        Check(!CleanerExcludeStore.IsExcluded(@"D:\Projects\Secret\a.txt", null),
            "Null exclusion list excludes nothing");
        Check(!CleanerExcludeStore.IsExcluded(@"D:\Projects\Secret\a.txt", new List<string>()),
            "Empty exclusion list excludes nothing");
        Check(!CleanerExcludeStore.IsExcluded(@"D:\Projects\Secret\a.txt", new List<string> { "  ", "" }),
            "Blank entries are ignored");

        // Root handling: a drive root excludes the drive but must not crash normalization.
        Check(CleanerExcludeStore.IsExcluded(@"C:\Windows\Temp\a.tmp", new List<string> { @"C:\" }),
            "Drive-root entry covers the drive");
        Check(!CleanerExcludeStore.IsExcluded(@"D:\file.tmp", new List<string> { @"C:\" }),
            "Drive-root entry does not cover other drives");
    }

    private static void TestScheduledCategories()
    {
        var defaults = CleanupModePolicy.GetNormalModeDefaults();
        Check(defaults.Count > 0, $"Normal defaults are non-empty ({defaults.Count} categories)");
        Check(defaults.All(CleanupModePolicy.IsNormalModeJunkType),
            "Every Normal default is a Normal-mode junk type");
        Check(!defaults.Contains(JunkType.RecycleBin) && !defaults.Contains(JunkType.WinSxS),
            "Review-only types are not in the Normal defaults");

        var empty = CleanupModePolicy.ResolveScheduledCategories(null);
        Check(empty.SetEquals(defaults), "Null config falls back to Normal defaults");

        var emptyList = CleanupModePolicy.ResolveScheduledCategories(new List<string>());
        Check(emptyList.SetEquals(defaults), "Empty config falls back to Normal defaults");

        var blanks = CleanupModePolicy.ResolveScheduledCategories(new List<string> { " ", "" });
        Check(blanks.SetEquals(defaults), "Blank-only config falls back to Normal defaults");

        var chosen = CleanupModePolicy.ResolveScheduledCategories(
            new List<string> { "TempFile", "prefetch" });
        Check(chosen.SetEquals(new[] { JunkType.TempFile, JunkType.Prefetch }),
            "Valid names resolve case-insensitively");

        var mixed = CleanupModePolicy.ResolveScheduledCategories(
            new List<string> { "TempFile", "NoSuchType", "  ", "RecycleBin", "WinSxS" });
        Check(mixed.SetEquals(new[] { JunkType.TempFile }),
            "Unknown and review-only names are dropped, valid ones kept");

        var reviewOnly = CleanupModePolicy.ResolveScheduledCategories(
            new List<string> { "RecycleBin", "WindowsOld" });
        Check(reviewOnly.SetEquals(defaults),
            "All-review-only config falls back to Normal defaults");

        var garbage = CleanupModePolicy.ResolveScheduledCategories(
            new List<string> { "???", "123" });
        Check(garbage.SetEquals(defaults),
            "All-invalid config falls back to Normal defaults");

        var dupes = CleanupModePolicy.ResolveScheduledCategories(
            new List<string> { "LogFile", "logfile", " LOGFILE " });
        Check(dupes.SetEquals(new[] { JunkType.LogFile }),
            "Duplicate names collapse with trimming");
    }

    private static void TestDailyTrend()
    {
        var today = DateTime.Today.AddHours(12);

        var records = new List<CleanupRecord>
        {
            new() { Timestamp = today.AddHours(-1), ItemCount = 5, BytesFreed = 1000 },
            new() { Timestamp = today.AddHours(-2), ItemCount = 3, BytesFreed = 500 },
            new() { Timestamp = today.AddDays(-2), ItemCount = 7, BytesFreed = 2048 },
            new() { Timestamp = today.AddDays(-6), ItemCount = 1, BytesFreed = 100 },
            // Out-of-window records must not leak in.
            new() { Timestamp = today.AddDays(-7), ItemCount = 9, BytesFreed = 9999 },
            new() { Timestamp = today.AddDays(1), ItemCount = 9, BytesFreed = 9999 }
        };

        var trend = CleanupHistoryService.BuildDailyTrend(records, 7, today);
        Check(trend.Count == 7, $"Seven days returned (got {trend.Count})");
        Check(trend[0].Date == today.AddDays(-6).Date && trend[6].Date == today.Date,
            "Points run oldest-first ending today");
        Check(trend.Zip(trend.Skip(1), (a, b) => (b.Date - a.Date).TotalDays == 1).All(x => x),
            "Days are consecutive with no gaps");

        Check(trend[6].Operations == 2 && trend[6].BytesFreed == 1500,
            "Today aggregates both operations (2 ops, 1500 bytes)");
        Check(trend[4].Operations == 1 && trend[4].BytesFreed == 2048,
            "Day -2 bucketed correctly (1 op, 2048 bytes)");
        Check(trend[0].Operations == 1 && trend[0].BytesFreed == 100,
            "Oldest day (day -6) included");
        Check(trend[1].Operations == 0 && trend[1].BytesFreed == 0,
            "Quiet days are present with zeros");
        Check(trend.Sum(t => t.Operations) == 4 && trend.Sum(t => t.BytesFreed) == 3648,
            "Out-of-window records excluded from totals");

        var empty = CleanupHistoryService.BuildDailyTrend(new List<CleanupRecord>(), 7, today);
        Check(empty.Count == 7 && empty.All(t => t.Operations == 0 && t.BytesFreed == 0),
            "Empty history yields seven zero days");

        var clamped = CleanupHistoryService.BuildDailyTrend(records, 0, today);
        Check(clamped.Count == 1 && clamped[0].Date == today.Date,
            "days=0 clamps to a single today bucket");

        var single = CleanupHistoryService.BuildDailyTrend(records, 1, today);
        Check(single.Count == 1 && single[0].Operations == 2 && single[0].BytesFreed == 1500,
            "days=1 returns today only");

        var negative = CleanupHistoryService.BuildDailyTrend(
            new List<CleanupRecord> { new() { Timestamp = today, ItemCount = 1, BytesFreed = -50 } }, 1, today);
        Check(negative[0].BytesFreed == 0 && negative[0].Operations == 1,
            "Negative byte counts clamp to zero but still count the operation");
    }

    private static void TestHistoryCsv()
    {
        var stamp = new DateTime(2026, 9, 27, 14, 30, 0);
        var records = new List<CleanupRecord>
        {
            new()
            {
                Timestamp = stamp,
                OperationType = CleanupOperationType.SystemClean,
                ItemCount = 12,
                BytesFreed = 2048,
                Details = "Temp files",
                WasDryRun = false
            },
            new()
            {
                Timestamp = stamp.AddHours(1),
                OperationType = CleanupOperationType.BrowserClean,
                ItemCount = 3,
                BytesFreed = 512,
                Details = "Cache, \"cookies\", and\nsite data",
                WasDryRun = true
            }
        };

        var csv = CleanupHistoryService.BuildHistoryCsv(records);
        var lines = csv.Split(["\r\n", "\n"], StringSplitOptions.None);
        Check(lines.Length >= 3 && lines[0] == "Timestamp,Operation,Items Cleaned,Space Freed (bytes),Dry Run,Details",
            "Header row has the expected six columns");
        Check(lines[1] == "2026-09-27 14:30:00,System Cleanup,12,2048,No,Temp files",
            "Plain record serializes to one row with display operation name");
        Check(csv.Contains("Yes,\"Cache, \"\"cookies\"\", and\nsite data\""),
            "Commas, quotes, and newlines are RFC-4180 quoted");
        Check(csv.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Length == 4,
            "Two records plus header span four physical lines (embedded newline stays inside quotes)");

        var empty = CleanupHistoryService.BuildHistoryCsv(new List<CleanupRecord>());
        Check(empty.Trim() == "Timestamp,Operation,Items Cleaned,Space Freed (bytes),Dry Run,Details",
            "Empty history yields header only");
        var nullList = CleanupHistoryService.BuildHistoryCsv(null);
        Check(nullList.Trim().StartsWith("Timestamp,"),
            "Null record list yields header only without throwing");

        var negative = CleanupHistoryService.BuildHistoryCsv(
            new List<CleanupRecord>
            {
                new() { Timestamp = stamp, ItemCount = 1, BytesFreed = -50, Details = "neg" }
            });
        Check(negative.Contains(",0,No,neg"),
            "Negative byte counts clamp to zero in the CSV");
    }
}
