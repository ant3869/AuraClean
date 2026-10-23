<#
.SYNOPSIS
    AuraClean RC1 client-Windows acceptance kit: prepare (read-only), run the automated MSI lifecycle, and build the report.

.DESCRIPTION
    Modes
      Prepare    (default) READ-ONLY. Records the Windows build / UAC / SmartScreen state, verifies the release artifacts against
                 SHA256SUMS.txt, reads MSI properties, checks that no AuraClean is already present, and seeds the manual checklist
                 results. Installs and changes nothing on the machine (it only writes files into -OutDir).
      Lifecycle  DESTRUCTIVE. Runs build/Test-MsiLifecycle.ps1 (real msiexec install, repair, upgrade, downgrade refusal, uninstall,
                 integration cleanup, data preservation). Only for a throw-away PC / VM: requires -ConfirmDisposable with the exact text
                 I-CONFIRM-<COMPUTERNAME>-IS-DISPOSABLE, administrator rights, and refuses if AuraClean or its data already exist.
      Report     Combines environment.json, the automated results and manual-results.json into results\report.md.
      Pack       (developer machine; verifies and copies files only) Zips this kit + the lifecycle script + artifacts + test packages.

    Never run Lifecycle on the developer's own computer. The UAC prompt, SmartScreen, the wizard and the other interactive checks
    are manual: see manual-checklist.md.

.EXAMPLE
    .\Start-Acceptance.ps1 -Mode Prepare   -ArtifactDir C:\kit\artifacts -PackageDir C:\kit\packages
    .\Start-Acceptance.ps1 -Mode Lifecycle -ArtifactDir C:\kit\artifacts -PackageDir C:\kit\packages -ConfirmDisposable I-CONFIRM-TESTPC-IS-DISPOSABLE
    .\Start-Acceptance.ps1 -Mode Report
#>
[CmdletBinding()]
param(
    [ValidateSet('Prepare', 'Lifecycle', 'Report', 'Pack')][string]$Mode = 'Prepare',
    # Folder with the OFFICIAL RC1 files: AuraClean-1.6.0-rc.1-win-x64.msi, -portable.exe and SHA256SUMS.txt.
    [string]$ArtifactDir = (Join-Path $PSScriptRoot 'artifacts'),
    # Folder from build/Build-AcceptancePackages.ps1 (rc1/rc2/final/final-rebuild .msi and portable\AuraClean.exe): throw-away upgrade/downgrade packages.
    [string]$PackageDir = (Join-Path $PSScriptRoot 'packages'),
    [string]$OutDir = (Join-Path $PSScriptRoot 'results'),
    [string]$ConfirmDisposable,
    [string]$KitZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$ExpectedLabel = '1.6.0-rc.1'
$ExpectedMsiVersions = @{ 'rc1.msi' = '1.6.1'; 'rc2.msi' = '1.6.2'; 'final.msi' = '1.6.99'; 'final-rebuild.msi' = '1.6.99' }

$ManualChecks = @(
    @{ Id = 'M01'; Title = 'Interactive installer wizard: Welcome, feature page, install, finish page (text mentions data is kept)' }
    @{ Id = 'M02'; Title = 'Installer UAC prompt appears; publisher shown as Unknown/unsigned (screenshot)' }
    @{ Id = 'M03'; Title = 'Installed apps (Settings > Apps): AuraClean, version 1.6.1, icon, Comments "AuraClean 1.6.0-rc.1"' }
    @{ Id = 'M04'; Title = 'Desktop shortcut is OFF by default; selecting it on the feature page creates it' }
    @{ Id = 'M05'; Title = 'Start Menu shortcut launches AuraClean; UAC prompt at app start; window shows v1.6.0-rc.1' }
    @{ Id = 'M06'; Title = 'SmartScreen / "Unknown publisher" behaviour for the downloaded MSI and portable EXE (file with Mark of the Web)' }
    @{ Id = 'M07'; Title = 'Settings UI: enable Launch at sign-in; task AuraClean_LaunchAtLogon exists; after sign-out/in AuraClean starts minimized (--minimized)' }
    @{ Id = 'M08'; Title = 'Settings UI: enable Scheduled cleanup; task AuraClean_ScheduledCleanup exists in Task Scheduler' }
    @{ Id = 'M09'; Title = 'Settings UI: enable Explorer context menu; right-click an .exe and .msi shows "Deep Uninstall with AuraClean"' }
    @{ Id = 'M10'; Title = 'rc.1 -> rc.2 upgrade and repair keep the three app-created integrations (M07-M09) working' }
    @{ Id = 'M11'; Title = 'Files in use: run the installer/upgrade while AuraClean is running: standard prompt (or reboot-deferred when silent), AuraClean is NOT killed' }
    @{ Id = 'M12'; Title = 'Uninstall via Installed apps removes the program, tasks and menu entries; %LocalAppData%\AuraClean remains' }
    @{ Id = 'M13'; Title = 'Data created through the app UI (setting, leftover backup of a disposable test app, quarantine) survives upgrade, uninstall and reinstall; restore still works' }
    @{ Id = 'M14'; Title = 'Legacy install.ps1 copy: final page mentions it, nothing deleted; scripts\Remove-LegacyInstall.ps1 -WhatIf, then a real run' }
)

# ── helpers ──────────────────────────────────────────────────────────────────────────────────────
function Write-Json($obj, $path) { $obj | ConvertTo-Json -Depth 8 | Out-File $path -Encoding utf8 }
function Read-Json($path) { if (Test-Path $path) { Get-Content $path -Raw | ConvertFrom-Json } else { $null } }
function Get-Reg($path, $name) { try { (Get-ItemProperty -Path $path -Name $name -ErrorAction Stop).$name } catch { $null } }

function Get-MsiProperty([string]$path, [string]$name) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $installer.OpenDatabase((Resolve-Path $path).Path, 0)   # 0 = read-only
    $view = $db.OpenView("SELECT Value FROM Property WHERE Property='$name'"); [void]$view.Execute($null)
    $rec = $view.Fetch(); $v = if ($rec) { $rec.StringData(1) } else { $null }
    [void]$view.Close(); $v
}

