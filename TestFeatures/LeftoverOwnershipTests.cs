using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;

namespace TestFeatures;

/// <summary>
/// Evidence-based leftover removal: ownership verdicts (incl. shared, contradictory and
/// colliding evidence), execution-time re-authorization, and the recoverable backup store
/// (move, restore, locked files, interrupted operations, corrupt journal, cross-drive).
/// Everything runs inside a throwaway temp folder; no real program data is touched.
/// </summary>
public static class LeftoverOwnershipTests
{
    private static int _pass;
    private static int _fail;
    private static string _root = string.Empty;

    public static int Run()
    {
        _pass = 0;
        _fail = 0;
        _root = LongPathHelper.Canonicalize(Path.Combine(Path.GetTempPath(), "AuraLeftoverTests_" + Guid.NewGuid().ToString("N")[..8]))!;
        Directory.CreateDirectory(_root);

        try
        {
            Section("Ownership verdicts", TestVerdicts);
            Section("Path boundaries, links and short names", TestPathEdgeCases);
            Section("Selection guard", TestSelectionGuard);
            Section("Removal: legit move + restore", TestMoveAndRestore);
            Section("Removal: refusals at execution time", TestExecutionRefusals);
            Section("Backup store: failures and interruption", TestStoreFailures);
            Section("Crash consistency (injected process death)", TestCrashConsistency);
            Section("Concurrent journal access", TestConcurrency);
            Section("Adversarial: paths beyond MAX_PATH", TestLongPaths);
            Section("Security: forged journal entries and root ACL hardening", TestForgedEntriesAndAclHardening);
            Section("Security: junction-planted backup root fails closed", TestJunctionPlantedRoot);
            Section("Recovery save failure is reported honestly", TestRecoverySaveFailure);
            Section("Recovery report distinguishes resolved from needs-review from error", TestRecoveryReportCategorization);
        }
        finally
        {
            try
            {
                // Remove junctions first (non-recursive) so the recursive delete never walks them.
                foreach (var d in Directory.EnumerateDirectories(_root))
                    if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        Directory.Delete(d);
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) { Console.WriteLine($"  (cleanup of {_root} failed: {ex.Message})"); }
        }

        Console.WriteLine($"  Batch L: {_pass} passed, {_fail} failed");
        return _fail;
    }

    // ───────────────────────── Ownership verdicts ─────────────────────────

    private static void TestVerdicts()
    {
        var acmeDir = Dir("Acme", "AcmeTool");
        var acme = Program("AcmeTool", "Acme Corp", acmeDir, key: "HKLM\\...\\AcmeTool");

        var legit = Assess(acmeDir, acme);
        Assert(legit.Verdict == OwnershipVerdict.Recommended, "Surviving recorded install folder is Recommended");
        Assert(legit.Evidence.Any(e => e.Strength == EvidenceStrength.Strong), "...with Strong evidence");

        // Shared vendor folder: contains another installed program's folder.
        var vendor = Dir("SharedTools");
        Dir("SharedTools", "Plugin");
        var sharedTarget = Program("Shared Tools", "Vendor", "", key: "k1");
        var plugin = Program("Shared Plugin", "Vendor", Path.Combine(vendor, "Plugin"), key: "k2");
        var shared = Assess(vendor, sharedTarget, plugin);
        Assert(shared.IsBlocked && shared.BlockReasons.Any(r => r.Contains("contains the install folder")),
            "Folder containing another installed program's folder is Blocked");

        // Vendor-level InstallLocation (critic finding): a sibling product with a blank
        // InstallLocation is revealed by its DisplayIcon / uninstaller paths.
        var adobe = Dir("AdobeLike");
        Dir("AdobeLike", "Reader");
        Dir("AdobeLike", "Photoshop");
        var reader = Program("Reader", "AdobeLike", adobe, key: "v1");
        var photoshop = Program("Photoshop", "AdobeLike", "", key: "v2");
        photoshop.DisplayIcon = Path.Combine(adobe, "Photoshop", "Photoshop.exe") + ",0";
        Assert(Assess(adobe, reader, photoshop).IsBlocked,
            "Vendor-level install folder holding another product (found via DisplayIcon) is Blocked");
        var viaUninstaller = Program("Photoshop", "AdobeLike", "", key: "v3");
        viaUninstaller.UninstallString = "\"" + Path.Combine(adobe, "Photoshop", "uninst.exe") + "\" /S";
        Assert(Assess(adobe, reader, viaUninstaller).IsBlocked, "...and also when revealed via the quoted UninstallString");
        var msiOther = Program("Some MSI", "X", "", key: "v4");
        msiOther.UninstallString = "MsiExec.exe /X{00000000-0000-0000-0000-000000000000}";
        Assert(Assess(acmeDir, acme, msiOther).Verdict == OwnershipVerdict.Recommended,
            "A bare MsiExec uninstall string (System32) does not block anything");

        // Inside another program's folder.
        var hostDir = Dir("HostApp");
        var inner = Dir("HostApp", "AcmeTool");
        var host = Program("Host App", "Host", hostDir, key: "k3");
        var nested = Assess(inner, Program("AcmeTool", "Acme Corp", "", key: "k3b"), host);
        Assert(nested.IsBlocked && nested.BlockReasons.Any(r => r.Contains("inside the install folder")),
            "Folder inside another installed program's folder is Blocked");

        // Publisher words are never match terms, so Roaming\Python (shared by every Python)
        // yields no evidence for "Python 3.11" from "Python Software Foundation": abstain.
        var pyDir = Dir("Python");
        var py311 = Program("Python 3.11.4 (64-bit)", "Python Software Foundation", "", key: "py311");
        var pyAbstain = Assess(pyDir, py311);
        Assert(pyAbstain.IsBlocked && pyAbstain.Evidence.Count == 0,
            "Roaming Python folder gets no evidence from a publisher word => Blocked (abstain)");

        // Name contention: a folder named after a word two installed editions share.
        var npDir = Dir("Notepad3");
        var classic = Program("Notepad3 Editor Classic", "Rizonesoft", "", key: "np1");
        var portable = Program("Notepad3 Editor Portable", "Rizonesoft", "", key: "np2");
        var contended = Assess(npDir, classic, portable);
        Assert(contended.IsBlocked && contended.BlockReasons.Any(r => r.Contains("may be shared")),
            "Name also matching another installed edition is Blocked (shared resource)");
        var alone = Assess(npDir, classic);
        Assert(alone.Verdict == OwnershipVerdict.Review && alone.StrongestEvidence == EvidenceStrength.Weak,
            "Same folder with no other edition installed is only a Weak 'Review' guess");

        // Contradictory evidence: strong path evidence outranks a name collision.
        var npOwner = Program("Notepad3 Editor Classic", "Rizonesoft", npDir, key: "np3");
        var strongWins = Assess(npDir, npOwner, portable);
        Assert(strongWins.Verdict == OwnershipVerdict.Recommended,
            "Recorded install folder stays Recommended despite another program sharing its name");

        // Abstain when nothing links the item.
        var unrelated = Assess(Dir("Unrelated"), acme);
        Assert(unrelated.IsBlocked && unrelated.Evidence.Count == 0, "No evidence => Blocked (abstain)");

        // Medium evidence: full display-name match without a recorded path.
        var fullName = Assess(Dir("AcmeTool"), Program("AcmeTool", "Acme Corp", "", key: "k4"));
        Assert(fullName.Verdict == OwnershipVerdict.Review && fullName.StrongestEvidence == EvidenceStrength.Medium,
            "Full-name match without a recorded path is Medium 'Review'");

        // A bogus install location (drive root / Program Files) proves nothing either way.
        var bogusRoot = Program("Broken Entry", "X", Path.GetPathRoot(_root)!, key: "bogus1");
        var bogusPf = Program("Broken Entry 2", "X", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), key: "bogus2");
        var stillLegit = Assess(acmeDir, acme, bogusRoot, bogusPf);
        Assert(stillLegit.Verdict == OwnershipVerdict.Recommended,
            "Other entries with drive-root/Program Files install locations do not block everything");
        var bogusTarget = Assess(Dir("Whatever"), Program("Whatever Else", "X", Path.GetPathRoot(_root)!, key: "bogus3"));
        Assert(bogusTarget.Verdict != OwnershipVerdict.Recommended &&
               bogusTarget.Evidence.All(e => e.Strength != EvidenceStrength.Strong),
            "A target whose install location is a drive root gets no Strong evidence");

