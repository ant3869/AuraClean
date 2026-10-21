# Installing, upgrading and uninstalling AuraClean

This guide covers the **MSI installer** and the **portable EXE** of release candidate `1.6.0-rc.1`.

## Requirements

| | |
|---|---|
| OS | 64-bit **Windows 10 or Windows 11** (the installer refuses to run on anything older). The release candidate has so far only been exercised on Windows 11; see "Known limitations" in the release notes. |
| Privileges | **Administrator.** The MSI installs per-machine (UAC prompt), and AuraClean itself always starts elevated by design (registry scanning, restore points, system cleanup). |
| Runtime | None to install. The build is self-contained (.NET 8 is bundled). |
| Disk | About 75 MB for the program. |

## Verify what you downloaded

Each release lists `SHA256SUMS.txt`. In PowerShell:

```powershell
Get-FileHash .\AuraClean-1.6.0-rc.1-win-x64.msi -Algorithm SHA256   # compare with SHA256SUMS.txt
```

> Release candidates are **not code-signed**. A matching checksum proves the file is the one that was published; it does not make the publisher "verified" to Windows.

## Install (MSI)

1. Double-click `AuraClean-1.6.0-rc.1-win-x64.msi`.
2. Accept the UAC prompt. Because the file is unsigned it will say **Unknown publisher**; this is expected for test builds. SmartScreen may first show "Windows protected your PC" for a downloaded file: choose *More info → Run anyway* only if the checksum matched.
3. Choose features: AuraClean (required) and, optionally, a **Desktop shortcut** (off by default).
4. Finish. Start AuraClean from the Start Menu. Launching shows a UAC prompt every time (that is the app's administrator requirement, not an installer problem).

Silent install (for managed deployment):

```powershell
msiexec /i AuraClean-1.6.0-rc.1-win-x64.msi /qn                    # Start Menu shortcut only
msiexec /i AuraClean-1.6.0-rc.1-win-x64.msi /qn ADDLOCAL=ALL       # plus desktop shortcut
msiexec /i AuraClean-1.6.0-rc.1-win-x64.msi /qn /l*v install.log    # with a verbose log
```

### What goes where

| What | Where | Touched by install / upgrade / uninstall? |
|---|---|---|
| Program (`AuraClean.exe`) | `%ProgramFiles%\AuraClean` (not writable by standard users) | Yes (that is the install) |
| Start Menu / Desktop shortcuts | All-users Start Menu / Public Desktop | Yes |
| Installed apps entry | `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall` + `HKLM\Software\AuraClean` (install folder, version) | Yes |
| **Your data**: settings, logs, cleanup history, leftover-backup journals and backups, quarantine, install-monitor snapshots | `%LocalAppData%\AuraClean\...` | **Never** |

The installer contains no services, scheduled tasks or background components.

## Upgrade

Run the newer MSI. It replaces the installed version in place and keeps everything in `%LocalAppData%\AuraClean`, including leftover-backup journals needed for recovery.

- Release candidates that share a numeric version (`1.6.0-rc.1` → `1.6.0-rc.2` → `1.6.0`) replace each other.
- Installing an **older** version over a newer one is refused with a message.
- **Close AuraClean first.** The installer never closes it for you (it might be in the middle of a cleanup). If it is still running, Windows shows the standard "files in use" prompt; for silent installs the replacement is completed after a reboot instead.

## Uninstall

*Settings → Apps → Installed apps → AuraClean → Uninstall* (or `msiexec /x AuraClean-1.6.0-rc.1-win-x64.msi`).

This removes the program, shortcuts and install metadata. **It does not delete your data** in `%LocalAppData%\AuraClean`, so leftover backups you may still want to restore survive an uninstall/reinstall. If you also want that data gone, delete the folder yourself (this is a separate, deliberate choice and cannot be undone):

```powershell
Remove-Item "$env:LOCALAPPDATA\AuraClean" -Recurse   # only if you are sure; it contains your leftover backups
```

## Coming from `install.ps1` (older developer installs)

The previous `install.ps1` copied `AuraClean.exe` into `%LocalAppData%\AuraClean` and made a desktop shortcut. The MSI **never deletes or modifies that copy**; it only mentions on the last page if it finds one. Both can coexist, but you will have two shortcuts/executables.

Recommended migration (your data needs no migration because both use the same data folder):

1. Close AuraClean and install the MSI. Confirm the new Start Menu entry works and your settings and leftover backups are there.
2. See what would be cleaned up:
   ```powershell
   pwsh .\scripts\Remove-LegacyInstall.ps1 -WhatIf
   ```
3. If you agree, run it without `-WhatIf` (it asks for confirmation). It removes **only** the old `AuraClean.exe` and a desktop shortcut that points at it. It never touches settings, logs, history, backups or quarantine, and refuses to run while that executable is running.

`install.ps1` itself is now a developer-only script: it refuses to run next to an MSI install (unless `-Force`) and never terminates a running AuraClean.

## Portable EXE

`AuraClean-1.6.0-rc.1-win-x64-portable.exe` is the same self-contained program as inside the MSI, as a single file. It is meant for trying AuraClean or running it from a USB stick.

- No install, no shortcuts, no Installed apps entry, no upgrade path. Delete the file to remove it.
- It still requires administrator rights (UAC prompt) and stores data in the **same** `%LocalAppData%\AuraClean` folder, so settings are shared with an installed copy.
- It is bit-for-bit the executable inside the MSI (checked at build time).

## UAC and SmartScreen (unsigned test builds)

| Situation | What you will see |
|---|---|
| Running the MSI | UAC dialog with **Unknown publisher** (yellow/blue shield, file origin shown). |
| Running a downloaded EXE/MSI | Possibly SmartScreen "Windows protected your PC / Unknown publisher" → *More info → Run anyway*. |
| Starting AuraClean | UAC dialog every launch; for the installed copy it shows the executable path under Program Files. |

A signed build changes the publisher line, but **does not by itself guarantee** that SmartScreen stays quiet; reputation also depends on download history. See [SIGNING.md](SIGNING.md).

## Troubleshooting

- *Installer says "newer version installed"* → uninstall the newer version first if you really want to go back.
- *"Files in use"* → close AuraClean (check the system tray; it minimizes there) and retry.
- Collect a log: `msiexec /i <msi> /l*v install.log` and attach it to an issue.
