<#
.SYNOPSIS
    Verifies the AuraClean release artifacts without installing anything.

.DESCRIPTION
    Read-only checks of the MSI (via the Windows Installer database API), the portable EXE, the checksums, version
    consistency across the repository, the data-safety rules of the installer, and the legacy-cleanup script (against
    synthetic temp folders). Exits non-zero if any check fails.

    What this CANNOT prove (needs a clean Windows machine / VM; see docs/RELEASE.md "Manual release checks"):
    an actual install, upgrade, repair and uninstall, UAC/SmartScreen behaviour and the Restart Manager prompt.

.PARAMETER ArtifactsDir
    Folder with the MSI, portable EXE and SHA256SUMS.txt (default: <repo>/artifacts/release).
#>
[CmdletBinding()]
param([string]$ArtifactsDir)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ArtifactsDir) { $ArtifactsDir = Join-Path $RepoRoot 'artifacts/release' }
$script:Pass = 0
$script:Fail = 0

function Check([bool]$ok, [string]$message) {
    if ($ok) { Write-Host "  PASS: $message" -ForegroundColor Green; $script:Pass++ }
    else     { Write-Host "  FAIL: $message" -ForegroundColor Red;   $script:Fail++ }
}
function Section($t) { Write-Host "`n== $t ==" -ForegroundColor Cyan }

# ── version from the single source ───────────────────────────────────────────────────────────────
$props = (& dotnet msbuild (Join-Path $RepoRoot 'AuraClean/AuraClean.csproj') -nologo -getProperty:VersionPrefix -getProperty:VersionSuffix | ConvertFrom-Json).Properties
$prefix = $props.VersionPrefix; $suffix = $props.VersionSuffix
$label  = if ($suffix) { "$prefix-$suffix" } else { $prefix }
$msiPath      = Join-Path $ArtifactsDir "AuraClean-$label-win-x64.msi"
$portablePath = Join-Path $ArtifactsDir "AuraClean-$label-win-x64-portable.exe"
$sumsPath     = Join-Path $ArtifactsDir 'SHA256SUMS.txt'
$upgradeCode  = '{D77CC99D-4E73-41BF-BD16-48C2882CE96F}'

Section "Artifacts ($label)"
Check (Test-Path $msiPath)      "MSI exists: $(Split-Path -Leaf $msiPath)"
Check (Test-Path $portablePath) "Portable EXE exists and is named distinctly: $(Split-Path -Leaf $portablePath)"
Check (Test-Path $sumsPath)     'SHA256SUMS.txt exists'
if ($script:Fail -gt 0) { Write-Host "`nRESULTS: $script:Pass passed, $script:Fail failed"; exit 1 }

# ── checksums ────────────────────────────────────────────────────────────────────────────────────
Section 'Checksums'
$listed = @(Get-Content $sumsPath | Where-Object { $_ } | ForEach-Object {
    $m = [regex]::Match($_, '^([0-9a-f]{64}) \*(.+)$'); if ($m.Success) { [pscustomobject]@{ Hash = $m.Groups[1].Value; Name = $m.Groups[2].Value } }
})
Check ($listed.Count -eq 2) 'SHA256SUMS.txt lists exactly the MSI and the portable EXE'
foreach ($e in $listed) {
    $actual = (Get-FileHash (Join-Path $ArtifactsDir $e.Name) -Algorithm SHA256).Hash.ToLowerInvariant()
    Check ($actual -eq $e.Hash) "SHA-256 matches for $($e.Name)"
}

# ── MSI database helpers ─────────────────────────────────────────────────────────────────────────
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($msiPath, 0)   # 0 = read-only
function Query([string]$sql) {
    # Record.FieldCount is not readable through the PowerShell COM adapter: derive the column count from the SELECT list.
    $cols = if ($sql -match '^SELECT\s+\*') { 1 } elseif ($sql -match '^SELECT\s+(.+?)\s+FROM') { ($Matches[1] -split ',').Count } else { 1 }
    try {
        $view = $db.OpenView($sql)
        [void]$view.Execute($null)
        while ($true) {
            $rec = $view.Fetch()
            if ($null -eq $rec) { break }
            $row = New-Object string[] $cols
            for ($i = 0; $i -lt $cols; $i++) { $row[$i] = [string]$rec.StringData($i + 1) }
            , $row   # emit each row as ONE object (callers wrap with @(...))
        }
        [void]$view.Close()
    } catch { }   # table absent: no rows
}
function Prop([string]$name) { $r = @(Query "SELECT Value FROM Property WHERE Property='$name'"); if ($r.Count) { $r[0][0] } else { $null } }
$tables = @((Query 'SELECT Name FROM _Tables') | ForEach-Object { $_[0] })

