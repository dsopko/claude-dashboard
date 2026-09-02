<#
.SYNOPSIS
Publishes and packs Claude Dashboard (PKG.2, PKG.3; Packaging Design D2-D6).

.DESCRIPTION
Part one: dotnet publish, self-contained, win-x64, as a DIRECTORY of files — deliberately not
single-file, because Velopack diffs releases at the file level and a single-file bundle
collapses every update into one opaque blob (Design D2).

Part two: dotnet vpk pack over that directory, producing the per-user Setup, the portable zip,
the full update package and the release manifests in artifacts\releases (Design D1, D3).

The version is mandatory and is the one number the whole release carries (Design D4): the same
value goes to dotnet publish and to vpk pack. Velopack requires full semver, so 0.1 is refused
here rather than failing later inside the pack step.

ONE-TIME SETUP: dotnet tool restore. vpk is pinned as a repo-local tool in
.config\dotnet-tools.json, at the version the app's own Velopack package reference names — a
test fails when the two drift (Design D5). Never install vpk globally for this repo.

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

# ---- Part two: pack (PKG.3) --------------------------------------------------------------------

$releasesDir = Join-Path $repoRoot 'artifacts\releases'
$icon = Join-Path $repoRoot 'src\ClaudeDashboard.App\Assets\app.ico'

# Cleared for the same reason part one clears publish: vpk writes into whatever is here, and a
# stale artefact from a previous version would ride along into whatever uploads this folder.
# Local deltas are deliberately not a goal — Step 3 builds deltas against the published release
# feed, not against leftovers on one machine's disk.
if (Test-Path $releasesDir) {
    Remove-Item -Recurse -Force $releasesDir
}

# dotnet vpk, never a global vpk: the manifest pins the version to the app's own Velopack
# package, and a global tool is whatever somebody installed last (Design D5).
#
# FROM THE REPO ROOT, AND THAT IS WHAT MAKES "ANY CWD" TRUE (PKG.3 fix cycle 1). dotnet finds a
# local tool manifest by walking up from the CURRENT DIRECTORY, not from the script or the
# project - measured: from a directory outside the repo, part one published and this call died
# with "command or file was not found". Push/Pop rather than passing a path, because there is
# nothing to pass: the manifest lookup has no override switch.
#
# --shortcuts StartMenuRoot and --packAuthors are D113's two rulings: vpk's default also puts a
# shortcut on the desktop, where a tray app has no business, and its default publisher string in
# the Apps list is the dotted package id rather than a person.
Push-Location $repoRoot
try {
    dotnet vpk pack `
        --packId dsopko.ClaudeDashboard `
        --packVersion $Version `
        --packDir $publishDir `
        --mainExe ClaudeDashboard.App.exe `
        --packTitle "Claude Dashboard" `
        --packAuthors "David Sopko" `
        --shortcuts StartMenuRoot `
        --icon $icon `
        --outputDir $releasesDir
}
finally {
    Pop-Location
}

if ($LASTEXITCODE -ne 0) {
    throw "dotnet vpk pack failed with exit code $LASTEXITCODE. The publish output in $publishDir is intact."
}

# Everything vpk produced stays: Setup, portable zip, the full .nupkg and the release manifests
# are one release and Step 3 uploads them together. Nothing here filters or deletes.
Get-ChildItem -File $releasesDir | ForEach-Object {
    Write-Host ("Packed  {0,12:N0} KB  {1}" -f ($_.Length / 1KB), $_.Name)
}
