namespace ClaudeDashboard.Core.Ports;

/// <summary>
/// Reports what the operator is looking at (TS §III.5; Impl §1.3, §6.2).
/// <strong>Implemented in Phase 3</strong> over <c>SetWinEventHook</c> plus UI Automation
/// selection events; Phase 1 ships no adapter and acknowledges manually and by new prompt.
/// </summary>
/// <remarks>
/// <para>
/// Plain .NET events, deliberately. Impl §1.3 says this port "raises
/// <c>ForegroundChanged</c>/<c>TabFocusChanged</c>", which is the event idiom; an
/// <c>IObservable</c> surface would mean taking a reactive dependency Impl Appendix A does
/// not list, and a callback would be a worse-typed event.
/// </para>
/// <para>
/// Two events rather than one because the platform genuinely distinguishes them: switching
/// tabs <em>inside</em> one terminal window does not raise a foreground change, since the
/// window handle never changes (Impl §6.2). Window focus is the cheap, reliable signal;
/// tab focus is the later, finer one, and either may be unavailable — a subscriber must
/// work correctly when only <see cref="ForegroundChanged"/> ever fires (TS §IV.7).
/// </para>
/// <para>
/// Handlers may be invoked on any thread — the WinEvent hook delivers on whichever thread
/// runs its message loop (Impl §6.2) — so subscribers marshal for themselves. This port
/// only reports focus; deciding that a dwell is long enough to count as an acknowledgment,
/// and raising the synthetic ack event, belongs to Phase 3's adapter and the pipeline.
/// </para>
/// </remarks>
public interface IFocusSource
{
    /// <summary>Raised when a different top-level window comes to the foreground.</summary>
    event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    /// <summary>
    /// Raised when the selected tab changes within a terminal window — including when the
    /// foreground window did not change, which is the case that makes this event necessary.
    /// </summary>
    event EventHandler<TabFocusChangedEventArgs>? TabFocusChanged;
}

/// <summary>The foreground window changed.</summary>
/// <param name="window">The window now in the foreground.</param>
public sealed class ForegroundChangedEventArgs(WindowHandle window) : EventArgs
{
    /// <summary>
    /// The window now in the foreground. It need not be a terminal — subscribers that only
    /// care about terminals filter for themselves.
    /// </summary>
    public WindowHandle Window { get; } = window;
}

/// <summary>The selected tab changed within a terminal window.</summary>
/// <param name="tab">The tab now selected.</param>
public sealed class TabFocusChangedEventArgs(TabRef tab) : EventArgs
{
    /// <summary>The tab now selected.</summary>
    public TabRef Tab { get; } = tab;
}
