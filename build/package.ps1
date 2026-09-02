<#
.SYNOPSIS
Publishes Claude Dashboard for packaging (PKG.2; Packaging Design D2, D4).

.DESCRIPTION
Part one of the package pipeline: dotnet publish, self-contained, win-x64, as a DIRECTORY of
files — deliberately not single-file, because Velopack diffs releases at the file level and a
single-file bundle collapses every update into one opaque blob (Design D2).

The version is mandatory and is the one number the whole release carries (Design D4): it goes to
dotnet publish here, and PKG.3 hands the same value to vpk pack. Velopack requires full semver,
so 0.1 is refused here rather than failing later inside the pack step.

Part two (vpk pack) is PKG.3's, not this script's yet.

.PARAMETER Version
Full semver: 0.1.0, optionally with -prerelease and +buildmetadata.

.EXAMPLE
.\build\package.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

# Full semver, the shape Velopack itself demands (Design D4). Checked here so a short version
# fails in one second with a sentence, not minutes later inside a pack step with a stack.
$semver = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)' +
    '(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?' +
    '(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$'

if ($Version -notmatch $semver) {
    throw "-Version must be full semver, for example 0.1.0 - Velopack refuses anything shorter (Design D4). Got: '$Version'."
}

# Every path from the script's own location, never the caller's cwd: this runs identically from
# the repo root, from build\, and from a scheduled invocation with no cwd worth trusting.
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ClaudeDashboard.App\ClaudeDashboard.App.csproj'
$publishDir = Join-Path $repoRoot 'artifacts\publish'

# A fresh output directory every run. dotnet publish overlays rather than replaces, so a file
# from a previous publish that this build no longer produces would linger - and PKG.3 packs
# whatever is in this folder.
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

dotnet publish $project -c Release -r win-x64 --self-contained -p:Version=$Version -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE. Nothing was packed."
}

$files = Get-ChildItem -Recurse -File $publishDir
$bytes = ($files | Measure-Object -Property Length -Sum).Sum

Write-Host ("Published {0} files, {1:N1} MB, to {2}" -f $files.Count, ($bytes / 1MB), $publishDir)
