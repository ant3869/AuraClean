<#
.SYNOPSIS
    AuraClean - One-click install script.
    Checks for .NET 8 SDK, builds a self-contained single-file EXE, creates a
    desktop shortcut, and optionally launches the app as Administrator.

.DESCRIPTION
    Run this script from the repo root after cloning:
        git clone https://github.com/ant3869/AuraClean.git
        cd AuraClean
        powershell -ExecutionPolicy Bypass -File install.ps1
#>

param(
    [switch]$SkipLaunch,
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot   = $PSScriptRoot
$Project    = Join-Path $RepoRoot 'AuraClean\AuraClean.csproj'
$PublishDir = Join-Path $RepoRoot 'AuraClean\bin\Release\net8.0-windows\win-x64\publish'
$ExeName    = 'AuraClean.exe'
$ExePath    = Join-Path $PublishDir $ExeName
$InstallDir = Join-Path $env:LOCALAPPDATA 'AuraClean'
$InstalledExe = Join-Path $InstallDir $ExeName

function Write-Step  { param([string]$msg) Write-Host "`n[*] $msg" -ForegroundColor Cyan }
function Write-Ok    { param([string]$msg) Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn  { param([string]$msg) Write-Host "    $msg" -ForegroundColor Yellow }
function Write-Fail  { param([string]$msg) Write-Host "    $msg" -ForegroundColor Red }

# --- 1. .NET SDK check ---

function Ensure-DotnetSdk {
    Write-Step 'Checking for .NET 8 SDK...'

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnet) {
        $sdks = & dotnet --list-sdks 2>&1
        $has8 = $sdks | Where-Object { $_ -match '^8\.' }
        if ($has8) {
            Write-Ok ".NET 8 SDK found: $($has8[0])"
            return
        }
    }

    Write-Warn '.NET 8 SDK not found - installing automatically...'

    $installerUrl = 'https://dot.net/v1/dotnet-install.ps1'
    $installerPath = Join-Path $env:TEMP 'dotnet-install.ps1'

    Write-Step 'Downloading official .NET install script from dot.net...'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath -UseBasicParsing

    Write-Step 'Installing .NET 8 SDK (user-local, no admin required)...'
    & $installerPath -Channel 8.0 -InstallDir (Join-Path $env:LOCALAPPDATA 'dotnet')

    $dotnetDir = Join-Path $env:LOCALAPPDATA 'dotnet'
    if ($env:PATH -notlike "*$dotnetDir*") {
        $env:PATH = "$dotnetDir;$env:PATH"
    }

    $ver = & dotnet --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Fail 'Failed to install .NET 8 SDK. Please install manually from https://dotnet.microsoft.com/download/dotnet/8.0'
        exit 1
    }
    Write-Ok "Installed .NET SDK $ver"
}

# --- 2. Build / Publish ---

function Publish-App {
    if ($NoBuild -and (Test-Path $ExePath)) {
        Write-Step 'Skipping build - using existing publish output.'
        return
    }

    Write-Step 'Publishing AuraClean (self-contained single-file, Release)...'
    & dotnet publish $Project -c Release --self-contained true -r win-x64 /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true

    if ($LASTEXITCODE -ne 0) {
        Write-Fail 'Build failed. Check the output above for errors.'
        exit 1
    }

    if (-not (Test-Path $ExePath)) {
        Write-Fail "Expected output not found at: $ExePath"
        exit 1
    }

    $size = [math]::Round((Get-Item $ExePath).Length / 1MB, 1)
    Write-Ok "Published successfully - $ExeName ($size MB)"
}

# --- 3. Install to LocalAppData ---

function Install-App {
    Write-Step "Installing to $InstallDir ..."

    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }

    $publishFiles = @(Get-ChildItem $PublishDir -File)
    foreach ($f in $publishFiles) {
        Copy-Item $f.FullName -Destination $InstallDir -Force
    }

    Write-Ok "Copied $($publishFiles.Count) file(s) to $InstallDir"
}

# --- 4. Desktop shortcut ---

function Create-Shortcut {
    Write-Step 'Creating desktop shortcut...'

    $desktop  = [Environment]::GetFolderPath('Desktop')
    $lnkPath  = Join-Path $desktop 'AuraClean.lnk'

    $shell    = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnkPath)
    $shortcut.TargetPath       = $InstalledExe
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description      = 'AuraClean - Windows System Cleaner'
    $shortcut.Save()

    Write-Ok "Shortcut created: $lnkPath"
}

# --- 5. Launch ---

function Launch-App {
    if ($SkipLaunch) { return }

    Write-Step 'Launching AuraClean as Administrator...'
    Write-Warn 'AuraClean requires Administrator privileges for full functionality.'
    Write-Warn 'You may see a UAC prompt - click Yes to continue.'

    Start-Process $InstalledExe -Verb RunAs
}

# --- Main ---

Write-Host ''
Write-Host '  =======================================' -ForegroundColor Magenta
Write-Host '          AuraClean Installer             ' -ForegroundColor Magenta
Write-Host '    Windows System Cleaner and Optimizer   ' -ForegroundColor Magenta
Write-Host '  =======================================' -ForegroundColor Magenta

Ensure-DotnetSdk
Publish-App
Install-App
Create-Shortcut
Launch-App

Write-Host ''
Write-Host '  [OK] AuraClean installed successfully!' -ForegroundColor Green
Write-Host "    Location:  $InstalledExe" -ForegroundColor Gray
Write-Host '    Shortcut:  Desktop -> AuraClean' -ForegroundColor Gray
Write-Host '    Data dir:  %LocalAppData%\AuraClean\' -ForegroundColor Gray
Write-Host ''
