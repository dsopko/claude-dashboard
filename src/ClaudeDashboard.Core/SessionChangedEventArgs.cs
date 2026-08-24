namespace ClaudeDashboard.Core;

/// <summary>What happened to a session (see <see cref="SessionChangedEventArgs"/>).</summary>
public enum SessionChangeKind
{
    /// <summary>The Registry had never seen this session before.</summary>
    Added = 1,

    /// <summary>An existing session changed.</summary>
    Updated = 2,
}

/// <summary>
/// A session was added or changed (Impl §2.2). Raised by <see cref="SessionRegistry"/> once
/// per event that actually had an effect.
/// </summary>
/// <remarks>
/// <para>
/// Carries the changed <see cref="Session"/> and what happened to it, rather than being a
/// bare "something changed" signal. Impl §4 marshals these onto the WPF dispatcher to update
/// an <c>ObservableCollection</c>, and a bare signal would force the UI to re-project the
/// whole Registry on every event just to discover which row moved. Carrying the session makes
/// each notification an <c>Add</c> or a targeted replace instead.
/// </para>
/// <para>
/// There is deliberately no "Removed" kind yet: nothing removes a session at this point.
/// TS §IV.1 has <see cref="SessionState.Ended"/> lead to removal on a timer, but Core does no
/// scheduling (see <see cref="Ports.IClock"/>), so whichever task implements that removal adds
/// the kind along with it.
/// </para>
/// <para>
/// Core does not know — and must not learn — that the subscriber is a UI (Impl §2.2).
/// </para>
/// </remarks>
/// <param name="kind">What happened.</param>
/// <param name="session">The session as it now stands.</param>
public sealed class SessionChangedEventArgs(SessionChangeKind kind, Session session) : EventArgs
{
    /// <summary>What happened.</summary>
    public SessionChangeKind Kind { get; } = kind;

    /// <summary>The session as it now stands, after the event was applied.</summary>
    public Session Session { get; } = session ?? throw new ArgumentNullException(nameof(session));
}