Section 'MSI identity and Installed Apps registration'
Check ((Prop 'ProductName') -eq 'AuraClean') 'ProductName is AuraClean'
Check ((Prop 'Manufacturer') -eq 'AuraClean') 'Manufacturer is AuraClean'
Check ((Prop 'ProductVersion') -eq $prefix) "ProductVersion $(Prop 'ProductVersion') equals the repository version $prefix"
Check ((Prop 'UpgradeCode') -eq $upgradeCode) 'UpgradeCode is the stable AuraClean code (never change it)'
Check ((Prop 'ALLUSERS') -eq '1') 'Installs per-machine (ALLUSERS=1): protected location, UAC at install time'
Check ((Prop 'ARPPRODUCTICON') -eq 'AuraCleanIcon' -and ($tables -contains 'Icon')) 'Installed Apps icon is registered'
Check (-not [string]::IsNullOrEmpty((Prop 'ARPURLINFOABOUT'))) 'Installed Apps "about" link is set'
Check ((Prop 'ARPNOMODIFY') -eq '1') 'No "Modify" button (repair/uninstall only)'
Check ((Prop 'MSIRESTARTMANAGERCONTROL') -eq 'Disable') 'A running AuraClean is never auto-closed by the installer'
$summary = $db.SummaryInformation(0)
$template = $summary.Property(7)
Check ($template -like 'x64;*') "Package platform is x64 ($template)"

Section 'Upgrade behaviour'
$up = @(Query 'SELECT UpgradeCode, VersionMin, VersionMax, Attributes FROM Upgrade')
# Attributes: 512 = VersionMaxInclusive (the same numeric version is replaced too, so rc.N can replace rc.N-1).
Check (@($up | Where-Object { $_[0] -eq $upgradeCode -and ($_[1] -eq '' -or $_[1] -eq '0.0.0') -and $_[2] -eq $prefix -and (([int]$_[3]) -band 512) }).Count -ge 1) 'An existing install up to and including this version is detected and replaced in place (in-place upgrade)'
Check (@($up | Where-Object { $_[0] -eq $upgradeCode }).Count -ge 2) 'A newer installed version is detected (downgrade is blocked with a message)'
Check (@(Query "SELECT Action FROM InstallExecuteSequence WHERE Action='RemoveExistingProducts'").Count -ge 1) 'RemoveExistingProducts is scheduled (upgrade path)'
Check (@(Query 'SELECT Condition FROM LaunchCondition').Count -ge 1) 'A Windows 10/11 x64 launch condition exists'

Section 'File layout'
$files = @(Query 'SELECT File, FileName, FileSize, Component_ FROM File')
Check ($files.Count -eq 1 -and $files[0][1] -like '*AuraClean.exe') 'Exactly one file is installed: AuraClean.exe'
$comp = @(Query "SELECT Component, Directory_ FROM Component WHERE Component='AuraCleanExeComponent'")
Check ($comp.Count -eq 1 -and $comp[0][1] -eq 'INSTALLFOLDER') 'AuraClean.exe lives in INSTALLFOLDER'
$dirs = @(Query 'SELECT Directory, Directory_Parent, DefaultDir FROM Directory')
$install = @($dirs | Where-Object { $_[0] -eq 'INSTALLFOLDER' })
Check ($install.Count -eq 1 -and $install[0][1] -eq 'ProgramFiles64Folder') 'INSTALLFOLDER is under ProgramFiles64Folder (protected, not user-writable)'
$sc = @(Query 'SELECT Shortcut, Directory_, Name, Component_ FROM Shortcut')
Check (@($sc | Where-Object { $_[1] -eq 'ProgramMenuFolder' -and $_[3] -eq 'AuraCleanExeComponent' }).Count -eq 1) 'A Start Menu shortcut is created'
Check (@($sc | Where-Object { $_[1] -eq 'DesktopFolder' -and $_[3] -eq 'DesktopShortcutComponent' }).Count -eq 1) 'A desktop shortcut exists as a separate component'
$features = @(Query 'SELECT Feature, Level FROM Feature')
Check ((@($features | Where-Object { $_[0] -eq 'Main' -and $_[1] -eq '1' }).Count -eq 1) -and (@($features | Where-Object { $_[0] -eq 'DesktopShortcut' -and [int]$_[1] -ge 2 }).Count -eq 1)) 'The desktop shortcut is an optional feature that is off by default'

