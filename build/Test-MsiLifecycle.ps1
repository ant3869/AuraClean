<#
.SYNOPSIS
    REAL MSI lifecycle acceptance test: installs, repairs, upgrades, downgrades and uninstalls the AuraClean MSI with msiexec.

.DESCRIPTION
    DESTRUCTIVE to the machine it runs on (installs software, creates scheduled tasks and registry keys). It therefore refuses to
    run unless EITHER it is on a disposable GitHub-hosted runner (GITHUB_ACTIONS=true and RUNNER_ENVIRONMENT=github-hosted)
    OR the operator passes -ConfirmDisposableMachine "I-CONFIRM-<COMPUTERNAME>-IS-DISPOSABLE" (the acceptance kit,
    acceptance/Start-Acceptance.ps1, does this for a separate test PC / VM). Never run it on a developer machine.
    In both cases it also refuses if AuraClean (installed copy, data folder, tasks or menu keys) is already present.

    Needs the packages from build/Build-AcceptancePackages.ps1. Every msiexec exit code, file/registry state and data hash is
    asserted; any unexpected outcome fails the run. Nothing is skipped silently.

    Limitations (see docs/RELEASE.md): runners are administrator with UAC disabled, so elevation prompts, SmartScreen, the
    "files in use" dialog and interactive UI are NOT exercised here.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDir,
    [Parameter(Mandatory)][string]$LogDir,
    # Manual disposable-machine confirmation (not needed on a GitHub-hosted runner). Must equal I-CONFIRM-<COMPUTERNAME>-IS-DISPOSABLE.
    [string]$ConfirmDisposableMachine,
    # The ORIGINAL released RC1 MSI (.NET 8). It is never rebuilt: it must match the SHA-256 pinned in historical-artifacts.json.
    [Parameter(Mandatory)][string]$HistoricalRc1Msi,
    [string]$HistoricalJson = $(@((Join-Path $PSScriptRoot 'historical-artifacts.json'), (Join-Path $PSScriptRoot '..\build\historical-artifacts.json')) | Where-Object { Test-Path $_ } | Select-Object -First 1)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$isHostedRunner = ($env:GITHUB_ACTIONS -eq 'true' -and $env:RUNNER_ENVIRONMENT -eq 'github-hosted')
$expectedConfirmation = "I-CONFIRM-$($env:COMPUTERNAME.ToUpperInvariant())-IS-DISPOSABLE"
if (-not $isHostedRunner -and $ConfirmDisposableMachine -cne $expectedConfirmation) {
    throw "REFUSING TO RUN: this test installs and uninstalls software. Run it only on a disposable GitHub-hosted runner, or on a throw-away machine/VM and pass -ConfirmDisposableMachine $expectedConfirmation"
}

New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
$PackageDir = (Resolve-Path $PackageDir).Path
# Roles: rc1 = the ORIGINAL released .NET 8 RC1 (historical, hash-pinned); rc2 = the candidate under test (.NET 10);
# final / rebuild = disposable future-version packages used only for the upgrade, downgrade and duplicate rules.
$ExpectedRc2Version = '1.6.2'; $ExpectedRc2Label = '1.6.0-rc.2'      # update with each new candidate (see docs/RELEASE.md)
$Msi = @{ rc1 = $HistoricalRc1Msi; rc2 = "$PackageDir\rc2.msi"; final = "$PackageDir\final.msi"; rebuild = "$PackageDir\final-rebuild.msi" }
foreach ($k in $Msi.Keys) { if (-not (Test-Path $Msi[$k])) { throw "Missing package $($Msi[$k])" } }

$InstallDir  = Join-Path $env:ProgramFiles 'AuraClean'
$ExePath     = Join-Path $InstallDir 'AuraClean.exe'
$StartMenu   = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\AuraClean.lnk'
$PublicDesk  = Join-Path $env:PUBLIC 'Desktop\AuraClean.lnk'
$DataDir     = Join-Path $env:LOCALAPPDATA 'AuraClean'
$ExeMenuKey  = 'HKLM:\SOFTWARE\Classes\exefile\shell\AuraCleanUninstall'
$MsiMenuKey  = 'HKLM:\SOFTWARE\Classes\Msi.Package\shell\AuraCleanUninstall'
$DecoyKeys   = @('HKLM:\SOFTWARE\Classes\exefile\shell\OtherToolDecoy', 'HKLM:\SOFTWARE\Classes\exefile\shell\AuraCleanUninstallDecoy', 'HKLM:\SOFTWARE\Classes\Msi.Package\shell\OtherToolDecoy')
$OwnedTasks  = @('AuraClean_LaunchAtLogon', 'AuraClean_ScheduledCleanup')
$DecoyTasks  = @('OtherTool_Cleanup', 'NotAuraClean_LaunchAtLogon', 'AuraClean_LaunchAtLogon_Decoy')

