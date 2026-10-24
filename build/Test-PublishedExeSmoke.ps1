<#
.SYNOPSIS
    Real-GUI smoke test of the published single-file AuraClean.exe: it must start, show the genuine application window (not a startup
    error dialog), stay up, write no crash log, and run on its own bundled .NET runtime without any separately installed .NET.

.DESCRIPTION
    Starts the app (which creates %LocalAppData%\AuraClean on the machine and requests elevation), so it refuses to run anywhere except
    a disposable GitHub-hosted runner (GITHUB_ACTIONS=true and RUNNER_ENVIRONMENT=github-hosted). Never run it on a developer machine.
    It performs no cleanup or uninstall operation.

    Startup is accepted only when ALL of these hold (see build/SmokeChecks.ps1):
      * the process has a visible, non-minimised WPF window of class HwndWrapper[AuraClean.exe;;...] titled "AuraClean" of a real size;
      * no system dialog (class #32770, e.g. the MessageBox App.OnStartup shows when MainWindow fails, also titled "AuraClean") exists;
      * the process stays alive and the window stays valid for a settling period;
      * AURACLEAN_LOG_DIR (a fresh temp folder) holds no crash.log;
      * after closing, an exited process has exit code 0; a process that does not exit is terminated (never left running).
    Before the real run it self-tests the detector with real stand-in processes: a WPF window titled "AuraClean" must be accepted, a
    MessageBox titled "AuraClean" (alone, or on top of a window) must be rejected.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExePath, [string]$ExpectedRuntimeMajor = '10', [int]$SettleSeconds = 10)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'REFUSING TO RUN: this smoke test launches AuraClean and writes its data folder; run it only on a disposable GitHub-hosted runner.'
}
. (Join-Path $PSScriptRoot 'SmokeChecks.ps1')
$script:Pass = 0; $script:Fail = 0
function Check([bool]$ok, [string]$m) { if ($ok) { Write-Host "  PASS: $m" -ForegroundColor Green; $script:Pass++ } else { Write-Host "  FAIL: $m" -ForegroundColor Red; $script:Fail++ } }
function Section($t) { Write-Host "`n== $t ==" -ForegroundColor Cyan }
$work = Join-Path $env:RUNNER_TEMP ('smoke_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

# Poll until the assessment is final: acceptable, or a blocking dialog appeared, or the process exited, or the timeout elapsed.
function Wait-AppWindow([Diagnostics.Process]$P, [string]$HostName, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds); $last = $null
    while ((Get-Date) -lt $deadline) {
        if ($P.HasExited) { return @{ Ok = $false; Reasons = @("the process exited with code $($P.ExitCode) before a window was shown") } }
        $last = Test-AppWindow (Get-AppWindows $P.Id) $HostName
        if ($last.Ok -or @($last.Reasons | Where-Object { $_ -match 'blocking system dialog' }).Count) { return $last }
        Start-Sleep -Milliseconds 500
    }
    if ($null -eq $last) { $last = @{ Ok = $false; Reasons = @('timed out') } }
    $last
}

# ── 1. self-test of the detector with real stand-in processes ────────────────────────────────────
Section 'Detector self-test (stand-in processes with a real WPF window / a real MessageBox titled "AuraClean")'
$child = Join-Path $work 'standin.ps1'
@'
param($mode)
Add-Type -AssemblyName PresentationFramework
$w = New-Object System.Windows.Window
$w.Title = 'AuraClean'; $w.Width = 900; $w.Height = 600; $w.WindowStartupLocation = 'CenterScreen'
switch ($mode) {
    'good'  { [void]$w.ShowDialog() }
    'error' { [void][System.Windows.MessageBox]::Show('AuraClean could not start: simulated', 'AuraClean', 'OK', 'Error') }
    'both'  { $w.Show(); [void][System.Windows.MessageBox]::Show('AuraClean could not start: simulated', 'AuraClean', 'OK', 'Error') }
}
'@ | Set-Content -LiteralPath $child -Encoding UTF8
$pwsh = (Get-Process -Id $PID).Path
$cases = @(
    @{ Mode = 'good';  Accept = $true;  Name = 'A genuine WPF window titled "AuraClean" is accepted' }
    @{ Mode = 'error'; Accept = $false; Name = 'A MessageBox titled "AuraClean" (simulated startup error) is rejected' }
    @{ Mode = 'both';  Accept = $false; Name = 'A real window with a startup-error MessageBox on top is rejected' })
foreach ($case in $cases) {
    $sp = Start-Process -FilePath $pwsh -ArgumentList @('-NoProfile', '-STA', '-File', $child, $case.Mode) -PassThru -WindowStyle Hidden
    try {
        $r = Wait-AppWindow $sp 'pwsh.exe' 60
        if ($case.Accept) { Check $r.Ok "$($case.Name)$(if (-not $r.Ok) { ': ' + ($r.Reasons -join ' | ') })" }
        else { Check (-not $r.Ok) "$($case.Name) ($($r.Reasons -join ' | '))" }
    }
    finally { if (-not $sp.HasExited) { $sp.Kill($true); [void]$sp.WaitForExit(10000) } }
}

