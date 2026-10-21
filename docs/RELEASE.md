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

Prerequisites: 64-bit Windows 10/11, **.NET 8 SDK** (8.0.x), **PowerShell 7** (`pwsh`), network access for NuGet restore. Nothing else is installed machine-wide.

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

To cut a new candidate: change `VersionSuffix` (`rc.2`) or `VersionPrefix`, update `app.manifest`, add a `CHANGELOG.md` entry and `docs/release-notes/v<label>.md`, then build. Windows Installer ignores the fourth version field and has no pre-release concept, so same-numeric-version candidates are allowed to replace each other (`AllowSameVersionUpgrades`), and the final `X.Y.Z` replaces its candidates.

**Never change the MSI `UpgradeCode`** (`D77CC99D-4E73-41BF-BD16-48C2882CE96F`) — it is how upgrades find older installs.

## Automated verification (`build/Test-Release.ps1` + `TestFeatures`)

Read-only; installs nothing and touches no real application data:

- artifact names, SHA-256 checksums, portable EXE ↔ MSI payload identity;
- MSI tables: identity, per-machine scope, Installed apps properties, upgrade rows, launch condition, one file in `ProgramFiles64Folder`, shortcuts/features (desktop shortcut off by default);
- **data safety**: no AppData/ProgramData directories, no `RemoveFile`/`RemoveRegistry`, only `HKLM\Software\AuraClean` registry values, no services, no executable/script custom actions;
- version consistency (props ↔ assembly ↔ manifest ↔ MSI ↔ changelog ↔ release notes);
- the legacy-cleanup script against synthetic folders (data preserved; `-WhatIf` removes nothing);
- unsigned status matches the release notes.

`TestFeatures/ReleaseMetadataTests.cs` repeats the source-level rules inside the normal test run.

## Manual release checks (what automation cannot prove)

Do these on a **clean Windows 10 and a clean Windows 11 VM** (take a snapshot first) before publishing:

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

## CI

`.github/workflows/release.yml` (manual dispatch, or a pushed `v*` tag) runs `build/Build-Release.ps1 -NoUiTests` on `windows-latest` and uploads `artifacts/release/*` as a workflow artifact. It **never creates a GitHub Release**. The WPF UI fixtures need an interactive desktop and are run locally (they also run when you invoke the script locally without `-NoUiTests`).

## Publishing (manual, after approval)

1. All manual checks above pass and are recorded in the release notes.
2. Decide on signing ([SIGNING.md](SIGNING.md)); if signed, re-run the checks on the signed artifacts.
3. Create a **draft** GitHub Release for the tag, attach the three files, paste `docs/release-notes/v<label>.md`, and publish only after sign-off.
