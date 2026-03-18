using AuraClean.Helpers;
using System.Diagnostics;
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
    /// Checks if winget is available on the system.
    /// </summary>
    public static async Task<bool> IsWingetAvailableAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("winget", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch
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
        var results = new List<OutdatedProgram>();

        progress?.Report("Checking for outdated software via winget...");

        try
        {
            var psi = new ProcessStartInfo("winget", "upgrade --include-unknown")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                progress?.Report("Failed to start winget.");
                return results;
            }

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            results = ParseWingetUpgradeOutput(output);
            progress?.Report($"Found {results.Count} program(s) with available updates.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Info("SoftwareUpdater", $"winget check failed: {ex.Message}");
            progress?.Report($"Error checking updates: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Updates a specific program using winget.
    /// </summary>
    public static async Task<(bool Success, string Message)> UpdateProgramAsync(
        OutdatedProgram program,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report($"Updating {program.Name}...");

        try
        {
            var psi = new ProcessStartInfo("winget", $"upgrade --id \"{program.Id}\" --accept-package-agreements --accept-source-agreements --silent")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Failed to start winget.");

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            var error = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode == 0)
            {
                progress?.Report($"{program.Name} updated successfully.");
                return (true, $"{program.Name} updated to {program.AvailableVersion}.");
            }

            var msg = string.IsNullOrWhiteSpace(error) ? output : error;
            return (false, $"Update failed: {msg.Trim()}");
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
    /// Cleans winget output by splitting on both \n and \r to handle progress spinners
    /// that use carriage returns to overwrite text on the same line.
    /// Returns clean individual lines ready for column-based parsing.
    /// </summary>
    private static string CleanWingetOutput(string rawOutput)
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
    private static List<OutdatedProgram> ParseWingetUpgradeOutput(string output)
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

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
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
