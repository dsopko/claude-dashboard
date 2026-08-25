namespace ClaudeDashboard.Core;

/// <summary>
/// What the whole dashboard amounts to right now: the worst state across every session, and how
/// many sessions are in each state worth counting (Impl §5.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the Needs-You kinds are counted separately.</strong> The tray glyph is a
/// coarsening — five colours for seven states — and it merges <see cref="SessionState.Error"/>
/// with <see cref="SessionState.NeedsQuestion"/> onto amber. The tooltip is where that
/// distinction survives, so it cannot reuse the header's "3 need you": Impl §5.2 requires
/// <c>2 permissions · 1 error · 1 question · 2 unread · 3 working</c>. Counting is a fact about
/// the sessions and lives here; turning these numbers into that sentence is presentation and
/// lives in the host.
/// </para>
/// <para>
/// <strong>Ended sessions.</strong> They are counted by nothing here and reach
/// <see cref="Worst"/> only as rank 0, which is the same answer an empty dashboard gives. So
/// whether an Ended session "participates" cannot change the glyph or the tooltip: an Ended
/// session can never be the worst unless everything is at rank 0, and every rank-0 state maps
/// to grey. The question is decided rather than left open.
/// </para>
/// </remarks>
public readonly record struct StatusSummary
{
    /// <summary>The most severe state across every session (TS §IV.3).</summary>
    public required SessionState Worst { get; init; }

    /// <summary>Sessions blocked asking permission.</summary>
    public required int Permissions { get; init; }

    /// <summary>Sessions whose turn died.</summary>
    public required int Errors { get; init; }

    /// <summary>Sessions blocked asking a question.</summary>
    public required int Questions { get; init; }

    /// <summary>Sessions finished and not yet seen.</summary>
    public required int Unread { get; init; }

    /// <summary>Sessions Claude is working.</summary>
    public required int Working { get; init; }

    /// <summary>
    /// Whether nothing is worth reporting — every counted state is empty.
    /// </summary>
    /// <remarks>
    /// Read off the counts rather than off <see cref="Worst"/>, so that the tooltip's
    /// <c>all quiet</c> and its counts can never disagree: they are the same numbers.
    /// </remarks>
    public bool IsAllQuiet =>
        Permissions == 0 && Errors == 0 && Questions == 0 && Unread == 0 && Working == 0;

    /// <summary>Summarises <paramref name="sessions"/>.</summary>
    /// <param name="sessions">Every session the dashboard knows about.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is null.</exception>
    public static StatusSummary Of(IEnumerable<Session> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var permissions = 0;
        var errors = 0;
        var questions = 0;
        var unread = 0;
        var working = 0;
        var worst = SessionState.Ended;
        var worstRank = AttentionOrder.Rank(worst);

        foreach (var session in sessions)
        {
            var state = session.State;

            switch (state)
            {
                case SessionState.NeedsPermission:
                    permissions++;
                    break;

                case SessionState.Error:
                    errors++;
                    break;

                case SessionState.NeedsQuestion:
                    questions++;
                    break;

                case SessionState.Unread:
                    unread++;
                    break;

                case SessionState.Working:
                    working++;
                    break;

                default:
                    break;
            }

            // The same roll-up AttentionOrder.WorstOf performs, inlined only to avoid walking
            // the sessions twice; the ranking itself is still AttentionOrder's and is not
            // restated. Asserted equal to WorstOf in StatusSummaryTests, so the two cannot drift.
            var rank = AttentionOrder.Rank(state);

            if (rank > worstRank)
            {
                worst = state;
                worstRank = rank;
            }
        }

        return new StatusSummary
        {
            Worst = worst,
            Permissions = permissions,
            Errors = errors,
            Questions = questions,
            Unread = unread,
            Working = working,
        };
    }
}
