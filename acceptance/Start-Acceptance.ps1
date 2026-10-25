<#
.SYNOPSIS
    AuraClean client-Windows acceptance kit (candidate 1.6.0-rc.2): prepare (read-only), run the automated MSI lifecycle, and build the report.

.DESCRIPTION
    Modes
      Prepare    (default) READ-ONLY on the machine. Records the Windows build / UAC state, verifies the release artifacts against
                 SHA256SUMS.txt (fail closed: exactly the official MSI and portable EXE, each present and matching), reads MSI
                 versions, checks that no AuraClean is already present, starts or continues a run (run.json) and seeds the manual
                 checklist. It only writes files into -OutDir.
      Lifecycle  DESTRUCTIVE. Runs build/Test-MsiLifecycle.ps1 (real msiexec install, repair, upgrade, downgrade refusal, uninstall,
                 integration cleanup, data preservation). Only for a throw-away PC / VM: requires -ConfirmDisposable with the exact text
                 I-CONFIRM-<COMPUTERNAME>-IS-DISPOSABLE, administrator rights, and refuses if AuraClean or its data already exist.
      Report     Combines this run's preparation, lifecycle and manual results into results\report.md. Verdicts: FAIL > INCOMPLETE >
                 ALL CHECKS PASSED. Results from another run are never accepted. Exit code 0 only for ALL CHECKS PASSED.
      Pack       (developer machine; verifies and copies files only) Zips the kit, the lifecycle test, scripts\Remove-LegacyInstall.ps1,
                 the verified artifacts and the test packages. Nothing is executed.

    Earlier results are archived under results\previous\<timestamp>\ (never deleted) whenever a new run starts.
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
    # Folder with the OFFICIAL RC2 files (.NET 10): AuraClean-1.6.0-rc.2-win-x64.msi, -portable.exe and SHA256SUMS.txt.
    [string]$ArtifactDir = (Join-Path $PSScriptRoot 'artifacts'),
    # Folder from build/Build-AcceptancePackages.ps1 (rc1/rc2/final/final-rebuild .msi and portable\AuraClean.exe): throw-away upgrade/downgrade packages.
    [string]$PackageDir = (Join-Path $PSScriptRoot 'packages'),
    # Folder with the ORIGINAL released rc.1 (.NET 8) MSI and portable EXE, pinned by SHA-256 in historical-artifacts.json (never rebuilt).
    [string]$HistoricalDir = (Join-Path $PSScriptRoot 'historical'),
    [string]$OutDir = (Join-Path $PSScriptRoot 'results'),
    [string]$ConfirmDisposable,
    [string]$KitZip,
    # Start a fresh run even if the artifacts are unchanged (the previous run's results are archived).
    [switch]$NewRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AcceptanceChecks.ps1')
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$ExpectedMsiVersions = @{ 'rc2.msi' = '1.6.2'; 'final.msi' = '1.6.99'; 'final-rebuild.msi' = '1.6.99' }
$ExpectedRc2Version = '1.6.2'
$HistoricalJson = @((Join-Path $PSScriptRoot 'historical-artifacts.json'), (Join-Path $PSScriptRoot '..\build\historical-artifacts.json')) | Where-Object { Test-Path $_ } | Select-Object -First 1