$script:Results = New-Object System.Collections.Generic.List[object]
$script:Section = ''
function Section($t) { $script:Section = $t; Write-Host "`n== $t ==" -ForegroundColor Cyan }
function Check([bool]$ok, [string]$message) {
    $script:Results.Add([pscustomobject]@{ Section = $script:Section; Ok = $ok; Message = $message })
    if ($ok) { Write-Host "  PASS: $message" -ForegroundColor Green } else { Write-Host "  FAIL: $message" -ForegroundColor Red }
}

$script:LogN = 0
function Invoke-Msi([string]$what, [string[]]$msiArgs) {
    $script:LogN++
    $log = Join-Path $LogDir ('{0:00}-{1}.log' -f $script:LogN, ($what -replace '[^\w\-]+', '_'))
    $argList = $msiArgs + @('/qn', '/norestart', '/l*v', "`"$log`"")
    $p = Start-Process msiexec.exe -ArgumentList $argList -Wait -PassThru -WindowStyle Hidden
    Write-Host "  msiexec $($msiArgs -join ' ') -> exit $($p.ExitCode)  (log: $(Split-Path -Leaf $log))"
    $p.ExitCode
}
function Get-LogText([string]$what) { Get-Content (Get-ChildItem $LogDir -Filter "*-$what.log" | Select-Object -First 1).FullName -Raw }

# ── state readers ─────────────────────────────────────────────────────────────────────────────────
function Get-ArpEntries {
    $roots = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    @($roots | Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem $_ } | ForEach-Object { Get-ItemProperty $_.PSPath } |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'AuraClean' })
}
function Get-Installed { $e = @(Get-ArpEntries); if ($e.Count -eq 1) { $e[0] } else { $null } }
function Get-InstalledVersion { $e = Get-Installed; if ($e) { $e.DisplayVersion } else { $null } }
function Test-Task($name) { $null -ne (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) }
function Get-AllTaskNames { @(Get-ScheduledTask | ForEach-Object TaskName | Sort-Object) }

function New-OwnedIntegrations([string]$exe) {
    # The same objects LaunchAtLogonService / ScheduledCleanupService / ContextMenuService create.
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    Register-ScheduledTask -TaskName 'AuraClean_LaunchAtLogon' -Principal $principal -Action (New-ScheduledTaskAction -Execute $exe -Argument '--minimized') -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $user) | Out-Null
    Register-ScheduledTask -TaskName 'AuraClean_ScheduledCleanup' -Principal $principal -Action (New-ScheduledTaskAction -Execute $exe -Argument '/autoclean') -Trigger (New-ScheduledTaskTrigger -Daily -At 3am) | Out-Null
    foreach ($key in $ExeMenuKey, $MsiMenuKey) {
        New-Item -Path $key -Force | Out-Null
        Set-ItemProperty -Path $key -Name '(default)' -Value 'Deep Uninstall with AuraClean'
        Set-ItemProperty -Path $key -Name 'Icon' -Value $exe
        New-Item -Path "$key\command" -Force | Out-Null
        Set-ItemProperty -Path "$key\command" -Name '(default)' -Value "`"$exe`" --deep-uninstall `"%1`""
    }
}
function New-Decoys {
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    foreach ($t in $DecoyTasks) { Register-ScheduledTask -TaskName $t -Principal $principal -Action (New-ScheduledTaskAction -Execute "$env:windir\System32\whoami.exe") -Trigger (New-ScheduledTaskTrigger -Daily -At 4am) | Out-Null }
    foreach ($k in $DecoyKeys) { New-Item -Path "$k\command" -Force | Out-Null; Set-ItemProperty -Path "$k\command" -Name '(default)' -Value 'decoy' }
}
function Test-OwnedIntegrations([bool]$expected, [string]$when) {
    $tasksOk = @($OwnedTasks | Where-Object { (Test-Task $_) -ne $expected }).Count -eq 0
    $keysOk = ((Test-Path $ExeMenuKey) -eq $expected) -and ((Test-Path $MsiMenuKey) -eq $expected)
    $state = if ($expected) { 'present' } else { 'removed' }
    Check $tasksOk "${when}: both AuraClean scheduled tasks are $state"
    Check $keysOk "${when}: both AuraCleanUninstall context-menu keys are $state"
}
function Test-DecoysIntact([string]$when) {
    Check (@($DecoyTasks | Where-Object { -not (Test-Task $_) }).Count -eq 0) "${when}: unrelated and look-alike scheduled tasks are untouched ($($DecoyTasks -join ', '))"
    Check (@($DecoyKeys | Where-Object { -not (Test-Path $_) }).Count -eq 0) "${when}: unrelated and look-alike context-menu keys are untouched"
    Check ((Test-Path 'HKLM:\SOFTWARE\Classes\exefile\shell\open\command') -and (Test-Path 'HKLM:\SOFTWARE\Classes\Msi.Package\shell\Open\command')) "${when}: the stock Open verbs of exefile / Msi.Package still exist"
}