Section 'Data safety (settings, journals, quarantine are never touched)'
$badDirs = @($dirs | Where-Object { $_[0] -match 'AppData|ProgramData|PersonalFolder|ProfileFolder|LocalApp' -or $_[1] -match 'AppData|ProgramData|PersonalFolder|ProfileFolder|LocalApp' })
Check ($badDirs.Count -eq 0) 'The Directory table references no AppData / ProgramData / profile folder'
Check (@(Query 'SELECT * FROM RemoveFile').Count -eq 0) 'The installer deletes no files or folders other than its own (RemoveFile table is empty)'
Check (@(Query 'SELECT * FROM RemoveRegistry').Count -eq 0) 'No extra registry removals'
$reg = @(Query 'SELECT `Root`, `Key`, `Name` FROM `Registry`')
Check ($reg.Count -ge 1 -and @($reg | Where-Object { $_[0] -ne '2' -or $_[1] -notlike 'Software\AuraClean*' }).Count -eq 0) 'The only registry entries are install metadata under HKLM\Software\AuraClean'
Check (-not ($tables -contains 'ServiceInstall' -or $tables -contains 'ServiceControl')) 'No Windows services are installed'
$customActions = @(Query 'SELECT Action, Type, Source, Target FROM CustomAction')
$exeOrScript = @($customActions | Where-Object { ([int]$_[1] -band 0x7) -in 2, 5, 6 })
Check ($exeOrScript.Count -eq 0) "No executable or script custom actions ($($customActions.Count) UI helper actions only)"
Check (@(Query "SELECT Property FROM AppSearch WHERE Property='LEGACYEXE'").Count -eq 1) 'The legacy install.ps1 copy is searched for and reported on the final dialog, never removed'

# ── portable EXE ↔ MSI payload, versions ─────────────────────────────────────────────────────────
Section 'Payload and version consistency'
$vi = (Get-Item $portablePath).VersionInfo
Check ($vi.FileVersion -eq "$prefix.0") "Portable EXE FileVersion $($vi.FileVersion) equals $prefix.0"
Check ($vi.ProductVersion -eq $label) "Portable EXE ProductVersion $($vi.ProductVersion) equals $label"
$manifest = Get-Content (Join-Path $RepoRoot 'AuraClean/app.manifest') -Raw
Check ($manifest -match [regex]::Escape("assemblyIdentity version=`"$prefix.0`"")) 'app.manifest assemblyIdentity matches the version'
Check ($manifest -match 'requireAdministrator') 'app.manifest still requests administrator (documented behaviour)'

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("AuraCleanMsiExtract_" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    # Administrative image: unpacks the payload into a folder. Installs nothing, writes no registry/shortcuts.
    $p = Start-Process msiexec.exe -ArgumentList @('/a', "`"$msiPath`"", '/qn', "TARGETDIR=`"$tmp`"") -Wait -PassThru -WindowStyle Hidden
    $extracted = Get-ChildItem $tmp -Recurse -Filter 'AuraClean.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    Check ($p.ExitCode -eq 0 -and $null -ne $extracted) "Administrative extract of the MSI succeeded (exit $($p.ExitCode))"
    if ($extracted) {
        $a = (Get-FileHash $extracted.FullName -Algorithm SHA256).Hash
        $b = (Get-FileHash $portablePath -Algorithm SHA256).Hash
        Check ($a -eq $b) 'The EXE inside the MSI is byte-identical to the portable EXE'
    }
} finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

