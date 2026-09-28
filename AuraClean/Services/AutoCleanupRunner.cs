using AuraClean.Helpers;

namespace AuraClean.Services;

/// <summary>
/// Headless cleanup executed by the scheduled task (<c>AuraClean.exe /autoclean</c>).
/// Unattended runs are restricted to the low-risk (Normal mode) categories regardless of the
/// interactive experience mode, so nothing reviewable — Recycle Bin, Windows.old, WinSxS,
/// abandoned folders — is ever removed without the user looking at it first.
/// </summary>
public static class AutoCleanupRunner
{
    public static async Task RunAsync(AppSettings settings, CancellationToken ct = default)
    {
        DiagnosticLogger.Info("AutoCleanup", "Starting scheduled cleanup...");

        var items = await FileCleanerService.AnalyzeSystemJunkAsync(ct: ct, includeReviewOnlyCategories: false);
        CleanupModePolicy.ApplyDefaultSelection(items, settings, isAdvancedMode: false);

        var selected = items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            DiagnosticLogger.Info("AutoCleanup", "No eligible junk found.");
            return;
        }

        var (deleted, skipped, bytesFreed, errors) = await FileCleanerService.CleanItemsAsync(
            selected, ct: ct, dryRun: settings.DryRunMode);

        var summary = settings.DryRunMode
            ? $"Dry run: would clean {deleted} item(s), freeing {FormatHelper.FormatBytes(bytesFreed)}."
            : $"Cleaned {deleted} item(s), freed {FormatHelper.FormatBytes(bytesFreed)}, skipped {skipped}.";
        DiagnosticLogger.Info("AutoCleanup", summary);

        foreach (var error in errors.Take(20))
            DiagnosticLogger.Warn("AutoCleanup", error);

        if (!settings.DryRunMode)
        {
            CleanupHistoryService.Record(CleanupOperationType.SystemClean, deleted, bytesFreed,
                $"Scheduled cleanup — {skipped} item(s) skipped");
            LastCleanedStore.Save(DateTime.Now);
        }
    }
}
