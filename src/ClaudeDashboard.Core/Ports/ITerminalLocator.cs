namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// Finds where a session lives on screen by matching a terminal tab's visible text against
/// what the Registry already knows the session said (TS §III.2; Impl §1.3, §6.1).
/// <strong>Implemented in Phase 2/3</strong> over UI Automation; Phase 1 ships no adapter.
/// </summary>
/// <remarks>
/// <para>
/// This port carries the seam's shared failure convention, which every Phase 2/3/4 port
/// here follows: <strong>a query returns null when it could not determine an answer, a
/// command returns <see langword="false"/> when it could not act, and neither throws for a
/// platform failure</strong> (TS §IV.7). UI Automation is the least reliable thing this
/// application touches — it can be slow, blocked by an unresponsive target, or defeated by
/// two tabs showing identical text — and none of that is exceptional. Callers walk down the
/// degradation ladder instead: tab, then window, then nothing.
/// </para>
/// <para>
/// Note the two shapes of "less than a full answer", which are different: a
/// <em>window-level</em> <see cref="TabRef"/> (see <see cref="TabRef.TabIndex"/>) means the
/// window was found but the tab was not, and navigation should proceed at window
/// granularity; a <em>null</em> <see cref="TabRef"/> means nothing was found at all.
/// </para>
/// </remarks>
public interface ITerminalLocator
{
    /// <summary>
    /// Locates the tab showing <paramref name="session"/>, or null if it could not be
    /// located — the session may not be on screen, or its text may be ambiguous against
    /// another tab's (TS §III.7).
    /// </summary>
    /// <remarks>
    /// Asynchronous because the implementation walks UI Automation trees across every
    /// terminal window, which is far too slow to run on the UI thread (Impl §6.1).
    /// </remarks>
    Task<TabRef?> FindTab(SessionId session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the selected tab of the foreground terminal window, or null if the foreground
    /// window is not a terminal or the tab could not be read.
    /// </summary>
    /// <remarks>
    /// Synchronous and cheap by comparison with <see cref="FindTab"/> — it inspects one
    /// known window rather than searching — but it is still a UI Automation call and belongs
    /// off the UI thread.
    /// </remarks>
    TabRef? IdentifyForegroundTab();
}
