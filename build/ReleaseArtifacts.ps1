<#
.SYNOPSIS
    Helpers shared by build/Build-Release.ps1 and build/Test-BuildScripts.ps1 (dot-source this file).

.DESCRIPTION
    Get-ExpectedMsi never "picks the first *.msi": it returns the one file the current build must have produced and refuses
    anything missing or older than the start of this build, so a leftover MSI from an earlier version/build can never be
    collected and renamed as the new release.
#>

function Get-ExpectedMsi {
    [CmdletBinding()]
    param(
        # Folder the WiX project builds into (installer/bin/Release).
        [Parameter(Mandatory)][string]$BuildOutputDir,
        # Release label, e.g. 1.6.0-rc.1 (the WiX project names its output AuraClean-<label>-win-x64.msi).
        [Parameter(Mandatory)][string]$Label,
        # Captured immediately before the installer build started.
        [Parameter(Mandatory)][datetime]$BuildStartedUtc
    )

    $expected = Join-Path $BuildOutputDir "AuraClean-$Label-win-x64.msi"
    if (-not (Test-Path -LiteralPath $expected -PathType Leaf)) {
        $others = @(Get-ChildItem -LiteralPath $BuildOutputDir -Filter '*.msi' -ErrorAction SilentlyContinue | ForEach-Object Name)
        $seen = if ($others.Count) { " Found other MSI file(s) that were NOT used: $($others -join ', ')." } else { '' }
        throw "The installer build did not produce the expected MSI '$expected'.$seen"
    }

    $item = Get-Item -LiteralPath $expected
    if ($item.LastWriteTimeUtc -lt $BuildStartedUtc) {
        throw "The MSI '$expected' (written $($item.LastWriteTimeUtc.ToString('o'))) is older than this build (started $($BuildStartedUtc.ToString('o'))): it is a stale artifact, not the output of this build."
    }
    $item
}

function Clear-StaleInstallerOutput {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BuildOutputDir)

    # Only generated installer outputs are removed; the folder is inside the git-ignored installer/bin tree.
    if (Test-Path -LiteralPath $BuildOutputDir) {
        Get-ChildItem -LiteralPath $BuildOutputDir -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in '.msi', '.wixpdb', '.cab' } |
            Remove-Item -Force
    }
}
