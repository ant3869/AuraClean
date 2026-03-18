<#
.SYNOPSIS
    AuraClean build helper - build, publish, or clean.

.EXAMPLE
    .\build.ps1                  # Debug build
    .\build.ps1 -Release         # Release publish (single-file)
    .\build.ps1 -Clean           # Clean build artifacts
    .\build.ps1 -Run             # Build + run
#>

param(
    [switch]$Release,
    [switch]$Clean,
    [switch]$Run
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = $PSScriptRoot
$Project  = Join-Path $RepoRoot 'AuraClean\AuraClean.csproj'

if ($Clean) {
    Write-Host '[*] Cleaning build artifacts...' -ForegroundColor Cyan
    & dotnet clean $Project -c Debug   2>&1 | Out-Null
    & dotnet clean $Project -c Release 2>&1 | Out-Null
    Write-Host '    Done.' -ForegroundColor Green
    exit 0
}

if ($Release) {
    Write-Host '[*] Publishing Release (single-file, self-contained)...' -ForegroundColor Cyan
    & dotnet publish $Project -c Release --self-contained true -r win-x64 /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true

    if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed.' -ForegroundColor Red; exit 1 }

    $exe = Join-Path $RepoRoot 'AuraClean\bin\Release\net8.0-windows\win-x64\publish\AuraClean.exe'
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "    Published: $exe ($size MB)" -ForegroundColor Green
    exit 0
}

Write-Host '[*] Building Debug...' -ForegroundColor Cyan
& dotnet build $Project -c Debug

if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed.' -ForegroundColor Red; exit 1 }
Write-Host '    Build succeeded.' -ForegroundColor Green

if ($Run) {
    $exe = Join-Path $RepoRoot 'AuraClean\bin\Debug\net8.0-windows\win-x64\AuraClean.exe'
    Write-Host "[*] Launching $exe as Administrator..." -ForegroundColor Cyan
    Start-Process $exe -Verb RunAs
}
