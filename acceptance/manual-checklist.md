# Manual acceptance checklist (client Windows)

Run on a disposable Windows 11 machine, then repeat on Windows 10. Revert to the clean snapshot between independent scenarios. Use only synthetic
files and disposable apps. For each item set `Status` in `results\manual-results.json` to **PASS**, **FAIL** or **NOT TESTED**, with a note and a screenshot name.
Log each MSI run with `msiexec /i <msi> /l*v results\msi-logs\manual-<n>.log`.

Evidence per item: a screenshot named `Mxx-short-description.png` in `results\screenshots\`.

| ID | Steps | Pass when | Evidence |
|---|---|---|---|
| **M01 Wizard** | Double-click the official MSI (internet-downloaded copy). Welcome → Next → feature page → Install → Finish. | All pages appear; Finish page says settings/backups/recovery data are never removed; installs without errors. | Screenshot of each page |
| **M02 Install UAC** | Same run: note the UAC prompt before install. | Prompt appears; publisher shown as "Unknown"/unverified (unsigned). | UAC screenshot |
| **M03 Installed apps** | Settings → Apps → Installed apps → AuraClean. | Name AuraClean, version **1.6.1**, icon, publisher AuraClean; Comments (Programs and Features) "AuraClean 1.6.0-rc.1". | Screenshot |
| **M04 Desktop shortcut** | First install: leave the feature page default. Repeat after reverting: select "Desktop shortcut". | Default: no shortcut. Selected: `AuraClean` on the desktop. | Two screenshots |
| **M05 Elevated launch** | Start Menu → AuraClean. | UAC prompt; app opens; shows **v1.6.0-rc.1**; Task Manager shows it elevated. | UAC + window |
| **M06 SmartScreen** | Run the internet-downloaded official MSI and portable EXE from Explorer (not PowerShell). | Record exactly what SmartScreen shows (blocked / "Windows protected your PC" / Unknown publisher / nothing) and whether "Run anyway" is available. | Screenshots; fill the release notes |
| **M07 Launch at sign-in** | AuraClean → Settings → enable *Launch at sign-in*. Check Task Scheduler for `AuraClean_LaunchAtLogon`. Sign out and in. | Task exists (Highest privileges); ~30 s after sign-in AuraClean starts minimized (tray). | Task Scheduler + tray |
| **M08 Scheduled cleanup** | Settings → enable *Scheduled cleanup* (any frequency/time). | Task `AuraClean_ScheduledCleanup` exists with action `AuraClean.exe /autoclean`. | Task Scheduler |
| **M09 Explorer menu** | Settings → enable the Explorer context menu. Right-click a `.exe` and a `.msi`. | "Deep Uninstall with AuraClean" appears for both. | Screenshots |
| **M10 Integrations survive upgrade/repair** | With M07–M09 enabled: install the rc.2 test MSI over it (`packages\rc2.msi`), then repair (`msiexec /fa`). | Tasks and menu entries still present and working after each. | Task Scheduler + menu |
| **M11 Files in use** | Start AuraClean, then run an upgrade (`packages\rc2.msi`) interactively; then repeat silently (`/qn`). | Interactive: standard "files in use" prompt; AuraClean is **not** closed by the installer. Silent: upgrade completes after reboot/close, still no kill. | Prompt screenshot, exit code |
| **M12 Uninstall** | Settings → Apps → AuraClean → Uninstall. | Program, shortcuts, tasks (M07/M08) and menu entries (M09) gone; `%LocalAppData%\AuraClean` still present; unrelated tasks/keys untouched. | Before/after listings |
| **M13 Data survives** | Via the app: change a setting, create a leftover backup of a disposable test app (Uninstaller), create a quarantine item. Upgrade, uninstall, reinstall. | Everything is still there after each; the leftover restore works. | Screenshots |
| **M14 Legacy install** | On a machine that has a legacy `install.ps1` copy in `%LocalAppData%\AuraClean`: install the MSI. Then `scripts\Remove-LegacyInstall.ps1 -WhatIf`, then run it for real. | Finish page mentions the old copy; nothing deleted by the MSI; the script removes only the old EXE/shortcut; data untouched. | Finish page + folder listing |

Note: M14 needs the legacy copy, so run it on its own snapshot **before** `Lifecycle` mode (which requires a machine with no AuraClean data).

Record the exact Windows build (`winver` / `results\environment.json`) in every report. Windows 10 and Windows 11 results are separate.