function Get-EnvironmentInfo {
    $cv = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $os = Get-CimInstance Win32_OperatingSystem
    $cs = Get-CimInstance Win32_ComputerSystem
    $build = [int](Get-Reg $cv 'CurrentBuildNumber')
    $family = if ($build -ge 22000) { 'Windows 11' } elseif ($build -ge 10240) { 'Windows 10' } else { 'Older than Windows 10' }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    $sys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    [ordered]@{
        Computer       = $env:COMPUTERNAME
        Family         = $family
        ProductName    = (Get-Reg $cv 'ProductName')   # Windows 11 still reports "Windows 10" here; use Family
        Edition        = (Get-Reg $cv 'EditionID')
        DisplayVersion = (Get-Reg $cv 'DisplayVersion')
        Build          = "$build.$((Get-Reg $cv 'UBR'))"
        Architecture   = $os.OSArchitecture
        Machine        = "$($cs.Manufacturer) $($cs.Model)"
        LooksVirtual   = [bool]("$($cs.Manufacturer) $($cs.Model)" -match 'Virtual|VMware|VirtualBox|innotek|QEMU|KVM|Hyper-V')
        IsAdmin        = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        UacEnabled     = [bool](Get-Reg $sys 'EnableLUA')
        UacAdminPrompt = (Get-Reg $sys 'ConsentPromptBehaviorAdmin')
        SmartScreen    = (Get-Reg 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' 'SmartScreenEnabled')
        PowerShell     = $PSVersionTable.PSVersion.ToString()
        Captured       = (Get-Date).ToString('s')
    }
}

function Get-AuraCleanPresence {
    $roots = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    $arp = @($roots | Where-Object { Test-Path $_ } | ForEach-Object { Get-ChildItem $_ } | ForEach-Object { Get-ItemProperty $_.PSPath } |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'AuraClean' })
    $found = @()
    if ($arp.Count) { $found += 'installed AuraClean (Installed apps entry)' }
    if (Test-Path (Join-Path $env:ProgramFiles 'AuraClean')) { $found += "$env:ProgramFiles\AuraClean" }
    if (Test-Path (Join-Path $env:LOCALAPPDATA 'AuraClean')) { $found += "$env:LOCALAPPDATA\AuraClean (user data)" }
    foreach ($t in 'AuraClean_LaunchAtLogon', 'AuraClean_ScheduledCleanup') { if (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) { $found += "scheduled task $t" } }
    foreach ($k in 'HKLM:\SOFTWARE\Classes\exefile\shell\AuraCleanUninstall', 'HKLM:\SOFTWARE\Classes\Msi.Package\shell\AuraCleanUninstall') { if (Test-Path $k) { $found += $k } }
    , $found
}

