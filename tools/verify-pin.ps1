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

  The general form is worth carrying: ask whether the experiment could have produced the OTHER
  outcome — and ask it of the environment, not only of the code and the assertion. What answers it
  is a control that shares the experiment's environment but not its subject. An unpinned window
  goes through the same desktop switch, the same oracle and the same clock as ours; the only thing
  it does not share is the thing under test.

  INCONCLUSIVE IS A REAL OUTCOME.
  This reports three results, not two. A tool that can only say PASS or FAIL has to call a
  no-switch run one of them, and it will call it PASS.

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

for ($i = 0; $i -lt $Seconds; $i++) {
    $ours = [PinOracle]::On($dash.MainWindowHandle)
    $ctl  = [PinOracle]::On($control.MainWindowHandle)

    $meaning = ''
    if ($ctl -eq 'False') {
        $sawSwitch = $true
        if ($ours -eq 'True') { $meaning = '<-- on another desktop, and the dashboard is still there: PINNED'; $pinnedThere = $true }
        else                  { $meaning = '<-- on another desktop, and the dashboard is not: NOT pinned' }
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
else {
    Write-Output "NOT PINNED. On a desktop the control was absent from, the dashboard was absent too."
}
