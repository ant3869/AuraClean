using System.Diagnostics;
using System.IO;

namespace AuraClean.Helpers;

/// <summary>
/// Opens Explorer windows. Explorer is always launched by absolute path so an executable
/// named explorer.exe planted next to AuraClean can never be started with admin rights.
/// </summary>
public static class ShellHelper
{
    private static string ExplorerPath => Path.Combine(PathSafety.WindowsDirectory, "explorer.exe");

    /// <summary>Opens Explorer with the file (or folder) selected. Returns false on failure.</summary>
    public static bool RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            if (File.Exists(path))
            {
                Process.Start(ExplorerPath, $"/select,\"{path}\"")?.Dispose();
                return true;
            }

            if (Directory.Exists(path))
            {
                Process.Start(ExplorerPath, $"\"{path}\"")?.Dispose();
                return true;
            }

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                Process.Start(ExplorerPath, $"\"{parent}\"")?.Dispose();
                return true;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            DiagnosticLogger.Warn("ShellHelper", $"Could not open Explorer for {path}", ex);
        }

        return false;
    }
}
