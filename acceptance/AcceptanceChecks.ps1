<#
.SYNOPSIS
    Pure verification/aggregation logic of the acceptance kit (dot-sourced by Start-Acceptance.ps1 and Test-AcceptanceKit.ps1).
    Nothing here installs, deletes or changes machine state.
#>

$script:ExpectedLabel = '1.6.0-rc.2'   # the candidate under acceptance; the original 1.6.0-rc.1 (.NET 8) is historical (build/historical-artifacts.json)
$script:ManualIds = 'M01', 'M02', 'M03', 'M04', 'M05', 'M06', 'M07', 'M08', 'M09', 'M10', 'M11', 'M12', 'M13', 'M14'
$script:ManualStatuses = 'PASS', 'FAIL', 'NOT TESTED'

# Files the kit contains, source (repo-relative) -> destination (kit-relative). The manual checklist refers to scripts\Remove-LegacyInstall.ps1;
# it is shipped for a human to run on purpose and is never executed by the kit.
$script:KitFiles = @(
    @{ Source = 'acceptance/Start-Acceptance.ps1'; Dest = 'Start-Acceptance.ps1' }
    @{ Source = 'acceptance/AcceptanceChecks.ps1'; Dest = 'AcceptanceChecks.ps1' }
    @{ Source = 'acceptance/Test-AcceptanceKit.ps1'; Dest = 'Test-AcceptanceKit.ps1' }
    @{ Source = 'acceptance/README.md'; Dest = 'README.md' }
    @{ Source = 'acceptance/manual-checklist.md'; Dest = 'manual-checklist.md' }
    @{ Source = 'acceptance/results-template.md'; Dest = 'results-template.md' }
    @{ Source = 'build/Test-MsiLifecycle.ps1'; Dest = 'Test-MsiLifecycle.ps1' }
    @{ Source = 'build/historical-artifacts.json'; Dest = 'historical-artifacts.json' }
    @{ Source = 'scripts/Remove-LegacyInstall.ps1'; Dest = 'scripts/Remove-LegacyInstall.ps1' }
)

function Get-ExpectedArtifactNames { @("AuraClean-$($script:ExpectedLabel)-win-x64.msi", "AuraClean-$($script:ExpectedLabel)-win-x64-portable.exe") }

# Strict-mode-safe property read on parsed JSON.
function P($o, [string]$name) { if ($null -ne $o -and $o.PSObject.Properties[$name]) { $o.$name } else { $null } }

# ── checksum manifest: fail closed ───────────────────────────────────────────────────────────────
# Returns rows {File, Ok, Detail}. The manifest must list EXACTLY the two official artifacts, once each, in valid
# "<64 hex> *<name>" form; each file must exist and match. Anything else yields at least one failed row.
function Test-ChecksumManifest([string]$ArtifactDir) {
    $rows = New-Object System.Collections.Generic.List[object]
    function Add-Row($f, $ok, $d) { $rows.Add([pscustomobject]@{ File = $f; Ok = [bool]$ok; Detail = $d }) }
    $expected = Get-ExpectedArtifactNames
    $manifest = Join-Path $ArtifactDir 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { Add-Row 'SHA256SUMS.txt' $false "not found in $ArtifactDir"; return $rows.ToArray() }

    $entries = @{}
    $lineNo = 0
    foreach ($line in [IO.File]::ReadAllLines($manifest)) {
        $lineNo++
        if ($line.Trim().Length -eq 0) { continue }
        $m = [regex]::Match($line, '^([0-9a-fA-F]{64}) \*([^\\/:*?"<>|]+)$')
        if (-not $m.Success) { Add-Row 'SHA256SUMS.txt' $false "line ${lineNo} is malformed"; continue }
        $name = $m.Groups[2].Value
        if ($entries.ContainsKey($name)) { Add-Row $name $false "listed more than once (line $lineNo)"; continue }
        if ($expected -cnotcontains $name) { Add-Row $name $false "unexpected entry (line $lineNo): only the official MSI and portable EXE may be listed"; continue }
        $entries[$name] = $m.Groups[1].Value.ToLowerInvariant()
    }
    foreach ($name in $expected) {
        if (-not $entries.ContainsKey($name)) { Add-Row $name $false 'missing from SHA256SUMS.txt'; continue }
        $file = Join-Path $ArtifactDir $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { Add-Row $name $false 'listed but the file is missing'; continue }
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        Add-Row $name ($actual -eq $entries[$name]) $(if ($actual -eq $entries[$name]) { "sha256 $actual" } else { "HASH MISMATCH: manifest $($entries[$name]), file $actual" })
    }
    if ($rows.Count -eq 0) { Add-Row 'SHA256SUMS.txt' $false 'empty manifest' }
    $rows.ToArray()   # callers wrap the call in @() so a single row is still an array
}

