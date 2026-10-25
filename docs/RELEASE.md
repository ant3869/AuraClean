# Building and verifying a release

## What a release consists of

For version `X.Y.Z-rc.N` (the label comes from `Directory.Build.props`):

| Artifact | Purpose |
|---|---|
| `AuraClean-X.Y.Z-rc.N-win-x64.msi` | **Installer** (per-machine, upgrade/repair/uninstall, Installed apps). |
| `AuraClean-X.Y.Z-rc.N-win-x64-portable.exe` | **Portable** self-contained single-file EXE; byte-identical to the one inside the MSI. |
| `SHA256SUMS.txt` | SHA-256 of both files (`<hash> *<name>`; works with `sha256sum -c` and `Get-FileHash`). |

Draft notes live in `docs/release-notes/vX.Y.Z-rc.N.md`.

## Why an MSI built with WiX v5

- **Windows Installer is what Installed apps, repair, upgrade and uninstall are built on**, and what IT tooling (Intune, SCCM, `msiexec /qn`) expects. No custom installer engine and no auto-updater to maintain.
- **WiX v5 is an open, established toolset distributed as NuGet packages** (`WixToolset.Sdk`, `WixToolset.UI.wixext`): `dotnet build` produces the MSI, nothing is installed machine-wide, and CI needs no extra setup.
- Alternatives considered: *Inno Setup* is simpler but needs a separate installer download and is not an MSI (weaker managed deployment); *MSIX* needs a signed package to install at all and runs the app in a container/identity model that does not suit a tool that edits the system; *ClickOnce* is per-user and cannot install an elevated tool to a protected folder.

## Build it (reproducible steps)

Prerequisites: 64-bit Windows 10/11, **.NET 10 SDK** (10.0.x; the app targets `net10.0-windows`), **PowerShell 7** (`pwsh`), network access for NuGet restore. Nothing else is installed machine-wide.

```powershell
git clone https://github.com/ant3869/AuraClean.git
cd AuraClean
pwsh build/Build-Release.ps1              # publish → tests → MSI → artifacts/release → verification
pwsh build/Build-Release.ps1 -SkipTests   # packaging only (not for releases)
pwsh build/Build-Release.ps1 -NoUiTests   # skip the WPF fixtures that need an interactive desktop (what CI does)
```

The script runs, in order: `dotnet publish` (self-contained, single-file, win-x64) → full test suite → optional signing → `dotnet build installer/AuraClean.Installer.wixproj` → copy artifacts + `SHA256SUMS.txt` → `build/Test-Release.ps1`.

Manual equivalent:

```powershell
dotnet publish AuraClean/AuraClean.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
dotnet build installer/AuraClean.Installer.wixproj -c Release -p:PublishDir="$PWD\artifacts\publish\"
```

### Reproducibility

- Source → EXE: deterministic compile (`Deterministic`, `ContinuousIntegrationBuild`, path mapping, no commit hash in the version). Rebuilding on the same SDK produces an equivalent EXE; byte equality across machines depends on the exact .NET SDK/workload versions, so **compare the checksum you publish, not a rebuilt one**.
- MSI: Windows Installer stamps package/product codes and timestamps, so **the MSI is not byte-for-byte reproducible**. Its *content* is verified instead: `Test-Release.ps1` unpacks it (administrative image, installs nothing) and checks that the EXE inside equals the portable EXE.

## Versioning

One source of truth: `Directory.Build.props` (`VersionPrefix`, `VersionSuffix`). It drives the assembly's file/product version, the MSI `ProductVersion`, the displayed app version and artifact names. The only repeated copy is `AuraClean/app.manifest` (`assemblyIdentity`), which the tests verify.

To cut a new candidate: change `VersionSuffix` (`rc.2`) or `VersionPrefix`, update `app.manifest`, add a `CHANGELOG.md` entry and `docs/release-notes/v<label>.md`, then build. Windows Installer compares only `major.minor.build` and has no pre-release concept, so the MSI `ProductVersion` (`MsiProductVersion` in `Directory.Build.props`) folds the stage into the build field: `build = patch*100 + stage`, with `stage = N` for `rc.N` (1–98) and `99` for the final release.

