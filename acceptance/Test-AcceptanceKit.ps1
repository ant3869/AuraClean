<#
.SYNOPSIS
    Regression and failure-injection tests for the acceptance kit's verification logic (checksum manifest, report verdict, manual
    results, kit contents). Pure: synthetic files in a temp folder, no installer, no machine state. Exits non-zero on any failure.

.DESCRIPTION
    Each "legacy" function reproduces the ORIGINAL, flawed logic of PR #25's first revision; the tests first prove that the
    injected defect is accepted by the legacy logic (so the test can detect the problem) and then that the current logic rejects it.
#>
[CmdletBinding()]
param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'AcceptanceChecks.ps1')
$script:Pass = 0; $script:Fail = 0
function Check([bool]$ok, [string]$m) { if ($ok) { Write-Host "  PASS: $m" -ForegroundColor Green; $script:Pass++ } else { Write-Host "  FAIL: $m" -ForegroundColor Red; $script:Fail++ } }
function Section($t) { Write-Host "`n== $t ==" -ForegroundColor Cyan }
$root = Join-Path ([IO.Path]::GetTempPath()) ('AuraKitTest_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$names = Get-ExpectedArtifactNames

# ── legacy (original, flawed) logic, kept only to prove the tests detect the original defects ─────
function Get-LegacyManifestOk([string]$dir) {
    $ok = $true
    foreach ($line in Get-Content (Join-Path $dir 'SHA256SUMS.txt') | Where-Object { $_ }) {
        $m = [regex]::Match($line, '^([0-9a-f]{64}) \*(.+)$'); if (-not $m.Success) { continue }
        $f = Join-Path $dir $m.Groups[2].Value
        if (-not (Test-Path $f)) { $ok = $false; continue }
        if ((Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant() -ne $m.Groups[1].Value) { $ok = $false }
    }
    $ok
}
function Get-LegacyVerdict($manual, $autoFails) {   # original summary: counters start at zero and only count what is present
    $fails = $autoFails; $untested = 0
    foreach ($m in @($manual)) { if ($m.Status -eq 'FAIL') { $fails++ } elseif ($m.Status -ne 'PASS') { $untested++ } }
    if ($fails) { 'NO-GO' } elseif ($untested) { 'INCOMPLETE' } else { 'all checks passed' }
}

function New-Artifacts([string]$name) {
    $d = Join-Path $root $name; New-Item -ItemType Directory -Path $d | Out-Null
    foreach ($n in $names) { Set-Content -LiteralPath (Join-Path $d $n) "synthetic content of $n" }
    $lines = foreach ($n in $names) { "$((Get-FileHash (Join-Path $d $n) -Algorithm SHA256).Hash.ToLowerInvariant()) *$n" }
    [IO.File]::WriteAllText((Join-Path $d 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"))
    $d
}
function Test-Rejected([string]$dir) { @(Test-ChecksumManifest $dir | Where-Object { -not $_.Ok }).Count -gt 0 }

Section 'Checksum manifest fails closed'
$d = New-Artifacts 'valid'
Check (-not (Test-Rejected $d) -and @(Test-ChecksumManifest $d).Count -eq 2) 'A complete, correct manifest for exactly the MSI and portable EXE is accepted (2 verified rows)'

$d = New-Artifacts 'truncated'; $first = (Get-Content (Join-Path $d 'SHA256SUMS.txt'))[0]; [IO.File]::WriteAllText((Join-Path $d 'SHA256SUMS.txt'), "$first`n")
Check (Get-LegacyManifestOk $d) 'INJECTION (truncated manifest, one entry): the ORIGINAL logic accepts it'
Check (Test-Rejected $d) 'Truncated manifest: rejected (the missing artifact is reported)'

$d = New-Artifacts 'empty'; [IO.File]::WriteAllText((Join-Path $d 'SHA256SUMS.txt'), '')
Check (Get-LegacyManifestOk $d) 'INJECTION (empty manifest): the ORIGINAL logic accepts it'
Check (Test-Rejected $d) 'Empty manifest: rejected'

$d = New-Artifacts 'malformed'; $lines = Get-Content (Join-Path $d 'SHA256SUMS.txt'); $lines[1] = $lines[1].Substring(1); [IO.File]::WriteAllText((Join-Path $d 'SHA256SUMS.txt'), ($lines -join "`n") + "`n")
Check (Get-LegacyManifestOk $d) 'INJECTION (malformed line, 63-hex hash): the ORIGINAL logic silently skips it and accepts'
Check (Test-Rejected $d) 'Malformed line: rejected'

$d = New-Artifacts 'duplicate'; $lines = Get-Content (Join-Path $d 'SHA256SUMS.txt'); [IO.File]::WriteAllText((Join-Path $d 'SHA256SUMS.txt'), (($lines + $lines[0]) -join "`n") + "`n")
Check (Get-LegacyManifestOk $d) 'INJECTION (duplicate entry): the ORIGINAL logic accepts it'
Check (Test-Rejected $d) 'Duplicate entry: rejected'

$d = New-Artifacts 'unexpected'; Set-Content (Join-Path $d 'evil.exe') 'x'; $lines = Get-Content (Join-Path $d 'SHA256SUMS.txt')
[IO.File]::WriteAllText((Join-Path $d 'SHA256SUMS.txt'), (($lines + "$((Get-FileHash (Join-Path $d 'evil.exe') -Algorithm SHA256).Hash.ToLowerInvariant()) *evil.exe") -join "`n") + "`n")
Check (Get-LegacyManifestOk $d) 'INJECTION (extra, unexpected entry): the ORIGINAL logic accepts it'
Check (Test-Rejected $d) 'Unexpected entry: rejected'

$d = New-Artifacts 'traversal'; $lines = Get-Content (Join-Path $d 'SHA256SUMS.txt'); $lines[1] = $lines[1].Substring(0, 66) + '..\x\' + $names[1]; [IO.File]::WriteAllText((Join-Path $d 'SHA256SUMS.txt'), ($lines -join "`n") + "`n")
Check (Test-Rejected $d) 'Entry with a path component: rejected'

$d = New-Artifacts 'wrongname'; $lines = Get-Content (Join-Path $d 'SHA256SUMS.txt'); $lines[0] = $lines[0].Replace('AuraClean-', 'auraclean-'); [IO.File]::WriteAllText((Join-Path $d 'SHA256SUMS.txt'), ($lines -join "`n") + "`n")
Check (Test-Rejected $d) 'Entry whose name is not the exact official file name: rejected'

$d = New-Artifacts 'corruptfile'; Add-Content (Join-Path $d $names[0]) 'tampered'
Check (Test-Rejected $d) 'Corrupted artifact (hash mismatch): rejected'

$d = New-Artifacts 'corruptsum'; $lines = Get-Content (Join-Path $d 'SHA256SUMS.txt'); $c = $lines[0][0]; $lines[0] = ($(if ($c -eq '0') { '1' } else { '0' })) + $lines[0].Substring(1); [IO.File]::WriteAllText((Join-Path $d 'SHA256SUMS.txt'), ($lines -join "`n") + "`n")
Check (Test-Rejected $d) 'Corrupted checksum in the manifest: rejected'

$d = New-Artifacts 'missingfile'; [IO.File]::Delete((Join-Path $d $names[1]))
Check (Test-Rejected $d) 'Listed artifact missing from disk: rejected'

$d = New-Artifacts 'nomanifest'; [IO.File]::Delete((Join-Path $d 'SHA256SUMS.txt'))
Check (Test-Rejected $d) 'No SHA256SUMS.txt: rejected'

# ── report verdict ───────────────────────────────────────────────────────────────────────────────
function New-Run([scriptblock]$Mutate) {
    $dir = Join-Path $root ('run_' + [guid]::NewGuid().ToString('N').Substring(0, 8)); New-Item -ItemType Directory -Path "$dir\msi-logs", "$dir\screenshots" -Force | Out-Null
    $rid = 'run-A'
    $o = @{ Dir = $dir; RunId = $rid
        Run = [ordered]@{ RunId = $rid; MsiSha256 = 'abc' }
        Prepare = [ordered]@{ RunId = $rid; Checks = @([ordered]@{ Name = 'Windows 10 or later'; Ok = $true; Advisory = $false; Detail = 'x' }, [ordered]@{ Name = 'UAC'; Ok = $true; Advisory = $true; Detail = 'x' }, [ordered]@{ Name = 'verified: msi'; Ok = $true; Advisory = $false; Detail = 'x' }) }
        Results = @([ordered]@{ Section = 's'; Ok = $true; Message = 'a' }, [ordered]@{ Section = 's'; Ok = $true; Message = 'b' })
        Lifecycle = $true; LifecycleRunId = $rid; ExitCode = 0; TamperResults = $false
        Manual = [ordered]@{ RunId = $rid; Checks = @($script:ManualIds | ForEach-Object { [ordered]@{ Id = $_; Title = "t $_"; Status = 'PASS'; Note = "observed $_"; Screenshot = "$_.png" } }) }
        WriteManual = $true; WritePrepare = $true; WriteRun = $true; Shots = $true }
    if ($Mutate) { & $Mutate $o }
    function J($x, $p) { $x | ConvertTo-Json -Depth 8 | Out-File $p -Encoding utf8 }
    if ($o.WriteRun) { J $o.Run "$dir\run.json" }
    if ($o.WritePrepare) { J $o.Prepare "$dir\prepare.json" }
    if ($o.Shots) { foreach ($c in @($o.Manual.Checks)) { if ($c.Screenshot) { foreach ($s in ($c.Screenshot -split '[;,]')) { Set-Content "$dir\screenshots\$($s.Trim())" 'png' } } } }
    if ($o.WriteManual) { J $o.Manual "$dir\manual-results.json" }
    if ($o.Lifecycle) {
        J $o.Results "$dir\msi-logs\results.json"
        $h = (Get-FileHash "$dir\msi-logs\results.json" -Algorithm SHA256).Hash
        J ([ordered]@{ RunId = $o.LifecycleRunId; ExitCode = $o.ExitCode; ResultsSha256 = $h; ResultsCount = @($o.Results).Count }) "$dir\lifecycle-run.json"
        if ($o.TamperResults) { Add-Content "$dir\msi-logs\results.json" ' ' }
    }
    $dir
}
function Expect([string]$name, [scriptblock]$Mutate, [string]$expected) {
    $v = (Get-AcceptanceAssessment (New-Run $Mutate)).Verdict
    Check ($v -eq $expected) "$name => $v (expected $expected)"
}

Section 'Report verdict: complete run and the original defects'
Expect 'A complete, current run with 14 evidenced PASS and a valid lifecycle' $null 'ALL CHECKS PASSED'
Check ((Get-LegacyVerdict @() 0) -eq 'all checks passed') 'INJECTION (missing/empty manual results): the ORIGINAL summary reports "all checks passed"'
Expect 'Missing manual-results.json' { param($o) $o.WriteManual = $false } 'INCOMPLETE'
Expect 'Empty manual Checks list' { param($o) $o.Manual.Checks = @() } 'INCOMPLETE'

$prepFailInject = { param($o) $o.Prepare.Checks = @($o.Prepare.Checks) + [ordered]@{ Name = 'verified: portable'; Ok = $false; Advisory = $false; Detail = 'HASH MISMATCH' } }
Expect 'INJECTION: a failed preparation check next to otherwise passing lifecycle and manual results' $prepFailInject 'FAIL'
Expect 'Missing prepare.json (preparation not recorded)' { param($o) $o.WritePrepare = $false } 'FAIL'
Expect 'prepare.json with no checks' { param($o) $o.Prepare.Checks = @() } 'FAIL'
Expect 'prepare.json from another run (stale)' { param($o) $o.Prepare.RunId = 'run-OLD' } 'FAIL'
Expect 'Failed advisory preparation check (e.g. UAC off) cannot be a full pass' { param($o) $o.Prepare.Checks[1].Ok = $false } 'INCOMPLETE'
Expect 'Missing run.json' { param($o) $o.WriteRun = $false } 'FAIL'

Section 'Report verdict: stale and missing lifecycle results'
Expect 'No lifecycle results at all (NOT TESTED)' { param($o) $o.Lifecycle = $false } 'INCOMPLETE'
Expect 'Lifecycle results from another run (stale pass)' { param($o) $o.LifecycleRunId = 'run-OLD' } 'FAIL'
Expect 'Lifecycle results.json modified after the run' { param($o) $o.TamperResults = $true } 'FAIL'
Expect 'One failed lifecycle check' { param($o) $o.Results = @($o.Results) + [ordered]@{ Section = 's'; Ok = $false; Message = 'bad' } } 'FAIL'
Expect 'Lifecycle exit code non-zero' { param($o) $o.ExitCode = 1 } 'FAIL'
Expect 'Lifecycle results.json with zero checks' { param($o) $o.Results = @() } 'FAIL'

Section 'Report verdict: all 14 manual checks required, exactly once, valid and evidenced'
Expect 'Only 13 manual entries (M14 missing)' { param($o) $o.Manual.Checks = @($o.Manual.Checks | Where-Object { $_.Id -ne 'M14' }) } 'INCOMPLETE'
Expect 'Duplicate manual id' { param($o) $o.Manual.Checks = @($o.Manual.Checks) + $o.Manual.Checks[2] } 'INCOMPLETE'
Expect 'Unknown manual id M15' { param($o) $o.Manual.Checks = @($o.Manual.Checks) + [ordered]@{ Id = 'M15'; Title = 'x'; Status = 'PASS'; Note = 'n'; Screenshot = 'M15.png' } } 'INCOMPLETE'
Expect 'Lower-case status "pass"' { param($o) $o.Manual.Checks[0].Status = 'pass' } 'INCOMPLETE'
Expect 'Free-text status "PASSED"' { param($o) $o.Manual.Checks[0].Status = 'PASSED' } 'INCOMPLETE'
Expect 'Missing status' { param($o) $o.Manual.Checks[0].Status = '' } 'INCOMPLETE'
Expect 'PASS without a note' { param($o) $o.Manual.Checks[3].Note = '' } 'INCOMPLETE'
Expect 'PASS without a screenshot' { param($o) $o.Manual.Checks[3].Screenshot = '' } 'INCOMPLETE'
Expect 'PASS whose screenshot file does not exist' { param($o) $o.Shots = $false } 'INCOMPLETE'
Expect 'One check left NOT TESTED' { param($o) $o.Manual.Checks[5].Status = 'NOT TESTED' } 'INCOMPLETE'
Expect 'One manual FAIL (with a note)' { param($o) $o.Manual.Checks[5].Status = 'FAIL' } 'FAIL'
Expect 'Manual results from another run (stale pass)' { param($o) $o.Manual.RunId = 'run-OLD' } 'FAIL'
Expect 'Everything NOT TESTED' { param($o) foreach ($c in $o.Manual.Checks) { $c.Status = 'NOT TESTED' } } 'INCOMPLETE'
Expect 'FAIL takes precedence over INCOMPLETE' { param($o) $o.Manual.Checks[1].Status = 'FAIL'; $o.Manual.Checks[2].Status = 'NOT TESTED' } 'FAIL'
Check ($script:ManualIds.Count -eq 14 -and @($script:ManualIds | Select-Object -Unique).Count -eq 14) 'Exactly 14 distinct manual check ids are required'

# ── kit contents ─────────────────────────────────────────────────────────────────────────────────
Section 'Kit contents'
if (-not (Test-Path (Join-Path $RepoRoot 'build\Test-MsiLifecycle.ps1'))) {
    Write-Host '  NOT RUN: not inside a repository checkout (the kit staging test needs the repo layout)' -ForegroundColor Yellow
}
else {
    $art = New-Artifacts 'stage-art'; $pk = Join-Path $root 'stage-pkg'; New-Item -ItemType Directory -Path "$pk\portable" -Force | Out-Null
    foreach ($f in 'rc1.msi', 'rc2.msi', 'final.msi', 'final-rebuild.msi', 'portable\AuraClean.exe') { Set-Content (Join-Path $pk $f) 'x' }
    $stage = Join-Path $root 'stage-out'; New-Item -ItemType Directory -Path $stage | Out-Null
    New-KitStage -RepoRoot $RepoRoot -ArtifactDir $art -PackageDir $pk -StageDir $stage
    foreach ($f in $script:KitFiles) { Check (Test-Path (Join-Path $stage $f.Dest)) "Packed kit contains $($f.Dest)" }
    Check (Test-Path (Join-Path $stage 'scripts\Remove-LegacyInstall.ps1')) 'The legacy cleanup script used by check M14 ships in the kit (scripts\Remove-LegacyInstall.ps1)'
    Check (@(@($names) + 'SHA256SUMS.txt' | Where-Object { -not (Test-Path (Join-Path $stage "artifacts\$_")) }).Count -eq 0 -and (Test-Path (Join-Path $stage 'packages\rc2.msi'))) 'The packed kit contains the artifacts, checksum file and test packages'

    $docs = (Get-Content (Join-Path $RepoRoot 'acceptance\manual-checklist.md') -Raw) + (Get-Content (Join-Path $RepoRoot 'acceptance\README.md') -Raw)
    $referenced = @([regex]::Matches($docs, 'scripts[\\/][A-Za-z0-9._-]+\.ps1') | ForEach-Object { $_.Value -replace '\\', '/' } | Select-Object -Unique)
    $shipped = @($script:KitFiles | ForEach-Object { $_.Dest -replace '\\', '/' })
    Check (@($referenced | Where-Object { $shipped -notcontains $_ }).Count -eq 0 -and $referenced.Count -ge 1) "Every script the checklist/README refers to is in the kit ($($referenced -join ', '))"
    $checklist = Get-Content (Join-Path $RepoRoot 'acceptance\manual-checklist.md') -Raw; $tmpl = Get-Content (Join-Path $RepoRoot 'acceptance\results-template.md') -Raw
    Check (@($script:ManualIds | Where-Object { $checklist -notmatch "\*\*$_ " -or $tmpl -notmatch "\| $_ " }).Count -eq 0) 'The checklist and the results template list all 14 check ids'
    $kitScript = Get-Content (Join-Path $RepoRoot 'acceptance\Start-Acceptance.ps1') -Raw
    Check ($kitScript -notmatch '(&|\. |Invoke-Expression|Start-Process)\s*[^\r\n#]*Remove-LegacyInstall') 'No kit mode executes Remove-LegacyInstall.ps1 (it is for a person to run deliberately)'
}

try { [IO.Directory]::Delete($root, $true) } catch { }
Write-Host ''
Write-Host "RESULTS: $script:Pass passed, $script:Fail failed" -ForegroundColor $(if ($script:Fail) { 'Red' } else { 'Green' })
exit $(if ($script:Fail) { 1 } else { 0 })
