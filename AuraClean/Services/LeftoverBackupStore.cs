using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuraClean.Helpers;

namespace AuraClean.Services;

public enum LeftoverBackupState { Pending, Moved, Restored }

/// <summary>One journaled leftover removal. Dates are UTC, serialized ISO-8601.</summary>
public sealed class LeftoverBackupEntry
{
    public string Id { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public LeftoverBackupState State { get; set; }
    public string ProgramName { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public enum LeftoverRemovalOutcome { MovedToBackup, Missing, Refused, Failed }

public sealed record LeftoverRemovalResult(
    string Path, LeftoverRemovalOutcome Outcome, string Message, string? BackupEntryId = null,
    AuraClean.Models.OwnershipAssessment? RefusedOwnership = null);

/// <summary>
/// Recoverable removal for uninstall leftovers. Each item is renamed (never copied, never
/// deleted) into a per-operation folder under <see cref="RootDirectory"/>, which must be on the
/// same volume: a same-volume rename is atomic, so a folder with a locked file is either moved
/// whole or not at all. Write-ahead journal: an entry is saved as Pending before the move and
/// as Moved after it, so a crash between the two is resolved by <see cref="RecoverInterrupted"/>.
/// Cross-volume items, an unreadable journal, or any doubt fail closed (nothing is removed).
/// Space is only freed when the backup is purged.
/// </summary>
public sealed class LeftoverBackupStore
{
    private static readonly object JournalLock = new();

    /// <summary>Serializes journal access across AuraClean processes for this user session.</summary>
    private const string JournalMutexName = @"Local\AuraClean.LeftoverBackupJournal";
    private static readonly TimeSpan JournalMutexTimeout = TimeSpan.FromSeconds(30);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existing, string target, uint flags);

    /// <summary>
    /// Same-volume file rename only. File.Move may silently fall back to copy+delete across
    /// volumes (e.g. a mount point under the same drive letter); flags=0 forbids that.
    /// </summary>
    private static void MoveFileNoCopy(string source, string destination)
    {
        if (!MoveFileExW(source, destination, 0))
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    /// <summary>Runs <paramref name="body"/> under the in-process lock and the cross-process mutex.</summary>
    private static T WithJournalLock<T>(Func<T> body, Func<T> onTimeout)
    {
        lock (JournalLock)
        {
            using var mutex = new Mutex(false, JournalMutexName);
            bool acquired;
            try { acquired = mutex.WaitOne(JournalMutexTimeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                return onTimeout();
            try { return body(); }
            finally { mutex.ReleaseMutex(); }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public LeftoverBackupStore(string rootDirectory)
    {
        RootDirectory = PathSafety.Normalize(rootDirectory)
            ?? throw new ArgumentException("Backup root must be a fully qualified path.", nameof(rootDirectory));
    }

    public static LeftoverBackupStore CreateDefault()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuraClean", "LeftoverBackup");
        TryHardenRootAcl(root);
        return new(root);
    }

    /// <summary>
    /// Best-effort: restricts the real backup root to Administrators + SYSTEM. AuraClean's
    /// manifest always requires elevation, but without this the journal and backup payload
    /// otherwise sit in ordinary, unelevated-writable LocalAppData — letting an unprivileged
    /// process on the same account forge a journal entry (see <see cref="Restore"/>'s
    /// BackupPath check) or plant a payload for one. Only called from <see cref="CreateDefault"/>;
    /// a custom root (tests) never gets this, so non-elevated test runs are unaffected. Failure
    /// here is logged and swallowed — it never blocks the feature, and the BackupPath check in
    /// <see cref="Restore"/> is independent defense-in-depth either way.
    /// </summary>
    /// <summary>
    /// True when <paramref name="path"/> itself, or any ancestor, is a reparse point (junction or
    /// symbolic link) — or could not be inspected, which fails closed the same way. An attacker
    /// could pre-plant a junction at this exact path before AuraClean's elevated process ever
    /// creates the real folder (Directory.CreateDirectory on an existing reparse point is a
    /// silent no-op), or swap the real folder for one afterward: the parent (e.g.
    /// %LocalAppData%\AuraClean) is not itself ACL-hardened, and by default grants the owning
    /// user delete rights over its children regardless of a child's own restrictive ACL. Checked
    /// before hardening and at the start of every journal operation, so a tampered root always
    /// fails closed instead of being silently followed.
    /// </summary>
    private static bool IsReparseOrTampered(string path, out string reason)
    {
        if (Directory.Exists(path))
        {
            try
            {
                if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    reason = $"'{path}' is a junction or symbolic link, not a real folder.";
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reason = $"'{path}' could not be inspected: {ex.Message}";
                return true;
            }
        }
        if (LeftoverRemovalService.HasReparseAncestor(path, out var link))
        {
            reason = $"A parent folder ({link}) is a junction or link.";
            return true;
        }
        reason = string.Empty;
        return false;
    }

    internal static void TryHardenRootAcl(string root)
    {
        if (IsReparseOrTampered(root, out var tamperReason))
        {
            DiagnosticLogger.Warn("LeftoverBackupStore",
                $"Refusing to create or harden the backup folder: {tamperReason}");
            return;
        }

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        try
        {
            Directory.CreateDirectory(root);
            var info = new DirectoryInfo(root);

            // The DACL restriction is the actual security property and must be applied on its
            // own: setting it together with a new owner in one call fails the whole operation
            // (ERROR_INVALID_OWNER, 1307 — verified) whenever the caller's token can't take
            // ownership of the target SID, silently leaving the folder unrestricted. Modifying
            // only the DACL always succeeds for whoever owns the folder (the creator here),
            // regardless of elevation — a basic, always-available NTFS right.
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            DiagnosticLogger.Warn("LeftoverBackupStore",
                "Could not restrict the backup folder's permissions to Administrators; continuing without it.", ex);
            return;
        }

        // Best-effort only, separate from the DACL above: reassigning ownership to Administrators
        // requires the caller's own token to already have that SID enabled (true for AuraClean's
        // always-elevated process) or SeRestorePrivilege. When it isn't available the DACL
        // restriction set above still holds either way, so this failing changes nothing load-bearing.
        try
        {
            var info = new DirectoryInfo(root);
            var security = info.GetAccessControl();
            security.SetOwner(admins);
            info.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SystemException)
        {
            DiagnosticLogger.Warn("LeftoverBackupStore",
                "Could not reassign the backup folder's owner to Administrators; its access is still restricted.", ex);
        }
    }

    public string RootDirectory { get; }

    private string JournalPath => Path.Combine(RootDirectory, "journal.json");

    /// <summary>The previous journal version. Every transition is write-ahead (Pending is saved
    /// before the move, the move happens before Moved/Restored is saved), so the previous
    /// version is always a safe state to recover from if the current one is torn or missing.</summary>
    private string PreviousJournalPath => Path.Combine(RootDirectory, "journal.prev.json");

    /// <summary>
    /// Test-only crash injection. Invoked with a named point; a test throws an exception no
    /// production catch filter handles, which stops the operation exactly like process death.
    /// </summary>
    internal Action<string>? CrashPoint { get; init; }

    public IReadOnlyList<LeftoverBackupEntry> GetEntries() =>
        WithJournalLock<IReadOnlyList<LeftoverBackupEntry>>(() =>
        {
            if (IsReparseOrTampered(RootDirectory, out var tamperReason))
            {
                DiagnosticLogger.Warn("LeftoverBackupStore", $"Refusing to list backup entries: {tamperReason}");
                return [];
            }
            return TryLoad(out var entries, out _) ? entries : [];
        }, () => []);

    /// <summary>
    /// Moves an already-validated path into the backup. The caller is responsible for ownership
    /// and path-safety validation immediately before calling (see <see cref="LeftoverRemovalService"/>).
    /// </summary>
    public LeftoverRemovalResult MoveToBackup(
        string path, bool isDirectory, string programName, string evidence, long sizeBytes)
    {
        var source = PathSafety.Normalize(path);
        if (source == null)
            return new(path, LeftoverRemovalOutcome.Refused, "The path is not fully qualified.");

        if (PathSafety.IsSameOrUnder(source, RootDirectory) || PathSafety.IsSameOrUnder(RootDirectory, source))
            return new(source, LeftoverRemovalOutcome.Refused, "It overlaps the leftover backup folder.");

        if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(RootDirectory), StringComparison.OrdinalIgnoreCase))
            return new(source, LeftoverRemovalOutcome.Refused,
                "It is on a different drive than the backup folder, so it cannot be removed recoverably. Nothing was removed.");

        return WithJournalLock(() => MoveLocked(source, isDirectory, programName, evidence, sizeBytes),
            () => new LeftoverRemovalResult(source, LeftoverRemovalOutcome.Refused,
                "The backup journal is busy in another AuraClean window. Nothing was removed."));
    }

    private LeftoverRemovalResult MoveLocked(
        string source, bool isDirectory, string programName, string evidence, long sizeBytes)
    {
        {
            if (IsReparseOrTampered(RootDirectory, out var tamperReason))
                return new(source, LeftoverRemovalOutcome.Refused, $"The backup folder itself can't be trusted ({tamperReason}). Nothing was removed.");

            if (!TryLoad(out var entries, out var loadError))
                return new(source, LeftoverRemovalOutcome.Refused, $"The backup journal is unreadable ({loadError}). Nothing was removed.");

            var id = Guid.NewGuid().ToString("N")[..12];
            var container = Path.Combine(RootDirectory, id);
            var entry = new LeftoverBackupEntry
            {
                Id = id,
                OriginalPath = source,
                BackupPath = Path.Combine(container, Path.GetFileName(source)),
                IsDirectory = isDirectory,
                State = LeftoverBackupState.Pending,
                ProgramName = programName,
                Evidence = evidence,
                SizeBytes = sizeBytes,
                CreatedUtc = DateTime.UtcNow
            };

            try
            {
                Directory.CreateDirectory(container);
                entries.Add(entry);
                Save(entries);
                CrashPoint?.Invoke("move:after-pending-saved");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeleteEmptyDirectory(container);
                return new(source, LeftoverRemovalOutcome.Failed, $"Could not prepare the backup: {ex.Message} Nothing was removed.");
            }

            try
            {
                if (isDirectory)
                    Directory.Move(source, entry.BackupPath);
                else
                    MoveFileNoCopy(source, entry.BackupPath);
                CrashPoint?.Invoke("move:after-move");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                entries.Remove(entry);
                TrySave(entries);
                TryDeleteEmptyDirectory(container);
                return new(source, LeftoverRemovalOutcome.Failed,
                    $"It is in use or access was denied, so nothing was removed: {ex.Message}");
            }

            entry.State = LeftoverBackupState.Moved;
            if (!TrySave(entries))
            {
                // The Pending record already points at the backup, so the item stays findable
                // and RecoverInterrupted() will promote it to Moved.
                DiagnosticLogger.Warn("LeftoverBackupStore", $"Moved {source} but could not mark it Moved; left Pending.");
            }

            return new(source, LeftoverRemovalOutcome.MovedToBackup, "Moved to the leftover backup; it can be restored.", id);
        }
    }

    /// <summary>Moves a backed-up item back to its original path. Never overwrites.</summary>
    public (bool Success, string Message) Restore(string entryId) =>
        WithJournalLock(() => RestoreLocked(entryId), () => (false, "The backup journal is busy in another AuraClean window."));

    private (bool Success, string Message) RestoreLocked(string entryId)
    {
        {
            if (IsReparseOrTampered(RootDirectory, out var tamperReason))
                return (false, $"The backup folder itself can't be trusted ({tamperReason}).");

            if (!TryLoad(out var entries, out var loadError))
                return (false, $"The backup journal is unreadable ({loadError}).");

            var entry = entries.FirstOrDefault(e => e.Id == entryId);
            if (entry == null)
                return (false, "No such backup entry.");

            // The journal lives in ordinary user-writable storage even though AuraClean always
            // runs elevated, so an unprivileged process on the same account could otherwise forge
            // an entry whose BackupPath points anywhere on disk and whose OriginalPath is a
            // privileged destination, turning this elevated restore into an arbitrary-file-write.
            // Every legitimate entry's BackupPath is generated by MoveToBackup() under
            // RootDirectory, so this is a pure sanity check for real entries. Checked before the
            // State check below too, since a forged BackupPath pointing at an attacker file that
            // happens to exist could otherwise satisfy pendingButMoved regardless of claimed State.
            if (PathSafety.Normalize(entry.BackupPath) is not { } backupPath || !PathSafety.IsSameOrUnder(backupPath, RootDirectory))
                return (false, "The backup path is not inside this backup folder; refusing to restore from it.");

            // Pending with an intact backup and no original means the move completed but was
            // never marked Moved (crash or failed journal save): still restorable.
            bool pendingButMoved = entry.State == LeftoverBackupState.Pending &&
                                   Exists(entry.BackupPath, entry.IsDirectory) &&
                                   !File.Exists(entry.OriginalPath) && !Directory.Exists(entry.OriginalPath);
            if (entry.State != LeftoverBackupState.Moved && !pendingButMoved)
                return (false, $"The entry is {entry.State}, not restorable.");
            if (PathSafety.Normalize(entry.OriginalPath) is not { } target ||
                PathSafety.IsProtectedRoot(target) || PathSafety.IsWithinWindowsDirectory(target))
                return (false, "The original location is not a safe restore target.");
            if (LeftoverRemovalService.HasReparseAncestor(target, out var link))
                return (false, $"A parent folder ({link}) is now a junction or link; restore refused.");
            // Defense in depth: RootDirectory's own hardened ACL should already prevent an
            // unprivileged process from planting a junction inside it, but this is cheap and
            // catches it regardless of how it got there.
            if (LeftoverRemovalService.HasReparseAncestor(backupPath, out var backupLink))
                return (false, $"A parent folder ({backupLink}) of the backup copy is a junction or link; restore refused.");
            if (OccupiesOriginalPath(entry))
                return (false, "Something already exists at the original location; restore refused to avoid overwriting it.");
            if (!Exists(entry.BackupPath, entry.IsDirectory))
                return (false, "The backup copy is missing.");

            try
            {
                var parent = Path.GetDirectoryName(entry.OriginalPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                if (entry.IsDirectory)
                    Directory.Move(entry.BackupPath, entry.OriginalPath);
                else
                    MoveFileNoCopy(entry.BackupPath, entry.OriginalPath);
                CrashPoint?.Invoke("restore:after-move");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return (false, $"Restore failed: {ex.Message}");
            }

            entry.State = LeftoverBackupState.Restored;
            TrySave(entries);
            TryDeleteEmptyDirectory(Path.GetDirectoryName(entry.BackupPath));
            return (true, $"Restored {entry.OriginalPath}.");
        }
    }

    /// <summary>
    /// Resolves entries left Pending by a crash (promotes them to Moved when the backup exists
    /// and the original is gone, drops them when the move never happened, reports conflicts) and
    /// reconciles stale Moved entries whose restore actually completed before a crash prevented
    /// the journal from recording it. Every returned message describes something that was
    /// resolved or needs attention; a message starting with "Error:" means the check itself
    /// could not run (journal unreadable or busy) and nothing was resolved.
    /// </summary>
    public IReadOnlyList<string> RecoverInterrupted() =>
        WithJournalLock<IReadOnlyList<string>>(RecoverLocked, () => ["Error: The backup journal is busy in another AuraClean window."]);

    /// <summary>
    /// Splits a <see cref="RecoverInterrupted"/> report by its documented message-prefix
    /// contract: "Error:" means the check itself couldn't run (nothing resolved); "Needs manual
    /// review:" means it ran fine but found an entry neither cleanly promotable nor droppable
    /// (both original and backup present, or both missing), left Pending either way; anything
    /// else is a genuine resolution (promoted, dropped, or reconciled). The single place that
    /// owns this contract, so callers (e.g. MainViewModel's startup recovery) never have to
    /// duplicate the prefix matching and risk it drifting out of sync.
    /// </summary>
    public static (int Resolved, int NeedsReview, int Errors) CategorizeRecoveryReport(IReadOnlyList<string> report)
    {
        var errors = report.Count(m => m.StartsWith("Error:"));
        var needsReview = report.Count(m => m.StartsWith("Needs manual review:"));
        return (report.Count - errors - needsReview, needsReview, errors);
    }

    private IReadOnlyList<string> RecoverLocked()
    {
        var report = new List<string>();
        {
            if (IsReparseOrTampered(RootDirectory, out var tamperReason))
                return [$"Error: The backup folder itself can't be trusted ({tamperReason})."];

            if (!TryLoad(out var entries, out var loadError))
                return [$"Error: The backup journal is unreadable ({loadError})."];

            foreach (var entry in entries.Where(e => e.State == LeftoverBackupState.Pending).ToList())
            {
                bool backupExists = Exists(entry.BackupPath, entry.IsDirectory);
                bool originalExists = Exists(entry.OriginalPath, entry.IsDirectory);

                if (backupExists && !originalExists)
                {
                    entry.State = LeftoverBackupState.Moved;
                    report.Add($"Recovered interrupted removal of {entry.OriginalPath}.");
                }
                else if (!backupExists && originalExists)
                {
                    entries.Remove(entry);
                    TryDeleteEmptyDirectory(Path.GetDirectoryName(entry.BackupPath));
                    report.Add($"Interrupted removal of {entry.OriginalPath} never started; it is untouched.");
                }
                else
                {
                    report.Add($"Needs manual review: {entry.OriginalPath} (original {(originalExists ? "present" : "missing")}, " +
                               $"backup {(backupExists ? "present" : "missing")}).");
                }
            }

            // A Moved entry whose backup is gone but whose original path is occupied again means
            // Restore() actually completed (it moves backup -> original) but crashed before the
            // journal recorded State=Restored. This never moves data — Restore() already refuses
            // to touch an entry whose backup is missing — it only corrects bookkeeping once the
            // filesystem shows the restore already happened, so a stale entry stops being shown
            // as a false "needs review" conflict forever.
            foreach (var entry in entries.Where(e => e.State == LeftoverBackupState.Moved).ToList())
            {
                bool backupExists = Exists(entry.BackupPath, entry.IsDirectory);
                bool originalExists = Exists(entry.OriginalPath, entry.IsDirectory);
                if (!backupExists && originalExists)
                {
                    entry.State = LeftoverBackupState.Restored;
                    report.Add($"Reconciled {entry.OriginalPath}: its restore had completed but the journal was not updated before a crash.");
                }
            }

            // A save failure here (disk full, journal unwritable) must not be reported as success:
            // the entries above were only mutated in memory, so if the save doesn't stick, the
            // next load sees the original Pending/stale-Moved state and must retry — a caller
            // that recorded these as resolved (history, status bar) would otherwise lie.
            if (report.Count == 0)
            {
                TrySave(entries);
                return report;
            }
            if (!TrySave(entries))
                return [$"Error: Found {report.Count} issue(s) to resolve, but could not save the journal " +
                        "(disk full or inaccessible?). Nothing changed; it will be retried."];
        }
        return report;
    }

    private static bool Exists(string path, bool isDirectory) =>
        isDirectory ? Directory.Exists(path) : File.Exists(path);

    /// <summary>
    /// True when something already exists at <paramref name="entry"/>'s original path, i.e.
    /// restoring it would overwrite that path. Exposed (read-only, no side effects) so a restore
    /// list can show the conflict before the user tries to restore; <see cref="Restore"/> is the
    /// only method that ever acts on it.
    /// </summary>
    public static bool OccupiesOriginalPath(LeftoverBackupEntry entry) =>
        File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath);

    private bool TryLoad(out List<LeftoverBackupEntry> entries, out string error)
    {
        entries = [];
        error = string.Empty;
        // Current journal first; if it is missing or torn (e.g. power loss mid-replace), fall
        // back to the previous version. Fail closed only when neither is readable.
        if (TryRead(JournalPath, out entries, out error))
            return true;

        var primaryError = error;
        if (TryRead(PreviousJournalPath, out entries, out error))
        {
            DiagnosticLogger.Warn("LeftoverBackupStore",
                $"Journal unreadable or missing ({primaryError}); recovered from the previous version.");
            return true;
        }

        if (!File.Exists(JournalPath) && !File.Exists(PreviousJournalPath))
        {
            entries = [];
            error = string.Empty;
            return true;   // no journal yet
        }

        error = File.Exists(JournalPath) ? primaryError : error;
        return false;
    }

    /// <summary>Reads one journal file. A missing file counts as unreadable here.</summary>
    private static bool TryRead(string path, out List<LeftoverBackupEntry> entries, out string error)
    {
        entries = [];
        error = string.Empty;
        try
        {
            if (!File.Exists(path))
            {
                error = "missing";
                return false;
            }
            var parsed = JsonSerializer.Deserialize<List<LeftoverBackupEntry>>(File.ReadAllText(path), JsonOptions);
            if (parsed == null || parsed.Any(e => e == null || string.IsNullOrEmpty(e.Id)))
            {
                error = "malformed entries";
                return false;
            }
            entries = parsed;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            DiagnosticLogger.Warn("LeftoverBackupStore", $"Journal file unreadable: {path}", ex);
            return false;
        }
    }

    /// <summary>
    /// Durable replace: write and flush a temp file to disk, then swap it in with ReplaceFile,
    /// which keeps the prior journal as <see cref="PreviousJournalPath"/>. Without the flush a
    /// power loss can leave a renamed but empty journal.
    /// </summary>
    private void Save(List<LeftoverBackupEntry> entries)
    {
        Directory.CreateDirectory(RootDirectory);
        var temp = $"{JournalPath}.{Guid.NewGuid():N}.tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
        using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }

        if (TryRead(JournalPath, out _, out _))
            File.Replace(temp, JournalPath, PreviousJournalPath, ignoreMetadataErrors: true);
        else
            File.Move(temp, JournalPath, overwrite: true);   // never rotate a torn journal into the fallback slot
    }

    private bool TrySave(List<LeftoverBackupEntry> entries)
    {
        try
        {
            Save(entries);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("LeftoverBackupStore", "Journal save failed", ex);
            return false;
        }
    }

    private static void TryDeleteEmptyDirectory(string? dir)
    {
        try
        {
            if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("LeftoverBackupStore", $"Could not remove empty backup folder {dir}", ex);
        }
    }
}
