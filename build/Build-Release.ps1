<#
.SYNOPSIS
    Builds the AuraClean release artifacts: MSI installer, portable EXE and SHA-256 checksums, then verifies them.

.DESCRIPTION
    Repeatable release build (same steps locally and in .github/workflows/release.yml):
      1. dotnet publish  -> self-contained, single-file, win-x64 AuraClean.exe
      2. (optional) run the full automated test suite
      3. (optional) Authenticode-sign the EXE            [only if signing secrets are provided; see docs/SIGNING.md]
      4. dotnet build installer/AuraClean.Installer.wixproj -> MSI wrapping the (signed) EXE
      5. (optional) sign the MSI
      6. copy artifacts + write SHA256SUMS.txt
      7. build/Test-Release.ps1 verification (metadata, layout, version consistency, data-safety rules)

    Nothing is installed and no host data is touched. The version comes from Directory.Build.props.

.PARAMETER OutputDir
    Where the final artifacts are written (default: <repo>/artifacts/release).

.PARAMETER SkipTests
    Skip the TestFeatures suite (not recommended for a release).

.PARAMETER NoUiTests
    Skip the WPF fixtures that need an interactive desktop (sets AURACLEAN_NO_UI_TESTS=1); used by CI.

.EXAMPLE
    pwsh build/Build-Release.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [switch]$SkipTests,
    [switch]$NoUiTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$AppProject = Join-Path $RepoRoot 'AuraClean/AuraClean.csproj'
$WixProject = Join-Path $RepoRoot 'installer/AuraClean.Installer.wixproj'
$PublishDir = Join-Path $RepoRoot 'artifacts/publish'
if (-not $OutputDir) { $OutputDir = Join-Path $RepoRoot 'artifacts/release' }

. (Join-Path $PSScriptRoot 'ReleaseArtifacts.ps1')

function Write-Step($m) { Write-Host "`n[*] $m" -ForegroundColor Cyan }
function Invoke-Checked([string]$what, [scriptblock]$cmd) {
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

# ── version (single source: Directory.Build.props) ────────────────────────────────────────────────
$props = (& dotnet msbuild $AppProject -nologo -getProperty:VersionPrefix -getProperty:VersionSuffix | ConvertFrom-Json).Properties
$prefix = $props.VersionPrefix
$suffix = $props.VersionSuffix
$label  = if ($suffix) { "$prefix-$suffix" } else { $prefix }
Write-Host "AuraClean release version: $label (MSI ProductVersion $prefix)" -ForegroundColor Green

# ── 1. publish ────────────────────────────────────────────────────────────────────────────────────
Write-Step 'Publishing self-contained single-file win-x64 build'
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
Invoke-Checked 'dotnet publish' {
    dotnet publish $AppProject -c Release -r win-x64 --self-contained true -o $PublishDir -p:ContinuousIntegrationBuild=true
}
$exe = Join-Path $PublishDir 'AuraClean.exe'
if (-not (Test-Path $exe)) { throw "Publish output not found: $exe" }

# ── 2. tests ──────────────────────────────────────────────────────────────────────────────────────
if ($SkipTests) {
    Write-Warning 'Skipping the automated test suite (-SkipTests).'
} else {
    Write-Step 'Running the full automated test suite'
    if ($NoUiTests) { $env:AURACLEAN_NO_UI_TESTS = '1' }
    Invoke-Checked 'dotnet build (solution)' { dotnet build (Join-Path $RepoRoot 'cleaner.sln') -c Release -p:ContinuousIntegrationBuild=true }
    Invoke-Checked 'TestFeatures' { dotnet run --project (Join-Path $RepoRoot 'TestFeatures') -c Release --no-build }
}

# ── optional signing (never required; secrets come from the environment, never from the repo) ────
function Invoke-Sign([string]$file) {
    if (-not $env:SIGN_CERT_PFX_PATH -or -not $env:SIGN_CERT_PASSWORD) { return $false }
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue)?.Source
    if (-not $signtool) { throw 'SIGN_CERT_PFX_PATH is set but signtool.exe was not found (install the Windows SDK).' }
    $timestamp = if ($env:SIGN_TIMESTAMP_URL) { $env:SIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
    Write-Step "Signing $(Split-Path -Leaf $file)"
    & $signtool sign /fd SHA256 /f $env:SIGN_CERT_PFX_PATH /p $env:SIGN_CERT_PASSWORD /tr $timestamp /td SHA256 /d 'AuraClean' $file
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $file" }
    return $true
}
$signed = Invoke-Sign $exe

# ── 4. MSI ────────────────────────────────────────────────────────────────────────────────────────
Write-Step 'Building the MSI installer (WiX v5)'
# Isolate this build: remove earlier installer outputs, then accept only the exact file this build must produce.
$msiBuildDir = Join-Path $RepoRoot 'installer/bin/Release'
Clear-StaleInstallerOutput -BuildOutputDir $msiBuildDir
$buildStartedUtc = (Get-Date).ToUniversalTime()
Invoke-Checked 'dotnet build (installer)' {
    dotnet build $WixProject -c Release "-p:PublishDir=$PublishDir$([IO.Path]::DirectorySeparatorChar)" -p:ContinuousIntegrationBuild=true
}
$msiBuilt = Get-ExpectedMsi -BuildOutputDir $msiBuildDir -Label $label -BuildStartedUtc $buildStartedUtc
if (Invoke-Sign $msiBuilt.FullName) { $signed = $true }

# ── 6. artifacts + checksums ──────────────────────────────────────────────────────────────────────
Write-Step "Collecting artifacts in $OutputDir"
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir | Out-Null
$msiOut      = Join-Path $OutputDir "AuraClean-$label-win-x64.msi"
$portableOut = Join-Path $OutputDir "AuraClean-$label-win-x64-portable.exe"
Copy-Item $msiBuilt.FullName $msiOut
Copy-Item $exe $portableOut

$sums = Get-ChildItem $OutputDir -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$h *$($_.Name)"
}
# LF line endings, no BOM: verifiable with `sha256sum -c SHA256SUMS.txt` as well as Get-FileHash.
[IO.File]::WriteAllText((Join-Path $OutputDir 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
$sums | ForEach-Object { Write-Host "    $_" }

# ── 7. verification ───────────────────────────────────────────────────────────────────────────────
Write-Step 'Verifying the artifacts'
& (Join-Path $PSScriptRoot 'Test-Release.ps1') -ArtifactsDir $OutputDir
if ($LASTEXITCODE -ne 0) { throw 'Release verification failed.' }

Write-Host ''
Write-Host "Release artifacts for $label are in $OutputDir" -ForegroundColor Green
if (-not $signed) { Write-Warning 'These artifacts are NOT Authenticode-signed (no signing secrets were provided). Windows will show "Unknown publisher" / SmartScreen warnings. See docs/SIGNING.md.' }
