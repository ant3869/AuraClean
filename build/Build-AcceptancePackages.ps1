<#
.SYNOPSIS
    Builds the MSI packages for the MSI acceptance tests from the CURRENT source: rc2.msi (the label in Directory.Build.props, i.e. the candidate under test), plus throw-away final.msi and final-rebuild.msi that exist only to test upgrade/downgrade/duplicate rules. The original .NET 8 RC1 is never rebuilt here: the tests use the hash-pinned historical files (build/historical-artifacts.json).

.DESCRIPTION
    The version overrides are passed on the command line (-p:VersionSuffix=...); nothing in the repository is changed and the
    packages must never be published. Building is safe anywhere; installing them is done only on disposable GitHub-hosted
    runners by build/Test-MsiLifecycle.ps1.

.PARAMETER OutputDir
    Folder that receives rc2.msi, final.msi, final-rebuild.msi and portable/AuraClean.exe.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDir,
    # Do not build rc2.msi (the caller supplies the OFFICIAL, already-built candidate instead of a rebuilt substitute).
    [switch]$SkipCandidate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseArtifacts.ps1')

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$PublishDir = Join-Path $RepoRoot 'artifacts/acceptance-publish'
$BuildDir   = Join-Path $RepoRoot 'installer/bin/Release'
$prefix     = "$(& dotnet msbuild (Join-Path $RepoRoot 'AuraClean/AuraClean.csproj') -nologo -getProperty:VersionPrefix)".Trim()

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
dotnet publish (Join-Path $RepoRoot 'AuraClean/AuraClean.csproj') -c Release -r win-x64 --self-contained true -o $PublishDir -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
New-Item -ItemType Directory -Path (Join-Path $OutputDir 'portable') -Force | Out-Null
Copy-Item (Join-Path $PublishDir 'AuraClean.exe') (Join-Path $OutputDir 'portable/AuraClean.exe')

function Build-Msi([string]$suffix, [string]$target) {
    $label = if ($suffix) { "$prefix-$suffix" } else { $prefix }
    Clear-StaleInstallerOutput -BuildOutputDir $BuildDir
    # The version comes from command-line properties, which WiX's incremental build does not track: start from a clean obj.
    Remove-Item (Join-Path $RepoRoot 'installer/obj') -Recurse -Force -ErrorAction SilentlyContinue
    $started = (Get-Date).ToUniversalTime()
    dotnet build (Join-Path $RepoRoot 'installer/AuraClean.Installer.wixproj') -c Release "-p:PublishDir=$PublishDir$([IO.Path]::DirectorySeparatorChar)" `
        "-p:VersionSuffix=$suffix" -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw "MSI build failed for $label." }
    $msi = Get-ExpectedMsi -BuildOutputDir $BuildDir -Label $label -BuildStartedUtc $started
    Copy-Item $msi.FullName (Join-Path $OutputDir $target)
    Write-Host "Built $target from $label"
}

if (-not $SkipCandidate) {
    $suffixNow = "$(& dotnet msbuild (Join-Path $RepoRoot 'AuraClean/AuraClean.csproj') -nologo -getProperty:VersionSuffix)".Trim()
    Build-Msi $suffixNow 'rc2.msi'   # the real candidate label (e.g. rc.2), not an override
}
Build-Msi ''     'final.msi'
Start-Sleep -Seconds 2
Build-Msi ''     'final-rebuild.msi'   # same version, new ProductCode: a "rebuilt package of an installed version"
Get-ChildItem $OutputDir -Recurse -File | ForEach-Object { Write-Host ("  {0}  {1:N0} bytes" -f $_.FullName, $_.Length) }