# Persistent user data that must survive everything. Synthetic content; hashed so any change is detected.
function New-UserData {
    foreach ($d in 'Settings', 'LeftoverBackup\journals', 'LeftoverBackup\store', 'Quarantine', 'Logs', 'History') { New-Item -ItemType Directory -Path (Join-Path $DataDir $d) -Force | Out-Null }
    Set-Content (Join-Path $DataDir 'settings.json') '{"ScheduledCleanupEnabled":true,"LaunchAtLogon":true}'
    Set-Content (Join-Path $DataDir 'LeftoverBackup\journals\backup-0001.json') '{"id":"0001","state":"Committed","items":["C:\\synthetic\\leftover.txt"]}'
    Set-Content (Join-Path $DataDir 'LeftoverBackup\store\0001.bin') ('x' * 4096)
    Set-Content (Join-Path $DataDir 'Quarantine\q-0001.quarantine') 'quarantined synthetic sample'
    Set-Content (Join-Path $DataDir 'Logs\app.log') 'log line'
    Set-Content (Join-Path $DataDir 'History\history.json') '[]'
}
function Get-DataHash {
    if (-not (Test-Path $DataDir)) { return 'MISSING' }
    $lines = Get-ChildItem $DataDir -Recurse -File | Sort-Object FullName | ForEach-Object { '{0}:{1}' -f $_.FullName.Substring($DataDir.Length), (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    $sha = [Security.Cryptography.SHA256]::Create()
    [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))))
}

function Get-MsiProperty([string]$path, [string]$name) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.OpenDatabase($path, 0)
    $view = $db.OpenView("SELECT Value FROM Property WHERE Property='$name'"); [void]$view.Execute($null)
    $rec = $view.Fetch(); $v = if ($rec) { $rec.StringData(1) } else { $null }
    [void]$view.Close(); $v
}

# ═════════════════════════════════════════════════════════════════════════════════════════════════
# Hard stop (before anything is created or installed): never touch a machine that already has AuraClean or its data.
$preexisting = @()
if (@(Get-ArpEntries).Count -gt 0) { $preexisting += 'an installed AuraClean (Installed apps entry)' }
if (Test-Path $InstallDir) { $preexisting += "$InstallDir" }
if (Test-Path $DataDir) { $preexisting += "$DataDir (user data)" }
if (Test-Path 'HKLM:\SOFTWARE\AuraClean') { $preexisting += 'HKLM\SOFTWARE\AuraClean' }
foreach ($t in $OwnedTasks) { if (Test-Task $t) { $preexisting += "scheduled task $t" } }
foreach ($k in $ExeMenuKey, $MsiMenuKey) { if (Test-Path $k) { $preexisting += $k } }
if ($preexisting.Count) { throw "REFUSING TO RUN: this machine is not clean. Found: $($preexisting -join '; '). Use a fresh disposable machine/VM snapshot." }

Write-Host "Runner: $([Environment]::OSVersion.VersionString), image $($env:ImageOS) $($env:ImageVersion)"
Write-Host 'Packages:'
Get-ChildItem $PackageDir -Filter *.msi | ForEach-Object { Write-Host "  $($_.Name) ProductVersion=$(Get-MsiProperty $_.FullName 'ProductVersion') ProductCode=$(Get-MsiProperty $_.FullName 'ProductCode')" }

