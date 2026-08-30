namespace ClaudeDashboard.Core;

/// <summary>
/// The settle window: how long every member of a roster group must have been quiet before the
/// group reads finished, and how soon a return to working proves that reading was wrong
/// (issue #16).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a delay exists at all.</strong> During a hand-off there is a moment when no member
/// is working — the director has stopped and the coder has not yet started — and without a delay
/// the group blips <em>finished</em> for an instant. That blip is not cosmetic: it is a finished
/// chime for work that is still running, which is the one false reading this product must never
/// produce.
/// </para>
/// <para>
/// <strong>THIS NEEDS NO HISTORY, AND THAT IS THE WHOLE DESIGN.</strong> "Quiet since" is already
/// on the sessions: <see cref="Session.EnteredAt"/> is when a session entered its current state,
/// and the Registry advances it <em>only</em> on a real state change. So for a group whose members
/// have all stopped, the latest <c>EnteredAt</c> among them is exactly the moment the last one
/// stopped working, and the group's displayed state is a pure function of the group and the
/// instant it is asked about. Nothing has to remember anything, and <see cref="Group"/> stays the
/// shape it says it is.
/// </para>
/// <para>
/// <strong><see cref="Session.LastActivity"/> would be wrong here</strong>, and the difference is
/// the reason this works. It advances on every event the Registry applies, including ones that
/// change no state — a tool batch on a session already working, a title latching. Measuring
/// quietness with it would restart the window on events that are not the session becoming quiet.
/// </para>
/// <para>
/// <strong>Both numbers are guesses and are treated as guesses.</strong> 1.5 seconds is the
/// operator's starting value, not a measured one, which is why
/// <see cref="MisMarkWindow"/> exists at all: a group that shows finished and returns to working
/// inside it proves the window was too short, and says so in the log. Both are injectable so the
/// tests drive them from a clock rather than sleeping.
/// </para>
/// </remarks>
public static class RosterSettle
{
    /// <summary>
    /// How long every member must have been quiet before a roster group reads finished.
    /// </summary>
    /// <remarks>
    /// The operator's value. They rejected a longer window as a worse guess: a delay long enough
    /// to be safe is long enough for the operator to have looked away.
    /// </remarks>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How soon a return to working proves the finished reading was wrong.
    /// </summary>
    /// <remarks>
    /// Deliberately much larger than <see cref="DefaultWindow"/>. It is not a second settle window
    /// — it is the instrument that decides whether the first one holds, so it has to be wide enough
    /// to catch a hand-off that the settle window missed by a margin.
    /// </remarks>
    public static readonly TimeSpan DefaultMisMarkWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What <paramref name="group"/> reads at <paramref name="now"/>, settle window included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only a roster group whose raw roll-up is <see cref="SessionState.Unread"/> is ever held
    /// back, and it is held at <see cref="SessionState.Working"/> — the truthful reading, because
    /// the premise of the window is that a hand-off is still in flight. Every other group and every
    /// other state answers exactly what <see cref="Group.WorstState"/> says, so a session in no
    /// roster is unaffected in every respect.
    /// </para>
    /// <para>
    /// A group of members that have all been quiet for hours is not held back either: its raw
    /// roll-up is <see cref="SessionState.Acked"/>, not Unread, so the window never applies to it.
    /// </para>
    /// </remarks>
    /// <param name="group">The group to read.</param>
    /// <param name="now">The instant to read it at.</param>
    /// <param name="window">The settle window; <see cref="DefaultWindow"/> when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is null.</exception>
    public static SessionState StateOf(Group group, DateTimeOffset now, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(group);

        return PendingDeadlineOf(group, now, window) is not null
            ? SessionState.Working
            : group.WorstState;
    }

    /// <summary>
    /// When <paramref name="group"/> is <strong>still</strong> due to change state on its own, or
    /// null when it is not waiting on the clock at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is what lets the host wake once instead of polling.</strong> The deadline is
    /// known the instant the group goes quiet, so nothing needs to check repeatedly whether it has
    /// passed. A host that ticks every fifteen seconds — which this one does — would otherwise
    /// deliver a 1.5-second window up to fifteen seconds late, which is not a settle window at all.
    /// </para>
    /// <para>
    /// Null for every group that is not a roster group mid-settle, which is nearly all of them
    /// nearly all of the time. A caller takes the earliest non-null deadline across the groups and
    /// waits until then or until its ordinary tick, whichever comes first.
    /// </para>
    /// </remarks>
    /// <param name="group">The group to ask about.</param>
    /// <param name="now">The instant to judge "still" against.</param>
    /// <param name="window">The settle window; <see cref="DefaultWindow"/> when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="group"/> is null.</exception>
    public static DateTimeOffset? PendingDeadlineOf(Group group, DateTimeOffset now, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (group.Order != SeverityOrder.RosterGroup || group.WorstState != SessionState.Unread)
        {
            return null;
        }

        var deadline = QuietSince(group) + (window ?? DefaultWindow);

        // A DEADLINE THAT HAS PASSED IS NOT A PENDING DEADLINE, and taking `now` is what makes this
        // method able to say so. Without it a settled group reported a deadline in the past for as
        // long as it stayed unread — which is until the operator acknowledges it, and that is the
        // state this product exists to leave sitting on screen. The host would then wake, find
        // nothing to do, and re-arm on the same past instant, about a hundred times a second.
        return deadline > now ? deadline : null;
    }

    /// <summary>
    /// When the last member of <paramref name="group"/> stopped — the latest
    /// <see cref="Session.EnteredAt"/> among its members.
    /// </summary>
    /// <remarks>
    /// Every member, not only the finished ones. A member acknowledged after another finished is
    /// still the most recent thing that happened to this group, and starting the window from the
    /// earlier event would let the group settle while something was still moving.
    /// </remarks>
    private static DateTimeOffset QuietSince(Group group)
    {
        var latest = group.Members[0].EnteredAt;

        foreach (var member in group.Members)
        {
            if (member.EnteredAt > latest)
            {
                latest = member.EnteredAt;
            }
        }

        return latest;
    }
}