        // The target's own duplicate instance (same uninstall key) is not "another program".
        var acmeCopy = Program("AcmeTool", "Acme Corp", acmeDir, key: "HKLM\\...\\AcmeTool");
        Assert(Assess(acmeDir, acme, acmeCopy).Verdict == OwnershipVerdict.Recommended,
            "The target's own uninstall entry is excluded from the conflict check");

        // AuraClean's own data folder is never a leftover.
        var ownData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuraClean");
        Assert(Assess(ownData, Program("AuraClean", "AuraClean", "", key: "self")).IsBlocked,
            "AuraClean's own data folder is Blocked");

        // Registry items keep the existing identity-checked, backup-first path.
        var reg = LeftoverOwnershipEvaluator.Assess(
            new JunkItem { Type = JunkType.OrphanedRegistryKey, Path = @"HKCU\Software\AcmeTool" }, acme, []);
        Assert(reg.Verdict == OwnershipVerdict.Review, "Registry leftovers are Review, never auto-blocked");
    }

    private static void TestPathEdgeCases()
    {
        var install = Dir("Prefix", "AcmeTool");
        var sibling = Dir("Prefix", "AcmeTools2");
        var acme = Program("AcmeTool", "Acme Corp", install, key: "p1");
        var collision = Assess(sibling, acme);
        Assert(collision.Verdict != OwnershipVerdict.Recommended &&
               collision.Evidence.All(e => e.Strength != EvidenceStrength.Strong),
            "Prefix collision (AcmeTools2 vs AcmeTool) gets no Strong path evidence");

        // Junction as the candidate itself.
        var junctionTarget = Dir("JunctionTarget");
        File.WriteAllText(Path.Combine(junctionTarget, "keep.txt"), "user data");
        var junction = Path.Combine(_root, "AcmeLinked");
        if (TryCreateJunction(junction, junctionTarget))
        {
            var j = Assess(junction, Program("AcmeLinked", "Acme Corp", "", key: "p2"));
            Assert(j.IsBlocked && j.BlockReasons.Any(r => r.Contains("junction", StringComparison.OrdinalIgnoreCase)),
                "A junction candidate is Blocked");
        }
        else Skip("junction candidate (mklink /J unavailable)");

        // 8.3 short name must not bypass the shared-folder check.
        var parent = Dir("Long Folder Name Here");
        Dir("Long Folder Name Here", "Product Data");
        var shortForm = LongPathHelper.TryGetShortPath(parent);
        if (shortForm != null && shortForm.Contains('~'))
        {
            var other = Program("Other Product", "Other", Path.Combine(shortForm, "Product Data"), key: "p3");
            var target = Program("Long Folder Name Here", "Vendor", "", key: "p4");
            var r = Assess(parent, target, other);
            Assert(r.IsBlocked && r.BlockReasons.Any(b => b.Contains("contains the install folder")),
                "Another program's install location in 8.3 form still blocks its parent folder");
        }
        else Skip("8.3 short names disabled on this volume");
    }

    private static void TestSelectionGuard()
    {
        var item = new JunkItem { Type = JunkType.RemnantDirectory, Path = Dir("Guard"), IsSelected = true };
        item.Ownership = new OwnershipAssessment(OwnershipVerdict.Blocked, [], ["test"]);
        Assert(!item.IsSelected, "Assigning a Blocked verdict clears the selection");
        item.IsSelected = true;
        Assert(!item.IsSelected && !item.CanSelect, "A Blocked item cannot be re-selected");

        var items = new[] { new JunkItem { Type = JunkType.RemnantDirectory, Path = Dir("Nothing"), IsSelected = true } };
        LeftoverOwnershipEvaluator.Annotate(items, Program("AcmeTool", "Acme", "", key: "g"), []);
        Assert(!items[0].IsSelected && items[0].IsOwnershipBlocked, "Annotate deselects blocked scan results");
    }

    // ───────────────────────── Removal ─────────────────────────

    private static void TestMoveAndRestore()
    {
        var store = new LeftoverBackupStore(Path.Combine(_root, "_backup"));
        var install = Dir("Move", "AcmeTool");
        File.WriteAllText(Path.Combine(install, "config.ini"), "x=1");
        var acme = Program("AcmeTool", "Acme Corp", install, key: "m1");
        var item = Leftover(install, JunkType.RemnantDirectory);

        var result = LeftoverRemovalService.RemoveOne(item, acme, [], store);
        Assert(result.Outcome == LeftoverRemovalOutcome.MovedToBackup, $"Legit leftover moved to backup ({result.Message})");
        Assert(!Directory.Exists(install), "Original folder is gone after the move");
        var entry = store.GetEntries().Single(e => e.Id == result.BackupEntryId);
        Assert(entry.State == LeftoverBackupState.Moved && File.Exists(Path.Combine(entry.BackupPath, "config.ini")),
            "Journal says Moved and the backup holds the contents");
        Assert(entry.Evidence.Contains("install folder"), "Journal records the evidence that authorized the move");

        var (ok, msg) = store.Restore(entry.Id);
        Assert(ok && File.ReadAllText(Path.Combine(install, "config.ini")) == "x=1", $"Restore puts it back intact ({msg})");
        Assert(store.GetEntries().Single(e => e.Id == entry.Id).State == LeftoverBackupState.Restored, "Journal says Restored");
        Assert(!store.Restore(entry.Id).Success, "A restored entry cannot be restored twice");

        // Restore must not overwrite something recreated at the original path.
        var file = FileAt(Path.Combine(Dir("Move2"), "AcmeTool.lnk"));
        var fileItem = Leftover(file, JunkType.AbandonedFile);
        var moved = LeftoverRemovalService.RemoveOne(fileItem, Program("AcmeTool", "Acme Corp", "", key: "m2"), [], store);
        Assert(moved.Outcome == LeftoverRemovalOutcome.MovedToBackup, $"Name-matched file moved to backup ({moved.Message})");
        File.WriteAllText(file, "reinstalled");
        var refused = moved.BackupEntryId == null ? (false, "") : store.Restore(moved.BackupEntryId);
        Assert(!refused.Item1 && File.ReadAllText(file) == "reinstalled", "Restore refuses to overwrite a recreated item");
    }

    private static void TestExecutionRefusals()
    {
        var store = new LeftoverBackupStore(Path.Combine(_root, "_backup2"));
        var acme = Program("AcmeTool", "Acme Corp", "", key: "r1");

        var missing = Leftover(Path.Combine(_root, "Gone", "AcmeTool"), JunkType.RemnantDirectory);
        Assert(LeftoverRemovalService.RemoveOne(missing, acme, [], store).Outcome == LeftoverRemovalOutcome.Missing,
            "Missing item reports Missing, not success");

        var nowFile = FileAt(Path.Combine(Dir("Kind"), "AcmeTool"));
        var kind = LeftoverRemovalService.RemoveOne(Leftover(nowFile, JunkType.RemnantDirectory), acme, [], store);
        Assert(kind.Outcome == LeftoverRemovalOutcome.Refused && File.Exists(nowFile), "Folder that became a file is refused");

        var unselected = Leftover(Dir("Unsel", "AcmeTool"), JunkType.RemnantDirectory);
        unselected.IsSelected = false;
        Assert(LeftoverRemovalService.RemoveOne(unselected, acme, [], store).Outcome == LeftoverRemovalOutcome.Refused,
            "Unselected item is refused");

        // Re-authorization: another program was installed into the folder after the scan.
        var later = Dir("Later", "AcmeTool");
        Dir("Later", "AcmeTool", "NewApp");
        var laterItem = Leftover(later, JunkType.RemnantDirectory);
        var newApp = Program("New App", "Other", Path.Combine(later, "NewApp"), key: "r2");
        var reauth = LeftoverRemovalService.RemoveOne(laterItem, acme, [newApp], store);
        Assert(reauth.Outcome == LeftoverRemovalOutcome.Refused && Directory.Exists(later) && reauth.RefusedOwnership?.IsBlocked == true,
            "Item that became shared after the scan is refused at execution time");

        // Ancestor junction: <root>\Link -> <root>\RealVendor, candidate <root>\Link\AcmeTool.
        var real = Dir("RealVendor", "AcmeTool");
        File.WriteAllText(Path.Combine(real, "data.bin"), "keep");
        var link = Path.Combine(_root, "Link");
        if (TryCreateJunction(link, Path.Combine(_root, "RealVendor")))
        {
            var viaLink = Leftover(Path.Combine(link, "AcmeTool"), JunkType.RemnantDirectory);
            var r = LeftoverRemovalService.RemoveOne(viaLink, acme, [], store);
            Assert(r.Outcome == LeftoverRemovalOutcome.Refused && File.Exists(Path.Combine(real, "data.bin")),
                "Candidate reached through a junctioned parent is refused; real target untouched");
        }
        else Skip("ancestor junction (mklink /J unavailable)");

        // Locked file: the atomic rename fails and nothing is removed.
        var locked = Dir("Locked", "AcmeTool");
        var lockedFile = Path.Combine(locked, "inuse.dat");
        File.WriteAllText(lockedFile, "busy");
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = LeftoverRemovalService.RemoveOne(Leftover(locked, JunkType.RemnantDirectory), acme, [], store);
            Assert(r.Outcome == LeftoverRemovalOutcome.Failed, $"Folder with a locked file fails cleanly ({r.Outcome})");
        }
        Assert(File.Exists(lockedFile), "...and every file is still in place (no partial delete)");
        Assert(store.GetEntries().All(e => e.State != LeftoverBackupState.Pending), "...and no Pending journal entry is left behind");

        // Cancellation before work starts moves nothing.
        var cancelDir = Dir("Cancel", "AcmeTool");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        bool threw = false;
        try
        {
            LeftoverRemovalService.RemoveAsync([Leftover(cancelDir, JunkType.RemnantDirectory)], acme, [], store, ct: cts.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { threw = true; }
        Assert(threw && Directory.Exists(cancelDir), "Cancelled removal throws and moves nothing");
    }

    private static void TestStoreFailures()
    {
        var storeRoot = Path.Combine(_root, "_backup3");
        var store = new LeftoverBackupStore(storeRoot);
        var journal = Path.Combine(storeRoot, "journal.json");

        // Corrupt journal => refuse, nothing removed.
        Directory.CreateDirectory(storeRoot);
        File.WriteAllText(journal, "{ not json");
        var dir = Dir("Corrupt", "AcmeTool");
        var r = store.MoveToBackup(dir, true, "AcmeTool", "test", 0);
        Assert(r.Outcome == LeftoverRemovalOutcome.Refused && Directory.Exists(dir), "Unreadable journal => refuse, item untouched");
        File.Delete(journal);

        // Overlap with the backup folder itself.
        Assert(store.MoveToBackup(storeRoot, true, "x", "x", 0).Outcome == LeftoverRemovalOutcome.Refused,
            "The backup folder can never be moved into itself");

        // Interrupted A: crash after the move but before marking Moved.
        var crashed = Dir("Crash", "AcmeTool");
        var moved = store.MoveToBackup(crashed, true, "AcmeTool", "test", 0);
        // Interrupted B: crash after journaling Pending but before the move (simulated by
        // restoring the item, then rewinding its journal state to Pending).
        var notStarted = Dir("Crash2", "AcmeTool");
        var pending = store.MoveToBackup(notStarted, true, "AcmeTool", "test", 0);
        var restoredOk = pending.BackupEntryId != null && store.Restore(pending.BackupEntryId).Success;
        File.WriteAllText(journal, File.ReadAllText(journal)
            .Replace("\"Moved\"", "\"Pending\"").Replace("\"Restored\"", "\"Pending\""));

        var report = store.RecoverInterrupted();
        var entries = store.GetEntries();
        Assert(moved.Outcome == LeftoverRemovalOutcome.MovedToBackup && restoredOk, "Interrupted-op fixtures prepared");
        Assert(entries.SingleOrDefault(e => e.Id == moved.BackupEntryId)?.State == LeftoverBackupState.Moved,
            "Pending entry whose move completed is recovered as Moved (restorable)");
        Assert(entries.All(e => e.Id != pending.BackupEntryId) && Directory.Exists(notStarted),
            "Pending entry whose move never happened is dropped; original untouched");
        Assert(report.Count == 2, $"Recovery reports both interrupted entries ({report.Count})");

        // Pending-but-moved is restorable directly, without RecoverInterrupted first.
        var direct = Dir("Crash3", "AcmeTool");
        var directMove = store.MoveToBackup(direct, true, "AcmeTool", "test", 0);
        File.WriteAllText(journal, File.ReadAllText(journal).Replace("\"Moved\"", "\"Pending\""));
        Assert(directMove.BackupEntryId != null && store.Restore(directMove.BackupEntryId).Success && Directory.Exists(direct),
            "A Pending entry whose move completed can be restored directly");

        // Cross-drive: backup root on another volume => refuse rather than copy+delete.
        var otherVolumeRoot = Path.Combine(AppContext.BaseDirectory, "_leftover_xvol_" + Guid.NewGuid().ToString("N")[..6]);
        if (!string.Equals(Path.GetPathRoot(otherVolumeRoot), Path.GetPathRoot(_root), StringComparison.OrdinalIgnoreCase))
        {
            var xvol = new LeftoverBackupStore(otherVolumeRoot);
            var xdir = Dir("XVol", "AcmeTool");
            var x = xvol.MoveToBackup(xdir, true, "AcmeTool", "test", 0);
            Assert(x.Outcome == LeftoverRemovalOutcome.Refused && Directory.Exists(xdir),
                "Cross-drive item is refused (no non-atomic copy+delete)");
            try { if (Directory.Exists(otherVolumeRoot)) Directory.Delete(otherVolumeRoot, true); } catch (IOException) { }
        }
        else Skip("cross-drive (test output is on the same volume as %TEMP%)");
    }

    // ───────────────────────── Crash consistency ─────────────────────────

    /// <summary>Thrown by the crash hook. No production catch filter handles it, so the
    /// operation stops at that exact point, like process death (finally blocks still run).</summary>
    private sealed class SimulatedCrash : Exception { }

    private static LeftoverBackupStore Crashing(string root, string point) =>
        new(root) { CrashPoint = p => { if (p == point) throw new SimulatedCrash(); } };

    private static bool Crash(Action action)
    {
        try { action(); return false; }
        catch (SimulatedCrash) { return true; }
    }

    /// <summary>Safety invariant: the item's data is at its original path, or a Moved journal
    /// entry points at a backup holding it. Proved by actually restoring it.</summary>
    private static bool DataIsRecoverable(LeftoverBackupStore store, string original, string content)
    {
        var file = Path.Combine(original, "data.txt");
        if (File.Exists(file))
            return File.ReadAllText(file) == content;
        // Newest Moved entry whose backup exists (a crash mid-restore can leave an older,
        // stale Moved entry whose backup is already back at the original path).
        var entry = store.GetEntries()
            .Where(e => e.OriginalPath.Equals(original, StringComparison.OrdinalIgnoreCase) &&
                        e.State == LeftoverBackupState.Moved && Directory.Exists(e.BackupPath))
            .OrderByDescending(e => e.CreatedUtc)
            .FirstOrDefault();
        return entry != null && store.Restore(entry.Id).Success && File.ReadAllText(file) == content;
    }

    private static string Payload(string name, string content)
    {
        var dir = Dir("CrashCases", name, "AcmeTool");
        File.WriteAllText(Path.Combine(dir, "data.txt"), content);
        return dir;
    }

    private static void TestCrashConsistency()
    {
        // A: death after Pending is journaled, before the move.
        var rootA = Path.Combine(_root, "_crashA");
        var a = Payload("A", "alpha");
        Assert(Crash(() => Crashing(rootA, "move:after-pending-saved").MoveToBackup(a, true, "AcmeTool", "t", 0)),
            "A: crash injected after Pending saved");
        var restartA = new LeftoverBackupStore(rootA);
        Assert(restartA.GetEntries().Single().State == LeftoverBackupState.Pending, "A: journal shows the Pending intent");
        restartA.RecoverInterrupted();
        Assert(restartA.GetEntries().Count == 0 && File.ReadAllText(Path.Combine(a, "data.txt")) == "alpha",
            "A: recovery drops the intent; original untouched");

        // B: death after the move, before Moved is journaled.
        var rootB = Path.Combine(_root, "_crashB");
        var b = Payload("B", "bravo");
        Assert(Crash(() => Crashing(rootB, "move:after-move").MoveToBackup(b, true, "AcmeTool", "t", 0)),
            "B: crash injected after the move");
        var restartB = new LeftoverBackupStore(rootB);
        Assert(!Directory.Exists(b) && restartB.GetEntries().Single().State == LeftoverBackupState.Pending,
            "B: original gone, journal still Pending");
        restartB.RecoverInterrupted();
        Assert(restartB.GetEntries().Single().State == LeftoverBackupState.Moved, "B: recovery promotes it to Moved");
        Assert(DataIsRecoverable(restartB, b, "bravo"), "B: data restores intact");

        // C: death during restore, after moving back, before Restored is journaled.
        var rootC = Path.Combine(_root, "_crashC");
        var c = Payload("C", "charlie");
        var moved = new LeftoverBackupStore(rootC).MoveToBackup(c, true, "AcmeTool", "t", 0);
        Assert(Crash(() => Crashing(rootC, "restore:after-move").Restore(moved.BackupEntryId!)),
            "C: crash injected after restore move");
        var restartC = new LeftoverBackupStore(rootC);
        Assert(File.ReadAllText(Path.Combine(c, "data.txt")) == "charlie", "C: data is back at the original path");
        var again = restartC.Restore(moved.BackupEntryId!);
        Assert(!again.Success && File.ReadAllText(Path.Combine(c, "data.txt")) == "charlie",
            "C: a stale Moved entry cannot overwrite the restored data");
        var reconciled = restartC.RecoverInterrupted();
        Assert(reconciled.Count == 1 && !reconciled[0].StartsWith("Error:"), "C: recovery reconciles the stale entry");
        Assert(restartC.GetEntries().Single(e => e.Id == moved.BackupEntryId).State == LeftoverBackupState.Restored,
            "C: the stale Moved entry is now correctly classified as Restored");
        var reRemove = restartC.MoveToBackup(c, true, "AcmeTool", "t", 0);
        Assert(reRemove.Outcome == LeftoverRemovalOutcome.MovedToBackup && DataIsRecoverable(restartC, c, "charlie"),
            "C: the same path can be removed and restored again afterwards");

        // D: torn journal (power loss) after two completed removals. The previous version is
        // one transition behind, and write-ahead ordering makes that state safe.
        var rootD = Path.Combine(_root, "_crashD");
        var storeD = new LeftoverBackupStore(rootD);
        var d1 = Payload("D1", "delta1");
        var d2 = Payload("D2", "delta2");
        storeD.MoveToBackup(d1, true, "AcmeTool", "t", 0);
        storeD.MoveToBackup(d2, true, "AcmeTool", "t", 0);
        var journal = Path.Combine(rootD, "journal.json");
        var text = File.ReadAllText(journal);
        File.WriteAllText(journal, text[..(text.Length / 2)]);
        var restartD = new LeftoverBackupStore(rootD);
        Assert(restartD.GetEntries().Count == 2, "D: torn journal falls back to the previous version (both entries known)");
        restartD.RecoverInterrupted();
        Assert(DataIsRecoverable(restartD, d1, "delta1") && DataIsRecoverable(restartD, d2, "delta2"),
            "D: both removals restore intact after the torn write");

        // E: journal missing (death mid-ReplaceFile) with the previous version present.
        var rootE = Path.Combine(_root, "_crashE");
        var storeE = new LeftoverBackupStore(rootE);
        var e1 = Payload("E1", "echo1");
        var e2 = Payload("E2", "echo2");
        storeE.MoveToBackup(e1, true, "AcmeTool", "t", 0);
        storeE.MoveToBackup(e2, true, "AcmeTool", "t", 0);
        File.Delete(Path.Combine(rootE, "journal.json"));
        var restartE = new LeftoverBackupStore(rootE);
        restartE.RecoverInterrupted();
        Assert(DataIsRecoverable(restartE, e1, "echo1") && DataIsRecoverable(restartE, e2, "echo2"),
            "E: missing journal recovers from the previous version");

        // F: both versions unreadable => fail closed; nothing new is moved.
        var rootF = Path.Combine(_root, "_crashF");
        Directory.CreateDirectory(rootF);
        File.WriteAllText(Path.Combine(rootF, "journal.json"), "{");
        File.WriteAllText(Path.Combine(rootF, "journal.prev.json"), "[{");
        var f = Payload("F", "foxtrot");
        var refused = new LeftoverBackupStore(rootF).MoveToBackup(f, true, "AcmeTool", "t", 0);
        Assert(refused.Outcome == LeftoverRemovalOutcome.Refused && File.ReadAllText(Path.Combine(f, "data.txt")) == "foxtrot",
            "F: both journal versions unreadable => refuse, item untouched");

        // G: a torn journal is never rotated into the fallback slot by the next save.
        var rootG = Path.Combine(_root, "_crashG");
        var storeG = new LeftoverBackupStore(rootG);
        var g1 = Payload("G1", "golf1");
        storeG.MoveToBackup(g1, true, "AcmeTool", "t", 0);
        var gJournal = Path.Combine(rootG, "journal.json");
        File.WriteAllText(gJournal, "{ torn");
        var g2 = Payload("G2", "golf2");
        storeG.MoveToBackup(g2, true, "AcmeTool", "t", 0);
        File.WriteAllText(gJournal, "{ torn again");
        var restartG = new LeftoverBackupStore(rootG);
        restartG.RecoverInterrupted();
        Assert(DataIsRecoverable(restartG, g1, "golf1"),
            "G: the fallback still holds the earlier removal after repeated torn writes");
    }

    private static void TestConcurrency()
    {
        var root = Path.Combine(_root, "_concurrent");
        var storeA = new LeftoverBackupStore(root);
        var storeB = new LeftoverBackupStore(root);
        var dirs = Enumerable.Range(0, 24).Select(i => Payload("P" + i, "payload" + i)).ToList();
        var results = new LeftoverRemovalResult[dirs.Count];
        System.Threading.Tasks.Parallel.For(0, dirs.Count, i =>
            results[i] = (i % 2 == 0 ? storeA : storeB).MoveToBackup(dirs[i], true, "AcmeTool", "t", 0));

        var entries = new LeftoverBackupStore(root).GetEntries();
        Assert(results.All(r => r.Outcome == LeftoverRemovalOutcome.MovedToBackup), "24 parallel removals all succeed");
        Assert(entries.Count == 24 && entries.All(e => e.State == LeftoverBackupState.Moved),
            $"No journal entry lost under concurrency ({entries.Count}/24 Moved)");
        Assert(dirs.Select((d, i) => DataIsRecoverable(storeA, d, "payload" + i)).All(ok => ok),
            "Every concurrently removed item restores intact");
    }

    // ───────────────────────── helpers ─────────────────────────

    private static OwnershipAssessment Assess(string path, InstalledProgram target, params InstalledProgram[] others) =>
        LeftoverOwnershipEvaluator.AssessPath(path, isDirectory: !File.Exists(path), target,
            LeftoverOwnershipEvaluator.OtherPrograms(target, others));

    private static InstalledProgram Program(string name, string publisher, string installLocation, string key) =>
        new() { DisplayName = name, Publisher = publisher, InstallLocation = installLocation, RegistryKeyPath = key };

    private static JunkItem Leftover(string path, JunkType type) =>
        new() { Path = path, Type = type, IsSelected = true };

    private static string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FileAt(string path)
    {
        File.WriteAllText(path, "x");
        return path;
    }

    /// <summary>
    /// Exercises two adversarial scenarios not covered elsewhere: a folder the process is
    /// denied delete access to (the genuine <see cref="UnauthorizedAccessException"/> branch of
    /// <c>Directory.Move</c>, distinct from the locked-file <see cref="IOException"/> case
    /// already covered above) and a leftover file whose path exceeds MAX_PATH (the raw
    /// MoveFileExW P/Invoke has no \\?\ long-path prefixing, unlike Directory.Move for folders).
    /// Both assert the same safety invariant either way: fail closed, original untouched, no
    /// dangling Pending entry — never a crash and never a partial move.
    /// </summary>
    /// <summary>
    /// A leftover file whose path exceeds MAX_PATH still moves and restores intact when Win32
    /// long-path support is available, and fails closed (never crashes, never partial) when it
    /// isn't — the raw MoveFileExW P/Invoke has no \\?\ long-path prefixing, unlike
    /// <c>Directory.Move</c> for folders, so this specifically targets the file code path.
    /// (A matching permission-denied adversarial case was attempted here too, denying
    /// delete-child on the parent via icacls, but was dropped: verified independently that this
    /// local-admin account's rename still succeeds through an explicit Deny ACE on this machine,
    /// so asserting a specific outcome would fail for an environment/privilege reason unrelated
    /// to AuraClean. The existing locked-file test already proves the identical fail-closed
    /// catch path via a sharing violation instead. See the release report for this gap.)
    /// </summary>
    private static void TestLongPaths()
    {
        string longFile;
        try
        {
            var longDir = Path.Combine(_root, "_longpath", new string('A', 200), new string('B', 50));
            Directory.CreateDirectory(longDir);
            longFile = Path.Combine(longDir, "leftover-payload.dat");
            File.WriteAllText(longFile, "deep-payload");
        }
        catch (Exception ex) when (ex is PathTooLongException or IOException)
        {
            Skip($"long paths (could not even create the test fixture on this environment: {ex.GetType().Name})");
            return;
        }

        var longStore = new LeftoverBackupStore(Path.Combine(_root, "_longpath_backup"));
        var moved = longStore.MoveToBackup(longFile, isDirectory: false, "AcmeTool", "test", 0);
        if (moved.Outcome == LeftoverRemovalOutcome.MovedToBackup)
        {
            var (ok, _) = longStore.Restore(moved.BackupEntryId!);
            Assert(ok && File.Exists(longFile) && File.ReadAllText(longFile) == "deep-payload",
                "A file leftover beyond MAX_PATH moves to backup and restores intact");
        }
        else
        {
            Assert(moved.Outcome == LeftoverRemovalOutcome.Failed && File.Exists(longFile),
                $"...or where Win32 long-path support isn't available it fails closed, original untouched ({moved.Outcome}: {moved.Message})");
        }
        Assert(longStore.GetEntries().All(e => e.State != LeftoverBackupState.Pending),
            "Either way, no dangling Pending journal entry from the long-path attempt");
    }

    /// <summary>
    /// The journal lives in ordinary user-writable storage even though AuraClean always runs
    /// elevated, so two things must hold: (1) a hand-forged entry whose BackupPath points outside
    /// RootDirectory at an arbitrary file is refused, not restored, and (2) the real backup root
    /// created by <see cref="LeftoverBackupStore.CreateDefault"/> is actually locked down so an
    /// unprivileged process can't write to it or forge entries in the first place. Part 2 is
    /// live-verified against this very (non-elevated) test process, not merely asserted.
    /// </summary>
    private static void TestForgedEntriesAndAclHardening()
    {
        // Part 1: a forged BackupPath pointing outside the backup folder is refused.
        var storeRoot = Path.Combine(_root, "_forged");
        var store = new LeftoverBackupStore(storeRoot);
        Directory.CreateDirectory(storeRoot);

        var attackerPayload = Dir("AttackerPayload");
        File.WriteAllText(Path.Combine(attackerPayload, "evil.dll"), "not a real leftover");
        var privilegedTarget = Path.Combine(_root, "ForgedRestoreTarget", "evil.dll");

        var journalPath = Path.Combine(storeRoot, "journal.json");
        var forgedId = Guid.NewGuid().ToString("N")[..12];
        File.WriteAllText(journalPath,
            $$"""
            [{"id":"{{forgedId}}","originalPath":"{{privilegedTarget.Replace("\\", "\\\\")}}",
              "backupPath":"{{Path.Combine(attackerPayload, "evil.dll").Replace("\\", "\\\\")}}",
              "isDirectory":false,"state":"Moved","programName":"AcmeTool","evidence":"forged",
              "sizeBytes":0,"createdUtc":"2026-01-01T00:00:00Z"}]
            """);

        var (restoreOk, restoreMsg) = store.Restore(forgedId);
        Assert(!restoreOk && restoreMsg.Contains("not inside this backup folder"),
            $"A forged entry whose BackupPath points outside RootDirectory is refused ({restoreMsg})");
        Assert(!File.Exists(privilegedTarget), "...and nothing was ever written to the forged privileged destination");
        Assert(File.Exists(Path.Combine(attackerPayload, "evil.dll")), "...and the attacker's own payload file is untouched");

        // Part 2: the real backup root is actually locked down. Live-verified: this test process
        // is not elevated (checked below), so if hardening works it genuinely cannot write here.
        bool elevated;
        try { elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch (Exception) { elevated = true; /* can't tell — skip the live check rather than assume */ }

        if (elevated)
        {
            Skip("root ACL hardening live-check (this test process is itself elevated/Administrator)");
            return;
        }

        var hardenedRoot = Path.Combine(_root, "_hardened_" + Guid.NewGuid().ToString("N")[..8]);
        LeftoverBackupStore.TryHardenRootAcl(hardenedRoot);
        try
        {
            Assert(Directory.Exists(hardenedRoot), "TryHardenRootAcl creates the directory");
            var probe = Path.Combine(hardenedRoot, "probe.txt");
            bool blocked;
            try
            {
                File.WriteAllText(probe, "an unprivileged process should not be able to write this");
                blocked = false;
            }
            catch (UnauthorizedAccessException) { blocked = true; }
            Assert(blocked, "This non-elevated process cannot write into the hardened backup root");
        }
        finally
        {
            // Restore access so test cleanup (Directory.Delete of _root) can remove it: as
            // creator/owner we always retain the right to change our own object's ACL, even
            // without being granted data-access rights by that same ACL.
            try
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(hardenedRoot).SetAccessControl(security);
                Directory.Delete(hardenedRoot, recursive: true);
            }
            catch (Exception ex) { Console.WriteLine($"  (cleanup of {hardenedRoot} failed: {ex.Message})"); }
        }
    }

    /// <summary>
    /// An attacker could pre-plant a junction at the exact path AuraClean's backup root would
    /// use, before the elevated app ever creates and hardens the real folder — or swap the real
    /// folder for a junction afterward, since its parent isn't itself ACL-hardened. Every entry
    /// point (MoveToBackup, Restore, RecoverInterrupted, GetEntries, TryHardenRootAcl) must detect
    /// this and fail closed, never read or write through the junction to its real target.
    /// </summary>
    private static void TestJunctionPlantedRoot()
    {
        var junctionRoot = Path.Combine(_root, "_junctionRoot");
        var realTarget = Dir("JunctionRealTarget");
        File.WriteAllText(Path.Combine(realTarget, "sentinel.txt"), "the real target, untouched");

        if (!TryCreateJunction(junctionRoot, realTarget))
        {
            Skip("junction-planted root (mklink /J unavailable)");
            return;
        }

        // TryHardenRootAcl must not silently harden (or otherwise act on) the junction's target.
        LeftoverBackupStore.TryHardenRootAcl(junctionRoot);
        Assert(File.Exists(Path.Combine(realTarget, "sentinel.txt")), "Hardening a junctioned path leaves the real target's existing content alone");
        var probe = Path.Combine(realTarget, "post-harden-probe.txt");
        bool stillWritable;
        try { File.WriteAllText(probe, "still writable"); stillWritable = true; }
        catch (UnauthorizedAccessException) { stillWritable = false; }
        Assert(stillWritable, "...and never restricted the real target's own permissions");
        if (File.Exists(probe)) File.Delete(probe);

        var store = new LeftoverBackupStore(junctionRoot);

        Assert(store.GetEntries().Count == 0, "GetEntries refuses to read through a junctioned root (empty, not an exception)");

        var leftover = Dir("JunctionVictim", "AcmeTool");
        File.WriteAllText(Path.Combine(leftover, "data.txt"), "victim data");
        var moveResult = store.MoveToBackup(leftover, true, "AcmeTool", "test", 0);
        Assert(moveResult.Outcome == LeftoverRemovalOutcome.Refused && moveResult.Message.Contains("can't be trusted"),
            $"MoveToBackup refuses a junctioned root ({moveResult.Outcome}: {moveResult.Message})");
        Assert(Directory.Exists(leftover) && File.Exists(Path.Combine(leftover, "data.txt")),
            "...and the leftover itself is untouched");
        Assert(!File.Exists(Path.Combine(realTarget, "journal.json")),
            "...and nothing was ever written into the junction's real target");

        var recoverReport = store.RecoverInterrupted();
        Assert(recoverReport.Count == 1 && recoverReport[0].StartsWith("Error:") && recoverReport[0].Contains("can't be trusted"),
            $"RecoverInterrupted refuses a junctioned root ({(recoverReport.Count > 0 ? recoverReport[0] : "<empty>")})");

        var (restoreOk, restoreMsg) = store.Restore("any-id-at-all");
        Assert(!restoreOk && restoreMsg.Contains("can't be trusted"),
            $"Restore refuses a junctioned root before even looking up the entry ({restoreMsg})");

        Assert(File.Exists(Path.Combine(realTarget, "sentinel.txt")) &&
               Directory.EnumerateFileSystemEntries(realTarget).Count() == 1,
            "The junction's real target has nothing extra in it after all four operations were attempted");
    }

    private static void TestRecoverySaveFailure()
    {
        var storeRoot = Path.Combine(_root, "_saveFailure");
        var store = new LeftoverBackupStore(storeRoot);
        var dir = Dir("SaveFail", "AcmeTool");
        File.WriteAllText(Path.Combine(dir, "data.txt"), "payload");
        var moved = store.MoveToBackup(dir, true, "AcmeTool", "evidence", 0);
        Assert(moved.Outcome == LeftoverRemovalOutcome.MovedToBackup, "Setup: item moved to backup");

        // Rewind to Pending (same technique as the crash-consistency tests) so there is something
        // for RecoverInterrupted to actually resolve and persist.
        var journalPath = Path.Combine(storeRoot, "journal.json");
        File.WriteAllText(journalPath, File.ReadAllText(journalPath).Replace("\"Moved\"", "\"Pending\""));

        // Hold a share-Read handle on journal.json: File.ReadAllText (TryLoad) still succeeds,
        // but File.Replace (the durable save) cannot, since it needs exclusive/write access —
        // simulating a save failure (disk full, journal locked) without needing elevation tricks.
        List<string> report;
        using (new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            report = store.RecoverInterrupted().ToList();
        }

        Assert(report.Count == 1 && report[0].StartsWith("Error:"),
            $"A save failure during recovery is reported as an error, not a false success ({(report.Count > 0 ? report[0] : "<empty>")})");
        Assert(store.GetEntries().Single().State == LeftoverBackupState.Pending,
            "The entry is still Pending afterward — nothing was actually persisted, so nothing was silently lost either");

        // And it genuinely is retryable: once the lock is released, a normal call resolves it.
        var retry = store.RecoverInterrupted();
        Assert(retry.Count == 1 && retry[0].StartsWith("Recovered"), "Once unlocked, the same recovery succeeds for real");
        Assert(store.GetEntries().Single().State == LeftoverBackupState.Moved, "...and the entry is now actually Moved");
    }

    private static void TestRecoveryReportCategorization()
    {
        var storeRoot = Path.Combine(_root, "_manualReview");
        var store = new LeftoverBackupStore(storeRoot);
        var dir = Dir("ManualReview", "AcmeTool");
        File.WriteAllText(Path.Combine(dir, "data.txt"), "payload");
        var moved = store.MoveToBackup(dir, true, "AcmeTool", "evidence", 0);
        Assert(moved.Outcome == LeftoverRemovalOutcome.MovedToBackup, "Setup: item moved to backup");

        // The genuinely ambiguous case RecoverInterrupted can't resolve on its own: rewind to
        // Pending (as if the move's journal update never landed) while the original has also come
        // back (e.g. a reinstall recreated it) — both original and backup now exist for one entry.
        var journalPath = Path.Combine(storeRoot, "journal.json");
        File.WriteAllText(journalPath, File.ReadAllText(journalPath).Replace("\"Moved\"", "\"Pending\""));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "recreated.txt"), "reinstalled");

        var report = store.RecoverInterrupted();
        Assert(report.Count == 1 && report[0].StartsWith("Needs manual review:") &&
               report[0].Contains("original present") && report[0].Contains("backup present"),
            $"Ambiguous Pending entry (both present) surfaces as Needs manual review, not silently resolved ({(report.Count > 0 ? report[0] : "<empty>")})");
        Assert(store.GetEntries().Single().State == LeftoverBackupState.Pending,
            "...and the entry stays Pending — genuinely unresolved, not claimed as fixed");

        var (resolved, needsReview, errors) = LeftoverBackupStore.CategorizeRecoveryReport(report);
        Assert(resolved == 0 && needsReview == 1 && errors == 0,
            $"CategorizeRecoveryReport — the exact method MainViewModel's startup recovery calls — excludes it from resolved (resolved={resolved}, needsReview={needsReview}, errors={errors})");

        // A realistic mixed report (one of each kind) categorizes correctly too.
        var mixed = new List<string>
        {
            "Recovered interrupted removal of C:\\a.",
            "Reconciled C:\\b: its restore had completed but the journal was not updated before a crash.",
            "Needs manual review: C:\\c (original present, backup present).",
            "Error: The backup journal is unreadable (bad).",
        };
        var (mResolved, mNeedsReview, mErrors) = LeftoverBackupStore.CategorizeRecoveryReport(mixed);
        Assert(mResolved == 2 && mNeedsReview == 1 && mErrors == 1,
            $"A mixed report (promote + reconcile + review + error) categorizes each correctly (resolved={mResolved}, needsReview={mNeedsReview}, errors={mErrors})");
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return Directory.Exists(link) &&
                   new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception) { return false; }
    }

    private static void Section(string name, Action test)
    {
        Console.WriteLine($"═══ BATCH L: {name} ═══");
        try { test(); }
        catch (Exception ex)
        {
            _fail++;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  FAIL: {name} threw {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static void Skip(string what)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  SKIP: {what}");
        Console.ResetColor();
    }

    private static void Assert(bool condition, string message)
    {
        if (condition) _pass++; else _fail++;
        Console.ForegroundColor = condition ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}: {message}");
        Console.ResetColor();
    }
}