# ── 2. the real published EXE ────────────────────────────────────────────────────────────────────
Section 'Published single-file EXE'
$ExePath = (Resolve-Path $ExePath).Path
Check ((Get-Item $ExePath).Length -gt 20MB) "Published EXE found and is self-contained size ($([int]((Get-Item $ExePath).Length / 1MB)) MB)"
$logDir = Join-Path $work 'applogs'
$psi = New-Object Diagnostics.ProcessStartInfo $ExePath
$psi.UseShellExecute = $false
$psi.WorkingDirectory = Split-Path $ExePath
foreach ($k in 'DOTNET_ROOT', 'DOTNET_ROOT(x86)', 'DOTNET_ROOT_X64', 'DOTNET_MULTILEVEL_LOOKUP') { [void]$psi.Environment.Remove($k) }
$psi.Environment['PATH'] = (($env:PATH -split ';' | Where-Object { $_ -and $_ -notmatch 'dotnet' }) -join ';')
$psi.Environment['AURACLEAN_LOG_DIR'] = $logDir   # crash.log (written by App.OnStartup / dispatcher failures) lands here
Check (-not ($psi.Environment['PATH'] -match 'dotnet')) 'The app is started with no dotnet folder on PATH and no DOTNET_ROOT'

$p = [Diagnostics.Process]::Start($psi)
$exitedGracefully = $null; $killed = $false
try {
    $r = Wait-AppWindow $p 'AuraClean.exe' 90
    Check $r.Ok "The genuine AuraClean window is up$(if (-not $r.Ok) { ': ' + ($r.Reasons -join ' | ') })"
    if (-not $p.HasExited -and $r.Ok) {
        $until = (Get-Date).AddSeconds($SettleSeconds); $stable = $true; $why = ''
        while ((Get-Date) -lt $until) {
            Start-Sleep -Seconds 1
            if ($p.HasExited) { $stable = $false; $why = "the process exited with code $($p.ExitCode)"; break }
            $a = Test-AppWindow (Get-AppWindows $p.Id) 'AuraClean.exe'
            if (-not $a.Ok) { $stable = $false; $why = ($a.Reasons -join ' | '); break }
        }
        Check $stable "The process and window stay valid for $SettleSeconds s of startup work$(if (-not $stable) { ': ' + $why })"
        if (-not $p.HasExited) {
            $p.Refresh(); Check ($stable -and $p.Responding) 'The window is responding'
            $mods = @($p.Modules)
            $fromInstalled = @($mods | Where-Object { $_.FileName -match '\\dotnet\\(shared|host|sdk)\\' })
            Check ($fromInstalled.Count -eq 0) "No module was loaded from an installed .NET ($($mods.Count) modules inspected)"
            $rt = @($mods | Where-Object { $_.ModuleName -in 'coreclr.dll', 'clrjit.dll', 'hostpolicy.dll' })
            foreach ($m in $rt) { Write-Host "  INFO: $($m.ModuleName) $($m.FileVersionInfo.ProductVersion) from $($m.FileName)" -ForegroundColor Yellow }
            Check (@($rt | Where-Object { $_.FileVersionInfo.ProductVersion -notlike "$ExpectedRuntimeMajor.*" }).Count -eq 0) "Any runtime DLL loaded is .NET $ExpectedRuntimeMajor ($(@($rt).Count) found; a single-file host links coreclr statically)"
            Check (@($mods | Where-Object { $_.ModuleName -match 'PresentationCore|wpfgfx' }).Count -ge 1) 'WPF rendering modules are loaded'
        }
    }
}
finally {
    # Always end the process we launched: ask politely, then force (never leave AuraClean running on the runner).
    if (-not $p.HasExited) {
        [void]$p.CloseMainWindow()
        if ($p.WaitForExit(15000)) { $exitedGracefully = $true }
        else { $killed = $true; try { $p.Kill($true) } catch { }; [void]$p.WaitForExit(10000) }
    }
}
Check $p.HasExited 'The launched process is no longer running'
if ($exitedGracefully -eq $true) { Check ($p.ExitCode -eq 0) "On close the app exited cleanly (exit code $($p.ExitCode))" }
elseif ($killed) { Write-Host '  INFO: the app did not exit within 15 s of closing its window (it may minimise to tray); it was terminated by the test' -ForegroundColor Yellow }
else { Check $false "The process had already exited before the test ended (exit code $($p.ExitCode))" }
$crash = Join-Path $logDir 'crash.log'
Check (-not (Test-Path $crash) -or (Get-Item $crash).Length -eq 0) "No startup/dispatcher crash was logged$(if (Test-Path $crash) { ': ' + ((Get-Content $crash -TotalCount 5 -ErrorAction SilentlyContinue) -join ' ') })"

Write-Host "RESULTS: $script:Pass passed, $script:Fail failed" -ForegroundColor $(if ($script:Fail) { 'Red' } else { 'Green' })
exit $(if ($script:Fail) { 1 } else { 0 })