# Hard stops before anything is installed: wrong-version or mismatched packages are refused, never adapted or substituted.
function Stop-Unless([bool]$ok, [string]$why) { if (-not $ok) { throw "REFUSING TO RUN: $why" } }
Stop-Unless ($HistoricalJson -and (Test-Path $HistoricalJson)) 'historical-artifacts.json was not found'
$hist = @((Get-Content $HistoricalJson -Raw | ConvertFrom-Json).releases | Where-Object { $_.label -ceq '1.6.0-rc.1' })[0]
Stop-Unless ($null -ne $hist) 'the original 1.6.0-rc.1 is not recorded in historical-artifacts.json'
$histMsi = @($hist.files | Where-Object { $_.name -like '*.msi' })[0]
$histSha = (Get-FileHash $Msi.rc1 -Algorithm SHA256).Hash.ToLowerInvariant()
Stop-Unless ($histSha -eq $histMsi.sha256) "the historical RC1 MSI has SHA-256 $histSha but the pinned original is $($histMsi.sha256): this is not the original $($hist.runtime) RC1 (a rebuilt or different package must never stand in for it)"
Stop-Unless ((Get-MsiProperty $Msi.rc1 'ProductVersion') -eq $hist.msiProductVersion -and (Get-MsiProperty $Msi.rc1 'ARPCOMMENTS') -eq $hist.arpComments) 'the historical RC1 MSI does not carry the recorded version/label'
Stop-Unless ((Get-MsiProperty $Msi.rc2 'ProductVersion') -eq $ExpectedRc2Version -and (Get-MsiProperty $Msi.rc2 'ARPCOMMENTS') -eq "AuraClean $ExpectedRc2Label") "rc2.msi is not the expected candidate $ExpectedRc2Label (MSI $ExpectedRc2Version): found $(Get-MsiProperty $Msi.rc2 'ProductVersion') / '$(Get-MsiProperty $Msi.rc2 'ARPCOMMENTS')'"
foreach ($k in 'final', 'rebuild') { Stop-Unless ((Get-MsiProperty $Msi[$k] 'ProductVersion') -eq '1.6.99') "$k.msi must be the disposable 1.6.99 package (found $(Get-MsiProperty $Msi[$k] 'ProductVersion'))" }
Stop-Unless ((Get-MsiProperty $Msi.final 'ProductCode') -ne (Get-MsiProperty $Msi.rebuild 'ProductCode')) 'final-rebuild.msi must have a different ProductCode than final.msi (a genuine rebuild)'
Stop-Unless (@($Msi.Values | ForEach-Object { Get-MsiProperty $_ 'UpgradeCode' } | Select-Object -Unique).Count -eq 1) 'all packages must share one UpgradeCode'

Section 'Package sanity (the packages are exactly what the test expects)'
$ver = @{}; foreach ($k in $Msi.Keys) { $ver[$k] = Get-MsiProperty $Msi[$k] 'ProductVersion' }
Check ($histSha -eq $histMsi.sha256) "The historical RC1 (.NET 8) MSI matches its pinned SHA-256 ($histSha)"
Check ($ver.rc1 -eq '1.6.1' -and $ver.rc2 -eq $ExpectedRc2Version -and $ver.final -eq '1.6.99' -and $ver.rebuild -eq '1.6.99') "MSI versions are rc1(historical)=$($ver.rc1) rc2(candidate)=$($ver.rc2) final(disposable)=$($ver.final) rebuild=$($ver.rebuild)"
Check ((Get-MsiProperty $Msi.final 'ProductCode') -ne (Get-MsiProperty $Msi.rebuild 'ProductCode')) 'The rebuilt final package has a different ProductCode (a genuine rebuild, not a repair)'
Check (@($Msi.Values | ForEach-Object { Get-MsiProperty $_ 'UpgradeCode' } | Select-Object -Unique).Count -eq 1) 'All packages share one UpgradeCode'
Check (-not (Test-Path $ExePath) -and @(Get-ArpEntries).Count -eq 0 -and -not (Test-Path $DataDir)) 'The runner starts clean: no AuraClean installed, no AuraClean data'

