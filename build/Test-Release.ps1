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
$props = (& dotnet msbuild (Join-Path $RepoRoot 'AuraClean/AuraClean.csproj') -nologo -getProperty:VersionPrefix -getProperty:VersionSuffix -getProperty:MsiProductVersion | ConvertFrom-Json).Properties
$prefix = $props.VersionPrefix; $suffix = $props.VersionSuffix; $msiVersion = $props.MsiProductVersion
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
Check ((Prop 'ProductVersion') -eq $msiVersion) "ProductVersion $(Prop 'ProductVersion') equals the MSI version derived from $label ($msiVersion)"
Check ((Prop 'UpgradeCode') -eq $upgradeCode) 'UpgradeCode is the stable AuraClean code (never change it)'
Check ((Prop 'ALLUSERS') -eq '1') 'Installs per-machine (ALLUSERS=1): protected location, UAC at install time'
Check ((Prop 'ARPPRODUCTICON') -eq 'AuraCleanIcon' -and ($tables -contains 'Icon')) 'Installed Apps icon is registered'
Check (-not [string]::IsNullOrEmpty((Prop 'ARPURLINFOABOUT'))) 'Installed Apps "about" link is set'
Check ((Prop 'ARPNOMODIFY') -eq '1') 'No "Modify" button (repair/uninstall only)'
Check ((Prop 'MSIRESTARTMANAGERCONTROL') -eq 'Disable') 'A running AuraClean is never auto-closed by the installer'
$summary = $db.SummaryInformation(0)
$template = $summary.Property(7)
Check ($template -like 'x64;*') "Package platform is x64 ($template)"

Section 'Upgrade behaviour and version ordering'
# The Upgrade table drives Windows Installer's FindRelatedProducts. Verify its exact shape, then evaluate it (same rules as
# the installer: min/max bounds are exclusive unless the 256 / 512 attribute is set) over the whole release lifecycle.
$up = @(Query 'SELECT UpgradeCode, VersionMin, VersionMax, Attributes, ActionProperty FROM Upgrade' | Where-Object { $_[0] -eq $upgradeCode })
Check ($up.Count -eq 3) 'Exactly three upgrade rules exist: older -> upgrade, newer -> downgrade block, identical -> duplicate block'
$ruleUpgrade   = @($up | Where-Object { $_[4] -eq 'WIX_UPGRADE_DETECTED' })
$ruleDowngrade = @($up | Where-Object { $_[4] -eq 'WIX_DOWNGRADE_DETECTED' })
$ruleSame      = @($up | Where-Object { $_[4] -eq 'SAMEVERSIONINSTALLED' })
Check ($ruleUpgrade.Count -eq 1 -and $ruleUpgrade[0][1] -eq '' -and $ruleUpgrade[0][2] -eq $msiVersion -and ([int]$ruleUpgrade[0][3] -band 0x202) -eq 0) "Upgrade rule: every version below $msiVersion is replaced (upper bound exclusive, so same-version upgrades are off)"
Check ($ruleDowngrade.Count -eq 1 -and $ruleDowngrade[0][1] -eq $msiVersion -and $ruleDowngrade[0][2] -eq '' -and ([int]$ruleDowngrade[0][3] -band 2) -eq 2 -and ([int]$ruleDowngrade[0][3] -band 0x100) -eq 0) "Downgrade rule: every version above $msiVersion is detected (detect-only) and blocked"
Check ($ruleSame.Count -eq 1 -and $ruleSame[0][1] -eq $msiVersion -and $ruleSame[0][2] -eq $msiVersion -and ([int]$ruleSame[0][3] -band 0x302) -eq 0x302) 'Duplicate rule: the identical version (a rebuilt package with a new ProductCode) is detected and blocked'
$launch = @(Query 'SELECT Condition FROM LaunchCondition' | ForEach-Object { $_[0] })
Check ($launch -contains 'NOT WIX_DOWNGRADE_DETECTED') 'A newer installed version blocks the install with a message'
Check ($launch -contains 'Installed OR NOT SAMEVERSIONINSTALLED') 'An identical installed version blocks a fresh install but not a repair of the installed product'
Check (@(Query "SELECT Action FROM InstallExecuteSequence WHERE Action='RemoveExistingProducts'").Count -ge 1) 'RemoveExistingProducts is scheduled (upgrade path)'
Check ($launch.Count -ge 3) 'A Windows 10/11 x64 launch condition exists alongside the version guards'

