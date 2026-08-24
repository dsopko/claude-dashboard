namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// Brings a located terminal tab to the foreground (TS §III.8; Impl §1.3, §6.4).
/// <strong>Implemented in Phase 2</strong> via <c>wt.exe … focus-tab</c> with direct window
/// activation as the fallback; Phase 1 ships no adapter.
/// </summary>
public interface ITerminalNavigator
{
    /// <summary>
    /// Activates <paramref name="tab"/>, bringing its window forward and selecting the tab
    /// when <see cref="TabRef.TabIndex"/> resolved.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the target was activated; <see langword="false"/> if it
    /// could not be — the window may have closed between locating and navigating, or the
    /// activation may simply not have taken. Per the seam's convention
    /// (see <see cref="ITerminalLocator"/>), that is a return value, not an exception.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Asynchronous because the implementation launches a process and may then drive UI
    /// Automation (Impl §6.4), neither of which may run on the WPF dispatcher thread
    /// (Execution Plan Part 1) — and navigation is triggered by a click on that very thread.
    /// </para>
    /// <para>
    /// Activation is click-initiated from the dashboard's own window, so the foreground lock
    /// generally does not bite and no foreground-stealing workaround is needed (TS §III.8).
    /// </para>
    /// </remarks>
    Task<bool> Activate(TabRef tab, CancellationToken cancellationToken = default);
}
