using AuraClean.Helpers;
using System.IO;
using System.Text;

namespace AuraClean.Services;

/// <summary>
/// Surgically removes host-name mappings from the Windows hosts file. Only the offending
/// host name is removed; every other line (including the user's own entries and comments)
/// is preserved, and a timestamped backup is written first.
/// </summary>
public static class HostsFileEditor
{
    public static string HostsPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    /// <summary>
    /// Removes every mapping of <paramref name="hostName"/> from the hosts file.
    /// </summary>
    public static (bool Ok, string Message) RemoveEntries(string hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName))
            return (false, "No host name specified.");

        var path = HostsPath;
        try
        {
            if (!File.Exists(path))
                return (true, "The hosts file no longer exists.");

            var bytes = File.ReadAllBytes(path);
            var encoding = DetectEncoding(bytes);
            var content = encoding.GetString(bytes);
            if (content.Length > 0 && content[0] == '﻿')
                content = content[1..];

            var (updated, removed) = RemoveHostEntries(content, hostName);
            if (removed == 0)
                return (true, "The hosts entry was already removed.");

            var backup = $"{path}.auraclean-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.Copy(path, backup, overwrite: true);

            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);

            try
            {
                File.WriteAllText(path, updated, encoding);
            }
            finally
            {
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(path, attributes);
            }

            DiagnosticLogger.Info("HostsFileEditor", $"Removed {removed} mapping(s) for {hostName}; backup at {backup}");
            return (true, $"Removed {removed} hosts mapping(s) for {hostName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("HostsFileEditor", $"Could not edit hosts file for {hostName}", ex);
            return (false, $"The hosts file could not be edited ({ex.Message}). Security software may be protecting it.");
        }
    }

    /// <summary>
    /// Pure text transform: removes <paramref name="hostName"/> from every mapping line,
    /// dropping a line only when it maps no other host. Comments and blank lines are untouched.
    /// </summary>
    internal static (string Content, int Removed) RemoveHostEntries(string content, string hostName)
    {
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = content.Split('\n');
        var output = new StringBuilder(content.Length);
        int removed = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var isLast = i == lines.Length - 1;

            var commentIdx = raw.IndexOf('#');
            var mapping = commentIdx >= 0 ? raw[..commentIdx] : raw;
            var comment = commentIdx >= 0 ? raw[commentIdx..] : string.Empty;
            var tokens = mapping.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length >= 2 &&
                tokens.Skip(1).Any(t => t.Equals(hostName, StringComparison.OrdinalIgnoreCase)))
            {
                var remainingHosts = tokens.Skip(1)
                    .Where(t => !t.Equals(hostName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                removed += tokens.Length - 1 - remainingHosts.Count;

                if (remainingHosts.Count == 0)
                    continue; // Drop the whole line (including any trailing comment).

                raw = $"{tokens[0]}\t{string.Join(' ', remainingHosts)}" +
                      (comment.Length > 0 ? $" {comment}" : string.Empty);
            }

            output.Append(raw);
            if (!isLast)
                output.Append(newline);
        }

        return (output.ToString(), removed);
    }

    private static Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}