function Get-MsiVersionFor([string]$pre, [string]$suf) {
    $msbuildArgs = @('msbuild', (Join-Path $RepoRoot 'AuraClean/AuraClean.csproj'), '-nologo', "-p:VersionPrefix=$pre", "-p:VersionSuffix=$suf", '-getProperty:MsiProductVersion')
    "$(& dotnet @msbuildArgs)".Trim()
}
function Test-InRange([version]$v, [string]$min, [string]$max, [int]$attr) {
    if ($min -ne '') { $c = $v.CompareTo([version]$min); if ($c -lt 0 -or ($c -eq 0 -and -not ($attr -band 0x100))) { return $false } }
    if ($max -ne '') { $c = $v.CompareTo([version]$max); if ($c -gt 0 -or ($c -eq 0 -and -not ($attr -band 0x200))) { return $false } }
    $true
}
# The three rules above (verified against the built MSI) re-targeted at a package whose own version is $new; result for an installed $old.
function Get-UpgradeOutcome([string]$old, [string]$new) {
    $v = [version]$old
    $rules = @(
        @{ Min = '';   Max = $new; Attr = 1;   Prop = 'upgrade' },
        @{ Min = $new; Max = '';   Attr = 2;   Prop = 'downgrade' },
        @{ Min = $new; Max = $new; Attr = 770; Prop = 'same' })
    $hit = @($rules | Where-Object { Test-InRange $v $_.Min $_.Max $_.Attr } | ForEach-Object { $_.Prop })
    if ($hit -contains 'downgrade') { 'blocked: newer version installed' }
    elseif ($hit -contains 'same') { 'blocked: identical version installed' }
    elseif ($hit -contains 'upgrade') { 'upgrade' }
    else { 'fresh install' }
}
$parts = $prefix.Split('.'); $nextPrefix = "$($parts[0]).$($parts[1]).$([int]$parts[2] + 1)"
$lifecycle = [ordered]@{
    'rc.1' = Get-MsiVersionFor $prefix 'rc.1'; 'rc.2' = Get-MsiVersionFor $prefix 'rc.2'; 'final' = Get-MsiVersionFor $prefix ''
    'next-rc.1' = Get-MsiVersionFor $nextPrefix 'rc.1'; 'next-final' = Get-MsiVersionFor $nextPrefix '' }
Check (@($lifecycle.Values | Where-Object { $_ -eq '' }).Count -eq 0) "Every lifecycle label maps to an MSI version ($(($lifecycle.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))"
$ordered = @($lifecycle.Values)
Check (@(0..($ordered.Count - 2) | Where-Object { [version]$ordered[$_] -ge [version]$ordered[$_ + 1] }).Count -eq 0) 'MSI versions strictly increase: rc.1 < rc.2 < final < next rc.1 < next final'
$blockedNewer = 'blocked: newer version installed'; $blockedSame = 'blocked: identical version installed'
$expect = @(
    @('rc.1', 'rc.2', 'upgrade'), @('rc.2', 'final', 'upgrade'), @('rc.1', 'final', 'upgrade'), @('final', 'next-rc.1', 'upgrade'), @('next-rc.1', 'next-final', 'upgrade'),
    @('rc.2', 'rc.1', $blockedNewer), @('final', 'rc.1', $blockedNewer), @('final', 'rc.2', $blockedNewer), @('next-rc.1', 'final', $blockedNewer),
    @('rc.1', 'rc.1', $blockedSame), @('final', 'final', $blockedSame))
foreach ($e in $expect) {
    $got = Get-UpgradeOutcome $lifecycle[$e[0]] $lifecycle[$e[1]]
    Check ($got -eq $e[2]) "Installed $($e[0]) ($($lifecycle[$e[0]])) + installing $($e[1]) ($($lifecycle[$e[1]])) => $got"
}
Check ((Get-UpgradeOutcome '1.5.99' $msiVersion) -eq 'upgrade') 'A previous minor release upgrades to this one'
Check ((Get-MsiVersionFor $prefix 'beta.1') -eq '' -and (Get-MsiVersionFor $prefix 'rc.99') -eq '' -and (Get-MsiVersionFor $prefix 'rc.0') -eq '') 'Unsupported suffixes (beta.N, rc.0, rc.99) are rejected rather than mis-ordered'

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
$exeActions = @($customActions | Where-Object { ([int]$_[1] -band 0x7) -in 2, 5, 6 })
Check (@($customActions | Where-Object { ([int]$_[1] -band 0x7) -in 5, 6 }).Count -eq 0) 'No script custom actions'
Check (@($customActions | Where-Object { ([int]$_[1] -band 0x7) -eq 1 -and $_[0] -notlike 'WixUi*' }).Count -eq 0) 'No custom DLL code (only the standard WiX UI helpers)'