| Release | MSI version |
|---|---|
| 1.6.0-rc.1 / rc.2 | 1.6.1 / 1.6.2 |
| 1.6.0 | 1.6.99 |
| 1.6.1-rc.1 / 1.6.1 | 1.6.101 / 1.6.199 |

So rc.1 → rc.2 → final upgrade in place, while rc.2 → rc.1 and final → any candidate are refused as downgrades. Same-version upgrades are **off** (`AllowSameVersionUpgrades` was removed): a rebuilt package of an installed version is rejected (a new rebuild needs the next `rc.N`), and re-running the installed MSI is a repair. Only `rc.N` pre-release suffixes are supported (anything else fails the installer build), the patch number is limited to 654, and Windows Installer limits major/minor to 255. Installed apps shows the numeric version; the readable label is in its *Comments*.

**Never change the MSI `UpgradeCode`** (`D77CC99D-4E73-41BF-BD16-48C2882CE96F`) — it is how upgrades find older installs.

## Automated verification (`build/Test-Release.ps1` + `TestFeatures`)

Read-only; installs nothing and touches no real application data:

- artifact names, SHA-256 checksums, portable EXE ↔ MSI payload identity;
- MSI tables: identity, per-machine scope, Installed apps properties, upgrade rows, launch condition, one file in `ProgramFiles64Folder`, shortcuts/features (desktop shortcut off by default);
- **data safety**: no AppData/ProgramData directories, no `RemoveFile`/`RemoveRegistry`, only `HKLM\Software\AuraClean` registry values, no services, no script/DLL custom code, and exactly four allow-listed uninstall-only cleanup commands (`schtasks.exe /Delete` for the two AuraClean tasks, `reg.exe delete` for the two `AuraCleanUninstall` keys);
- **upgrade ordering**: the Upgrade table's three rules (older → upgrade, newer → blocked, identical → blocked) are checked against the built MSI and evaluated over rc.1 → rc.2 → final → next release (and the reverse), using the MSI versions MSBuild derives;
- **uninstall-only cleanup**: the real conditions in the built MSI are evaluated (read-only, `OpenPackage` with *ignore machine state*) for fresh install, maintenance, repair, upgrade and uninstall — the four commands fire only on uninstall;
- **build-script safety** (`build/Test-BuildScripts.ps1`): a stale or other-version MSI left in `installer/bin/Release` is never collected or renamed; only the file this build produced is accepted;
- version consistency (props ↔ assembly ↔ manifest ↔ MSI ↔ changelog ↔ release notes);
- the legacy-cleanup script against synthetic folders (data preserved; `-WhatIf` removes nothing);
- unsigned status matches the release notes.

`TestFeatures/ReleaseMetadataTests.cs` repeats the source-level rules inside the normal test run.

## Manual release checks (what automation cannot prove)

Do these on a **clean, disposable Windows 10 and Windows 11 VM or Windows Sandbox** (take a snapshot first; **never on a machine with data you care about**) before publishing. The scripted part below (steps A–H) is automated on disposable Windows Server runners by `.github/workflows/msi-acceptance.yml` (`build/Test-MsiLifecycle.ps1`); repeat it by hand on the client VMs to confirm the same results on Windows 10/11. Commands assume an elevated PowerShell in the folder holding the MSIs; `$rc1` is the ORIGINAL released rc.1 (.NET 8, MSI 1.6.1; verify its SHA-256 against `build/historical-artifacts.json`), `$rc2` the rc.2 candidate (.NET 10, MSI 1.6.2) and `$final` a disposable 1.6.99 package from `build/Build-AcceptancePackages.ps1`.

**Integration + upgrade + downgrade script (every `msiexec` exit code is recorded; expected values in parentheses):**

