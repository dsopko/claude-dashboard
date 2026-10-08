namespace ClaudeDashboard.Core;

/// <summary>
/// What the whole dashboard amounts to right now: the worst state across every session, and how
/// many sessions are in each state worth counting (Impl §5.2), with a roster group counted once, at its
/// roll-up (T1.83, issue #130).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A roster counts once.</strong> The counts and <see cref="Worst"/> are taken over
/// <see cref="CountedStates.Of"/>, not over the sessions, so a working orchestration is one Working, not two Unread
/// and one Working, and the tray is blue while it works. <see cref="CountedStates"/> carries the reason.
/// </para>
/// <para>
/// <strong>Why the Needs-You kinds are counted separately.</strong> The tray glyph is a
/// coarsening — five colours for eight states — and it merges <see cref="SessionState.Error"/>
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

    /// <summary>
    /// Summarises <paramref name="sessions"/> as the tray reads them: a roster group counts once, at its roll-up
    /// (T1.83, issue #130; <see cref="CountedStates"/> gives the reason).
    /// </summary>
    /// <param name="sessions">Every session the dashboard knows about.</param>
    /// <param name="rosters">The rosters that decide which sessions form a roster group.</param>
    /// <param name="now">The instant to read each roster group at.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> or <paramref name="rosters"/> is null.</exception>
    public static StatusSummary Of(IEnumerable<Session> sessions, RosterBook rosters, DateTimeOffset now) =>
        OfCounted(CountedStates.Of(sessions, rosters, now));

    /// <summary>Summarises <paramref name="sessions"/> with no rosters: every session counts on its own.</summary>
    /// <remarks>
    /// <strong>For a caller that has no rosters, and only for one.</strong> The tray, the window and <c>/state</c> all
    /// have the rosters and count with them (T1.83, issue #130); no product code calls this. A caller that has a
    /// <see cref="RosterBook"/> uses <see cref="Of(IEnumerable{Session}, RosterBook, DateTimeOffset)"/>: this one would
    /// count a roster's members one by one, which is the defect #130 removed.
    /// </remarks>
    /// <param name="sessions">Every session the dashboard knows about.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is null.</exception>
    public static StatusSummary Of(IEnumerable<Session> sessions) =>
        Of(sessions, RosterBook.Empty, DateTimeOffset.MinValue);

    /// <summary>Summarises the states <see cref="CountedStates.Of"/> gives.</summary>
    /// <param name="counted">The states to count.</param>
    /// <exception cref="ArgumentNullException"><paramref name="counted"/> is null.</exception>
    public static StatusSummary OfCounted(IEnumerable<CountedState> counted)
    {
        ArgumentNullException.ThrowIfNull(counted);

        var permissions = 0;
        var errors = 0;
        var questions = 0;
        var unread = 0;
        var working = 0;
        var worst = SessionState.Ended;
        var worstRank = AttentionOrder.Rank(worst);

        foreach (var entry in counted)
        {
            var state = entry.State;

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

                // Waiting counts as working, by the operator's ruling on issue #52 (T1.41): the tray
                // light stays blue and the tooltip says "working" for a session paused on its own
                // background work.
                case SessionState.Working:
                case SessionState.Waiting:
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