function Test-Artifacts {
    $rows = New-Object System.Collections.Generic.List[object]
    $sums = Join-Path $ArtifactDir 'SHA256SUMS.txt'
    if (-not (Test-Path $sums)) { $rows.Add([pscustomobject]@{ File = 'SHA256SUMS.txt'; Ok = $false; Detail = "not found in $ArtifactDir" }); return , $rows.ToArray() }
    foreach ($line in Get-Content $sums | Where-Object { $_ }) {
        $m = [regex]::Match($line, '^([0-9a-f]{64}) \*(.+)$')
        if (-not $m.Success) { continue }
        $file = Join-Path $ArtifactDir $m.Groups[2].Value
        if (-not (Test-Path $file)) { $rows.Add([pscustomobject]@{ File = $m.Groups[2].Value; Ok = $false; Detail = 'listed but missing' }); continue }
        $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        $rows.Add([pscustomobject]@{ File = $m.Groups[2].Value; Ok = ($actual -eq $m.Groups[1].Value); Detail = "sha256 $actual" })
    }
    $msi = Get-ChildItem $ArtifactDir -Filter "AuraClean-$ExpectedLabel-win-x64.msi" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $msi) { $rows.Add([pscustomobject]@{ File = "AuraClean-$ExpectedLabel-win-x64.msi"; Ok = $false; Detail = 'official MSI not found' }) }
    else {
        $pv = Get-MsiProperty $msi.FullName 'ProductVersion'
        $rows.Add([pscustomobject]@{ File = $msi.Name; Ok = ($pv -eq '1.6.1'); Detail = "ProductVersion $pv (expected 1.6.1); signature $((Get-AuthenticodeSignature $msi.FullName).Status)" })
    }
    , $rows.ToArray()
}

function Test-Packages {
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($k in $ExpectedMsiVersions.Keys) {
        $f = Join-Path $PackageDir $k
        if (-not (Test-Path $f)) { $rows.Add([pscustomobject]@{ File = $k; Ok = $false; Detail = "missing in $PackageDir" }); continue }
        $pv = Get-MsiProperty $f 'ProductVersion'
        $rows.Add([pscustomobject]@{ File = $k; Ok = ($pv -eq $ExpectedMsiVersions[$k]); Detail = "ProductVersion $pv (expected $($ExpectedMsiVersions[$k]))" })
    }
    $p = Join-Path $PackageDir 'portable\AuraClean.exe'
    $rows.Add([pscustomobject]@{ File = 'portable\AuraClean.exe'; Ok = (Test-Path $p); Detail = $(if (Test-Path $p) { 'present' } else { 'missing' }) })
    , $rows.ToArray()
}

function Show-Rows($rows) { foreach ($r in $rows) { Write-Host ("  {0}: {1} - {2}" -f $(if ($r.Ok) { 'OK  ' } else { 'FAIL' }), $r.File, $r.Detail) -ForegroundColor $(if ($r.Ok) { 'Green' } else { 'Red' }) } }

function Invoke-Prepare {
    Write-Host '== Prepare (read-only) ==' -ForegroundColor Cyan
    $envInfo = Get-EnvironmentInfo
    $envInfo.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-15} {1}" -f $_.Key, $_.Value) }
    $present = Get-AuraCleanPresence
    $arts = Test-Artifacts
    $pkgs = Test-Packages
    Show-Rows $arts; Show-Rows $pkgs

    $problems = @()
    if ($envInfo.Family -eq 'Older than Windows 10') { $problems += 'OS is older than Windows 10: unsupported, do not use for acceptance' }
    if (-not $envInfo.IsAdmin) { $problems += 'not running as Administrator (needed for Lifecycle mode)' }
    if (-not $envInfo.UacEnabled) { $problems += 'UAC is OFF on this machine: the UAC prompt checks (M02, M05) cannot be performed' }
    if ($present.Count) { $problems += "AuraClean already present: $($present -join '; ')" }
    foreach ($r in @($arts) + @($pkgs)) { if (-not $r.Ok) { $problems += "$($r.File): $($r.Detail)" } }

    Write-Json $envInfo (Join-Path $OutDir 'environment.json')
    Write-Json ([ordered]@{ Artifacts = @($arts); Packages = @($pkgs); Presence = @($present); Problems = @($problems) }) (Join-Path $OutDir 'prepare.json')
    $manual = Join-Path $OutDir 'manual-results.json'
    if (-not (Test-Path $manual)) {
        Write-Json @($ManualChecks | ForEach-Object { [ordered]@{ Id = $_.Id; Title = $_.Title; Status = 'NOT TESTED'; Note = ''; Screenshot = '' } }) $manual
        Write-Host "  Seeded $manual (set Status to PASS or FAIL, add Note and Screenshot file names)"
    }
    Write-Host ''
    if ($problems.Count) { Write-Host 'NOT READY for Lifecycle mode:' -ForegroundColor Yellow; $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow } }
    else { Write-Host 'READY: artifacts verified, machine is clean. Take a VM snapshot / disk image now, then use -Mode Lifecycle.' -ForegroundColor Green }
    , $problems
}

