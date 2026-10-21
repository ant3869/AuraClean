using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AuraClean.ViewModels;

namespace TestFeatures;

/// <summary>
/// Release-packaging invariants that can be checked from source (no installer is built or run here):
/// one version everywhere, installer data-safety rules, no committed signing material, safe legacy script,
/// release workflow shape. The MSI itself is verified by build/Test-Release.ps1.
/// </summary>
public static class ReleaseMetadataTests
{
    private static int _pass;
    private static int _fail;

    public static (int Pass, int Fail) Run()
    {
        _pass = 0;
        _fail = 0;

        Section("One release version across assembly, manifest and installer inputs", TestVersionConsistency);
        Section("App version text keeps the release-candidate label", TestVersionFormatting);
        Section("Installer source never touches user data and never kills the app", TestInstallerRules);
        Section("Release workflow builds artifacts but publishes nothing and holds no secrets", TestWorkflow);
        Section("No signing material is committed", TestNoSecretsCommitted);
        Section("Legacy install.ps1 is guarded", TestLegacyScript);

        Console.WriteLine($"  Release metadata: {_pass} passed, {_fail} failed");
        return (_pass, _fail);
    }

    private static void Section(string name, Action test)
    {
        Console.WriteLine($"═══ RELEASE: {name} ═══");
        try { test(); }
        catch (Exception ex) { Check(false, $"Unexpected exception: {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine();
    }

    private static void Check(bool condition, string message)
    {
        Console.ForegroundColor = condition ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}: {message}");
        Console.ResetColor();
        if (condition) _pass++; else _fail++;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cleaner.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root (cleaner.sln) not found above " + AppContext.BaseDirectory);
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    private static string InformationalVersion() =>
        typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    // ── versions ──────────────────────────────────────────────────────────────────────────────────
    private static void TestVersionConsistency()
    {
        var props = XDocument.Parse(Read("Directory.Build.props"));
        string Get(string name) => props.Descendants(name).FirstOrDefault()?.Value ?? "";
        var prefix = Get("VersionPrefix");
        var suffix = Get("VersionSuffix");
        var label = suffix.Length > 0 ? $"{prefix}-{suffix}" : prefix;

        Check(Regex.IsMatch(prefix, @"^\d+\.\d+\.\d+$"), $"VersionPrefix is a three-part version ({prefix})");
        Check(suffix == "" || Regex.IsMatch(suffix, @"^(rc|beta|alpha)\.\d+$"), $"VersionSuffix is empty or a SemVer pre-release label ({suffix})");

        var app = typeof(MainViewModel).Assembly;
        var fvi = FileVersionInfo.GetVersionInfo(app.Location);
        Check(fvi.FileVersion == prefix + ".0", $"Application FileVersion {fvi.FileVersion} equals {prefix}.0");
        Check(fvi.ProductVersion == label, $"Application ProductVersion {fvi.ProductVersion} equals the release label {label}");

        var manifest = XDocument.Parse(Read("AuraClean/app.manifest"));
        var identity = manifest.Descendants().First(e => e.Name.LocalName == "assemblyIdentity");
        Check((string)identity.Attribute("version") == prefix + ".0", "app.manifest assemblyIdentity version matches (it is the only place the number is repeated)");

        var csproj = Read("AuraClean/AuraClean.csproj");
        Check(!Regex.IsMatch(csproj, @"<Version>\s*\d"), "AuraClean.csproj no longer hard-codes a version");

        var wxs = Read("installer/Package.wxs");
        Check(wxs.Contains("Version=\"$(var.ProductVersion)\""), "The MSI takes its version from the build (no literal version in Package.wxs)");
        Check(Read("installer/AuraClean.Installer.wixproj").Contains("ProductVersion=$(VersionPrefix)"), "ProductVersion is fed from VersionPrefix");

        var changelog = Read("CHANGELOG.md");
        Check(changelog.Contains($"[{label}]"), "CHANGELOG has an entry for the release label");
        Check(File.Exists(Path.Combine(RepoRoot(), "docs", "release-notes", $"v{label}.md")), $"Draft release notes exist for v{label}");
    }

    private static void TestVersionFormatting()
    {
        Check(MainViewModel.FormatAppVersion("1.6.0-rc.1", new Version(1, 6, 0, 0)) == "v1.6.0-rc.1", "The pre-release label is shown (v1.6.0-rc.1)");
        Check(MainViewModel.FormatAppVersion("1.6.0-rc.1+abc123", new Version(1, 6, 0, 0)) == "v1.6.0-rc.1", "Build metadata after '+' is hidden");
        Check(MainViewModel.FormatAppVersion("1.6.0", new Version(1, 6, 0, 0)) == "v1.6.0", "A final release shows no label");
        Check(MainViewModel.FormatAppVersion(null, new Version(1, 2, 3, 4)) == "v1.2.3", "Falls back to the numeric assembly version");
        Check(MainViewModel.FormatAppVersion("  ", null) == "v0.0.0", "Falls back to v0.0.0 when nothing is available");

        var shown = MainViewModel.FormatAppVersion(InformationalVersion(), typeof(MainViewModel).Assembly.GetName().Version);
        Check(shown.StartsWith("v") && shown.Contains("-rc.") == (InformationalVersion() ?? "").Contains("-rc."), $"The running assembly yields '{shown}'");
    }

    // ── installer rules ───────────────────────────────────────────────────────────────────────────
    private static void TestInstallerRules()
    {
        var wxs = Read("installer/Package.wxs");
        var code = Regex.Replace(wxs, "<!--.*?-->", "", RegexOptions.Singleline); // ignore comments

        Check(code.Contains("UpgradeCode=\"D77CC99D-4E73-41BF-BD16-48C2882CE96F\""), "The upgrade code is the fixed AuraClean code");
        Check(code.Contains("Scope=\"perMachine\""), "The package is per-machine (protected install location)");
        Check(code.Contains("StandardDirectory Id=\"ProgramFiles64Folder\""), "The executable is installed under Program Files (64-bit)");
        Check(code.Contains("<MajorUpgrade"), "In-place upgrades are enabled");
        Check(code.Contains("MSIRESTARTMANAGERCONTROL\" Value=\"Disable\""), "The installer never auto-closes a running AuraClean");

        foreach (var forbidden in new[] { "RemoveFile", "RemoveFolder", "RemoveRegistry", "ServiceInstall", "ServiceControl", "CustomAction", "CloseApplication", "SchedulerTask" })
            Check(!code.Contains("<" + forbidden), $"No <{forbidden}> in the installer (no deletions, services, custom code or process killing)");

        var directories = Regex.Matches(code, @"<(?:Standard)?Directory(?:Ref)?\s+Id=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
        Check(!directories.Any(d => d.Contains("AppData", StringComparison.OrdinalIgnoreCase) || d.Contains("ProgramData", StringComparison.OrdinalIgnoreCase) || d.Contains("Personal", StringComparison.OrdinalIgnoreCase)),
            $"The installer defines no AppData/ProgramData/profile directory ({string.Join(", ", directories)})");
        Check(Regex.Matches(code, @"<RegistryValue[^>]*Root=""([^""]+)""").All(m => m.Groups[1].Value == "HKLM")
              && Regex.Matches(code, @"<RegistryValue[^>]*Key=""([^""]+)""").All(m => m.Groups[1].Value.StartsWith(@"Software\AuraClean")),
            @"Registry entries are install metadata under HKLM\Software\AuraClean only");
        Check(code.Contains("Id=\"LEGACYEXE\""), "A legacy install.ps1 copy is detected (and only reported)");
    }

    // ── workflow ──────────────────────────────────────────────────────────────────────────────────
    private static void TestWorkflow()
    {
        var path = Path.Combine(RepoRoot(), ".github", "workflows", "release.yml");
        Check(File.Exists(path), "A release workflow exists");
        var yml = File.ReadAllText(path);
        Check(yml.Contains("windows-latest") && yml.Contains("Build-Release.ps1"), "It builds on Windows through the same script used locally");
        Check(yml.Contains("upload-artifact"), "It uploads the artifacts");
        Check(!Regex.IsMatch(yml, @"gh\s+release|action-gh-release|softprops|create-release", RegexOptions.IgnoreCase), "It does not publish a GitHub Release (that needs explicit approval)");
        Check(yml.Contains("workflow_dispatch"), "It can be run manually");
        Check(yml.Contains("secrets.SIGN_CERT_PFX_BASE64") && !Regex.IsMatch(yml, @"password\s*:\s*['""]?[A-Za-z0-9]{6,}"), "Signing reads secrets from GitHub secrets; no credential is inlined");
        Check(Regex.Matches(yml, @"uses:\s*\S+@v\d").Count >= 3 && !Regex.IsMatch(yml, @"uses:\s*\S+@(main|master)\b"), "Actions are pinned to major versions, not branches");
    }

    private static void TestNoSecretsCommitted()
    {
        var root = RepoRoot();
        char s = Path.DirectorySeparatorChar;
        var skip = new[] { $"{s}bin{s}", $"{s}obj{s}", $"{s}.git{s}", $"{s}artifacts{s}", $"{s}node_modules{s}", $"{s}.codebase-memory{s}" };
        var keyFiles = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => !skip.Any(x => f.Contains(x)))
            .Where(f => new[] { ".pfx", ".p12", ".pvk", ".snk", ".key", ".pem" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .ToList();
        Check(keyFiles.Count == 0, $"No certificate/private-key files in the repository ({keyFiles.Count} found)");

        var gitignore = Read(".gitignore");
        Check(gitignore.Contains("*.pfx") && gitignore.Contains("artifacts/"), ".gitignore excludes certificates and build artifacts");
    }

    // ── legacy install.ps1 ────────────────────────────────────────────────────────────────────────
    private static void TestLegacyScript()
    {
        var install = Read("install.ps1");
        Check(install.Contains("DEVELOPER"), "install.ps1 is labelled as a developer script");
        Check(install.Contains("$env:ProgramFiles") && install.Contains("-Force"), "It refuses to create a second copy next to an MSI install unless -Force");
        Check(!install.Contains("Stop-Process"), "It no longer terminates running AuraClean instances");

        var remove = Read("scripts/Remove-LegacyInstall.ps1");
        Check(remove.Contains("SupportsShouldProcess") && remove.Contains("ShouldProcess"), "The legacy cleanup script supports -WhatIf/-Confirm");
        Check(!Regex.IsMatch(remove, @"Remove-Item[^\r\n]*-Recurse", RegexOptions.IgnoreCase), "It never deletes recursively (data folders are safe by construction)");
        Check(remove.Contains("AuraClean.exe") && remove.Contains("AuraClean.lnk"), "It removes only the legacy executable and shortcut");
    }
}
