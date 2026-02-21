using Microsoft.Win32;
using System.IO;

namespace AuraClean.Services;

/// <summary>
/// Provides Windows Shell context menu integration — adds "Deep Uninstall with AuraClean"
/// to the right-click menu for .exe and .msi files.
/// </summary>
public static class ContextMenuService
{
    private const string MenuLabel = "Deep Uninstall with AuraClean";
    private const string ExeKeyPath = @"SOFTWARE\Classes\exefile\shell\AuraCleanUninstall";
    private const string MsiKeyPath = @"SOFTWARE\Classes\Msi.Package\shell\AuraCleanUninstall";

    /// <summary>
    /// Checks whether the context menu entries are currently installed.
    /// </summary>
    public static bool IsContextMenuInstalled()
    {
        try
        {
            using var exeKey = Registry.LocalMachine.OpenSubKey(ExeKeyPath);
            using var msiKey = Registry.LocalMachine.OpenSubKey(MsiKeyPath);
            return exeKey != null && msiKey != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Installs the context menu entries for .exe and .msi files.
    /// Requires administrator privileges.
    /// </summary>
    /// <param name="dryRun">If true, returns the registry script without applying it.</param>
    /// <returns>Success status and the registry script content.</returns>
    public static (bool Success, string Message, string RegistryScript) InstallContextMenu(bool dryRun = false)
    {
        var exePath = GetAuraCleanExePath();
        var script = GenerateRegistryScript(exePath, install: true);

        if (dryRun)
        {
            return (true,
                "[DRY RUN] Would install context menu entries for .exe and .msi files.",
                script);
        }

        try
        {
            // .exe context menu
            using (var key = Registry.LocalMachine.CreateSubKey(ExeKeyPath))
            {
                key.SetValue("", MenuLabel);
                key.SetValue("Icon", exePath);

                using var cmdKey = key.CreateSubKey("command");
                cmdKey.SetValue("", $"\"{exePath}\" --deep-uninstall \"%1\"");
            }

            // .msi context menu
            using (var key = Registry.LocalMachine.CreateSubKey(MsiKeyPath))
            {
                key.SetValue("", MenuLabel);
                key.SetValue("Icon", exePath);

                using var cmdKey = key.CreateSubKey("command");
                cmdKey.SetValue("", $"\"{exePath}\" --deep-uninstall \"%1\"");
            }

            return (true, "Context menu installed successfully.", script);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Administrator privileges required to install context menu.", script);
        }
        catch (Exception ex)
        {
            return (false, $"Error installing context menu: {ex.Message}", script);
        }
    }

    /// <summary>
    /// Removes the context menu entries.
    /// Requires administrator privileges.
    /// </summary>
    /// <param name="dryRun">If true, returns the uninstall registry script without applying it.</param>
    public static (bool Success, string Message, string RegistryScript) UninstallContextMenu(bool dryRun = false)
    {
        var script = GenerateRegistryScript(GetAuraCleanExePath(), install: false);

        if (dryRun)
        {
            return (true,
                "[DRY RUN] Would remove context menu entries for .exe and .msi files.",
                script);
        }

        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(ExeKeyPath, throwOnMissingSubKey: false);
            Registry.LocalMachine.DeleteSubKeyTree(MsiKeyPath, throwOnMissingSubKey: false);
            return (true, "Context menu entries removed.", script);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "Administrator privileges required to remove context menu.", script);
        }
        catch (Exception ex)
        {
            return (false, $"Error removing context menu: {ex.Message}", script);
        }
    }

    /// <summary>
    /// Generates a .reg file script that can be exported/shared for manual installation.
    /// </summary>
    public static string GenerateRegistryScript(string? exePath = null, bool install = true)
    {
        exePath ??= GetAuraCleanExePath();
        var escapedPath = exePath.Replace("\\", "\\\\");

        if (install)
        {
            return $"""
                Windows Registry Editor Version 5.00

                ; ═══════════════════════════════════════════════
                ; AuraClean — Shell Context Menu Integration
                ; Adds "Deep Uninstall with AuraClean" to .exe and .msi right-click
                ; ═══════════════════════════════════════════════

                ; --- .exe files ---
                [HKEY_LOCAL_MACHINE\{ExeKeyPath}]
                @="{MenuLabel}"
                "Icon"="{escapedPath}"

                [HKEY_LOCAL_MACHINE\{ExeKeyPath}\command]
                @="\"{escapedPath}\" --deep-uninstall \"%1\""

                ; --- .msi files ---
                [HKEY_LOCAL_MACHINE\{MsiKeyPath}]
                @="{MenuLabel}"
                "Icon"="{escapedPath}"

                [HKEY_LOCAL_MACHINE\{MsiKeyPath}\command]
                @="\"{escapedPath}\" --deep-uninstall \"%1\""
                """;
        }
        else
        {
            return $"""
                Windows Registry Editor Version 5.00

                ; ═══════════════════════════════════════════════
                ; AuraClean — Remove Shell Context Menu
                ; ═══════════════════════════════════════════════

                [-HKEY_LOCAL_MACHINE\{ExeKeyPath}]
                [-HKEY_LOCAL_MACHINE\{MsiKeyPath}]
                """;
        }
    }

    /// <summary>
    /// Saves the registry script to a .reg file on the user's desktop.
    /// </summary>
    public static (bool Success, string FilePath) ExportRegistryScript(bool install = true)
    {
        try
        {
            var fileName = install
                ? "AuraClean_InstallContextMenu.reg"
                : "AuraClean_RemoveContextMenu.reg";
            var filePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), fileName);

            File.WriteAllText(filePath, GenerateRegistryScript(install: install));
            return (true, filePath);
        }
        catch (Exception)
        {
            return (false, string.Empty);
        }
    }

    private static string GetAuraCleanExePath()
    {
        var current = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(current))
            return current;

        // Fallback: use the known build output path
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AuraClean.exe");
    }
}
