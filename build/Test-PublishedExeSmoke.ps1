<#
.SYNOPSIS
    Real-GUI smoke test of the published single-file AuraClean.exe: it must start, show a responding window and run on its own
    bundled .NET runtime, without any separately installed .NET (DOTNET_ROOT and every dotnet folder are removed from its environment).

.DESCRIPTION
    Starts the app (which creates %LocalAppData%\AuraClean on the machine and requests elevation), so it refuses to run anywhere except
    a disposable GitHub-hosted runner (GITHUB_ACTIONS=true and RUNNER_ENVIRONMENT=github-hosted). Never run it on a developer machine.
    It performs no cleanup or uninstall operation: it only launches the window, checks it, and closes it.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExePath, [string]$ExpectedRuntimeMajor = '10')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'REFUSING TO RUN: this smoke test launches AuraClean and writes its data folder; run it only on a disposable GitHub-hosted runner.'
}
$script:Pass = 0; $script:Fail = 0
function Check([bool]$ok, [string]$m) { if ($ok) { Write-Host "  PASS: $m" -ForegroundColor Green; $script:Pass++ } else { Write-Host "  FAIL: $m" -ForegroundColor Red; $script:Fail++ } }

$ExePath = (Resolve-Path $ExePath).Path
Check ((Get-Item $ExePath).Length -gt 20MB) "Published EXE found and is self-contained size ($([int]((Get-Item $ExePath).Length / 1MB)) MB)"

$psi = New-Object Diagnostics.ProcessStartInfo $ExePath
$psi.UseShellExecute = $false
$psi.WorkingDirectory = Split-Path $ExePath
foreach ($k in 'DOTNET_ROOT', 'DOTNET_ROOT(x86)', 'DOTNET_ROOT_X64', 'DOTNET_MULTILEVEL_LOOKUP') { [void]$psi.Environment.Remove($k) }
$psi.Environment['PATH'] = (($env:PATH -split ';' | Where-Object { $_ -and $_ -notmatch 'dotnet' }) -join ';')
Check (-not ($psi.Environment['PATH'] -match 'dotnet')) 'The app is started with no dotnet folder on PATH and no DOTNET_ROOT'

$p = [Diagnostics.Process]::Start($psi)
try {
    $deadline = (Get-Date).AddSeconds(90); $hwnd = [IntPtr]::Zero
    while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
        $p.Refresh(); $hwnd = $p.MainWindowHandle
        if ($hwnd -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 500
    }
    Check (-not $p.HasExited) "AuraClean is still running after startup (exit code if it died: $(if ($p.HasExited) { $p.ExitCode } else { 'n/a' }))"
    Check ($hwnd -ne [IntPtr]::Zero) 'A main window was created (WPF initialisation, resources and manifest ok)'
    if (-not $p.HasExited) {
        Start-Sleep -Seconds 10   # let the startup scan / initial navigation run
        $p.Refresh()
        Check (-not $p.HasExited -and $p.Responding) 'The window is still alive and responding after 10 s of startup work'
        # A single-file app statically links the runtime into the host (no separate coreclr.dll), so prove independence differently:
        # nothing was loaded from an installed .NET (\dotnet\shared, \dotnet\host), and any runtime DLL that is loaded matches the bundled major.
        $mods = @($p.Modules)
        $fromInstalled = @($mods | Where-Object { $_.FileName -match '\\dotnet\\(shared|host|sdk)\\' })
        Check ($fromInstalled.Count -eq 0) "No module was loaded from an installed .NET ($($mods.Count) modules inspected)"
        $rt = @($mods | Where-Object { $_.ModuleName -in 'coreclr.dll', 'clrjit.dll', 'hostpolicy.dll' })
        foreach ($m in $rt) { Write-Host "  INFO: $($m.ModuleName) $($m.FileVersionInfo.ProductVersion) from $($m.FileName)" -ForegroundColor Yellow }
        Check (@($rt | Where-Object { $_.FileVersionInfo.ProductVersion -notlike "$ExpectedRuntimeMajor.*" }).Count -eq 0) "Any runtime DLL loaded is .NET $ExpectedRuntimeMajor ($(@($rt).Count) found)"
        Check (@($p.Modules | Where-Object { $_.ModuleName -match 'PresentationCore|wpfgfx' }).Count -ge 1) 'WPF rendering modules are loaded'
    }
}
finally {
    if (-not $p.HasExited) { [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(15000)) { $p.Kill($true) } }
}
Write-Host "RESULTS: $script:Pass passed, $script:Fail failed" -ForegroundColor $(if ($script:Fail) { 'Red' } else { 'Green' })
exit $(if ($script:Fail) { 1 } else { 0 })