```powershell
# A. Fresh install, silent, with a log
msiexec /i $rc1 /qn /l*v a-install.log                          # 0
(Get-Package AuraClean -ProviderName msi).Version                # 1.6.1
# B. Create the three integrations exactly as the app does (Settings → Launch at sign-in, Scheduled cleanup, Explorer menu),
#    plus some state (a setting, a leftover backup of a disposable app).
Get-ScheduledTask AuraClean_LaunchAtLogon, AuraClean_ScheduledCleanup   # both present
reg query HKLM\SOFTWARE\Classes\exefile\shell\AuraCleanUninstall        # present
# C. Upgrade rc.1 -> rc.2 (needs AuraClean closed)
msiexec /i $rc2 /qn /l*v c-upgrade.log                          # 0; one Installed-apps entry, version 1.6.2
Get-ScheduledTask AuraClean_*; reg query HKLM\SOFTWARE\Classes\exefile\shell\AuraCleanUninstall   # STILL present (upgrade keeps them)
# D. Downgrade is refused
msiexec /i $rc1 /qn /l*v d-downgrade.log                        # 1603 ("newer ... already installed" in the log); installed stays 1.6.2
# E. Same version rebuilt / re-run is refused as a fresh install, but is a repair for the installed MSI
Remove-Item "$env:ProgramFiles\AuraClean\AuraClean.exe"
msiexec /fa $rc2 /qn /l*v e-repair.log                          # 0; AuraClean.exe is back
# F. rc.2 -> final
msiexec /i $final /qn /l*v f-final.log                          # 0; version 1.6.99; integrations still present
msiexec /i $rc2 /qn                                             # 1603 (final -> rc downgrade refused)
# G. Uninstall (genuine removal)
msiexec /x $final /qn /l*v g-uninstall.log                      # 0
Get-ScheduledTask AuraClean_* -ErrorAction SilentlyContinue     # none
reg query HKLM\SOFTWARE\Classes\exefile\shell\AuraCleanUninstall; reg query HKLM\SOFTWARE\Classes\Msi.Package\shell\AuraCleanUninstall   # both: key not found
Test-Path "$env:LOCALAPPDATA\AuraClean"                         # True: settings, journals, quarantine intact
Test-Path "$env:ProgramFiles\AuraClean\AuraClean.exe"           # False
# H. Reinstall: the leftover-backup list and settings are still there; restore still works.
```

