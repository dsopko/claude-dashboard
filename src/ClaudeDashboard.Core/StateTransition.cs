namespace ClaudeDashboard.Core;

/// <summary>
/// One entry in a session's transition log: the move from one <see cref="SessionState"/> to
/// another, and when it happened.
/// </summary>
/// <param name="From">The state left behind.</param>
/// <param name="To">The state entered.</param>
/// <param name="At">When the transition was applied, from <c>IClock</c>.</param>
/// <param name="Cause">
/// The hook event name that caused it (<c>Stop</c>, <c>UserPromptSubmit</c>, …), or a
/// non-event acknowledgment source. Free text carried for diagnostics — data, never
/// instruction (TS §II.5), and never parsed to decide anything.
/// </param>
public readonly record struct StateTransition(
    SessionState From,
    SessionState To,
    DateTimeOffset At,
    string? Cause = null);
