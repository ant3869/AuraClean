<#
.SYNOPSIS
    Regression tests for the release build scripts (no build, no installer, no real data: synthetic temp folders only).

.DESCRIPTION
    Proves that an old MSI left in the installer output folder can never be collected and labelled as the new release
    (Get-ExpectedMsi / Clear-StaleInstallerOutput in build/ReleaseArtifacts.ps1), and that Build-Release.ps1 uses them
    instead of "first *.msi". Exits non-zero on any failure. Also run by TestFeatures (ReleaseMetadataTests).
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Pass = 0
$script:Fail = 0
function Check([bool]$ok, [string]$message) {
    if ($ok) { Write-Host "  PASS: $message" -ForegroundColor Green; $script:Pass++ }
    else     { Write-Host "  FAIL: $message" -ForegroundColor Red;   $script:Fail++ }
}
function Throws([scriptblock]$code) { try { & $code | Out-Null; $false } catch { $true } }

. (Join-Path $PSScriptRoot 'ReleaseArtifacts.ps1')
$label = '1.6.0-rc.2'
$root = Join-Path ([IO.Path]::GetTempPath()) ('AuraBuildScripts_' + [guid]::NewGuid().ToString('N'))

function New-Msi([string]$dir, [string]$name, [datetime]$writtenUtc) {
    $f = Join-Path $dir $name
    Set-Content -LiteralPath $f -Value 'synthetic msi'
    (Get-Item -LiteralPath $f).LastWriteTimeUtc = $writtenUtc
    $f
}
function New-Case([string]$name) { $d = Join-Path $root $name; New-Item -ItemType Directory -Path $d -Force | Out-Null; $d }