try {
New-UserData
$dataHash = Get-DataHash
New-Decoys
$tasksBefore = (Get-AllTaskNames | Where-Object { $_ -notlike 'AuraClean_*' }) -join ','

# ── fresh install ─────────────────────────────────────────────────────────────────────────────────
Section 'Fresh silent install of the ORIGINAL rc.1 (.NET 8, hash-pinned)'
Check ((Invoke-Msi 'install-rc1' @('/i', "`"$($Msi.rc1)`"")) -eq 0) 'msiexec /i rc.1 /qn exits 0'
Check (Test-Path $ExePath) "AuraClean.exe is installed at $ExePath (under Program Files)"
Check (@(Get-ChildItem $InstallDir -Recurse -File).Count -eq 1) 'Exactly one file is installed (the self-contained single-file EXE)'
$arp = Get-Installed
Check ($null -ne $arp) 'Exactly one AuraClean entry exists in Installed apps (Uninstall registry)'
function Get-ArpValue($entry, [string]$name) { if ($entry -and $entry.PSObject.Properties[$name]) { $entry.$name } else { $null } }
# Windows Installer keeps ARPPRODUCTICON in its own product registration (shown by Installed apps), not as a DisplayIcon value.
$arpIcon = "$((New-Object -ComObject WindowsInstaller.Installer).ProductInfo((Get-MsiProperty $Msi.rc1 'ProductCode'), 'ProductIcon'))"
Check ((Get-ArpValue $arp 'DisplayVersion') -eq '1.6.1') "Installed apps version is $(Get-ArpValue $arp 'DisplayVersion') (expected 1.6.1)"
Check ((Get-ArpValue $arp 'Comments') -eq 'AuraClean 1.6.0-rc.1') "Installed apps Comments carry the readable label ('$(Get-ArpValue $arp 'Comments')')"
Check ((Get-ArpValue $arp 'Publisher') -eq 'AuraClean') "Publisher is AuraClean ('$(Get-ArpValue $arp 'Publisher')')"
Check ([bool]$arpIcon) "Installed apps has a registered product icon ('$arpIcon')"
Write-Host "  ARP entry: $(($arp.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join '; ')"
$meta = Get-ItemProperty 'HKLM:\SOFTWARE\AuraClean' -ErrorAction SilentlyContinue
Check ((Get-ArpValue $meta 'Version') -eq '1.6.0-rc.1' -and (Get-ArpValue $meta 'InstallDir') -like "$InstallDir*") 'HKLM\Software\AuraClean holds the install folder and release label'
Check (Test-Path $StartMenu) 'Start Menu shortcut exists'
Check (-not (Test-Path $PublicDesk)) 'Desktop shortcut is NOT created by default'
$sc = (New-Object -ComObject WScript.Shell).CreateShortcut($StartMenu)
Check ($sc.TargetPath -eq $ExePath) "Start Menu shortcut targets the installed EXE ($($sc.TargetPath))"
Check (-not (Test-Path (Join-Path $InstallDir 'settings.json'))) 'The install folder holds no user data'
Check ((Get-DataHash) -eq $dataHash) 'Persistent data in %LocalAppData%\AuraClean is byte-identical after install'
Check ((Get-Item $ExePath).VersionInfo.ProductVersion -eq '1.6.0-rc.1') "Installed EXE reports product version $((Get-Item $ExePath).VersionInfo.ProductVersion) (expected the original 1.6.0-rc.1)"
$rc1ExeHash = (Get-FileHash $ExePath -Algorithm SHA256).Hash

# ── integrations created as the app would, then repair ────────────────────────────────────────────
Section 'AuraClean-owned integrations created (as the app does), then repair'
New-OwnedIntegrations $ExePath
Test-OwnedIntegrations $true 'Before repair'
Remove-Item $ExePath -Force
Check (-not (Test-Path $ExePath)) 'AuraClean.exe deleted to simulate damage'
Check ((Invoke-Msi 'repair-rc1' @('/fa', "`"$($Msi.rc1)`"")) -eq 0) 'msiexec /fa rc.1 (repair) exits 0'
Check (Test-Path $ExePath) 'Repair restored the missing AuraClean.exe'
Check ((Get-InstalledVersion) -eq '1.6.1' -and @(Get-ArpEntries).Count -eq 1) 'Still exactly one entry, still 1.6.1'
Test-OwnedIntegrations $true 'After repair'
Check ((Get-DataHash) -eq $dataHash) 'Persistent data unchanged by repair'
Check ((Invoke-Msi 'reinstall-same-rc1' @('/i', "`"$($Msi.rc1)`"")) -eq 0 -and @(Get-ArpEntries).Count -eq 1) 'Re-running the installed MSI (maintenance mode) succeeds and leaves one entry'
Test-OwnedIntegrations $true 'After maintenance re-run'

# ── upgrade chain ─────────────────────────────────────────────────────────────────────────────────
Section 'Cross-runtime upgrade: original rc.1 (.NET 8) -> rc.2 (.NET 10); downgrade rc.2 -> rc.1 refused'
Check ((Invoke-Msi 'upgrade-rc1-to-rc2' @('/i', "`"$($Msi.rc2)`"")) -eq 0) 'msiexec /i rc.2 over the ORIGINAL rc.1 exits 0'
Check ((Get-InstalledVersion) -eq $ExpectedRc2Version -and @(Get-ArpEntries).Count -eq 1) "Installed version is $ExpectedRc2Version with a single Installed apps entry (no side-by-side copy)"
Check (Test-Path $ExePath -PathType Leaf) 'AuraClean.exe present after upgrade'
Check ((Get-ArpValue (Get-Installed) 'Comments') -eq "AuraClean $ExpectedRc2Label") "Installed apps now shows the new label ('$(Get-ArpValue (Get-Installed) 'Comments')')"
Check ((Get-Item $ExePath).VersionInfo.ProductVersion -eq $ExpectedRc2Label) "The installed EXE was replaced by the $ExpectedRc2Label build (product version $((Get-Item $ExePath).VersionInfo.ProductVersion))"
Check ((Get-FileHash $ExePath -Algorithm SHA256).Hash -ne $rc1ExeHash) 'The installed EXE differs from the original rc.1 .NET 8 EXE'
Check ((Get-ItemProperty 'HKLM:\SOFTWARE\AuraClean' -ErrorAction SilentlyContinue | ForEach-Object { Get-ArpValue $_ 'Version' }) -eq $ExpectedRc2Label) 'HKLM\Software\AuraClean records the new release label'
Test-OwnedIntegrations $true 'After upgrade'
Test-DecoysIntact 'After upgrade'
Check ((Get-DataHash) -eq $dataHash) 'Persistent data unchanged by upgrade'
Remove-Item $ExePath -Force
Check ((Invoke-Msi 'repair-rc2' @('/fa', "`"$($Msi.rc2)`"")) -eq 0 -and (Test-Path $ExePath) -and (Get-Item $ExePath).VersionInfo.ProductVersion -eq $ExpectedRc2Label) 'Repair of the rc.2 install restores its EXE (still the rc.2 build)'
Test-OwnedIntegrations $true 'After rc.2 repair'
Check ((Get-DataHash) -eq $dataHash) 'Persistent data unchanged by rc.2 repair'
$rc = Invoke-Msi 'downgrade-rc2-to-rc1' @('/i', "`"$($Msi.rc1)`"")
Check ($rc -eq 1603) "Installing rc.1 over rc.2 is refused (exit $rc, expected 1603)"
Check ((Get-InstalledVersion) -eq '1.6.2' -and @(Get-ArpEntries).Count -eq 1 -and (Test-Path $ExePath)) 'After the refused downgrade rc.2 is still installed and intact'
Test-OwnedIntegrations $true 'After refused downgrade'
Check ((Get-LogText 'downgrade-rc2-to-rc1') -match 'newer or identical version of AuraClean') 'The log shows the refusal came from our launch condition'

Section 'Upgrade rc.2 -> final, downgrades and same-version rebuild refused'
Check ((Invoke-Msi 'upgrade-rc2-to-final' @('/i', "`"$($Msi.final)`"")) -eq 0) 'msiexec /i final over rc.2 exits 0'
Check ((Get-InstalledVersion) -eq '1.6.99' -and @(Get-ArpEntries).Count -eq 1) 'Installed version is 1.6.99 with a single entry'
Test-OwnedIntegrations $true 'After rc.2 -> final'
Check ((Get-DataHash) -eq $dataHash) 'Persistent data unchanged by rc.2 -> final'
$rc = Invoke-Msi 'downgrade-final-to-rc2' @('/i', "`"$($Msi.rc2)`"")
Check ($rc -eq 1603) "Installing rc.2 over the final release is refused (exit $rc, expected 1603)"
$rc = Invoke-Msi 'downgrade-final-to-rc1' @('/i', "`"$($Msi.rc1)`"")
Check ($rc -eq 1603) "Installing rc.1 over the final release is refused (exit $rc, expected 1603)"
$rc = Invoke-Msi 'rebuild-same-version' @('/i', "`"$($Msi.rebuild)`"")
Check ($rc -eq 1603) "Installing a rebuilt package of the same version is refused (exit $rc, expected 1603)"
Check ((Get-LogText 'rebuild-same-version') -match 'already installed') 'The log shows the duplicate-version launch condition fired'
Check ((Get-InstalledVersion) -eq '1.6.99' -and @(Get-ArpEntries).Count -eq 1 -and (Test-Path $ExePath)) 'The final release is still installed and intact after all refusals'
Test-OwnedIntegrations $true 'After refused installs'
Test-DecoysIntact 'After refused installs'

# ── genuine uninstall ─────────────────────────────────────────────────────────────────────────────
Section 'Genuine uninstall of the final release'
Check ((Invoke-Msi 'uninstall-final' @('/x', "`"$($Msi.final)`"")) -eq 0) 'msiexec /x final /qn exits 0'
Check (-not (Test-Path $ExePath) -and -not (Test-Path $InstallDir)) 'Program Files\AuraClean is gone'
Check (@(Get-ArpEntries).Count -eq 0) 'The Installed apps entry is gone'
Check (-not (Test-Path 'HKLM:\SOFTWARE\AuraClean')) 'HKLM\Software\AuraClean is gone'
Check (-not (Test-Path $StartMenu)) 'Start Menu shortcut is gone'
Test-OwnedIntegrations $false 'After uninstall'
Test-DecoysIntact 'After uninstall'
Check ((Get-DataHash) -eq $dataHash) 'Settings, backup journals, recovery store, quarantine, logs and history are byte-identical after uninstall'
Check (((Get-AllTaskNames | Where-Object { $_ -notlike 'AuraClean_*' }) -join ',') -eq $tasksBefore) 'The set of non-AuraClean scheduled tasks is exactly what it was before'
$ulog = Get-LogText 'uninstall-final'
foreach ($ca in 'RemoveTaskLaunchAtLogon', 'RemoveTaskScheduledCleanup', 'RemoveContextMenuExe', 'RemoveContextMenuMsi') {
    Check ($ulog -match "Action start .*: $ca\.") "The uninstall log shows custom action $ca ran"
}

# ── reinstall keeps data; optional desktop shortcut; uninstall with no integrations present ──────
Section 'Reinstall over preserved data, optional desktop shortcut, uninstall with no integrations present'
Check ((Invoke-Msi 'install-final-alldesktop' @('/i', "`"$($Msi.final)`"", 'ADDLOCAL=ALL')) -eq 0) 'msiexec /i final ADDLOCAL=ALL exits 0'
Check (Test-Path $PublicDesk) 'The optional desktop shortcut is created when the feature is selected'
Check ((Get-DataHash) -eq $dataHash) 'The preserved data is still intact after reinstall (restore data is available again)'
Check ((Invoke-Msi 'uninstall-by-productcode' @('/x', (Get-MsiProperty $Msi.final 'ProductCode'))) -eq 0) 'Uninstall by ProductCode with no integrations present exits 0 (missing tasks/keys never fail an uninstall)'
Check (-not (Test-Path $PublicDesk) -and @(Get-ArpEntries).Count -eq 0 -and -not (Test-Path $InstallDir)) 'Desktop shortcut, entry and files are gone'
Check ((Get-DataHash) -eq $dataHash) 'Data intact after the second uninstall'
Test-DecoysIntact 'After second uninstall'

# ── fresh install of the candidate itself ─────────────────────────────────────────────────────────
Section "Fresh silent install of the candidate $ExpectedRc2Label (.NET 10) over preserved data"
Check ((Invoke-Msi 'install-rc2-fresh' @('/i', "`"$($Msi.rc2)`"")) -eq 0) 'msiexec /i rc.2 /qn exits 0'
$arp2 = Get-Installed
Check ($null -ne $arp2 -and (Get-ArpValue $arp2 'DisplayVersion') -eq $ExpectedRc2Version -and (Get-ArpValue $arp2 'Comments') -eq "AuraClean $ExpectedRc2Label") "Installed apps shows $ExpectedRc2Version / 'AuraClean $ExpectedRc2Label'"
Check ((Test-Path $ExePath) -and (Get-Item $ExePath).VersionInfo.ProductVersion -eq $ExpectedRc2Label -and @(Get-ChildItem $InstallDir -Recurse -File).Count -eq 1) "One file installed: the $ExpectedRc2Label single-file EXE"
Check ((Test-Path $StartMenu) -and -not (Test-Path $PublicDesk)) 'Start Menu shortcut exists; no desktop shortcut by default'
Check ((Get-DataHash) -eq $dataHash) 'Preserved data intact after the fresh rc.2 install'
Check ((Invoke-Msi 'uninstall-rc2-fresh' @('/x', "`"$($Msi.rc2)`"")) -eq 0 -and @(Get-ArpEntries).Count -eq 0 -and -not (Test-Path $InstallDir)) 'The rc.2 uninstall removes the program and entry'
Check ((Get-DataHash) -eq $dataHash) 'Data intact after the rc.2 uninstall'

# ── portable copy: shared integrations ────────────────────────────────────────────────────────────
Section 'Portable copy: shared machine-wide integrations are removed by the MSI uninstall (documented limitation)'
$portableDir = 'C:\PortableAuraClean'; New-Item -ItemType Directory -Path $portableDir -Force | Out-Null
Copy-Item "$PackageDir\portable\AuraClean.exe" "$portableDir\AuraClean.exe"
New-OwnedIntegrations "$portableDir\AuraClean.exe"
Check ((Invoke-Msi 'install-rc2-with-portable' @('/i', "`"$($Msi.rc2)`"")) -eq 0) 'Installing the MSI next to a portable copy succeeds'
Test-OwnedIntegrations $true "Installing the MSI leaves the portable copy's registrations in place"
Check ((Invoke-Msi 'uninstall-with-portable' @('/x', "`"$($Msi.rc2)`"")) -eq 0) 'Uninstalling the MSI succeeds'
Test-OwnedIntegrations $false "After uninstall (documented: names are machine-wide, so the portable copy's registrations are removed too)"
Check ((Test-Path "$portableDir\AuraClean.exe") -and (Get-FileHash "$portableDir\AuraClean.exe").Hash -eq (Get-FileHash "$PackageDir\portable\AuraClean.exe").Hash) 'The portable executable is still present and byte-identical'
Test-DecoysIntact 'After portable scenario'
Check ((Get-DataHash) -eq $dataHash) 'Data intact after the portable scenario'
}
catch {
    # An unexpected error (usually a consequence of an earlier failed step) must fail the run visibly, not abort it silently.
    Check $false "Unexpected error, remaining scenarios not run: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)"
}

# ── summary ───────────────────────────────────────────────────────────────────────────────────────
$failed = @($script:Results | Where-Object { -not $_.Ok })
$summary = New-Object System.Text.StringBuilder
[void]$summary.AppendLine("### MSI lifecycle on $([Environment]::OSVersion.VersionString) (image $($env:ImageOS) $($env:ImageVersion))")
[void]$summary.AppendLine("**$($script:Results.Count - $failed.Count) passed, $($failed.Count) failed** (no scenario skipped)")
[void]$summary.AppendLine('')
foreach ($g in $script:Results | Group-Object Section) {
    $bad = @($g.Group | Where-Object { -not $_.Ok }).Count
    [void]$summary.AppendLine("- $(if ($bad) { ':x:' } else { ':white_check_mark:' }) $($g.Name): $($g.Count - $bad)/$($g.Count)")
}
foreach ($f in $failed) { [void]$summary.AppendLine("  - FAIL [$($f.Section)] $($f.Message)") }
$summary.ToString() | Out-File (Join-Path $LogDir 'summary.md') -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) { $summary.ToString() | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
$script:Results | ConvertTo-Json | Out-File (Join-Path $LogDir 'results.json') -Encoding utf8

Write-Host ''
Write-Host "RESULTS: $($script:Results.Count - $failed.Count) passed, $($failed.Count) failed" -ForegroundColor $(if ($failed.Count) { 'Red' } else { 'Green' })
exit $(if ($failed.Count) { 1 } else { 0 })