# ── historical (original, released) RC1: pinned by SHA-256, never rebuilt ────────────────────────
# Returns rows {File, Ok, Detail}. Both original files (MSI and portable EXE) must be present in $HistoricalDir and match the hashes
# recorded in historical-artifacts.json. A rebuilt or substituted package (e.g. a .NET 10 build under the rc.1 label) fails.
function Test-HistoricalArtifacts([string]$HistoricalDir, [string]$HistoricalJson, [string]$Label = '1.6.0-rc.1') {
    $rows = New-Object System.Collections.Generic.List[object]
    function Add-Row($f, $ok, $d) { $rows.Add([pscustomobject]@{ File = $f; Ok = [bool]$ok; Detail = $d }) }
    if (-not (Test-Path -LiteralPath $HistoricalJson -PathType Leaf)) { Add-Row 'historical-artifacts.json' $false "not found: $HistoricalJson"; return $rows.ToArray() }
    $rel = @((Get-Content -LiteralPath $HistoricalJson -Raw | ConvertFrom-Json).releases | Where-Object { $_.label -ceq $Label })
    if ($rel.Count -ne 1) { Add-Row 'historical-artifacts.json' $false "release $Label is not recorded exactly once"; return $rows.ToArray() }
    foreach ($f in @($rel[0].files)) {
        $path = Join-Path $HistoricalDir $f.name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Add-Row "historical $($f.name)" $false "missing from $HistoricalDir (the original $Label $($rel[0].runtime) file is required for the cross-runtime upgrade test)"; continue }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        Add-Row "historical $($f.name)" ($actual -eq $f.sha256) $(if ($actual -eq $f.sha256) { "original $Label ($($rel[0].runtime)) sha256 $actual" } else { "NOT the original: sha256 $actual, pinned $($f.sha256)" })
    }
    if ($rows.Count -eq 0) { Add-Row 'historical-artifacts.json' $false "no files recorded for $Label" }
    $rows.ToArray()
}

