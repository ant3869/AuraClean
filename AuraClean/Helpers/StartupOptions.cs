namespace AuraClean.Helpers;

/// <summary>
/// Parsed command-line switches understood by AuraClean.
/// </summary>
public sealed record StartupOptions(bool AutoClean, bool StartMinimized, string? DeepUninstallTarget)
{
    public const string AutoCleanSwitch = "/autoclean";
    public const string MinimizedSwitch = "--minimized";
    public const string DeepUninstallSwitch = "--deep-uninstall";

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        bool autoClean = false, minimized = false;
        string? deepUninstall = null;

        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i]?.Trim() ?? string.Empty;

            if (arg.Equals(AutoCleanSwitch, StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--autoclean", StringComparison.OrdinalIgnoreCase))
            {
                autoClean = true;
            }
            else if (arg.Equals(MinimizedSwitch, StringComparison.OrdinalIgnoreCase) ||
                     arg.Equals("/minimized", StringComparison.OrdinalIgnoreCase))
            {
                minimized = true;
            }
            else if (arg.Equals(DeepUninstallSwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                var target = args[++i]?.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(target))
                    deepUninstall = target;
            }
        }

        return new StartupOptions(autoClean, minimized, deepUninstall);
    }
}
