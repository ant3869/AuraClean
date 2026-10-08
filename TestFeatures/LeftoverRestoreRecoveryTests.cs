using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AuraClean.Services;
using AuraClean.ViewModels;
using CommunityToolkit.Mvvm.Messaging;

namespace TestFeatures;

/// <summary>
/// The recovery experience built on top of <see cref="LeftoverBackupStore"/>: idempotent
/// interrupted-operation recovery (the same call made at application startup and from the
/// Leftover Backups page), the data the restore list surfaces, conflict detection before an
/// individual restore, and the backup journal's named mutex serializing real cross-process
/// writes (not just concurrent threads within one process, which <c>LeftoverOwnershipTests</c>
/// already covers). Everything runs inside a throwaway temp folder.
/// </summary>
public static class LeftoverRestoreRecoveryTests
{
    private static int _pass;
    private static int _fail;
    private static string _root = string.Empty;

    public static int Run()
    {
        _pass = 0;
        _fail = 0;
        _root = Path.Combine(Path.GetTempPath(), "AuraRestoreRecoveryTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        try
        {
            Section("Idempotent startup recovery", TestIdempotentStartupRecovery);
            Section("Restore list surfaces correct data", TestRestoreListData);
            Section("Individual restore: conflict detection without overwrite", TestConflictDetection);
            Section("Cross-process journal locking", TestCrossProcessLocking);
            Section("ViewModel: storage visibility excludes restored history", TestStorageVisibility);
            Section("ViewModel: live refresh on external backup change", TestLiveRefresh);
        }
        finally
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (Exception ex) { Console.WriteLine($"  (cleanup of {_root} failed: {ex.Message})"); }
        }

        Console.WriteLine($"  Batch R: {_pass} passed, {_fail} failed");
        return _fail;
    }

    private static void TestIdempotentStartupRecovery()
    {
        var storeRoot = Path.Combine(_root, "_idempotent");
        var store = new LeftoverBackupStore(storeRoot);
        var dir = Dir("Idem", "AcmeTool");
        File.WriteAllText(Path.Combine(dir, "data.txt"), "payload");
        var moved = store.MoveToBackup(dir, true, "AcmeTool", "evidence", 0);
        Assert(moved.Outcome == LeftoverRemovalOutcome.MovedToBackup, "Setup: item moved to backup");

        // Simulate a crash between the move and the journal update: rewind State to Pending,
        // the same way LeftoverOwnershipTests simulates a torn/interrupted write.
        var journal = Path.Combine(storeRoot, "journal.json");
        File.WriteAllText(journal, File.ReadAllText(journal).Replace("\"Moved\"", "\"Pending\""));

        // First call — simulates application startup picking up the stale entry.
        var restart = new LeftoverBackupStore(storeRoot);
        var firstReport = restart.RecoverInterrupted();
        Assert(firstReport.Count == 1, $"First startup recovery resolves the stale Pending entry ({firstReport.Count})");
        Assert(restart.GetEntries().Single().State == LeftoverBackupState.Moved,
            "Entry promoted to Moved after the first recovery");

        // Second call — simulates a second startup (or opening Leftover Backups afterwards):
        // must be a no-op, never re-reporting or re-touching an already-settled entry.
        var secondReport = restart.RecoverInterrupted();
        Assert(secondReport.Count == 0, "Second recovery call is a no-op (idempotent)");
        var entry = restart.GetEntries().Single();
        Assert(entry.State == LeftoverBackupState.Moved, "Entry state is unchanged by the second call");

        var (ok, _) = restart.Restore(entry.Id);
        Assert(ok && File.ReadAllText(Path.Combine(dir, "data.txt")) == "payload",
            "The entry still restores intact after repeated idempotent recovery calls");
    }

