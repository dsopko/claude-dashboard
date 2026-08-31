namespace ClaudeDashboard.Core;

/// <summary>A session the silence sweep moved, and how long it had been quiet.</summary>
/// <param name="Session">The session as it now stands, already <see cref="SessionState.Interrupted"/>.</param>
/// <param name="Silence">How long since an event for it last arrived.</param>
/// <remarks>
/// The duration travels with the session because the log line needs it and the session does not
/// carry it: <see cref="Session.EnteredAt"/> is now, and <see cref="Session.LastHeardAt"/> is a
/// stamp rather than an age. Handing the caller a subtraction it would otherwise redo — against a
/// clock it would have to read again — is what keeps the logged number the same number the sweep
/// decided on.
/// </remarks>
public readonly record struct SilentSession(Session Session, TimeSpan Silence);

/// <summary>
/// How long a <see cref="SessionState.Working"/> session may be silent before the dashboard stops
/// calling it busy (issue #28).
/// </summary>
/// <remarks>
/// <para>
/// <strong>TEN MINUTES IS A GUESS AND IS TREATED AS ONE.</strong> Nothing measured it. It is long
/// because the expensive mistake is the false positive: marking a working session quiet is the
/// same sin as marking a quiet session busy, in the direction this product exists to prevent.
/// </para>
/// <para>
/// <strong>The gap it cannot close is a single long-running tool call.</strong> <c>PostToolBatch</c>
/// fires when a batch <em>resolves</em>, so one ten-minute <c>Bash</c> emits nothing at all while it
/// runs and is indistinguishable from an interrupted turn. A shorter threshold would grey out a
/// session working perfectly.
/// </para>
/// <para>
/// <strong>The log is the calibration path, which is why there is no setting.</strong> Every
/// transition is logged at Information with the silence that caused it, so the operator can see
/// from their own machine whether ten minutes is right — rather than being handed a knob and asked
/// to guess. If the log says it is wrong, changing this constant is a one-line commit and they get
/// a considered value instead of a preference.
/// </para>
/// <para>
/// Injectable everywhere it is used, so the tests drive it from <c>IClock</c> and nothing sleeps —
/// the same shape as <see cref="RosterSettle.DefaultWindow"/>.
/// </para>
/// </remarks>
public static class SilenceWatch
{
    /// <summary>How long a working session may be silent before it reads as interrupted.</summary>
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMinutes(10);

    /// <summary>
    /// What the transition log records as the cause.
    /// </summary>
    /// <remarks>
    /// <strong>It names silence, not interruption.</strong> Every other entry in that log is a
    /// hook event name — something that arrived — and this one is the absence of any. Writing
    /// <c>Interrupted</c> here would put a cause in the history that nothing observed, in the one
    /// place a reader goes to find out what actually happened.
    /// </remarks>
    public const string Cause = "silence";
}