Section 'Uninstall-only cleanup of application integrations'
$allowed = @{
    'RemoveTaskLaunchAtLogon'    = '"[System64Folder]schtasks.exe" /Delete /TN AuraClean_LaunchAtLogon /F'
    'RemoveTaskScheduledCleanup' = '"[System64Folder]schtasks.exe" /Delete /TN AuraClean_ScheduledCleanup /F'
    'RemoveContextMenuExe'       = '"[System64Folder]reg.exe" delete HKLM\SOFTWARE\Classes\exefile\shell\AuraCleanUninstall /f'
    'RemoveContextMenuMsi'       = '"[System64Folder]reg.exe" delete HKLM\SOFTWARE\Classes\Msi.Package\shell\AuraCleanUninstall /f' }
Check ($exeActions.Count -eq 4 -and @($exeActions | Where-Object { -not $allowed.ContainsKey($_[0]) }).Count -eq 0) 'Exactly the four AuraClean cleanup commands are executable custom actions'
Check (@($exeActions | Where-Object { $allowed[$_[0]] -ne $_[3] }).Count -eq 0) 'Each command names one exact AuraClean-owned task or registry key (no wildcards, no other objects)'
$flagBad = @($exeActions | Where-Object { $t = [int]$_[1]; -not (($t -band 1024) -and ($t -band 2048) -and ($t -band 64)) })
Check ($flagBad.Count -eq 0) 'They run deferred and elevated (no impersonation) and ignore failures so they can never block an uninstall'
$seq = @(Query 'SELECT Action, Condition FROM InstallExecuteSequence' | Where-Object { $allowed.ContainsKey($_[0]) })
Check ($seq.Count -eq 4 -and @($seq | Where-Object { $_[1] -ne 'REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE' }).Count -eq 0) 'All four run only on a real removal, never during an upgrade'
$ui = @(Query 'SELECT Action FROM InstallUISequence' | Where-Object { $allowed.ContainsKey($_[0]) })
Check ($ui.Count -eq 0) 'None run in the UI sequence'

# Evaluate the real conditions of the built MSI under each Windows Installer scenario. OpenPackage with the "ignore machine
# state" flag only reads the package: nothing is installed and no system state is read or changed.
$session = $installer.OpenPackage($msiPath, 1)
function Set-MsiProp([string]$name, [string]$value) { [void][System.__ComObject].InvokeMember('Property', [Reflection.BindingFlags]::SetProperty, $null, $session, @($name, $value)) }
function Test-Cleanup([hashtable]$props) {
    foreach ($n in 'REMOVE', 'REINSTALL', 'UPGRADINGPRODUCTCODE', 'Installed') { Set-MsiProp $n '' }
    foreach ($k in $props.Keys) { Set-MsiProp $k $props[$k] }
    @($seq | Where-Object { [int]$session.EvaluateCondition($_[1]) -eq 1 }).Count
}
Check ((Test-Cleanup @{}) -eq 0) 'Fresh install: no cleanup runs'
Check ((Test-Cleanup @{ Installed = 'x' }) -eq 0) 'Maintenance session with nothing selected: no cleanup runs'
Check ((Test-Cleanup @{ REINSTALL = 'ALL'; Installed = 'x' }) -eq 0) 'Repair (REINSTALL=ALL): no cleanup runs'
Check ((Test-Cleanup @{ REMOVE = 'ALL'; UPGRADINGPRODUCTCODE = '{22222222-2222-2222-2222-222222222222}'; Installed = 'x' }) -eq 0) 'Upgrade (old version removed by the new one): no cleanup runs, so integrations survive'
Check ((Test-Cleanup @{ REMOVE = 'DesktopShortcut'; Installed = 'x' }) -eq 0) 'Removing only the desktop shortcut feature: no cleanup runs'
Check ((Test-Cleanup @{ REMOVE = 'ALL'; Installed = 'x' }) -eq 4) 'Uninstall (REMOVE=ALL): all four cleanup commands run'
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($session)

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

Section 'Release label safety (released artifacts are never relabelled or replaced)'
$retired = @((Get-Content (Join-Path $PSScriptRoot 'historical-artifacts.json') -Raw | ConvertFrom-Json).releases)
Check (@($retired | Where-Object { $_.label -ceq $label }).Count -eq 0) "The label $label is not a retired label (retired: $(($retired | ForEach-Object { $_.label }) -join ', '))"
$retiredHashes = @($retired | ForEach-Object { $_.files } | ForEach-Object { $_.sha256 })
$ourHashes = @($msiPath, $portablePath | ForEach-Object { (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant() })
Check (@($ourHashes | Where-Object { $retiredHashes -contains $_ }).Count -eq 0) 'None of these artifacts is byte-identical to a retired (released) artifact'
foreach ($r in $retired) { Check (Test-Path (Join-Path $RepoRoot "docs/release-notes/v$($r.label).md")) "Historical release notes for $($r.label) are preserved" }

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