    private static void TestRestoreListData()
    {
        var storeRoot = Path.Combine(_root, "_listdata");
        var store = new LeftoverBackupStore(storeRoot);

        var a = Dir("ListA", "AcmeTool");
        File.WriteAllText(Path.Combine(a, "f.txt"), "a");
        var movedA = store.MoveToBackup(a, true, "AcmeTool", "Strong evidence: install folder", 12345);
        Assert(movedA.Outcome == LeftoverRemovalOutcome.MovedToBackup, "Setup: entry A moved");

        var b = Dir("ListB", "AcmeTool2");
        File.WriteAllText(Path.Combine(b, "f.txt"), "b");
        var movedB = store.MoveToBackup(b, true, "AcmeTool2", "Name guess", 999);
        Assert(movedB.Outcome == LeftoverRemovalOutcome.MovedToBackup, "Setup: entry B moved");
        store.Restore(movedB.BackupEntryId!);

        var entries = store.GetEntries();

        var entryA = entries.Single(e => e.Id == movedA.BackupEntryId);
        Assert(entryA.OriginalPath.Equals(a, StringComparison.OrdinalIgnoreCase),
            "Entry A's original path is exposed for the restore list");
        Assert(entryA.SizeBytes == 12345, "Entry A's size is exposed for the restore list");
        Assert(entryA.CreatedUtc > DateTime.UtcNow.AddMinutes(-5), "Entry A's created date is recent UTC");
        Assert(entryA.ProgramName == "AcmeTool" && entryA.Evidence.Contains("Strong"),
            "Entry A's program name and evidence are exposed");
        Assert(entryA.State == LeftoverBackupState.Moved, "Entry A is Moved (restorable)");
        Assert(!LeftoverBackupStore.OccupiesOriginalPath(entryA), "Entry A has no conflict (original path is free)");

        var entryB = entries.Single(e => e.Id == movedB.BackupEntryId);
        Assert(entryB.State == LeftoverBackupState.Restored, "Entry B shows Restored after being restored");
    }

