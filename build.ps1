<#
.SYNOPSIS
    Restore, build and test the Claude Dashboard solution, and say whether the test run is green.

.DESCRIPTION
    The one entry point for a full local verification pass. Product projects build
    with warnings-as-errors (see src/Directory.Build.props), so a clean run here is
    the Definition of Done check for "builds clean; named tests green".

    A test run is green only when all three hold (Execution Plan Part 1; issue #12, T1.80):
      1. dotnet test exits with code 0;
      2. its output has no line "The active test run was aborted";
      3. Total equals the count expected before the run.
    Why the third: when the test host crashes, dotnet test still prints "Passed!" for the
    tests that reported before the crash, and Total counts only those.

    The expected count is the test host's own list (dotnet test --list-tests), taken before
    the run, so no person types a number. The list names each theory case on its own line,
    as Total counts them. A theory whose data cannot be listed would be listed once and run
    many times; the check then fails safe, with a Total above the expected count.

    The whole output is kept: the list (list.txt), the console output of the run
    (console.txt, written line by line, so a crash leaves it) and the results file
    (test-results.trx), in artifacts/test-runs/<date-time>-<configuration>/, which git
    ignores. The script never deletes a saved run.

    The run ends with one verdict line, which names that folder:
      GREEN: exit 0, no abort, Total N, expected N, skipped S. Saved in <folder>
      NOT GREEN: <each check that failed, with the numbers>; skipped S. Saved in <folder>
    The script exits with 0 only for GREEN. A skipped test is listed and counted in Total, so
    the line shows the skipped count, summed from the summary lines, and never fails on it.

    Never run this elevated: the app is developed and run at normal integrity (Impl 6.5).

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER NoTest
    Build only; skip the test run and the verdict.

.PARAMETER ExpectedTotal
    The expected count, in place of the list's count. The list is still taken, as a check,
    and a difference is printed. For a suite whose list cannot count as Total does.

.PARAMETER Solution
    The solution or project to build and test. ClaudeDashboard.slnx by default; another
    path runs a throwaway project through the same checks.

.EXAMPLE
    ./build.ps1
    ./build.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [switch] $NoTest,

    [int] $ExpectedTotal,

    [string] $Solution
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot

if (-not $Solution) {
    $Solution = Join-Path $root 'ClaudeDashboard.slnx'
}

$AbortLine = 'The active test run was aborted'

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

# Runs dotnet with the arguments, shows each line as it comes (unless -Quiet), and writes it to the file at once.
# Returns the exit code. Standard error is read with standard output, as text.
function Invoke-Logged {
    param(
        [Parameter(Mandatory)] [string[]] $Arguments,
        [Parameter(Mandatory)] [string] $Path,
        [switch] $Quiet
    )

    $writer = New-Object System.IO.StreamWriter($Path, $false, (New-Object System.Text.UTF8Encoding($false)))
    $writer.AutoFlush = $true

    # Windows PowerShell turns a line on standard error into an error record; 'Stop' would end the run.
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        & dotnet @Arguments 2>&1 | ForEach-Object {
            $line = "$_"
            $writer.WriteLine($line)

            if (-not $Quiet) {
                Write-Host $line
            }
        }

        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $saved
        $writer.Dispose()
    }
}

# The folder for this run's output. A second run in the same second gets its own folder.
function New-RunFolder {
    $base = Join-Path $root (Join-Path 'artifacts' 'test-runs')
    $name = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Configuration
    $folder = Join-Path $base $name
    $n = 2

    while (Test-Path -LiteralPath $folder) {
        $folder = Join-Path $base ('{0}-{1}' -f $name, $n)
        $n++
    }

    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    return $folder
}

# The number of test cases the list names: one indented line each, under each assembly's heading.
function Get-ListedCount {
    param([Parameter(Mandatory)] [string] $Path)

    $count = 0
    $listing = $false

    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        if ($line -match '^The following Tests are available:') {
            $listing = $true
            continue
        }

        if ($listing -and $line -match '^    \S') {
            $count++
        }
        elseif ($listing -and $line -notmatch '^\s*$') {
            $listing = $false
        }
    }

    return $count
}