Unrelated scheduled tasks and Explorer context-menu entries (other tools' `shell\...` keys) must be untouched by step G; compare `Get-ScheduledTask` / the `exefile\shell` subkeys before and after.

**Interactive and environment checks:**

1. Install the MSI interactively: UAC prompt, feature page (desktop shortcut off), finish page; Start Menu entry, Installed apps entry with icon/version; app starts (UAC) and shows `v<label>`.
2. Create some state (change a setting, create a leftover backup with the Uninstaller on a disposable app). Close the app.
3. **Upgrade**: install the next build over it. Verify settings and the leftover-backup journal/restore list are intact and restore still works.
4. **Running app**: start AuraClean, run the installer: it must show "files in use" (or defer to reboot when silent) and must **not** kill AuraClean.
5. **Uninstall** via Installed apps: program, shortcuts and `HKLM\Software\AuraClean` are gone; `%LocalAppData%\AuraClean` and its data remain. Reinstall: everything is still there.
6. **Repair** (`msiexec /fa <msi>`) after deleting `AuraClean.exe`: file restored.
7. **Legacy**: on a machine with an `install.ps1` copy, install the MSI: final page mentions it, nothing deleted; then `scripts/Remove-LegacyInstall.ps1 -WhatIf` / run.
8. Silent install/uninstall (`/qn`) and `ADDLOCAL=ALL`.
9. Record exactly what SmartScreen/UAC showed for the unsigned build and update the release notes.
10. Windows 10 smoke test of the app itself (dashboard, cleaner scan, uninstaller list, shredder on synthetic files).

## .NET 10 LTS and release labels (rc.1 = .NET 8, rc.2 = .NET 10)

The application and tests target **`net10.0-windows`** (.NET 8 support ends 10 November 2026; .NET 10 is the LTS release). Publishing stays self-contained, single-file, win-x64, with native-library self-extract; the manifest (administrator elevation, per-monitor DPI) is unchanged. Compatibility finding: .NET 9+ WPF added `System.Windows.ThemeMode`, which collided with AuraClean's own `ThemeMode`; `ThemeService` aliases its own enum. Packages (`MaterialDesignThemes`, `System.Data.SQLite.Core` with its native `SQLite.Interop.dll`, `TaskScheduler`, …) needed no change.

**The current candidate is `1.6.0-rc.2` (MSI `1.6.2`, .NET 10).** The original **`1.6.0-rc.1` (MSI `1.6.1`) was built on .NET 8 and its label is retired**: those artifacts are never rebuilt, relabelled or replaced. They are recorded in [`build/historical-artifacts.json`](../build/historical-artifacts.json) (commit, workflow run, SHA-256 of the MSI and portable EXE, runtime) and the original release notes stay as `docs/release-notes/v1.6.0-rc.1.md`.

- **Guards:** `build/Build-Release.ps1` refuses to build under a retired label; `build/Test-Release.ps1` fails if the label is retired or if any artifact is byte-identical to a retired one; `build/Test-BuildScripts.ps1` and the acceptance-kit tests cover both.
- **Cross-runtime upgrade evidence:** the lifecycle test installs the **original** rc.1 (verified against its pinned hash, MSI version and label before anything is installed), then upgrades it in place to the rc.2 candidate and checks the version, label, replaced EXE, preserved data and integrations; it also repairs rc.2, refuses a downgrade to rc.1 (1603) and installs rc.2 fresh. The disposable `final` / `final-rebuild` (1.6.99) packages exist only to test the future-upgrade, downgrade and duplicate rules. The acceptance scripts refuse any package whose version, label or hash is not what they expect.
- **Where the original rc.1 comes from:** the `msi-acceptance` workflow downloads it from the workflow run that built it (`actions: read`) and verifies the pins; the acceptance kit's `Pack` mode ships it in `historical\`. GitHub workflow artifacts **expire** (the recorded run's on 2026-11-21): copy the two original files to durable storage before then (owner decision: where), otherwise the CI cross-runtime check fails with an explicit "NOT TESTED" error rather than being skipped.
- **For the next candidate (rc.3 / final):** update `VersionSuffix`/`VersionPrefix`, the `CHANGELOG.md` and `docs/release-notes/v<label>.md`, the expected candidate constants (`$ExpectedRc2Version`/`$ExpectedRc2Label` in `build/Test-MsiLifecycle.ps1`, `$ExpectedMsiVersions`/`$ExpectedRc2Version` in `acceptance/Start-Acceptance.ps1`, `ExpectedLabel` in `acceptance/AcceptanceChecks.ps1`), the checklist/template version text, add the just-superseded release to `build/historical-artifacts.json` when it is published, and rerun both workflows and client acceptance on the new artifacts. Evidence from one runtime or label does not carry over to another.

## Client-Windows acceptance kit

[`acceptance/`](../acceptance/README.md) packages the lifecycle test plus a read-only preparation mode, a typed disposable-machine confirmation, a manual checklist (UAC, SmartScreen, wizard, Settings-created integrations, files in use, legacy install), a results template and a report generator, for running gates 1–8 on a separate Windows 11 / Windows 10 machine. It never runs destructive steps without the confirmation and never on a machine that already has AuraClean data.

## Release gates (candidate 1.6.0-rc.2)

State as of the `1.6.0-rc.2` candidate (.NET 10). Merging packaging code is **not** approval to publish.

