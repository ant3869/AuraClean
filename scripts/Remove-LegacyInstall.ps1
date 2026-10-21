<#
.SYNOPSIS
    Optional, explicit clean-up of an old AuraClean copy installed by install.ps1.

.DESCRIPTION
    The old install.ps1 put AuraClean.exe directly into %LocalAppData%\AuraClean, next to your settings and data, and
    created a desktop shortcut. The MSI installs to %ProgramFiles%\AuraClean instead and never touches the old copy.

    This script is NEVER run by the installer. Run it yourself, after the MSI install works, if you want to remove the
    old executable and shortcut. It removes ONLY:
      * <LegacyDir>\AuraClean.exe
      * <DesktopDir>\AuraClean.lnk, and only if that shortcut points at the legacy executable
    It never touches Settings, Logs, History, LeftoverBackup, Quarantine or any other data. It refuses to run while
    that executable is running, and supports -WhatIf / -Confirm.

.PARAMETER LegacyDir
    Folder holding the legacy install (default: %LocalAppData%\AuraClean).

.PARAMETER DesktopDir
    Folder searched for the legacy shortcut (default: your desktop).

.EXAMPLE
    pwsh scripts/Remove-LegacyInstall.ps1 -WhatIf     # show what would be removed
    pwsh scripts/Remove-LegacyInstall.ps1             # remove (asks for confirmation)
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$LegacyDir = (Join-Path $env:LOCALAPPDATA 'AuraClean'),
    [string]$DesktopDir = [Environment]::GetFolderPath('Desktop')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$legacyExe = Join-Path $LegacyDir 'AuraClean.exe'
if (-not (Test-Path -LiteralPath $legacyExe -PathType Leaf)) {
    Write-Host "No legacy AuraClean.exe found in $LegacyDir - nothing to do." -ForegroundColor Green
    return
}

# Never remove a binary that is currently running.
$running = Get-Process -Name 'AuraClean' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -ieq [IO.Path]::GetFullPath($legacyExe)) } catch { $false }
}
if ($running) {
    throw "The legacy AuraClean.exe is running (PID $($running[0].Id)). Close AuraClean and run this script again."
}

# The shortcut is removed only if it demonstrably points at the legacy executable.
$shortcutToRemove = $null
$lnk = Join-Path $DesktopDir 'AuraClean.lnk'
if (Test-Path -LiteralPath $lnk -PathType Leaf) {
    $target = $null
    try { $target = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk).TargetPath } catch { }
    if ($target -and ([IO.Path]::GetFullPath($target) -ieq [IO.Path]::GetFullPath($legacyExe))) { $shortcutToRemove = $lnk }
    else { Write-Host "Leaving $lnk alone: it does not point at the legacy executable." -ForegroundColor Yellow }
}

if ($PSCmdlet.ShouldProcess($legacyExe, 'Delete the legacy AuraClean.exe')) {
    Remove-Item -LiteralPath $legacyExe -Force
    Write-Host "Removed $legacyExe" -ForegroundColor Green
}
if ($shortcutToRemove -and $PSCmdlet.ShouldProcess($shortcutToRemove, 'Delete the legacy desktop shortcut')) {
    Remove-Item -LiteralPath $shortcutToRemove -Force
    Write-Host "Removed $shortcutToRemove" -ForegroundColor Green
}
Write-Host "Your settings, logs, backups and recovery data in $LegacyDir were NOT touched." -ForegroundColor Cyan