# The sums of Total and of Skipped over each assembly's summary line. Total is $null when there is no summary line.
function Get-ReportedTotal {
    param([Parameter(Mandatory)] [string] $Path)

    $total = $null
    $skipped = 0

    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        if ($line -match '^\s*(Passed|Failed)!\s+-\s+Failed:\s+\d+,\s+Passed:\s+\d+,\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)') {
            if ($null -eq $total) {
                $total = 0
            }

            $skipped += [int] $Matches[2]
            $total += [int] $Matches[3]
        }
    }

    return [pscustomobject] @{ Total = $total; Skipped = $skipped }
}

function Get-ShownPath {
    param([Parameter(Mandatory)] [string] $Path)

    if ($Path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Path.Substring($root.Length).TrimStart('\', '/')
    }

    return $Path
}

Invoke-Step -Name 'restore' -Arguments @('restore', $Solution)
Invoke-Step -Name "build ($Configuration)" -Arguments @(
    'build', $Solution, '--configuration', $Configuration, '--no-restore'
)

if ($NoTest) {
    Write-Host ''
    Write-Host 'Build succeeded.' -ForegroundColor Green
    exit 0
}

$folder = New-RunFolder
$shown = Get-ShownPath $folder
$listFile = Join-Path $folder 'list.txt'
$consoleFile = Join-Path $folder 'console.txt'

Write-Host ''
Write-Host "==> list tests ($Configuration), to $shown" -ForegroundColor Cyan
$listExit = Invoke-Logged -Quiet -Path $listFile -Arguments @(
    'test', $Solution, '--configuration', $Configuration, '--no-build', '--list-tests'
)
$listed = Get-ListedCount -Path $listFile

if ($PSBoundParameters.ContainsKey('ExpectedTotal')) {
    $expected = $ExpectedTotal

    if ($listExit -ne 0 -or $listed -ne $ExpectedTotal) {
        Write-Host "The list counts $listed tests (exit $listExit); -ExpectedTotal says $ExpectedTotal. The verdict uses $ExpectedTotal." -ForegroundColor Yellow
    }
}
elseif ($listExit -ne 0 -or $listed -eq 0) {
    Write-Host ''
    Write-Host "NOT GREEN: the test list gave no expected count (exit $listExit, $listed tests listed). Saved in $shown" -ForegroundColor Red
    exit 1
}
else {
    $expected = $listed
}

Write-Host ''
Write-Host "==> test ($Configuration), expecting $expected, to $shown" -ForegroundColor Cyan
$testExit = Invoke-Logged -Path $consoleFile -Arguments @(
    'test', $Solution, '--configuration', $Configuration, '--no-build',
    '--results-directory', $folder, '--logger', 'trx;LogFileName=test-results.trx'
)

$aborted = [bool] (Select-String -LiteralPath $consoleFile -SimpleMatch -Pattern $AbortLine -Quiet)
$reported = Get-ReportedTotal -Path $consoleFile
$total = $reported.Total
$skipped = $reported.Skipped

$failures = @()

if ($testExit -ne 0) {
    $failures += "exit $testExit"
}

if ($aborted) {
    $failures += "the run was aborted (`"$AbortLine`")"
}

if ($null -eq $total) {
    $failures += "no Total line, expected $expected"
}
elseif ($total -ne $expected) {
    $failures += "Total $total, expected $expected"
}

Write-Host ''

if ($failures.Count -eq 0) {
    Write-Host "GREEN: exit 0, no abort, Total $total, expected $expected, skipped $skipped. Saved in $shown" -ForegroundColor Green
    exit 0
}

# A skip is shown, never failed on. With no summary line there is no count to show.
if ($null -ne $total) {
    $failures += "skipped $skipped"
}

Write-Host ("NOT GREEN: {0}. Saved in {1}" -f ($failures -join '; '), $shown) -ForegroundColor Red
exit 1
