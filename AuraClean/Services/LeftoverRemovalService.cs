using System.IO;
using AuraClean.Helpers;
using AuraClean.Models;

namespace AuraClean.Services;

/// <summary>
/// Executes a reviewed uninstall-leftover plan for files and folders. Each item is
/// independently re-authorized at execution time (the scan may be minutes old): it must still
/// exist with the same kind, not be user-excluded, re-pass the ownership evaluator against the
/// current installed-program list, and have no junction/symlink anywhere in its ancestry.
/// Only then is it moved into the recoverable <see cref="LeftoverBackupStore"/>; nothing is
/// permanently deleted here. Registry leftovers are not handled (they keep the existing
/// backup-then-delete path).
/// </summary>
public static class LeftoverRemovalService
{
    public static async Task<IReadOnlyList<LeftoverRemovalResult>> RemoveAsync(
        IEnumerable<JunkItem> items,
        InstalledProgram target,
        IEnumerable<InstalledProgram> installedPrograms,
        LeftoverBackupStore store,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var list = items.ToList();
        var others = LeftoverOwnershipEvaluator.OtherPrograms(target, installedPrograms);
        foreach (var item in list)
        {
            item.IsLocked = false;
            item.LockingProcess = string.Empty;
        }

        // Work happens off the UI thread; bound item properties are only written after the
        // await, on the caller's (UI) context.
        var results = await Task.Run(() =>
        {
            var batch = new List<LeftoverRemovalResult>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Removing leftover ({i + 1}/{list.Count}): {Path.GetFileName(list[i].Path)}");
                LeftoverRemovalResult result;
                try
                {
                    result = RemoveOne(list[i], target, others, store);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    DiagnosticLogger.Error("LeftoverRemovalService", $"Unexpected failure removing {list[i].Path}", ex);
                    result = new(list[i].Path, LeftoverRemovalOutcome.Failed, $"Unexpected error; nothing was removed: {ex.Message}");
                }
                batch.Add(result);
                if (result.Outcome != LeftoverRemovalOutcome.MovedToBackup)
                    DiagnosticLogger.Info("LeftoverRemovalService", $"{result.Outcome}: {result.Path} — {result.Message}");
            }
            return batch;
        }, ct);

        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].RefusedOwnership is { } fresh)
                list[i].Ownership = fresh;
            if (results[i].Outcome != LeftoverRemovalOutcome.MovedToBackup)
            {
                list[i].IsLocked = true;
                list[i].LockingProcess = results[i].Message;
            }
        }
        return results;
    }

    internal static LeftoverRemovalResult RemoveOne(
        JunkItem item, InstalledProgram target, IReadOnlyList<InstalledProgram> others, LeftoverBackupStore store)
    {
        if (item.Type is not (JunkType.RemnantDirectory or JunkType.AbandonedFile))
            return new(item.Path, LeftoverRemovalOutcome.Refused, "Not a file or folder leftover.");
        if (!item.IsSelected)
            return new(item.Path, LeftoverRemovalOutcome.Refused, "Not selected.");

        var path = LongPathHelper.Canonicalize(item.Path);
        if (path == null)
            return new(item.Path, LeftoverRemovalOutcome.Refused, "The path is not fully qualified.");

        bool isDirectory = item.Type == JunkType.RemnantDirectory;
        bool dirExists = Directory.Exists(path), fileExists = File.Exists(path);
        if (!dirExists && !fileExists)
            return new(path, LeftoverRemovalOutcome.Missing, "It no longer exists.");
        if (isDirectory != dirExists)
            return new(path, LeftoverRemovalOutcome.Refused, "It changed between file and folder since the scan.");

        if (CleanerExcludeStore.IsExcluded(path))
            return new(path, LeftoverRemovalOutcome.Refused, "Excluded by user.");

        if (HasReparseAncestor(path, out var ancestor))
            return new(path, LeftoverRemovalOutcome.Refused,
                $"A parent folder ({ancestor}) is a junction or link, or could not be inspected.");

        var fresh = LeftoverOwnershipEvaluator.AssessPath(path, isDirectory, target, others);
        if (fresh.IsBlocked)
            return new(path, LeftoverRemovalOutcome.Refused, fresh.Summary, RefusedOwnership: fresh);

        return store.MoveToBackup(path, isDirectory, target.DisplayName, fresh.Summary, item.SizeBytes);
    }

    /// <summary>
    /// True when any ancestor of <paramref name="path"/> is a reparse point, or cannot be
    /// inspected (fail closed). The leaf itself is checked by PathSafety.
    /// </summary>
    internal static bool HasReparseAncestor(string path, out string ancestor)
    {
        var current = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                // Not created yet (restore target); nothing to redirect through.
                current = Path.GetDirectoryName(current);
                continue;
            }
            try
            {
                if (new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    ancestor = current;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ancestor = current;
                return true;
            }
            current = Path.GetDirectoryName(current);
        }

        ancestor = string.Empty;
        return false;
    }
}
