using AuraClean.Helpers;
using System.Text.RegularExpressions;

namespace AuraClean.Services;

/// <summary>
/// Checks for outdated software using Windows Package Manager (winget).
/// Provides a list of programs that have available updates.
/// </summary>
public static class SoftwareUpdaterService
{
    public record OutdatedProgram
    {
        public string Name { get; init; } = string.Empty;
        public string Id { get; init; } = string.Empty;
        public string InstalledVersion { get; init; } = string.Empty;
        public string AvailableVersion { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
    }

    /// <summary>
    /// winget is an App Execution Alias in the user's WindowsApps folder. Resolving it by full
    /// path avoids launching a same-named executable planted next to AuraClean.
    /// </summary>
    private static string WingetExe
    {
        get
        {
            var alias = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WindowsApps\winget.exe");
            return System.IO.File.Exists(alias) ? alias : "winget.exe";
        }
    }

    private const string ModernFlags = " --accept-source-agreements --disable-interactivity";
    private const string LegacyFlags = " --accept-source-agreements";

    /// <summary>
    /// Runs winget, retrying without flags that older winget builds reject.
    /// </summary>
    private static async Task<ProcessRunner.Result> RunWingetAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(WingetExe, arguments + ModernFlags, ct,
            timeout: timeout, outputEncoding: System.Text.Encoding.UTF8);

        if (!result.Succeeded &&
            result.CombinedMessage.Contains("disable-interactivity", StringComparison.OrdinalIgnoreCase))
        {
            result = await ProcessRunner.RunAsync(WingetExe, arguments + LegacyFlags, ct,
                timeout: timeout, outputEncoding: System.Text.Encoding.UTF8);
        }

        return result;
    }

    /// <summary>
    /// Checks if winget is available on the system.
    /// </summary>
    public static async Task<bool> IsWingetAvailableAsync()
    {
        try
        {
            var result = await ProcessRunner.RunAsync(WingetExe, "--version", timeout: TimeSpan.FromSeconds(30));
            return result.Succeeded;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Uses 'winget upgrade' to find programs with available updates.
    /// </summary>
    public static async Task<List<OutdatedProgram>> CheckForUpdatesAsync(
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Checking for outdated software via winget...");

        try
        {
            // Agreements are accepted non-interactively; without this winget waits on stdin
            // for a Y/N answer on first use and the scan hangs forever.
            var result = await RunWingetAsync("upgrade --include-unknown", TimeSpan.FromMinutes(5), ct);

            var results = ParseWingetUpgradeOutput(result.StandardOutput);
            progress?.Report($"Found {results.Count} program(s) with available updates.");
            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("SoftwareUpdater", "winget check failed", ex);
            progress?.Report("Couldn't check for updates. Make sure App Installer (winget) is up to date.");
            return [];
        }
    }

    /// <summary>
    /// Updates a specific program using winget.
    /// </summary>
    public static async Task<(bool Success, string Message)> UpdateProgramAsync(
        OutdatedProgram program,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsValidPackageId(program.Id))
            return (false, $"'{program.Id}' is not a valid package identifier (it may be truncated in winget's output).");

        progress?.Report($"Updating {program.Name}...");

        try
        {
            var result = await RunWingetAsync(
                $"upgrade --id \"{program.Id}\" --exact --silent --accept-package-agreements",
                TimeSpan.FromMinutes(30), ct);

            if (result.Succeeded)
            {
                progress?.Report($"{program.Name} updated successfully.");
                return (true, $"{program.Name} updated to {program.AvailableVersion}.");
            }

            var detail = CleanWingetOutput(result.CombinedMessage)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()?.Trim() ?? $"exit code {result.ExitCode}";
            return (false, $"Update failed: {detail}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// winget package IDs never contain whitespace, quotes, or the "…" truncation marker.
    /// </summary>
    internal static bool IsValidPackageId(string id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.Length <= 128 &&
        !id.Contains('…') &&
        id.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or '{' or '}');

    /// <summary>
    /// Cleans winget output by splitting on both \n and \r to handle progress spinners
    /// that use carriage returns to overwrite text on the same line.
    /// Returns clean individual lines ready for column-based parsing.
    /// </summary>
    internal static string CleanWingetOutput(string rawOutput)
    {
        // winget uses \r to overwrite progress/spinner text on the same line,
        // so a single \n-delimited "line" can contain multiple \r-separated segments.
        // Split on both \r and \n to get all logical segments.
        var segments = rawOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', segments);
    }

    /// <summary>
    /// Parses the tabular output of 'winget upgrade'.
    /// </summary>
    internal static List<OutdatedProgram> ParseWingetUpgradeOutput(string output)
    {
        // Clean up winget output (handles \r progress spinners)
        output = CleanWingetOutput(output);

        var results = new List<OutdatedProgram>();
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Find the header line containing "Name" and "Id" columns
        int headerIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("Name") && lines[i].Contains("Id") && lines[i].Contains("Version"))
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0 || headerIndex + 1 >= lines.Length) return results;

        // The separator line (dashes) follows the header
        var separatorIndex = headerIndex + 1;
        if (separatorIndex >= lines.Length || !lines[separatorIndex].TrimStart().StartsWith('-'))
            return results;

        // Determine column positions from the header
        var header = lines[headerIndex];
        int nameCol = header.IndexOf("Name", StringComparison.Ordinal);
        int idCol = header.IndexOf("Id", StringComparison.Ordinal);
        int versionCol = header.IndexOf("Version", StringComparison.Ordinal);
        int availableCol = header.IndexOf("Available", StringComparison.Ordinal);
        int sourceCol = header.IndexOf("Source", StringComparison.Ordinal);

        if (idCol < 0 || versionCol < 0) return results;

        // Parse data lines after the separator
        for (int i = separatorIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            // Stop at summary lines like "X upgrades available"
            if (Regex.IsMatch(line.Trim(), @"^\d+ upgrade"))
                break;

            try
            {
                var name = SafeSubstring(line, nameCol, idCol).Trim();
                var id = SafeSubstring(line, idCol, versionCol).Trim();
                var version = availableCol > 0
                    ? SafeSubstring(line, versionCol, availableCol).Trim()
                    : SafeSubstring(line, versionCol, line.Length).Trim();
                var available = (availableCol > 0 && sourceCol > 0)
                    ? SafeSubstring(line, availableCol, sourceCol).Trim()
                    : (availableCol > 0 ? SafeSubstring(line, availableCol, line.Length).Trim() : "");
                var source = sourceCol > 0
                    ? SafeSubstring(line, sourceCol, line.Length).Trim()
                    : "";

                if (!string.IsNullOrEmpty(name) && IsValidPackageId(id))
                {
                    results.Add(new OutdatedProgram
                    {
                        Name = name,
                        Id = id,
                        InstalledVersion = version,
                        AvailableVersion = available,
                        Source = source
                    });
                }
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return results;
    }

    private static string SafeSubstring(string s, int start, int end)
    {
        if (start < 0) start = 0;
        if (end > s.Length) end = s.Length;
        if (start >= end || start >= s.Length) return string.Empty;
        return s[start..end];
    }
}
