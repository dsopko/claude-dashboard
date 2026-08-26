<#
.SYNOPSIS
    Restore, build and test the Claude Dashboard solution.

.DESCRIPTION
    The one entry point for a full local verification pass. Product projects build
    with warnings-as-errors (see src/Directory.Build.props), so a clean run here is
    the Definition of Done check for "builds clean; named tests green".

    Never run this elevated — the app is developed and run at normal integrity
    (Impl 6.5).

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER NoTest
    Build only; skip the test run.

.EXAMPLE
    ./build.ps1
    ./build.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $NoTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$solution = Join-Path $root 'ClaudeDashboard.slnx'

function Invoke-Step {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

Invoke-Step -Name 'restore' -Arguments @('restore', $solution)
Invoke-Step -Name "build ($Configuration)" -Arguments @(
    'build', $solution, '--configuration', $Configuration, '--no-restore'
)

if (-not $NoTest) {
    Invoke-Step -Name "test ($Configuration)" -Arguments @(
        'test', $solution, '--configuration', $Configuration, '--no-build'
    )
}

Write-Host ''
Write-Host 'Build succeeded.' -ForegroundColor Green
