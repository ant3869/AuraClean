# AuraClean RC2 acceptance report — <Windows 11 | Windows 10>

Tester: <name> · Date: <yyyy-mm-dd> · Kit zip SHA-256: <hash>

## Test machine
- Machine (physical / VM + host): 
- Windows edition, version, **exact build** (e.g. 26100.xxxx): 
- UAC enabled: yes/no · SmartScreen setting: · Snapshot/baseline taken: yes/no

## Artifacts
| File | SHA-256 (matches SHA256SUMS.txt?) | Version | Signature |
|---|---|---|---|
| AuraClean-1.6.0-rc.2-win-x64.msi (.NET 10) | | 1.6.2 | NotSigned |
| AuraClean-1.6.0-rc.2-win-x64-portable.exe (.NET 10) | | 1.6.0-rc.2 | NotSigned |
| ORIGINAL AuraClean-1.6.0-rc.1-win-x64.msi (.NET 8, upgrade source) | pinned b30513db...c795c | 1.6.1 | NotSigned |
| Disposable packages (final / final-rebuild) | | 1.6.99 / 1.6.99 | n/a (not for release) |

## Automated lifecycle (`results\report.md`)
Result: __ PASS / __ FAIL of __ · lifecycle exit code: __ · MSI logs: `results\msi-logs\` 

## Manual checks
| ID | Result (PASS / FAIL / NOT TESTED) | MSI exit code / log | Observation | Screenshot |
|---|---|---|---|---|
| M01 Wizard and finish page | | | | |
| M02 Install UAC prompt | | | | |
| M03 Installed apps entry | | | | |
| M04 Optional desktop shortcut | | | | |
| M05 Elevated launch | | | | |
| M06 SmartScreen / publisher | | | | |
| M07 Launch at sign-in (`--minimized`) | | | | |
| M08 Scheduled cleanup task | | | | |
| M09 Explorer context menu | | | | |
| M10 Integrations survive upgrade/repair | | | | |
| M11 Files in use | | | | |
| M12 Uninstall removes integrations, keeps data | | | | |
| M13 Data survives upgrade/uninstall/reinstall | | | | |
| M14 Legacy install | | | | |

## Confirmed release blockers
(None / list: ID, what happened, log + screenshot, reproduced on a clean snapshot yes/no, smallest proposed fix)

## Status
- Signing: unsigned; decision: <owner decision, see docs/SIGNING.md>
- Distribution: not published, no tag, no GitHub Release
- Verdict for this OS: GO / NO-GO / INCOMPLETE — reason:
