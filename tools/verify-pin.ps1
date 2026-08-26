<#
.SYNOPSIS
  Proves — or disproves — that the dashboard window is pinned to every virtual desktop (T1.16).

.DESCRIPTION
  Pinning is an undocumented call, and the thing performing it is the only thing that knows it
  succeeded. `PinToAllDesktops` returning true is exactly what a stub returns, and no test inside
  the process can contradict "the window is on every desktop".

  So this asks a DIFFERENT interface: `IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop`,
  which Microsoft documents, which the dashboard does not implement, and which has no stake in the
  outcome. A pinned window answers True from every desktop. An unpinned one answers False the
  moment you switch away.

  IT NEEDS A CONTROL, AND THAT IS THE POINT.
  "Our window says True on desktop 2" is also what you see when the desktop switch never happened.
  So an unpinned window is watched alongside. The result only means anything when the control
  reads False — that is the evidence the switch occurred.

  This is not hypothetical. The first run of this experiment reported our window present on
  "desktop 2" and looked like the strongest verification in the task. The control said True as
  well: no switch had happened, because Windows ignores an injected Win-key hotkey from a
  background process. Without the control, a verified pin would have been reported on no evidence
  whatsoever.

  THE RULE, IN FULL, BECAUSE HALF OF IT IS THE HALF THAT WORKS.
  "Find an oracle the implementation does not control" is not enough, and this run is the proof:
  that is exactly what we had — documented by Microsoft, no stake in the outcome, unreachable from
  a `return true` — and it reported the pin verified when no desktop switch had happened.

  It failed because it answers a DIFFERENT QUESTION. `IsWindowOnCurrentVirtualDesktop` reports
  PRESENCE, not pinning, and presence is equally true of an unpinned window that never left. It
  becomes evidence about pinning only when combined with a state change and a control proving the
  state change occurred. Remove any one of those three and it proves nothing.

  So carry it as: FIND AN ORACLE THE IMPLEMENTATION DOES NOT CONTROL, AND A CONTROL THAT PROVES
  THE ORACLE WAS ASKED UNDER THE CONDITIONS YOU THINK IT WAS. The first half is what this script
  set out to do. The second half is what caught the failure — and it is the half a later reader
  drops, because it looks like extra rigour rather than the load-bearing part.

  The general form: ask whether the experiment could have produced the OTHER outcome — and ask it
  of the environment, not only of the code and the assertion. What answers that is a control which
  shares the experiment's environment but not its subject. An unpinned window goes through the same
  desktop switch, the same oracle and the same clock as ours; the only thing it does not share is
  the thing under test.

  INCONCLUSIVE IS A REAL OUTCOME, AND THERE ARE TWO OF THEM.
  This reports four results, not two. A tool that can only say PASS or FAIL has to call a no-switch
  run one of them, and it will call it PASS. The second inconclusive is the oracle itself failing:
  `PinOracle.On` returns 'ERR' when the COM call throws, and 'ERR' is neither True nor False. An
  earlier version let it fall through to the else and print "the dashboard was absent too" — a
  positive claim about where the window is, made on no reading at all. Under-claiming is still a
  failure reported as a measurement.

  THIS FILE MUST KEEP ITS BYTE-ORDER MARK, AND SO MUST EVERY .ps1 IN THIS REPOSITORY.
  Windows PowerShell 5.1 reads a file with no BOM as ANSI. Every UTF-8 em dash in here is the bytes
  E2 80 94, which under CP1252 decode to three characters — â, €, and U+201D, RIGHT DOUBLE
  QUOTATION MARK. PowerShell accepts a curly quote as a string delimiter, so each em dash becomes
  an opening or closing quote, everything between two consecutive ones is swallowed as a string
  literal, and any brace or keyword inside that region goes with it. Whether the file still parses
  depends on WHERE the dashes fall and what the swallowed regions took, so do not reason about it.
  Run the check:

    $e=$null; [void][Management.Automation.Language.Parser]::ParseFile($PSCommandPath,[ref]$null,[ref]$e); $e

  U+201D is the name worth carrying: it is one search away from the well-known PowerShell curly-
  quote problem, which is the whole point of writing this down.

  AND READ THIS NOTE AS AN EXHIBIT, NOT ONLY AS AN INSTRUCTION. Its first version explained the
  breakage by parity — an even number of dashes parses, odd does not — which is false, and was
  falsified by content already in this repository: this script failed the parser with sixteen dashes
  and passed with eight, both even, while build.ps1 passed with one. The count went 8 to 16. It was
  never about the count. That explanation was formed in the middle of a bisection and written down
  as a conclusion, which is precisely the failure this script exists to catch, committed by the
  person documenting it, in the file where it is documented.

  IT NEEDS A HUMAN, AND THAT IS NOT LAZINESS.
  Switching virtual desktops cannot be automated from here. Both `keybd_event` and `SendInput`
  were tried: SendInput reports all six key events accepted and the desktop does not change —
  Windows does not honour an injected Win-key shell hotkey from a background process. The only
  documented alternative, `MoveWindowToDesktop`, needs a target desktop id, and enumerating
  desktops is undocumented — the tier this task exists to isolate. So the keystroke is yours.

