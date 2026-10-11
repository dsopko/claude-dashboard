namespace ClaudeDashboard.Core;

/// <summary>
/// Whose schedule a session's next reminder is on (T1.84, issue #58).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it exists.</strong> A roster member that finished has no reminder of its own: its roster group
/// announces the finish and sounds the one reminder. Before this, such a member and a session with no reminder
/// coming both had no time, and only the documents told a reader that a missing time can mean either.
/// </para>
/// <para>
/// <see cref="Session"/> is the zero value, so a <c>default</c> <see cref="NextNudge"/> is "no reminder, and none
/// from a group".
/// </para>
/// </remarks>
public enum NudgeOwner
{
    /// <summary>The session's own schedule: its notice was its own, and so is its reminder.</summary>
    Session = 0,

    /// <summary>
    /// Its roster group's schedule: a finished member whose finish the group announces, and whose reminder, if one
    /// comes, is the group's one nudge.
    /// </summary>
    Group = 1,
}

/// <summary>
/// A session's next reminder, as <see cref="SoundPolicyEngine.NextNudgeOf"/> answers it (T1.84, issue #58): when it
/// is due, and whose schedule it is on.
/// </summary>
/// <param name="At">
/// When the owner next sounds this session's state: the session's own nudge for <see cref="NudgeOwner.Session"/>, the
/// roster group's nudge for <see cref="NudgeOwner.Group"/>. Null when nothing is scheduled.
/// </param>
/// <param name="By">Whose schedule <paramref name="At"/> is.</param>
public readonly record struct NextNudge(DateTimeOffset? At, NudgeOwner By);