try {
    $start = (Get-Date).ToUniversalTime()
    $old = $start.AddHours(-3)

    Write-Host "`n== Stale MSI artifacts can never be mislabelled as the new release ==" -ForegroundColor Cyan

    $d = New-Case 'only-older-version'
    New-Msi $d 'AuraClean-1.6.0-rc.1-win-x64.msi' $old | Out-Null
    Check (Throws { Get-ExpectedMsi -BuildOutputDir $d -Label $label -BuildStartedUtc $start }) "Only an older version's MSI present (build produced nothing): refused, not renamed"

    $d = New-Case 'only-other-version'
    New-Msi $d 'AuraClean-1.6.0-rc.3-win-x64.msi' $start.AddMinutes(1) | Out-Null
    Check (Throws { Get-ExpectedMsi -BuildOutputDir $d -Label $label -BuildStartedUtc $start }) 'A fresh MSI of a different version is not accepted as this label either'

    $d = New-Case 'expected-name-but-stale'
    New-Msi $d "AuraClean-$label-win-x64.msi" $old | Out-Null
    Check (Throws { Get-ExpectedMsi -BuildOutputDir $d -Label $label -BuildStartedUtc $start }) 'Right file name but written before this build started (leftover): refused'

    $d = New-Case 'empty'
    Check (Throws { Get-ExpectedMsi -BuildOutputDir $d -Label $label -BuildStartedUtc $start }) 'No MSI at all: refused with an error'
    Check (Throws { Get-ExpectedMsi -BuildOutputDir (Join-Path $root 'does-not-exist') -Label $label -BuildStartedUtc $start }) 'Missing output folder: refused with an error'

    $d = New-Case 'fresh-among-stale'
    New-Msi $d 'AuraClean-1.6.0-rc.1-win-x64.msi' $old | Out-Null
    New-Msi $d 'AuraClean-0.9.0-win-x64.msi' $old.AddDays(-30) | Out-Null
    $fresh = New-Msi $d "AuraClean-$label-win-x64.msi" $start.AddSeconds(5)
    $picked = Get-ExpectedMsi -BuildOutputDir $d -Label $label -BuildStartedUtc $start
    Check ($picked.FullName -eq $fresh) "With stale MSIs alongside, exactly the current build's MSI is selected (alphabetical order is irrelevant)"

    $d = New-Case 'cleanup'
    $keep = Join-Path $d 'notes.txt'; Set-Content $keep 'keep'
    New-Msi $d 'AuraClean-1.6.0-rc.1-win-x64.msi' $old | Out-Null
    Set-Content (Join-Path $d 'AuraClean-1.6.0-rc.1-win-x64.wixpdb') 'pdb'
    Clear-StaleInstallerOutput -BuildOutputDir $d
    Check (@(Get-ChildItem $d -Filter '*.msi').Count -eq 0 -and @(Get-ChildItem $d -Filter '*.wixpdb').Count -eq 0) 'Clear-StaleInstallerOutput removes earlier MSI/wixpdb outputs'
    Check (Test-Path $keep) 'Clear-StaleInstallerOutput leaves unrelated files alone'
    Check (-not (Throws { Clear-StaleInstallerOutput -BuildOutputDir (Join-Path $root 'does-not-exist') })) 'Clearing a missing folder is a no-op'

    Write-Host "`n== Build-Release.ps1 uses the exact-output selection ==" -ForegroundColor Cyan
    $build = Get-Content (Join-Path $PSScriptRoot 'Build-Release.ps1') -Raw
    Check ($build -match 'ReleaseArtifacts\.ps1') 'It loads the shared helpers'
    Check ($build -match 'Get-ExpectedMsi\s+-BuildOutputDir' -and $build -match 'Clear-StaleInstallerOutput') 'It clears old installer output and selects the expected MSI'
    Check ($build -notmatch "Filter\s+'\*\.msi'[^\r\n]*Select-Object\s+-First\s+1") 'It no longer selects the first *.msi in the folder'
    $clear = $build.IndexOf('Clear-StaleInstallerOutput -BuildOutputDir'); $stamp = $build.IndexOf('$buildStartedUtc ='); $run = $build.IndexOf('dotnet build $WixProject'); $pick = $build.IndexOf('Get-ExpectedMsi -BuildOutputDir')
    Check ($clear -ge 0 -and $clear -lt $stamp -and $stamp -lt $run -and $run -lt $pick) 'Order is: clear, record start time, build, select (the start time precedes the build)'

    Write-Host "`n== A released label can never be rebuilt under a different runtime ==" -ForegroundColor Cyan
    $json = Join-Path $PSScriptRoot 'historical-artifacts.json'
    $hist = @(Get-RetiredReleases $json)
    Check ($hist.Count -ge 1 -and @($hist | Where-Object { $_.label -ceq '1.6.0-rc.1' -and $_.runtime -match '\.NET 8' -and $_.msiProductVersion -eq '1.6.1' }).Count -eq 1) 'The original 1.6.0-rc.1 (.NET 8, MSI 1.6.1) is recorded as a retired release'
    Check (@($hist | ForEach-Object { $_.files } | Where-Object { $_.sha256 -notmatch '^[0-9a-f]{64}$' -or -not $_.name }).Count -eq 0) 'Every historical artifact is pinned by a SHA-256'
    Check (Throws { Assert-LabelNotRetired -Label '1.6.0-rc.1' -HistoricalJson $json }) 'Building under the retired label 1.6.0-rc.1 is refused (a .NET 10 build must not be distributed as the .NET 8 RC1)'
    Check (-not (Throws { Assert-LabelNotRetired -Label '1.6.0-rc.2' -HistoricalJson $json })) 'A new label (1.6.0-rc.2) is allowed'
    Check (-not (Throws { Assert-LabelNotRetired -Label '1.6.0-rc.10' -HistoricalJson $json })) 'The check is an exact match (1.6.0-rc.10 is not the retired 1.6.0-rc.1)'
    Check ($build -match 'Assert-LabelNotRetired\s+-Label\s+\$label') 'Build-Release.ps1 applies the guard to the label it is about to build'
}
finally { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ''
Write-Host "RESULTS: $script:Pass passed, $script:Fail failed" -ForegroundColor $(if ($script:Fail) { 'Red' } else { 'Green' })
exit $(if ($script:Fail) { 1 } else { 0 })