# ── manual results: all 14 exactly once, valid status, evidence for PASS/FAIL ────────────────────
# Returns @{ Problems; Checks } where Checks has one normalized entry per expected id (missing ones are NOT TESTED).
function Test-ManualResults($Manual, [string]$ScreenshotDir) {
    $problems = New-Object System.Collections.Generic.List[string]
    $byId = @{}
    $items = @()
    if ($null -eq $Manual) { $problems.Add('manual-results.json is missing') }
    elseif ($null -eq (P $Manual 'Checks')) { $problems.Add('manual-results.json has no Checks list') }
    else { $items = @($Manual.Checks) }
    if ($null -ne $Manual -and $items.Count -eq 0) { $problems.Add('manual-results.json contains no checks') }

    foreach ($i in $items) {
        $id = [string](P $i 'Id')
        if ($script:ManualIds -cnotcontains $id) { $problems.Add("unknown or missing check id '$id'"); continue }
        if ($byId.ContainsKey($id)) { $problems.Add("$id appears more than once"); $byId[$id].Valid = $false; continue }
        $e = [pscustomobject]@{ Id = $id; Title = [string](P $i 'Title'); Status = ''; Note = ([string](P $i 'Note')).Trim(); Screenshot = ([string](P $i 'Screenshot')).Trim(); Valid = $true }
        $byId[$id] = $e
        $status = [string](P $i 'Status')
        if ($script:ManualStatuses -cnotcontains $status) { $problems.Add("$id has an invalid status '$status' (use PASS, FAIL or NOT TESTED)"); $e.Valid = $false; $e.Status = 'INVALID'; continue }
        $e.Status = $status
        if (($status -eq 'PASS' -or $status -eq 'FAIL') -and -not $e.Note) { $problems.Add("$id is $status but has no Note"); $e.Valid = $false }
        if ($status -eq 'PASS') {
            if (-not $e.Screenshot) { $problems.Add("$id is PASS but names no Screenshot"); $e.Valid = $false }
            elseif ($ScreenshotDir) {
                foreach ($s in @($e.Screenshot -split '[;,]' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
                    if ($s -match '[\\/:]' -or -not (Test-Path -LiteralPath (Join-Path $ScreenshotDir $s) -PathType Leaf)) { $problems.Add("$id screenshot '$s' not found in results\screenshots"); $e.Valid = $false }
                }
            }
        }
    }
    $checks = foreach ($id in $script:ManualIds) {
        if ($byId.ContainsKey($id)) { $byId[$id] }
        else { $problems.Add("$id is missing (counted as NOT TESTED)"); [pscustomobject]@{ Id = $id; Title = ''; Status = 'NOT TESTED'; Note = ''; Screenshot = ''; Valid = $false } }
    }
    @{ Problems = @($problems); Checks = @($checks) }
}

# ── overall assessment: FAIL > INCOMPLETE > ALL CHECKS PASSED, never from another run's results ───
function Get-AcceptanceAssessment([string]$OutDir) {
    function Read-J($p) { if (Test-Path -LiteralPath $p -PathType Leaf) { try { Get-Content -LiteralPath $p -Raw | ConvertFrom-Json } catch { $null } } else { $null } }
    $fails = New-Object System.Collections.Generic.List[string]
    $incomplete = New-Object System.Collections.Generic.List[string]

    $run = Read-J (Join-Path $OutDir 'run.json')
    $prep = Read-J (Join-Path $OutDir 'prepare.json')
    $lrun = Read-J (Join-Path $OutDir 'lifecycle-run.json')
    $manual = Read-J (Join-Path $OutDir 'manual-results.json')
    $runId = [string](P $run 'RunId')
    if (-not $runId) { $fails.Add('no valid run.json: run -Mode Prepare first') }

    # Preparation: any failed (non-advisory) check, a missing record, or a record from another run fails the verdict.
    $prepChecks = @()
    if (-not $prep) { $fails.Add('prepare.json is missing or unreadable (preparation was not recorded)') }
    elseif ([string](P $prep 'RunId') -ne $runId) { $fails.Add('prepare.json belongs to a different run (stale)') }
    else {
        $prepChecks = @(P $prep 'Checks')
        if ($prepChecks.Count -eq 0 -or $null -eq $prepChecks[0]) { $prepChecks = @(); $fails.Add('prepare.json records no checks') }
        foreach ($c in $prepChecks) {
            if (-not [bool](P $c 'Ok')) {
                $d = "$(P $c 'Name') - $(P $c 'Detail')"
                if ([bool](P $c 'Advisory')) { $incomplete.Add("preparation advisory: $d") } else { $fails.Add("preparation failed: $d") }
            }
        }
    }

    # Automated lifecycle: must be bound to this run and to the exact results file.
    $lifecycle = [ordered]@{ State = 'NOT TESTED'; Pass = 0; Fail = 0; Failed = @(); Results = @() }
    if (-not $lrun) { $incomplete.Add('automated lifecycle NOT TESTED (no lifecycle-run.json for this run)') }
    elseif ([string](P $lrun 'RunId') -ne $runId) { $lifecycle.State = 'STALE'; $fails.Add('lifecycle results belong to a different run (stale) and are ignored') }
    else {
        $resPath = Join-Path $OutDir 'msi-logs\results.json'
        $res = Read-J $resPath
        if ($null -eq $res) { $lifecycle.State = 'INVALID'; $fails.Add('lifecycle results.json is missing or unreadable') }
        elseif ((Get-FileHash -LiteralPath $resPath -Algorithm SHA256).Hash -ne [string](P $lrun 'ResultsSha256')) { $lifecycle.State = 'INVALID'; $fails.Add('lifecycle results.json changed after the run (hash mismatch)') }
        else {
            $res = @($res)
            $bad = @($res | Where-Object { -not [bool](P $_ 'Ok') })
            $lifecycle.Results = $res; $lifecycle.Pass = $res.Count - $bad.Count; $lifecycle.Fail = $bad.Count; $lifecycle.Failed = $bad
            if ($res.Count -eq 0) { $lifecycle.State = 'INVALID'; $fails.Add('lifecycle results.json holds no checks') }
            elseif ($bad.Count -or [int](P $lrun 'ExitCode') -ne 0) { $lifecycle.State = 'FAIL'; $fails.Add("lifecycle: $($bad.Count) failed check(s), exit code $(P $lrun 'ExitCode')") }
            else { $lifecycle.State = 'PASS' }
        }
    }

    # Manual checks: another run's file is ignored (all NOT TESTED) and fails the run as stale.
    $manualForCheck = $manual
    if ($manual -and [string](P $manual 'RunId') -ne $runId) { $fails.Add('manual-results.json belongs to a different run (stale) and is ignored'); $manualForCheck = $null }
    $mr = Test-ManualResults $manualForCheck (Join-Path $OutDir 'screenshots')
    foreach ($p in $mr.Problems) { $incomplete.Add("manual results: $p") }
    foreach ($c in $mr.Checks) {
        if ($c.Status -eq 'FAIL') { $fails.Add("$($c.Id) FAIL") }
        elseif ($c.Status -ne 'PASS' -or -not $c.Valid) { $incomplete.Add("$($c.Id) $(if ($c.Status -eq 'PASS') { 'PASS lacks valid evidence' } else { $c.Status })") }
    }

    $verdict = if ($fails.Count) { 'FAIL' } elseif ($incomplete.Count) { 'INCOMPLETE' } else { 'ALL CHECKS PASSED' }
    [pscustomobject]@{ Verdict = $verdict; Failures = @($fails); Incomplete = @($incomplete); RunId = $runId; Run = $run; Prepare = $prep; PrepareChecks = $prepChecks
        Lifecycle = [pscustomobject]$lifecycle; Manual = $mr.Checks; ManualProblems = $mr.Problems }
}

# ── kit staging (copy only) ──────────────────────────────────────────────────────────────────────
function New-KitStage([string]$RepoRoot, [string]$ArtifactDir, [string]$PackageDir, [string]$StageDir, [string]$HistoricalDir, [string]$HistoricalJsonPath = (Join-Path $RepoRoot 'build/historical-artifacts.json')) {
    foreach ($f in $script:KitFiles) {
        $src = Join-Path $RepoRoot $f.Source
        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { throw "Kit file missing from the repository: $($f.Source)" }
        $dst = Join-Path $StageDir $f.Dest
        New-Item -ItemType Directory -Path (Split-Path $dst) -Force | Out-Null
        Copy-Item -LiteralPath $src -Destination $dst
    }
    New-Item -ItemType Directory -Path (Join-Path $StageDir 'artifacts') -Force | Out-Null
    foreach ($n in (@(Get-ExpectedArtifactNames) + 'SHA256SUMS.txt')) { Copy-Item -LiteralPath (Join-Path $ArtifactDir $n) -Destination (Join-Path $StageDir 'artifacts') }
    Copy-Item -LiteralPath $PackageDir -Destination (Join-Path $StageDir 'packages') -Recurse
    # The original released RC1 (.NET 8) files, exactly as pinned: refuse to pack anything else.
    $bad = @(Test-HistoricalArtifacts $HistoricalDir $HistoricalJsonPath | Where-Object { -not $_.Ok })
    if ($bad.Count) { throw "Historical RC1 artifacts do not verify: $(($bad | ForEach-Object { "$($_.File): $($_.Detail)" }) -join '; ')" }
    New-Item -ItemType Directory -Path (Join-Path $StageDir 'historical') -Force | Out-Null
    foreach ($f in @((@((Get-Content -LiteralPath $HistoricalJsonPath -Raw | ConvertFrom-Json).releases | Where-Object { $_.label -ceq '1.6.0-rc.1' })[0]).files)) { Copy-Item -LiteralPath (Join-Path $HistoricalDir $f.name) -Destination (Join-Path $StageDir 'historical') }
    $missing = @($script:KitFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $StageDir $_.Dest)) })
    if ($missing.Count) { throw "Kit staging incomplete: $(($missing | ForEach-Object { $_.Dest }) -join ', ')" }
}
