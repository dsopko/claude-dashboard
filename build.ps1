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

    The usage mod's tests run after the build and before the .NET tests (Usage Mod Execution
    Plan, ruling R4): claude plugin validate --strict mods/usage, then claude plugin test
    mods/usage, with their output in mod-validate.txt and mod-test.txt in the same folder.
    They pass when validate exits with 0, and the test command exits with 0 and ends with
    "N pass" and "0 fail". Each other result is NOT GREEN, with its reason: no claude on the
    path, mods are turned off, validate failed, or M fail. A machine with no Claude Code, or
    with mods turned off, is NOT GREEN too: a green verdict says that everything was verified.
    The mod's count never joins Total: its tests are not .NET tests, so dotnet test does not
    list them, and Total and the expected count are both the .NET host's own counts.

    The run ends with one verdict line, which names that folder:
      GREEN: exit 0, no abort, Total N, expected N, skipped S, mod M pass. Saved in <folder>
      NOT GREEN: <each check that failed, with the numbers>; <the counts>. Saved in <folder>
    The script exits with 0 only for GREEN. A skipped test is listed and counted in Total, so
    the line shows the skipped count, summed from the summary lines, and never fails on it.
    A NOT GREEN line still shows Total, the skipped count and the mod's count when they were
    read, so a failure of one part does not hide the result of the other.

    Never run this elevated: the app is developed and run at normal integrity (Impl 6.5).

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER NoTest
    Build only; skip the mod's tests, the test run and the verdict.

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

# Runs the command (dotnet by default) with the arguments, shows each line as it comes (unless -Quiet), and
# writes it to the file at once. Returns the exit code. Standard error is read with standard output, as text.
# -Utf8 reads the command's output as UTF-8, whatever the console's code page: claude writes marks outside ASCII.
function Invoke-Logged {
    param(
        [Parameter(Mandatory)] [string[]] $Arguments,
        [Parameter(Mandatory)] [string] $Path,
        [string] $Command = 'dotnet',
        [switch] $Quiet,
        [switch] $Utf8
    )

    $writer = New-Object System.IO.StreamWriter($Path, $false, (New-Object System.Text.UTF8Encoding($false)))
    $writer.AutoFlush = $true

    # Windows PowerShell turns a line on standard error into an error record; 'Stop' would end the run.
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $savedEncoding = [Console]::OutputEncoding

    try {
        if ($Utf8) {
            [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        }

        & $Command @Arguments 2>&1 | ForEach-Object {
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
        [Console]::OutputEncoding = $savedEncoding
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

# The last count of the form "  N pass" or "  N fail" in the mod test's output, or $null when there is none.
function Get-ModCount {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Word
    )

    $count = $null

    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        if ($line -match "^\s*(\d+) $Word\s*$") {
            $count = [int] $Matches[1]
        }
    }

    return $count
}

# Validates and tests the usage mod (ruling R4). Passed is true only for "mod N pass"; Text is that, or the reason.
function Invoke-ModTests {
    param([Parameter(Mandatory)] [string] $Folder)

    $mod = Join-Path $root (Join-Path 'mods' 'usage')

    if (-not (Get-Command 'claude' -CommandType Application, ExternalScript -ErrorAction SilentlyContinue)) {
        Write-Host ''
        Write-Host '==> mod: no claude on the path, so the mod is not tested' -ForegroundColor Yellow
        return [pscustomobject] @{ Passed = $false; Text = 'mod not run: no claude on the path' }
    }

    $validateFile = Join-Path $Folder 'mod-validate.txt'
    $testFile = Join-Path $Folder 'mod-test.txt'

    Write-Host ''
    Write-Host "==> mod validate, to $(Get-ShownPath $validateFile)" -ForegroundColor Cyan
    $validateExit = Invoke-Logged -Utf8 -Command 'claude' -Path $validateFile -Arguments @(
        'plugin', 'validate', '--strict', $mod
    )

    Write-Host ''
    Write-Host "==> mod test, to $(Get-ShownPath $testFile)" -ForegroundColor Cyan
    $testExit = Invoke-Logged -Utf8 -Command 'claude' -Path $testFile -Arguments @('plugin', 'test', $mod)

    # The words Claude Code uses when a setting, or Anthropic, stops every mod on the machine.
    $turnedOff = [bool] (Select-String -LiteralPath $validateFile, $testFile -SimpleMatch -Pattern 'hooks modules are turned off' -Quiet)
    $pass = Get-ModCount -Path $testFile -Word 'pass'
    $fail = Get-ModCount -Path $testFile -Word 'fail'

    if ($turnedOff) {
        $text = 'mod not run: mods are turned off'
    }
    elseif ($validateExit -ne 0) {
        $text = "mod validate failed (exit $validateExit)"
    }
    elseif ($null -ne $fail -and $fail -gt 0) {
        $text = "mod $fail fail"
    }
    elseif ($testExit -ne 0 -or $null -eq $fail -or $null -eq $pass -or $pass -eq 0) {
        # No test failed, and still no pass to report: the exit code and what was read say why.
        $shownPass = if ($null -eq $pass) { 'no pass count' } else { "$pass pass" }
        $shownFail = if ($null -eq $fail) { 'no fail count' } else { "$fail fail" }
        $text = "mod test exit $testExit, $shownPass, $shownFail"
    }
    else {
        return [pscustomobject] @{ Passed = $true; Text = "mod $pass pass" }
    }

    return [pscustomobject] @{ Passed = $false; Text = $text }
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

$modResult = Invoke-ModTests -Folder $folder

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
    Write-Host "NOT GREEN: the test list gave no expected count (exit $listExit, $listed tests listed); $($modResult.Text). Saved in $shown" -ForegroundColor Red
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

if (-not $modResult.Passed) {
    $failures += $modResult.Text
}

Write-Host ''

if ($failures.Count -eq 0) {
    Write-Host "GREEN: exit 0, no abort, Total $total, expected $expected, skipped $skipped, $($modResult.Text). Saved in $shown" -ForegroundColor Green
    exit 0
}

# The counts that were read, after the checks that failed: a failure of the mod does not hide the .NET run's
# Total, and a failure of the .NET run does not hide the mod's count. A skip is shown, never failed on.
# With no summary line there is no count to show.
$counts = @()

if ($null -ne $total) {
    if ($total -eq $expected) {
        $counts += "Total $total, expected $expected"
    }

    $counts += "skipped $skipped"
}

if ($modResult.Passed) {
    $counts += $modResult.Text
}

Write-Host ("NOT GREEN: {0}. Saved in {1}" -f (($failures + $counts) -join '; '), $shown) -ForegroundColor Red
exit 1