$ManualChecks = @(
    @{ Id = 'M01'; Title = 'Interactive installer wizard: Welcome, feature page, install, finish page (text mentions data is kept)' }
    @{ Id = 'M02'; Title = 'Installer UAC prompt appears; publisher shown as Unknown/unsigned (screenshot)' }
    @{ Id = 'M03'; Title = 'Installed apps (Settings > Apps): AuraClean, version 1.6.2, icon, Comments "AuraClean 1.6.0-rc.2"' }
    @{ Id = 'M04'; Title = 'Desktop shortcut is OFF by default; selecting it on the feature page creates it' }
    @{ Id = 'M05'; Title = 'Start Menu shortcut launches AuraClean; UAC prompt at app start; window shows v1.6.0-rc.2' }
    @{ Id = 'M06'; Title = 'SmartScreen / "Unknown publisher" behaviour for the downloaded MSI and portable EXE (file with Mark of the Web)' }
    @{ Id = 'M07'; Title = 'Settings UI: enable Launch at sign-in; task AuraClean_LaunchAtLogon exists; after sign-out/in AuraClean starts minimized (--minimized)' }
    @{ Id = 'M08'; Title = 'Settings UI: enable Scheduled cleanup; task AuraClean_ScheduledCleanup exists in Task Scheduler' }
    @{ Id = 'M09'; Title = 'Settings UI: enable Explorer context menu; right-click an .exe and .msi shows "Deep Uninstall with AuraClean"' }
    @{ Id = 'M10'; Title = 'Upgrade from the ORIGINAL rc.1 (.NET 8) to rc.2 (.NET 10), then repair, keep the three app-created integrations (M07-M09) working' }
    @{ Id = 'M11'; Title = 'Files in use: run the installer/upgrade while AuraClean is running: standard prompt (or reboot-deferred when silent), AuraClean is NOT killed' }
    @{ Id = 'M12'; Title = 'Uninstall via Installed apps removes the program, tasks and menu entries; %LocalAppData%\AuraClean remains' }
    @{ Id = 'M13'; Title = 'Data created through the app UI (setting, leftover backup of a disposable test app, quarantine) survives upgrade, uninstall and reinstall; restore still works' }
    @{ Id = 'M14'; Title = 'Legacy install.ps1 copy: final page mentions it, nothing deleted; scripts\Remove-LegacyInstall.ps1 -WhatIf, then a real run' }
)
if ((@($ManualChecks | ForEach-Object { $_.Id }) -join ',') -ne ($script:ManualIds -join ',')) { throw 'Internal error: manual check ids differ from AcceptanceChecks.ps1' }

# ── helpers ──────────────────────────────────────────────────────────────────────────────────────
function Write-Json($obj, $path) { $obj | ConvertTo-Json -Depth 8 | Out-File $path -Encoding utf8 }
function Read-Json($path) { if (Test-Path $path) { try { Get-Content $path -Raw | ConvertFrom-Json } catch { $null } } else { $null } }
function Get-Reg($path, $name) { try { (Get-ItemProperty -Path $path -Name $name -ErrorAction Stop).$name } catch { $null } }

