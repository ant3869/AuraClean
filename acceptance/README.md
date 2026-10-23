# AuraClean RC1 — client Windows acceptance kit

For finishing RC1 acceptance on a **separate, disposable Windows 11 (and Windows 10) machine**. It reuses the lifecycle test that already
runs on GitHub (`build/Test-MsiLifecycle.ps1`, workflow *MSI acceptance*) and adds the safety rails, evidence capture and the manual checklist
for what automation cannot prove. Nothing here is meant for, or will run on, a developer's own computer.

| | Automated by the kit | Manual (human, screenshots) |
|---|---|---|
| Artifact SHA-256 + MSI version verification | yes (`Prepare`) | |
| Windows build / UAC / SmartScreen state capture | yes (`Prepare`) | |
| Real install, repair, upgrade, downgrade refusal, uninstall, silent mode, optional desktop shortcut | yes (`Lifecycle`, 96 checks, verbose `msiexec` logs) | |
| Data preservation (settings, journals, quarantine hash-compared), exact task/menu cleanup, look-alike items untouched | yes (`Lifecycle`) | |
| Report with PASS / FAIL / NOT TESTED | yes (`Report`) | fill `manual-results.json` |
| Installer wizard and finish page, **UAC prompts**, **SmartScreen**, running-app "files in use", sign-in launch, **integrations created through the Settings UI**, legacy `install.ps1` | | **yes**: [manual-checklist.md](manual-checklist.md) |

## Safety rules (enforced by the scripts)

- `Prepare` and `Report` are **read-only** apart from writing into `results\`.
- `Lifecycle` installs and uninstalls software. It requires `-ConfirmDisposable I-CONFIRM-<COMPUTERNAME>-IS-DISPOSABLE`, Administrator rights, passing `Prepare` checks, and **refuses if AuraClean, its data folder (`%LocalAppData%\AuraClean`), its scheduled tasks or its menu keys already exist**. It never deletes data it did not create.
- Use a fresh machine or a VM/disk snapshot and revert afterwards. Do not run it on a machine you care about.
- No signing secrets, no cloud resources, no publishing are involved. The artifacts are unsigned.

## What you need on the test machine

Windows 11 x64 (and later Windows 10) with UAC **on**, an Administrator account, internet access for the SmartScreen check, and Windows PowerShell 5.1 or PowerShell 7. A physical spare PC or a properly licensed client VM both work; take a snapshot first.

## 1. Build the kit (developer machine, build/copy only)

```powershell
# official RC1 files from the "Release build" workflow run on master (artifact AuraClean-release-artifacts) -> .\rc1-artifacts
# throw-away rc.2/final test packages (never published; builds only, installs nothing):
pwsh build/Build-AcceptancePackages.ps1 -OutputDir .\packages
pwsh acceptance/Start-Acceptance.ps1 -Mode Pack -ArtifactDir .\rc1-artifacts -PackageDir .\packages -KitZip .\AuraClean-acceptance-kit.zip
```

`Pack` verifies the artifacts and packages first (and refuses on any gap), then zips the kit scripts, docs, `Test-MsiLifecycle.ps1`, `scripts\Remove-LegacyInstall.ps1` (for check M14; never run automatically), the artifacts and the test packages, checks the zip contents, and prints its SHA-256. Copy the zip to the test machine (USB, or download it inside the VM) and compare that hash. For the SmartScreen check, also download the official MSI/EXE **from the internet** (e.g. the workflow artifact) so they carry the Mark of the Web.

## 2. On the test machine (elevated PowerShell)

```powershell
Expand-Archive .\AuraClean-acceptance-kit.zip C:\AuraKit; cd C:\AuraKit
.\Start-Acceptance.ps1 -Mode Prepare            # read-only: environment, hashes, "is this machine clean?"
# take a VM snapshot / disk image here, then:
.\Start-Acceptance.ps1 -Mode Lifecycle -ConfirmDisposable I-CONFIRM-$($env:COMPUTERNAME.ToUpper())-IS-DISPOSABLE
```

Revert to the clean snapshot, then do the manual checks ([manual-checklist.md](manual-checklist.md)), recording each result in `results\manual-results.json` (`Status`: `PASS`, `FAIL` or `NOT TESTED`; add `Note` and `Screenshot`). Save screenshots in `results\screenshots\`. Then:

```powershell
.\Start-Acceptance.ps1 -Mode Report              # writes results\report.md
```

Copy `results\` (report, JSON, `msi-logs\*.log`, screenshots) back and attach it to the release-readiness note. Use [results-template.md](results-template.md) as the hand-written summary on top of `report.md`.

## Verdicts and what the report will not accept

`Report` returns one of **FAIL**, **INCOMPLETE** or **ALL CHECKS PASSED** (exit code 1, 2, 0). FAIL outranks INCOMPLETE.

- **Preparation** is part of the verdict: a failed hash/manifest/version check, a missing `prepare.json`, or a machine that was not clean fails the run. Failed advisory checks (not administrator, UAC off) leave it INCOMPLETE.
- **Artifact verification fails closed**: `SHA256SUMS.txt` must list exactly the official MSI and portable EXE, once each, well-formed, and both files must exist and match. Missing, extra, duplicate, malformed or corrupted entries stop `Prepare`, `Lifecycle` and `Pack`.
- **One run at a time**: `Prepare` creates `run.json` (a run id bound to the MSI's hash). Preparation, lifecycle and manual results must carry that run id, and the lifecycle results must match the hash recorded when they were produced. Results from another run, or edited afterwards, fail the report. Changing the artifacts (or `-NewRun`) archives the old results under `results\previous\<time>\`; nothing is deleted.
- **Lifecycle not run** = NOT TESTED = INCOMPLETE.
- **All 14 manual checks** must appear exactly once with a valid status (see the checklist). Missing, duplicate, unknown or malformed entries, and PASS without a note and an existing screenshot file, never produce a pass.

## Interpreting results

- Any automated FAIL, or a manual FAIL, is a candidate release blocker: keep the log/screenshot, reproduce on the snapshot, and report before any code change.
- Runs on Windows 10 and Windows 11 are separate evidence; do not infer one from the other, or from the Windows Server runs.

## Windows 10 support recommendation (not a policy change)

Windows 10's standard support ended in October 2025 and, to my knowledge, the consumer Extended Security Updates window closes around October 2026 (verify on Microsoft's lifecycle page). Recommendation for the owner to decide: **RC1 officially targets Windows 11**; treat Windows 10 22H2 as *best-effort legacy compatibility* (the .NET 8 self-contained build and the MSI should run on it, but it is report-and-fix, with no promises), and say so in the release notes. The installer's launch condition only requires 64-bit Windows, so it does not itself block older systems. Separately, .NET 8 reaches end of support in November 2026 per Microsoft's published lifecycle; plan a move to the next LTS for the first non-RC release.