    private static void TestConflictDetection()
    {
        var storeRoot = Path.Combine(_root, "_conflict");
        var store = new LeftoverBackupStore(storeRoot);

        var dir = Dir("Conflict", "AcmeTool");
        File.WriteAllText(Path.Combine(dir, "original.txt"), "original-content");
        var moved = store.MoveToBackup(dir, true, "AcmeTool", "evidence", 0);
        Assert(moved.Outcome == LeftoverRemovalOutcome.MovedToBackup, "Setup: item moved to backup");
        var entry = store.GetEntries().Single(e => e.Id == moved.BackupEntryId);

        Assert(!LeftoverBackupStore.OccupiesOriginalPath(entry),
            "No conflict before anything is recreated at the original path");

        // Something reoccupies the original path (e.g. a reinstall) before the user restores.
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "reinstalled.txt"), "new-install");

        Assert(LeftoverBackupStore.OccupiesOriginalPath(entry),
            "Conflict is detected proactively (read-only check) before Restore() is even called");

        var (ok, message) = store.Restore(entry.Id);
        Assert(!ok && message.Contains("already exists"), $"Restore refuses the conflicting entry ({message})");
        Assert(File.Exists(Path.Combine(dir, "reinstalled.txt")) && !File.Exists(Path.Combine(dir, "original.txt")),
            "The recreated content is untouched — restore never overwrote it");
        Assert(store.GetEntries().Single(e => e.Id == entry.Id).State == LeftoverBackupState.Moved,
            "The backup entry itself is unchanged by the refused restore, so it can be restored once the conflict clears");

        // Conflict clears (the recreated item is removed again) => restore now succeeds.
        Directory.Delete(dir, recursive: true);
        Assert(!LeftoverBackupStore.OccupiesOriginalPath(entry), "Conflict clears once the original path is free again");
        var (ok2, _) = store.Restore(entry.Id);
        Assert(ok2 && File.ReadAllText(Path.Combine(dir, "original.txt")) == "original-content",
            "Restore succeeds once the conflict is gone, and the original content comes back intact");
    }

    /// <summary>
    /// Launches real OS processes (not threads) that each move a distinct item into the same
    /// backup root at once, proving <see cref="LeftoverBackupStore"/>'s named mutex
    /// (Local\AuraClean.LeftoverBackupJournal) serializes the journal across process boundaries.
    /// </summary>
    private static void TestCrossProcessLocking()
    {
        var storeRoot = Path.Combine(_root, "_crossproc");
        const int workerCount = 6;

        var (fileName, argsPrefix) = ResolveSelfLaunch();
        var sources = Enumerable.Range(0, workerCount)
            .Select(i =>
            {
                var dir = Dir("XProc" + i, "AcmeTool");
                File.WriteAllText(Path.Combine(dir, "payload.txt"), "worker" + i);
                return dir;
            })
            .ToList();

        var processes = sources.Select(source =>
        {
            var psi = new ProcessStartInfo(fileName)
            {
                Arguments = $"{argsPrefix}--leftover-worker \"{storeRoot}\" \"{source}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            return Process.Start(psi) ?? throw new InvalidOperationException($"Could not launch worker for {source}");
        }).ToList();

        foreach (var p in processes)
            p.WaitForExit(30_000);

        var exitCodes = processes.Select(p => p.HasExited ? p.ExitCode : -1).ToList();
        foreach (var p in processes)
            p.Dispose();

        Assert(exitCodes.All(c => c == 0),
            $"All {workerCount} real OS worker processes completed their move (exit codes: {string.Join(",", exitCodes)})");

        var entries = new LeftoverBackupStore(storeRoot).GetEntries();
        Assert(entries.Count == workerCount && entries.All(e => e.State == LeftoverBackupState.Moved),
            $"No journal entry lost under real cross-process contention ({entries.Count}/{workerCount} Moved)");
        Assert(entries.Select(e => e.Id).Distinct().Count() == workerCount,
            "Every entry has a distinct id (no overwritten/duplicated journal record)");
    }

    private static void TestStorageVisibility()
    {
        var storeRoot = Path.Combine(_root, "_storagevis");
        var store = new LeftoverBackupStore(storeRoot);

        var a = Dir("StorA", "AcmeTool");
        File.WriteAllText(Path.Combine(a, "f.txt"), "a");
        var movedA = store.MoveToBackup(a, true, "AcmeTool", "evidence", 1000);
        Assert(movedA.Outcome == LeftoverRemovalOutcome.MovedToBackup, "Setup: entry A moved (1000 bytes)");

        var b = Dir("StorB", "AcmeTool2");
        File.WriteAllText(Path.Combine(b, "f.txt"), "b");
        var movedB = store.MoveToBackup(b, true, "AcmeTool2", "evidence", 2000);
        Assert(movedB.Outcome == LeftoverRemovalOutcome.MovedToBackup, "Setup: entry B moved (2000 bytes)");
        var (restoredOk, _) = store.Restore(movedB.BackupEntryId!);
        Assert(restoredOk, "Setup: entry B restored (its bytes no longer occupy backup disk)");

        var vm = new LeftoverRestoreViewModel(store);
        Assert(vm.Entries.Count == 2, $"Both entries remain visible as history ({vm.Entries.Count})");
        Assert(vm.BackedUpCount == 1, $"Only the still-Moved entry counts as backed up ({vm.BackedUpCount})");
        Assert(vm.TotalSizeDisplay == AuraClean.Helpers.FormatHelper.FormatBytes(1000),
            $"Total size reflects only active backup disk usage, not restored history ({vm.TotalSizeDisplay})");
    }

    private static void TestLiveRefresh()
    {
        var storeRoot = Path.Combine(_root, "_liverefresh");
        var store = new LeftoverBackupStore(storeRoot);
        var vm = new LeftoverRestoreViewModel(store);
        Assert(vm.Entries.Count == 0, "Setup: ViewModel starts with no entries");

        // Simulate an unrelated page (e.g. the Uninstaller's Deep Scan) moving a leftover into the
        // same backup store and announcing it, exactly as production code now does, without this
        // test ever calling vm.LoadEntries() itself.
        var dir = Dir("LiveRefresh", "AcmeTool");
        File.WriteAllText(Path.Combine(dir, "f.txt"), "data");
        var moved = store.MoveToBackup(dir, true, "AcmeTool", "evidence", 500);
        Assert(moved.Outcome == LeftoverRemovalOutcome.MovedToBackup, "Setup: an external operation moves an item to backup");
        Assert(vm.Entries.Count == 0, "Before the message, the cached ViewModel is still stale (sanity check)");

        WeakReferenceMessenger.Default.Send(LeftoverBackupChangedMessage.Instance);

        Assert(vm.Entries.Count == 1, $"ViewModel refreshed itself after the change message ({vm.Entries.Count} entries)");
        Assert(vm.BackedUpCount == 1, "BackedUpCount reflects the externally-added entry");
    }

    /// <summary>How to re-launch this same test executable as a worker, whether it's currently
    /// running as its native apphost or hosted by the dotnet muxer.</summary>
    private static (string FileName, string ArgsPrefix) ResolveSelfLaunch()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath) &&
            !Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (exePath, string.Empty);

        var dll = Path.Combine(AppContext.BaseDirectory, "TestFeatures.dll");
        return ("dotnet", $"\"{dll}\" ");
    }

    // ───────────────────────── helpers ─────────────────────────

    private static string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Section(string name, Action test)
    {
        Console.WriteLine($"═══ BATCH R: {name} ═══");
        try { test(); }
        catch (Exception ex)
        {
            _fail++;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  FAIL: {name} threw {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (condition) _pass++; else _fail++;
        Console.ForegroundColor = condition ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}: {message}");
        Console.ResetColor();
    }
}