# ── modes ────────────────────────────────────────────────────────────────────────────────────────
switch ($Mode) {
    'Prepare' {
        $p = Invoke-Prepare
        exit $(if (@($p).Count) { 2 } else { 0 })
    }

    'Lifecycle' {
        $expected = "I-CONFIRM-$($env:COMPUTERNAME.ToUpperInvariant())-IS-DISPOSABLE"
        if ($ConfirmDisposable -cne $expected) {
            throw "REFUSING: Lifecycle mode installs and uninstalls AuraClean for real. Only run it on a throw-away PC/VM (snapshot taken). Re-run with -ConfirmDisposable $expected"
        }
        $p = Invoke-Prepare
        if (@($p).Count) { throw 'REFUSING: preparation checks failed (see above). Nothing was installed.' }

        $lifecycle = @((Join-Path $PSScriptRoot 'Test-MsiLifecycle.ps1'), (Join-Path $PSScriptRoot '..\build\Test-MsiLifecycle.ps1')) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $lifecycle) { throw 'Test-MsiLifecycle.ps1 not found next to this script or in ..\build.' }

        # Work copy of the packages in which rc1.msi is the OFFICIAL RC1 MSI (same version 1.6.1 as the generated one).
        $work = Join-Path $OutDir 'work-packages'
        if (Test-Path $work) { Get-ChildItem $work -Recurse -File | ForEach-Object { [IO.File]::Delete($_.FullName) } }
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        Copy-Item (Join-Path $PackageDir '*') $work -Recurse -Force
        Copy-Item (Join-Path $ArtifactDir "AuraClean-$ExpectedLabel-win-x64.msi") (Join-Path $work 'rc1.msi') -Force
        Write-Host "Official RC1 MSI used as rc1.msi in $work" -ForegroundColor Cyan

        $logDir = Join-Path $OutDir 'msi-logs'
        & $lifecycle -PackageDir $work -LogDir $logDir -ConfirmDisposableMachine $ConfirmDisposable
        $code = $LASTEXITCODE
        Set-Content (Join-Path $OutDir 'lifecycle-exit.txt') "$code"
        Write-Host "Lifecycle finished with exit code $code. Logs: $logDir. Next: -Mode Report" -ForegroundColor $(if ($code) { 'Red' } else { 'Green' })
        exit $code
    }

    'Report' {
        $envInfo = Read-Json (Join-Path $OutDir 'environment.json')
        $prep = Read-Json (Join-Path $OutDir 'prepare.json')
        $auto = Read-Json (Join-Path $OutDir 'msi-logs\results.json')
        $manual = Read-Json (Join-Path $OutDir 'manual-results.json')
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.AppendLine("# AuraClean $ExpectedLabel - client Windows acceptance report")
        [void]$sb.AppendLine("Generated $(Get-Date -Format 's')`n")
        [void]$sb.AppendLine('## Test machine')
        if ($envInfo) { foreach ($p in $envInfo.PSObject.Properties) { [void]$sb.AppendLine("- **$($p.Name):** $($p.Value)") } } else { [void]$sb.AppendLine('- environment.json missing: run -Mode Prepare on the test machine') }
        [void]$sb.AppendLine("`n## Artifacts (hash and version verification)")
        if ($prep) { foreach ($r in @($prep.Artifacts) + @($prep.Packages)) { [void]$sb.AppendLine("- $(if ($r.Ok) { 'PASS' } else { 'FAIL' }) $($r.File): $($r.Detail)") } }
        [void]$sb.AppendLine("`n## Automated MSI lifecycle (real msiexec)")
        $failCount = 0; $untested = 0
        if ($auto) {
            $auto = @($auto); $bad = @($auto | Where-Object { -not $_.Ok }); $failCount += $bad.Count
            $exitFile = Join-Path $OutDir 'lifecycle-exit.txt'
            [void]$sb.AppendLine("**$($auto.Count - $bad.Count) PASS, $($bad.Count) FAIL** of $($auto.Count) checks. Lifecycle exit code: $(if (Test-Path $exitFile) { (Get-Content $exitFile) } else { 'n/a' }). MSI logs: ``$OutDir\msi-logs``.`n")
            foreach ($g in $auto | Group-Object Section) { $b = @($g.Group | Where-Object { -not $_.Ok }).Count; [void]$sb.AppendLine("- $(if ($b) { 'FAIL' } else { 'PASS' }) $($g.Name): $($g.Count - $b)/$($g.Count)") }
            foreach ($f in $bad) { [void]$sb.AppendLine("  - FAIL [$($f.Section)] $($f.Message)") }
        } else { $untested++; [void]$sb.AppendLine('**NOT TESTED** (no msi-logs\results.json: Lifecycle mode has not been run on this machine).') }
        [void]$sb.AppendLine("`n## Manual / interactive checks")
        [void]$sb.AppendLine('| ID | Check | Result | Notes | Screenshot |'); [void]$sb.AppendLine('|---|---|---|---|---|')
        foreach ($m in @($manual)) {
            [void]$sb.AppendLine("| $($m.Id) | $($m.Title) | **$($m.Status)** | $($m.Note) | $($m.Screenshot) |")
            if ($m.Status -eq 'FAIL') { $failCount++ } elseif ($m.Status -ne 'PASS') { $untested++ }
        }
        [void]$sb.AppendLine("`n## Summary`n- Failures: **$failCount**; not tested: **$untested**")
        [void]$sb.AppendLine('- Confirmed release blockers: (list each FAIL that blocks RC1, with log/screenshot reference)')
        [void]$sb.AppendLine('- Signing: unsigned (decision pending, see docs/SIGNING.md). Distribution: not published, no tag, no release.')
        [void]$sb.AppendLine("- Verdict: $(if ($failCount) { 'NO-GO' } elseif ($untested) { 'INCOMPLETE (not all gates tested)' } else { 'all checks passed' })")
        $sb.ToString() | Out-File (Join-Path $OutDir 'report.md') -Encoding utf8
        Write-Host "Report written: $(Join-Path $OutDir 'report.md')"
    }

    'Pack' {
        $repo = Split-Path -Parent $PSScriptRoot
        $lifecycle = Join-Path $repo 'build\Test-MsiLifecycle.ps1'
        if (-not (Test-Path $lifecycle)) { throw 'Pack mode must run from a repository checkout (build\Test-MsiLifecycle.ps1 not found).' }
        $rows = @(Test-Artifacts) + @(Test-Packages); Show-Rows $rows
        if (@($rows | Where-Object { -not $_.Ok }).Count) { throw 'Artifacts/packages did not verify; not packing.' }
        if (-not $KitZip) { $KitZip = Join-Path $OutDir 'AuraClean-acceptance-kit.zip' }
        $stage = Join-Path ([IO.Path]::GetTempPath()) ('AuraKit_' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stage | Out-Null
        try {
            Get-ChildItem $PSScriptRoot -File | ForEach-Object { Copy-Item $_.FullName $stage }
            Copy-Item $lifecycle $stage
            Copy-Item $ArtifactDir (Join-Path $stage 'artifacts') -Recurse
            Copy-Item $PackageDir (Join-Path $stage 'packages') -Recurse
            if (Test-Path $KitZip) { [IO.File]::Delete($KitZip) }
            Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $KitZip
        } finally { [IO.Directory]::Delete($stage, $true) }
        Write-Host "Kit: $KitZip"; Write-Host "SHA-256: $((Get-FileHash $KitZip -Algorithm SHA256).Hash.ToLowerInvariant())"
    }
}
