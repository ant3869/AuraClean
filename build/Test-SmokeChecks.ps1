<#
.SYNOPSIS
    Regression tests for the smoke test's window-identity logic (build/SmokeChecks.ps1). Pure: synthetic window descriptors, no
    process is started and no window is opened, so it is safe anywhere (it also runs inside TestFeatures).

.DESCRIPTION
    Reproduces the original, flawed criterion ("the process has a main window handle and responds") and shows that it accepts a
    startup-error MessageBox, then shows that Test-AppWindow rejects that situation while accepting a genuine application window.
    The live version of the same check (real stand-in processes with a real MessageBox) runs on hosted runners from
    build/Test-PublishedExeSmoke.ps1.
#>
[CmdletBinding()] param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'SmokeChecks.ps1')
$script:Pass = 0; $script:Fail = 0
function Check([bool]$ok, [string]$m) { if ($ok) { Write-Host "  PASS: $m" -ForegroundColor Green; $script:Pass++ } else { Write-Host "  FAIL: $m" -ForegroundColor Red; $script:Fail++ } }
function W($class, $title = 'AuraClean', $visible = $true, $min = $false, $w = 1100, $h = 750) { [pscustomobject]@{ Class = $class; Title = $title; Visible = $visible; Minimized = $min; Width = $w; Height = $h } }
function Accepts($windows, $hostName = 'AuraClean') { [bool](Test-AppWindow $windows $hostName).Ok }
$real = W 'HwndWrapper[AuraClean.exe;;6f1b2c3d-1111-2222-3333-444455556666]'
$errorBox = W '#32770' 'AuraClean' $true $false 420 180
function Legacy-Accepts($windows) { @($windows | Where-Object { $_.Visible }).Count -gt 0 }   # original criterion: any visible window with a handle

Write-Host "`n== Original criterion vs. startup-error dialog ==" -ForegroundColor Cyan
Check (Legacy-Accepts @($errorBox)) 'INJECTION: the ORIGINAL criterion (a window handle that responds) accepts a startup-error MessageBox titled "AuraClean"'
Check (-not (Accepts @($errorBox))) 'The new check rejects the startup-error MessageBox'
Check (-not (Accepts @($real, $errorBox))) 'The new check rejects a genuine window with a startup-error MessageBox on top'
Check (@((Test-AppWindow @($errorBox)).Reasons | Where-Object { $_ -match 'blocking system dialog' }).Count -gt 0) 'The rejection says a blocking system dialog is showing'

Write-Host "`n== Genuine application window ==" -ForegroundColor Cyan
Check (Accepts @($real)) 'A visible HwndWrapper[AuraClean.exe;...] window titled "AuraClean" of normal size is accepted'
Check (Accepts @($real, (W 'HwndWrapper[AuraClean.exe;;aaaa]' '' $false $false 0 0))) 'Additional hidden helper windows do not matter'
Check (-not (Accepts @((W 'HwndWrapper[pwsh.exe;;1]')))) 'A pwsh window is not accepted as AuraClean (host name must match)'
Check (Accepts @((W 'HwndWrapper[pwsh;Pipeline Execution Thread;1b83b354-384d-4c6f-96fe-8e61992d81d6]')) 'pwsh') 'A stand-in host whose class has no .exe (observed on windows-2025) is accepted when it is the expected host (live self-test)'
Check (Accepts @((W 'HwndWrapper[AuraClean;;1]'))) 'The host name matches with or without ".exe" in the class'
Check (Accepts @((W 'HwndWrapper[AuraClean.exe;;1]')) 'AuraClean.exe') 'An expected host name given with ".exe" also matches'
Check (-not (Accepts $null)) 'No windows yet ($null from an empty enumeration) is a clean rejection, not an error'
Check (-not (Accepts @($null))) 'A list containing only $null is a clean rejection, not an error'

Write-Host "`n== Other states that are not a successful startup ==" -ForegroundColor Cyan
Check (-not (Accepts @())) 'No windows at all'
Check (-not (Accepts @((W 'HwndWrapper[AuraClean.exe;;1]' 'AuraClean' $false)))) 'Invisible application window'
Check (-not (Accepts @((W 'HwndWrapper[AuraClean.exe;;1]' 'AuraClean' $true $true)))) 'Minimised application window'
Check (-not (Accepts @((W 'HwndWrapper[AuraClean.exe;;1]' 'Something else')))) 'Wrong window title'
Check (-not (Accepts @((W 'HwndWrapper[AuraClean.exe;;1]' 'AuraClean' $true $false 120 40)))) 'A tiny window (not the real UI)'
Check (-not (Accepts @((W 'HwndWrapper[SomethingElse.exe;;1]')))) 'A WPF window from a different host process name'
Check (-not (Accepts @((W 'Window' 'AuraClean')))) 'A non-WPF window class titled "AuraClean"'
Check (-not (Accepts @($real, (W '#32770' 'Error' $true)))) 'Any visible system dialog of the process, whatever its title'
Check (Accepts @($real, (W '#32770' 'Error' $false))) 'A hidden dialog is not blocking'

Write-Host "`nRESULTS: $script:Pass passed, $script:Fail failed" -ForegroundColor $(if ($script:Fail) { 'Red' } else { 'Green' })
exit $(if ($script:Fail) { 1 } else { 0 })