# Move earlier results aside (never deletes) so they can not be mistaken for the current run.
function Move-ResultsToPrevious([string[]]$Names) {
    $existing = @($Names | Where-Object { Test-Path (Join-Path $OutDir $_) })
    if (-not $existing.Count) { return }
    $dest = Join-Path $OutDir ('previous\' + (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    foreach ($n in $existing) { Move-Item -LiteralPath (Join-Path $OutDir $n) -Destination $dest }
}
$AllRunFiles = 'run.json', 'prepare.json', 'lifecycle-run.json', 'lifecycle-exit.txt', 'manual-results.json', 'report.md', 'environment.json', 'msi-logs', 'work-packages'

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

# Strict manifest check (library) + official MSI version/signature.
function Test-Artifacts {
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($r in @(Test-ChecksumManifest $ArtifactDir)) { $rows.Add($r) }
    $msi = Join-Path $ArtifactDir (Get-ExpectedArtifactNames)[0]
    if (Test-Path -LiteralPath $msi -PathType Leaf) {
        $pv = Get-MsiProperty $msi 'ProductVersion'
        $cm = Get-MsiProperty $msi 'ARPCOMMENTS'
        $rows.Add([pscustomobject]@{ File = (Split-Path $msi -Leaf) + ' (version)'; Ok = ($pv -eq $ExpectedRc2Version -and $cm -eq "AuraClean $script:ExpectedLabel"); Detail = "ProductVersion $pv (expected $ExpectedRc2Version), label '$cm' (expected 'AuraClean $script:ExpectedLabel'); signature $((Get-AuthenticodeSignature $msi).Status)" })
    }
    $rows.ToArray()
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
    $rows.ToArray()
}

function Show-Rows($rows) { foreach ($r in $rows) { Write-Host ("  {0}: {1} - {2}" -f $(if ($r.Ok) { 'OK  ' } else { 'FAIL' }), $r.File, $r.Detail) -ForegroundColor $(if ($r.Ok) { 'Green' } else { 'Red' }) } }

function Invoke-Prepare {
    Write-Host '== Prepare (read-only on the machine) ==' -ForegroundColor Cyan
    $envInfo = Get-EnvironmentInfo
    $envInfo.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-15} {1}" -f $_.Key, $_.Value) }
    $present = Get-AuraCleanPresence
    $arts = @(Test-Artifacts)
    $pkgs = @(Test-Packages)
    $hist = @(Test-HistoricalArtifacts $HistoricalDir $HistoricalJson)
    Show-Rows $arts; Show-Rows $pkgs; Show-Rows $hist

    # Run identity: a run continues only while the artifacts are unchanged; otherwise earlier results are archived and a new run starts.
    $msiPath = Join-Path $ArtifactDir (Get-ExpectedArtifactNames)[0]
    $msiSha = if (Test-Path -LiteralPath $msiPath -PathType Leaf) { (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToLowerInvariant() } else { '' }
    $run = Read-Json (Join-Path $OutDir 'run.json')
    if ((P $run 'RunId') -and -not $NewRun -and [string](P $run 'MsiSha256') -eq $msiSha) {
        if (Test-Path (Join-Path $OutDir 'lifecycle-run.json')) {
            Write-Host "Run $($run.RunId) already has lifecycle results: preparation is not re-recorded (a finished run's machine is no longer clean). Use -Mode Report, or -NewRun (after reverting the machine) to start over." -ForegroundColor Yellow
            return , @()
        }
    }
    else {
        Move-ResultsToPrevious $AllRunFiles
        $run = [pscustomobject]@{ RunId = [guid]::NewGuid().ToString('N'); Created = (Get-Date).ToString('s'); MsiSha256 = $msiSha; Computer = $env:COMPUTERNAME; Build = $envInfo.Build }
        Write-Json $run (Join-Path $OutDir 'run.json')
        Write-Host "Started new run $($run.RunId)" -ForegroundColor Cyan
    }

    $checks = New-Object System.Collections.Generic.List[object]
    function Add-Check($name, $ok, $detail, $advisory = $false) { $checks.Add([ordered]@{ Name = $name; Ok = [bool]$ok; Advisory = [bool]$advisory; Detail = $detail }) }
    Add-Check 'Windows 10 or later' ($envInfo.Family -ne 'Older than Windows 10') "$($envInfo.Family) build $($envInfo.Build)"
    Add-Check 'Administrator rights (needed for Lifecycle)' $envInfo.IsAdmin "IsAdmin=$($envInfo.IsAdmin)" $true
    Add-Check 'UAC enabled (needed for the UAC prompt checks M02, M05)' $envInfo.UacEnabled "UacEnabled=$($envInfo.UacEnabled)" $true
    Add-Check 'Clean machine: no AuraClean installed or data present' (@($present).Count -eq 0) $(if (@($present).Count) { @($present) -join '; ' } else { 'none found' })
    foreach ($r in @($arts) + @($pkgs) + @($hist)) { Add-Check "verified: $($r.File)" $r.Ok $r.Detail }

    Write-Json $envInfo (Join-Path $OutDir 'environment.json')
    Write-Json ([ordered]@{ RunId = $run.RunId; Captured = (Get-Date).ToString('s'); Checks = $checks.ToArray() }) (Join-Path $OutDir 'prepare.json')
    $manualPath = Join-Path $OutDir 'manual-results.json'
    if (-not (Test-Path $manualPath)) {
        Write-Json ([ordered]@{ RunId = $run.RunId; Checks = @($ManualChecks | ForEach-Object { [ordered]@{ Id = $_.Id; Title = $_.Title; Status = 'NOT TESTED'; Note = ''; Screenshot = '' } }) }) $manualPath
        Write-Host "  Seeded $manualPath (set Status to PASS or FAIL; every PASS needs a Note and a Screenshot file in results\screenshots)"
    }
    $problems = @($checks.ToArray() | Where-Object { -not $_.Ok } | ForEach-Object { "$($_.Name): $($_.Detail)" })
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
        if (@($p).Count) { throw 'REFUSING: preparation checks failed (see above), including incomplete artifact verification. Nothing was installed.' }
        $run = Read-Json (Join-Path $OutDir 'run.json')
        if (-not $run -or (Test-Path (Join-Path $OutDir 'lifecycle-run.json'))) { throw 'REFUSING: this run already has lifecycle results. Revert the machine and start a new run with -NewRun.' }

        $lifecycle = @((Join-Path $PSScriptRoot 'Test-MsiLifecycle.ps1'), (Join-Path $PSScriptRoot '..\build\Test-MsiLifecycle.ps1')) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $lifecycle) { throw 'Test-MsiLifecycle.ps1 not found next to this script or in ..\build.' }

        # Work copy of the packages in which rc2.msi is the OFFICIAL RC2 MSI (the candidate under acceptance). The upgrade source is the
        # ORIGINAL released rc.1 (.NET 8) MSI from -HistoricalDir, already verified against its pinned SHA-256 by Prepare.
        $work = Join-Path $OutDir 'work-packages'
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        Copy-Item (Join-Path $PackageDir '*') $work -Recurse -Force
        Copy-Item (Join-Path $ArtifactDir (Get-ExpectedArtifactNames)[0]) (Join-Path $work 'rc2.msi') -Force
        Write-Host "Official RC2 MSI used as rc2.msi in $work; original rc.1 (.NET 8) upgrade source: $HistoricalDir" -ForegroundColor Cyan

        $logDir = Join-Path $OutDir 'msi-logs'
        Move-ResultsToPrevious @('msi-logs')
        $started = (Get-Date).ToUniversalTime().ToString('o')
        & $lifecycle -PackageDir $work -HistoricalRc1Msi (Join-Path $HistoricalDir 'AuraClean-1.6.0-rc.1-win-x64.msi') -HistoricalJson $HistoricalJson -LogDir $logDir -ConfirmDisposableMachine $ConfirmDisposable
        $code = $LASTEXITCODE
        $resPath = Join-Path $logDir 'results.json'
        $res = Read-Json $resPath
        Write-Json ([ordered]@{ RunId = $run.RunId; ExitCode = $code; StartedUtc = $started; FinishedUtc = (Get-Date).ToUniversalTime().ToString('o'); Computer = $env:COMPUTERNAME
                ResultsSha256 = $(if (Test-Path $resPath) { (Get-FileHash $resPath -Algorithm SHA256).Hash } else { '' }); ResultsCount = @($res).Count }) (Join-Path $OutDir 'lifecycle-run.json')
        Set-Content (Join-Path $OutDir 'lifecycle-exit.txt') "$code"
        Write-Host "Lifecycle finished with exit code $code. Logs: $logDir. Next: -Mode Report" -ForegroundColor $(if ($code) { 'Red' } else { 'Green' })
        exit $code
    }

    'Report' {
        $a = Get-AcceptanceAssessment $OutDir
        $envInfo = Read-Json (Join-Path $OutDir 'environment.json')
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.AppendLine("# AuraClean $script:ExpectedLabel - client Windows acceptance report")
        [void]$sb.AppendLine("Generated $(Get-Date -Format 's') · run ``$($a.RunId)```n")
        [void]$sb.AppendLine("## Verdict: **$($a.Verdict)**`n")
        foreach ($f in $a.Failures) { [void]$sb.AppendLine("- FAIL: $f") }
        foreach ($i in $a.Incomplete) { [void]$sb.AppendLine("- INCOMPLETE: $i") }
        if ($a.Verdict -eq 'ALL CHECKS PASSED') { [void]$sb.AppendLine('- Every preparation check, the automated lifecycle and all 14 manual checks passed with evidence. This is evidence for this OS/build only.') }
        [void]$sb.AppendLine("`n## Test machine")
        if ($envInfo) { foreach ($p in $envInfo.PSObject.Properties) { [void]$sb.AppendLine("- **$($p.Name):** $($p.Value)") } } else { [void]$sb.AppendLine('- environment.json missing (not recorded)') }
        [void]$sb.AppendLine("`n## Preparation checks")
        foreach ($c in $a.PrepareChecks) { [void]$sb.AppendLine("- $(if ($c.Ok) { 'PASS' } elseif ($c.Advisory) { 'ADVISORY' } else { 'FAIL' }) $($c.Name): $($c.Detail)") }
        if (-not @($a.PrepareChecks).Count) { [void]$sb.AppendLine('- none recorded for this run') }
        [void]$sb.AppendLine("`n## Automated MSI lifecycle (real msiexec): $($a.Lifecycle.State)")
        if ($a.Lifecycle.State -in 'PASS', 'FAIL') {
            [void]$sb.AppendLine("**$($a.Lifecycle.Pass) PASS, $($a.Lifecycle.Fail) FAIL** of $(@($a.Lifecycle.Results).Count). MSI logs: ``$OutDir\msi-logs``.`n")
            foreach ($g in @($a.Lifecycle.Results) | Group-Object Section) { $b = @($g.Group | Where-Object { -not $_.Ok }).Count; [void]$sb.AppendLine("- $(if ($b) { 'FAIL' } else { 'PASS' }) $($g.Name): $($g.Count - $b)/$($g.Count)") }
            foreach ($f in @($a.Lifecycle.Failed)) { [void]$sb.AppendLine("  - FAIL [$($f.Section)] $($f.Message)") }
        } else { [void]$sb.AppendLine('No valid lifecycle results for this run: treated as NOT TESTED / failed above.') }
        [void]$sb.AppendLine("`n## Manual / interactive checks (14 required)")
        [void]$sb.AppendLine('| ID | Check | Result | Notes | Screenshot |'); [void]$sb.AppendLine('|---|---|---|---|---|')
        foreach ($m in $a.Manual) { [void]$sb.AppendLine("| $($m.Id) | $($m.Title) | **$($m.Status)$(if ($m.Status -eq 'PASS' -and -not $m.Valid) { ' (evidence missing)' })** | $($m.Note) | $($m.Screenshot) |") }
        [void]$sb.AppendLine("`n## Status`n- Confirmed release blockers: list each FAIL above with its log/screenshot.`n- Signing: unsigned (decision pending, see docs/SIGNING.md). Distribution: not published, no tag, no release.")
        $sb.ToString() | Out-File (Join-Path $OutDir 'report.md') -Encoding utf8
        Write-Host "Report written: $(Join-Path $OutDir 'report.md')  Verdict: $($a.Verdict)" -ForegroundColor $(if ($a.Verdict -eq 'ALL CHECKS PASSED') { 'Green' } elseif ($a.Verdict -eq 'FAIL') { 'Red' } else { 'Yellow' })
        exit $(if ($a.Verdict -eq 'ALL CHECKS PASSED') { 0 } elseif ($a.Verdict -eq 'FAIL') { 1 } else { 2 })
    }

    'Pack' {
        $repo = Split-Path -Parent $PSScriptRoot
        $rows = @(Test-Artifacts) + @(Test-Packages) + @(Test-HistoricalArtifacts $HistoricalDir (Join-Path $repo 'build\historical-artifacts.json')); Show-Rows $rows
        if (@($rows | Where-Object { -not $_.Ok }).Count) { throw 'Artifacts/packages did not verify completely; not packing.' }
        if (-not $KitZip) { $KitZip = Join-Path $OutDir 'AuraClean-acceptance-kit.zip' }
        $stage = Join-Path ([IO.Path]::GetTempPath()) ('AuraKit_' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $stage | Out-Null
        try {
            New-KitStage -RepoRoot $repo -ArtifactDir $ArtifactDir -PackageDir $PackageDir -StageDir $stage -HistoricalDir $HistoricalDir
            if (Test-Path $KitZip) { [IO.File]::Delete($KitZip) }
            Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $KitZip
        } finally { [IO.Directory]::Delete($stage, $true) }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($KitZip)
        try { $entries = @($zip.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }) } finally { $zip.Dispose() }
        $lacking = @($script:KitFiles | Where-Object { $entries -notcontains ($_.Dest -replace '\\', '/') })
        if ($lacking.Count) { throw "The packed kit is missing: $(($lacking | ForEach-Object { $_.Dest }) -join ', ')" }
        Write-Host "Kit: $KitZip ($($entries.Count) files, includes scripts/Remove-LegacyInstall.ps1; nothing in it runs automatically)"; Write-Host "SHA-256: $((Get-FileHash $KitZip -Algorithm SHA256).Hash.ToLowerInvariant())"
    }
}