.PARAMETER Seconds
  How long to watch. Press the keys at any point inside the window.
#>
[CmdletBinding()]
param([int] $Seconds = 40)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IVirtualDesktopManagerDoc {
  bool IsWindowOnCurrentVirtualDesktop(IntPtr w);
  Guid GetWindowDesktopId(IntPtr w);
  void MoveWindowToDesktop(IntPtr w, ref Guid d);
}
public static class PinOracle {
  static IVirtualDesktopManagerDoc M() {
    return (IVirtualDesktopManagerDoc)Activator.CreateInstance(
      Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"), true));
  }
  public static string On(IntPtr h) {
    try { return M().IsWindowOnCurrentVirtualDesktop(h).ToString(); } catch { return "ERR"; }
  }
}
'@

$dash = Get-Process -Name ClaudeDashboard.App -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $dash) {
    Write-Output "No dashboard window found. Start the dashboard and open its window (tray -> Open) first."
    return
}

$control = Get-Process -Name explorer -ErrorAction SilentlyContinue |
           Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $control) {
    Write-Output "No control window found. Open a File Explorer window first — without a control this proves nothing."
    return
}

# A STEP THAT CONSUMES SOMETHING SAYS WHAT IT CONSUMED, NOT ONLY THAT IT FINISHED.
# This line exists for that reason and is worth keeping even though nothing reads it. Three of the
# measurement failures behind this script were stale OUTPUT — a result read from the run before.
# The fourth was stale INPUT, and it is the worse one: a bisection loop whose file read was failing
# silently, so every iteration reported a clean parse of a leftover file and the whole result was
# an artefact. It reported success at doing nothing. Naming the input is what makes that visible,
# because a wrong handle here is obvious on sight and a missing one cannot be printed at all.
Write-Output "dashboard window = $($dash.MainWindowHandle)   control (explorer) = $($control.MainWindowHandle)"
Write-Output ""
Write-Output "  PRESS  Ctrl+Win+D          to create and switch to a new desktop"
Write-Output "  THEN   Ctrl+Win+F4         to close it and come back"
Write-Output ""
Write-Output "  Watching for $Seconds seconds. The line that matters is the one where CONTROL is False."
Write-Output ""
Write-Output ("  {0,-9} {1,-9} {2}" -f 'DASHBOARD', 'CONTROL', 'meaning')

$sawSwitch = $false
$pinnedThere = $false
$oracleFailed = $false

for ($i = 0; $i -lt $Seconds; $i++) {
    $ours = [PinOracle]::On($dash.MainWindowHandle)
    $ctl  = [PinOracle]::On($control.MainWindowHandle)

    # The control reading False is the ONLY thing that establishes that a switch happened. A
    # control of 'ERR' is not a switch and is deliberately not counted as one: that is the oracle
    # failing, not the desktop changing.
    if ($ctl -eq 'False') {
        $sawSwitch = $true

        # THREE BRANCHES, NOT TWO. 'ERR' is the oracle refusing to answer about our window, and it
        # must never fall through to the else — that prints "the dashboard was absent too", which
        # is a positive claim about where the window is, made on no reading at all. Wrong in the
        # safe direction is still a failure reported as a measurement, and preventing exactly that
        # is why this script exists.
        if     ($ours -eq 'True')  { $meaning = '<-- on another desktop, and the dashboard is still there: PINNED'; $pinnedThere = $true }
        elseif ($ours -eq 'False') { $meaning = '<-- on another desktop, and the dashboard is not: NOT pinned' }
        else                       { $meaning = '<-- switched, but the oracle would not answer about the dashboard: NO READING'; $oracleFailed = $true }

        Write-Output ("  {0,-9} {1,-9} {2}" -f $ours, $ctl, $meaning)
    }

    Start-Sleep -Seconds 1
}

Write-Output ""
if (-not $sawSwitch) {
    Write-Output "INCONCLUSIVE. The control never read False, so no desktop switch was observed."
    Write-Output "That is not evidence of anything about pinning — it means the keys were not pressed,"
    Write-Output "or were pressed outside the watch window."
}
elseif ($pinnedThere) {
    Write-Output "PINNED. The dashboard reported present on a desktop the control was absent from."
}
elseif ($oracleFailed) {
    Write-Output "INCONCLUSIVE. A switch was observed, but every reading of the dashboard window was an"
    Write-Output "error rather than a True or a False. The oracle could not answer, so nothing here is a"
    Write-Output "statement about pinning. Check that the dashboard window is still open — its handle"
    Write-Output "goes stale the moment it closes — and run this again."
}
else {
    Write-Output "NOT PINNED. On a desktop the control was absent from, the dashboard was absent too."
}