**Done (automated, disposable GitHub-hosted runners):** real `msiexec` install, repair, rc.1 → rc.2 → final upgrade, downgrade and same-version-rebuild refusal (exit 1603), genuine uninstall, silent mode, optional desktop shortcut via `ADDLOCAL=ALL`, preservation of `%LocalAppData%\AuraClean` data, uninstall-only removal of the AuraClean scheduled tasks and context-menu keys (unrelated ones untouched), portable-copy interaction. 96/96 checks on Windows Server 2022 and on Windows Server 2025 (workflow *MSI acceptance*; the first run found and fixed a launch-condition defect that rejected every machine).

**Not proven, and not implied by the above:** the runners are Windows **Server**, administrator, UAC off, non-interactive. They do not prove Windows 10 or 11 client behavior.

| # | Gate | Needs |
|---|---|---|
| 1 | Interactive install on **Windows 10** and **Windows 11** (wizard, feature page, finish page, Installed apps entry/icon) | Client VM per OS |
| 2 | **UAC and elevated launch**: installer elevation prompt, AuraClean start prompt, `--minimized` launch at sign-in | Client VM, UAC on |
| 3 | Optional **desktop shortcut** chosen in the wizard (default off) | Client VM |
| 4 | **Files in use**: install/upgrade while AuraClean is running must show the standard prompt (or defer to reboot silently) and never kill the app | Client VM |
| 5 | **SmartScreen / "Unknown publisher"** behavior for the unsigned MSI and EXE; record what is shown and update the release notes | Client VM, file downloaded from the internet (Mark of the Web) |
| 6 | **App-created integrations through the real Settings UI** (launch at sign-in, scheduled cleanup, Explorer context menu): created by the app, kept by upgrade/repair, removed by uninstall | Client VM |
| 7 | **Signing decision** ([SIGNING.md](SIGNING.md)): sign RC1 or ship it unsigned with the SmartScreen caveat documented. Owner decision; never commit certificates or secrets. If signed, re-run gates 1–6 on the signed files | Owner |
| 8 | Legacy `install.ps1` detection and `scripts/Remove-LegacyInstall.ps1` on a machine that has an old copy | Client VM with an old install |

**Operating-system scope.** The MSI launch condition is `VersionNT64 >= 603` because Windows Installer reports a capped version (603 even on Server 2022/2025), so `>= 1000` rejected every machine. The installer therefore also runs on some older 64-bit Windows versions. **Windows 10 and 11 are the only supported targets; older systems are unsupported and have not been tested.**

**Recommended path (never on the maintainer's own machine or installation).**
1. Use a throw-away client Windows environment with snapshots: a VirtualBox (or other hypervisor) VM on the maintainer's PC using a Windows 10/11 evaluation or licensed image, or a spare physical test PC, or a temporary cloud Windows 10/11 client VM, whichever licensing and availability allow. Windows 11 Home cannot host Hyper-V or Windows Sandbox, so use VirtualBox/VMware or a separate machine. Give the VM its own copy of the artifacts (download the workflow artifact or the CI MSI into the VM; no shared folders with the real profile).
2. Take a snapshot of the clean VM, run gates 1–6 and 8 for Windows 11 (and again for Windows 10), revert to the snapshot between scenarios.
3. Record results (screenshots of UAC/SmartScreen, exit codes, `msiexec /l*v` logs) in the release notes' "Known limitations", then decide on signing and repeat on signed files if signed.
4. Only then create a **draft** release (see Publishing). No tag or public release before all gates are recorded.

## CI

`.github/workflows/release.yml` (manual dispatch, or a pushed `v*` tag) runs `build/Build-Release.ps1 -NoUiTests` on `windows-latest` and uploads `artifacts/release/*` as a workflow artifact. It **never creates a GitHub Release**. The WPF UI fixtures need an interactive desktop and are run locally (they also run when you invoke the script locally without `-NoUiTests`).

## Publishing (manual, after approval)

1. All manual checks above pass and are recorded in the release notes.
2. Decide on signing ([SIGNING.md](SIGNING.md)); if signed, re-run the checks on the signed artifacts.
3. Create a **draft** GitHub Release for the tag, attach the three files, paste `docs/release-notes/v<label>.md`, and publish only after sign-off.
