using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AuraClean.Helpers;

/// <summary>
/// Expands 8.3 short names (e.g. C:\PROGRA~1) so string-prefix ownership checks compare like
/// with like. Without this, "C:\PROGRA~1\Vendor" and "C:\Program Files\Vendor" look unrelated
/// and a shared-folder conflict check silently fails open.
/// </summary>
public static class LongPathHelper
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder? longPath, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder? shortPath, uint bufferLength);

    /// <summary>
    /// Normalizes and, when the path (or its deepest existing ancestor) exists, expands short
    /// names. Returns null for paths <see cref="PathSafety.Normalize"/> rejects.
    /// </summary>
    public static string? Canonicalize(string? path)
    {
        var normalized = PathSafety.Normalize(path);
        if (normalized == null || !normalized.Contains('~'))
            return normalized;

        // Expand the deepest existing ancestor, then re-append the non-existent tail.
        var existing = normalized;
        var tail = string.Empty;
        while (!string.IsNullOrEmpty(existing) && !Path.Exists(existing))
        {
            tail = Path.Combine(Path.GetFileName(existing), tail);
            existing = Path.GetDirectoryName(existing);
        }

        if (string.IsNullOrEmpty(existing))
            return normalized;

        // Fail closed: an existing path that may hold a short name but cannot be expanded
        // (e.g. longer than MAX_PATH) is rejected rather than compared in short form.
        var expanded = Call(GetLongPathNameW, existing);
        return expanded == null ? null : PathSafety.Normalize(Path.Combine(expanded, tail));
    }

    /// <summary>Test support: the 8.3 form of an existing path, or null when unavailable.</summary>
    internal static string? TryGetShortPath(string path) => Call(GetShortPathNameW, path);

    private static string? Call(Func<string, StringBuilder?, uint, uint> api, string path)
    {
        var size = api(path, null, 0);
        if (size == 0)
            return null;
        var buffer = new StringBuilder((int)size);
        var written = api(path, buffer, size);
        return written == 0 || written >= size ? null : buffer.ToString();
    }
}