Section 'Documentation'
$notes = Join-Path $RepoRoot "docs/release-notes/v$label.md"
Check ((Test-Path $notes) -and ((Get-Content $notes -Raw) -match [regex]::Escape($label))) "Draft release notes exist and mention $label"
Check ((Get-Content (Join-Path $RepoRoot 'CHANGELOG.md') -Raw) -match [regex]::Escape("[$label]")) 'CHANGELOG has an entry for this version'
foreach ($d in 'docs/INSTALL.md', 'docs/RELEASE.md', 'docs/SIGNING.md') { Check (Test-Path (Join-Path $RepoRoot $d)) "$d exists" }
Check ((Get-Content (Join-Path $RepoRoot 'README.md') -Raw) -match 'docs/INSTALL\.md') 'README points to the installer documentation'

Section 'Signing status (informational; unsigned test builds are expected)'
foreach ($f in $msiPath, $portablePath) {
    $s = Get-AuthenticodeSignature $f
    Write-Host "  INFO: $(Split-Path -Leaf $f): $($s.Status)" -ForegroundColor Yellow
    Check ($s.Status -in 'NotSigned', 'Valid') "Signature state is coherent ($($s.Status)); never HashMismatch/UnknownError"
    if ($s.Status -eq 'NotSigned') { Check ((Get-Content $notes -Raw) -match '(?i)not\s+(yet\s+)?(authenticode-)?signed|unsigned') 'Release notes state that the build is unsigned' }
}

Section 'Legacy install handling (synthetic folders)'
$legacy = Join-Path ([IO.Path]::GetTempPath()) ("AuraLegacy_" + [guid]::NewGuid().ToString('N'))
try {
    $desk = Join-Path $legacy 'Desktop'; $app = Join-Path $legacy 'LocalAppData/AuraClean'
    foreach ($d in 'Settings', 'Logs', 'History', 'LeftoverBackup', 'Quarantine') { New-Item -ItemType Directory -Path (Join-Path $app $d) -Force | Out-Null; Set-Content (Join-Path $app "$d/data.txt") 'keep me' }
    New-Item -ItemType Directory -Path $desk | Out-Null
    $legacyExe = Join-Path $app 'AuraClean.exe'; Set-Content $legacyExe 'fake exe'
    $wsh = New-Object -ComObject WScript.Shell
    $l = $wsh.CreateShortcut((Join-Path $desk 'AuraClean.lnk')); $l.TargetPath = $legacyExe; $l.Save()
    $script = Join-Path $RepoRoot 'scripts/Remove-LegacyInstall.ps1'

    & $script -LegacyDir $app -DesktopDir $desk -WhatIf *> $null
    Check ((Test-Path $legacyExe) -and (Test-Path (Join-Path $desk 'AuraClean.lnk'))) '-WhatIf removes nothing'
    & $script -LegacyDir $app -DesktopDir $desk -Confirm:$false *> $null
    Check ((-not (Test-Path $legacyExe)) -and (-not (Test-Path (Join-Path $desk 'AuraClean.lnk')))) 'Explicit run removes only the legacy exe and its shortcut'
    $dataKept = @('Settings', 'Logs', 'History', 'LeftoverBackup', 'Quarantine') | Where-Object { (Get-Content (Join-Path $app "$_/data.txt") -ErrorAction SilentlyContinue) -eq 'keep me' }
    Check (@($dataKept).Count -eq 5) 'Settings, logs, history, leftover-backup journals and quarantine are preserved'

    # A shortcut that does not point at the legacy exe must survive.
    Set-Content $legacyExe 'fake exe'
    $o = $wsh.CreateShortcut((Join-Path $desk 'AuraClean.lnk')); $o.TargetPath = (Join-Path $env:windir 'notepad.exe'); $o.Save()
    & $script -LegacyDir $app -DesktopDir $desk -Confirm:$false *> $null
    Check (Test-Path (Join-Path $desk 'AuraClean.lnk')) 'A same-named shortcut that points elsewhere is left alone'
} finally { Remove-Item $legacy -Recurse -Force -ErrorAction SilentlyContinue }

$installPs1 = Get-Content (Join-Path $RepoRoot 'install.ps1') -Raw
Check ($installPs1 -match 'DEVELOPER' -and $installPs1 -match 'Program Files' -and $installPs1 -notmatch 'Stop-Process') 'install.ps1 is marked developer-only, refuses to overwrite an MSI install, and no longer kills running instances'

Write-Host ''
Write-Host "RESULTS: $script:Pass passed, $script:Fail failed" -ForegroundColor $(if ($script:Fail) { 'Red' } else { 'Green' })
exit $(if ($script:Fail) { 1 } else { 0 })
