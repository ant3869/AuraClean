<#
.SYNOPSIS
    Builds throw-away MSI packages (rc.1, rc.2, final, and a rebuild of final) used ONLY by the MSI acceptance workflow.

.DESCRIPTION
    The version overrides are passed on the command line (-p:VersionSuffix=...); nothing in the repository is changed and the
    packages must never be published. Building is safe anywhere; installing them is done only on disposable GitHub-hosted
    runners by build/Test-MsiLifecycle.ps1.

.PARAMETER OutputDir
    Folder that receives rc1.msi, rc2.msi, final.msi, final-rebuild.msi and portable/AuraClean.exe.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDir)

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
    $started = (Get-Date).ToUniversalTime()
    dotnet build (Join-Path $RepoRoot 'installer/AuraClean.Installer.wixproj') -c Release "-p:PublishDir=$PublishDir$([IO.Path]::DirectorySeparatorChar)" `
        "-p:VersionSuffix=$suffix" -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw "MSI build failed for $label." }
    $msi = Get-ExpectedMsi -BuildOutputDir $BuildDir -Label $label -BuildStartedUtc $started
    Copy-Item $msi.FullName (Join-Path $OutputDir $target)
    Write-Host "Built $target from $label"
}

Build-Msi 'rc.1' 'rc1.msi'
Build-Msi 'rc.2' 'rc2.msi'
Build-Msi ''     'final.msi'
Start-Sleep -Seconds 2
Build-Msi ''     'final-rebuild.msi'   # same version, new ProductCode: a "rebuilt package of an installed version"
Get-ChildItem $OutputDir -Recurse -File | ForEach-Object { Write-Host ("  {0}  {1:N0} bytes" -f $_.FullName, $_.Length) }
