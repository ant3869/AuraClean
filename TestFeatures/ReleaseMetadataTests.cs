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
        Section("MSI versions order correctly across rc.N and final releases", TestMsiVersionOrdering);
        Section("Uninstall removes only AuraClean-owned integrations, and never on upgrade", TestUninstallCleanupRules);
        Section("Release build scripts never collect a stale MSI", TestBuildScriptRegression);
        Section("Destructive MSI lifecycle test cannot run on a developer machine by accident", TestLifecycleSafetyGuards);
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
        Check(Read("installer/AuraClean.Installer.wixproj").Contains("ProductVersion=$(MsiProductVersion)"), "ProductVersion is fed from MsiProductVersion (the rc.N-aware MSI version)");

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
        Check(!Regex.IsMatch(code, @"VersionNT(64)?\s*&gt;=\s*(1000|10\d\d)"), "The OS launch condition does not test for 10.0: Windows Installer reports VersionNT64=603 even on Server 2022/2025, which blocked every install (found by the MSI acceptance run)");
        Check(Regex.IsMatch(code, @"Installed OR \(VersionNT64 &gt;= 603\)"), "The launch condition requires 64-bit Windows using the floor Windows Installer actually reports");
        Check(!code.Contains("AllowSameVersionUpgrades"), "Same-version 'upgrades' are not allowed (rc.N ordering comes from the version, not from a loophole)");
        Check(code.Contains("SAMEVERSIONINSTALLED"), "A rebuilt package of an installed version is detected and blocked");
        Check(code.Contains("MSIRESTARTMANAGERCONTROL\" Value=\"Disable\""), "The installer never auto-closes a running AuraClean");

        foreach (var forbidden in new[] { "RemoveFile", "RemoveFolder", "RemoveRegistry", "ServiceInstall", "ServiceControl", "CloseApplication", "SchedulerTask", "RemoveRegistryKey", "RemoveRegistryValue" })
            Check(!code.Contains("<" + forbidden), $"No <{forbidden}> in the installer (no deletions, services or process killing)");

        var directories = Regex.Matches(code, @"<(?:Standard)?Directory(?:Ref)?\s+Id=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
        Check(!directories.Any(d => d.Contains("AppData", StringComparison.OrdinalIgnoreCase) || d.Contains("ProgramData", StringComparison.OrdinalIgnoreCase) || d.Contains("Personal", StringComparison.OrdinalIgnoreCase)),
            $"The installer defines no AppData/ProgramData/profile directory ({string.Join(", ", directories)})");
        Check(Regex.Matches(code, @"<RegistryValue[^>]*Root=""([^""]+)""").All(m => m.Groups[1].Value == "HKLM")
              && Regex.Matches(code, @"<RegistryValue[^>]*Key=""([^""]+)""").All(m => m.Groups[1].Value.StartsWith(@"Software\AuraClean")),
            @"Registry entries are install metadata under HKLM\Software\AuraClean only");
        Check(code.Contains("Id=\"LEGACYEXE\""), "A legacy install.ps1 copy is detected (and only reported)");
    }

    // ── MSI version ordering ──────────────────────────────────────────────────────────────────────
    private static string RunTool(string fileName, params string[] args)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot()
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        stderr.Wait();
        return output + (p.ExitCode == 0 ? "" : $"\n[exit {p.ExitCode}]");
    }

    private static string MsiVersionFor(string prefix, string suffix) =>
        RunTool("dotnet", "msbuild", "AuraClean/AuraClean.csproj", "-nologo", $"-p:VersionPrefix={prefix}", $"-p:VersionSuffix={suffix}", "-getProperty:MsiProductVersion").Trim();

    private static void TestMsiVersionOrdering()
    {
        // (label, prefix, suffix) in release order.
        var lifecycle = new[]
        {
            ("1.5.99", "1.5.99", ""),
            ("1.6.0-rc.1", "1.6.0", "rc.1"),
            ("1.6.0-rc.2", "1.6.0", "rc.2"),
            ("1.6.0-rc.98", "1.6.0", "rc.98"),
            ("1.6.0", "1.6.0", ""),
            ("1.6.1-rc.1", "1.6.1", "rc.1"),
            ("1.6.1", "1.6.1", ""),
            ("1.7.0-rc.1", "1.7.0", "rc.1"),
        };
        var versions = lifecycle.Select(l => (Label: l.Item1, Msi: MsiVersionFor(l.Item2, l.Item3))).ToList();
        Check(versions.All(v => Version.TryParse(v.Msi, out _)),
            "Every release label maps to a valid MSI version: " + string.Join(", ", versions.Select(v => $"{v.Label}={v.Msi}")));

        var parsed = versions.Select(v => Version.TryParse(v.Msi, out var ver) ? ver : new Version(0, 0, 0)).ToList();
        for (int i = 0; i < parsed.Count - 1; i++)
            Check(parsed[i] < parsed[i + 1],
                $"{versions[i].Label} ({parsed[i]}) sorts below {versions[i + 1].Label} ({parsed[i + 1]}): that upgrade is allowed and the reverse is rejected as a downgrade");

        Check(parsed.All(v => v.Major < 256 && v.Minor < 256 && v.Build < 65536 && v.Revision == -1),
            "All MSI versions stay within Windows Installer limits (major<256, minor<256, build<65536, three fields)");

        foreach (var bad in new[] { "beta.1", "alpha.2", "rc.0", "rc.99", "rc.100", "rc", "preview" })
            Check(MsiVersionFor("1.6.0", bad) == "", $"Unsupported suffix '{bad}' is rejected instead of being mis-ordered");

        var wixproj = Read("installer/AuraClean.Installer.wixproj");
        Check(wixproj.Contains("ValidateMsiVersion") && wixproj.Contains("MsiProductVersion)' == ''"), "The installer build fails when no valid MSI version exists");
        Check(Read("installer/Package.wxs").Contains("Version=\"$(var.ProductVersion)\""), "Package.wxs takes the mapped version from the build");
    }

    // ── uninstall cleanup ─────────────────────────────────────────────────────────────────────────
    private static string ServiceConst(Type type, string name) =>
        (string)type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!;

    private static void TestUninstallCleanupRules()
    {
        var code = Regex.Replace(Read("installer/Package.wxs"), "<!--.*?-->", "", RegexOptions.Singleline);

        var logon = ServiceConst(typeof(AuraClean.Services.LaunchAtLogonService), "TaskName");
        var cleanup = ServiceConst(typeof(AuraClean.Services.ScheduledCleanupService), "TaskName");
        var exeKey = ServiceConst(typeof(AuraClean.Services.ContextMenuService), "ExeKeyPath");
        var msiKey = ServiceConst(typeof(AuraClean.Services.ContextMenuService), "MsiKeyPath");

        Check(code.Contains($"/Delete /TN {logon} /F"), $"The installer deletes the exact task '{logon}' (name read from LaunchAtLogonService)");
        Check(code.Contains($"/Delete /TN {cleanup} /F"), $"The installer deletes the exact task '{cleanup}' (name read from ScheduledCleanupService)");
        Check(code.Contains($"delete HKLM\\{exeKey} /f"), $"The installer deletes the exact key HKLM\\{exeKey} (path read from ContextMenuService)");
        Check(code.Contains($"delete HKLM\\{msiKey} /f"), $"The installer deletes the exact key HKLM\\{msiKey} (path read from ContextMenuService)");
        Check(exeKey.EndsWith("\\AuraCleanUninstall") && msiKey.EndsWith("\\AuraCleanUninstall") && logon.StartsWith("AuraClean_") && cleanup.StartsWith("AuraClean_"),
            "Every removed object is AuraClean-named (nothing generic like exefile\\shell or a shared task folder is removed)");

        var actions = Regex.Matches(code, @"<CustomAction\b[^>]*>").Select(m => m.Value).ToList();
        Check(actions.Count == 4, $"Exactly four custom actions exist ({actions.Count})");
        Check(actions.All(a => a.Contains("Execute=\"deferred\"") && a.Contains("Impersonate=\"no\"") && a.Contains("Return=\"ignore\"")),
            "All are deferred, elevated and failure-tolerant (they can never block an uninstall)");
        Check(actions.All(a => Regex.IsMatch(a, @"ExeCommand=""&quot;\[System64Folder\](schtasks|reg)\.exe&quot; ")),
            "Only the OS schtasks.exe / reg.exe are run, from the 64-bit system folder (no scripts, no PATH lookup)");
        Check(!Regex.IsMatch(code, @"ExeCommand=""[^""]*(\*|%|cmd\.exe|powershell|wscript|cscript|rundll32)", RegexOptions.IgnoreCase),
            "No wildcards, shell interpreters or environment expansion in any command");

        var scheduled = Regex.Matches(code, @"<Custom\s+Action=""(Remove\w+)""[^>]*Condition=""([^""]*)""").ToList();
        Check(scheduled.Count == 4 && scheduled.All(m => m.Groups[2].Value == "REMOVE=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE"),
            "Each runs only for REMOVE=\"ALL\" AND NOT UPGRADINGPRODUCTCODE: a real uninstall, never install, repair, modify or upgrade");
        Check(!code.Contains("<InstallUISequence"), "Nothing is scheduled in the UI sequence");
        Check(scheduled.All(m => m.Value.Contains("Before=\"InstallFinalize\"")), "They are scheduled late in the transaction, after the product's own files are removed");
        Check(!code.Contains("<RemoveFolderEx") && !code.Contains("<RemoveFile"), "Nothing under %LocalAppData%\\AuraClean (settings, backups, journals, quarantine) can be removed by the installer");
    }

    // ── build scripts ─────────────────────────────────────────────────────────────────────────────
    private static void TestBuildScriptRegression()
    {
        var output = RunTool("pwsh", "-NoProfile", "-File", Path.Combine(RepoRoot(), "build", "Test-BuildScripts.ps1"));
        var m = Regex.Match(output, @"RESULTS: (\d+) passed, (\d+) failed");
        Check(m.Success && m.Groups[2].Value == "0" && int.Parse(m.Groups[1].Value) >= 10 && !output.Contains("[exit"),
            $"build/Test-BuildScripts.ps1 passes (stale or missing MSIs are refused; only the current build's MSI is selected): {(m.Success ? m.Value : "no result line")}");
    }

    // ── lifecycle-test safety ─────────────────────────────────────────────────────────────────────
    private static void TestLifecycleSafetyGuards()
    {
        var life = Read("build/Test-MsiLifecycle.ps1");
        var refuseIdx = life.IndexOf("REFUSING TO RUN", StringComparison.Ordinal);
        var firstInstall = life.IndexOf("Invoke-Msi 'install-rc1'", StringComparison.Ordinal);
        Check(life.Contains("github-hosted") && life.Contains("-cne $expectedConfirmation") && life.Contains("I-CONFIRM-"),
            "The lifecycle test runs only on a GitHub-hosted runner or with the exact I-CONFIRM-<COMPUTERNAME>-IS-DISPOSABLE text");
        Check(life.Contains("this machine is not clean") && life.Contains("(user data)"), "It refuses when AuraClean, its data, tasks or menu keys already exist");
        Check(refuseIdx >= 0 && firstInstall > refuseIdx && life.LastIndexOf("REFUSING TO RUN", StringComparison.Ordinal) < firstInstall,
            "Both refusals happen before the first real installation");
        Check(!Regex.IsMatch(life, @"Remove-Item\s+[^\r\n]*LOCALAPPDATA", RegexOptions.IgnoreCase) && !life.Contains("Remove-Item $DataDir"),
            "The lifecycle test never deletes the data folder");

        var kit = Read("acceptance/Start-Acceptance.ps1");
        Check(kit.Contains("-cne $expected") && kit.Contains("I-CONFIRM-") && kit.Contains("REFUSING"), "The acceptance kit's Lifecycle mode needs the same explicit confirmation");
        Check(kit.IndexOf("[ValidateSet('Prepare'", StringComparison.Ordinal) >= 0 && Regex.IsMatch(kit, @"\[string\]\$Mode = 'Prepare'|\$Mode = 'Prepare'"), "The default mode is the read-only Prepare");
        Check(File.Exists(Path.Combine(RepoRoot(), "acceptance", "manual-checklist.md")) && File.Exists(Path.Combine(RepoRoot(), "acceptance", "results-template.md")), "The manual checklist and the results template ship with the kit");

        var wf = Read(".github/workflows/msi-acceptance.yml");
        var wfCode = Regex.Replace(wf, @"(?m)^\s*#.*$", "");   // ignore comments
        Check(wfCode.Contains("runs-on: ${{ matrix.os }}") && wfCode.Contains("windows-2022") && wfCode.Contains("windows-2025") && !wfCode.Contains("self-hosted") && !wfCode.Contains("secrets."),
            "The GitHub acceptance workflow uses the hosted windows-2022 / windows-2025 runners only and no secrets");
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
