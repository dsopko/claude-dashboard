namespace ClaudeDashboard.Core;

/// <summary>
/// The display bands of TS §IV.2, most urgent first. Values descend with urgency, so a
/// larger value sorts higher on screen.
/// </summary>
public enum AttentionBand
{
    /// <summary>Blocked on the operator: a permission, an error, or a question.</summary>
    NeedsYou = 5,

    /// <summary>Finished, but not yet seen — the thing this tool exists to surface.</summary>
    Unread = 4,

    /// <summary>Claude is working the turn.</summary>
    Working = 3,

    /// <summary>Nothing wants the operator. Sinks, and collapsible (TS §IV.4).</summary>
    Quiet = 2,

    /// <summary>Terminated; dim, and removed after a short window.</summary>
    Ended = 1,
}

/// <summary>
/// The single ratified severity order over <see cref="SessionState"/> (TS §IV.2, §IV.3),
/// and the band each state falls into.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One ranking, one place.</strong> Both consumers read it here: the attention
/// engine, which bands and orders sessions for display, and <see cref="Group.WorstState"/>,
/// which rolls a group up to its most urgent member. Those two disagreeing is not a cosmetic
/// bug — a group would sort into a position its own roll-up state contradicts — and two
/// tables kept in step by hand is exactly how that happens. Before this was unified they had
/// in fact drifted: TS §IV.2 and §IV.3 disagreed about where <see cref="SessionState.Error"/>
/// sat, and the code inherited the disagreement.
/// </para>
/// <para>
/// <strong>Permission &gt; Error &gt; Question, ratified by the operator on 2026-08-24.</strong>
/// The rationale is throughput, not age. A permission prompt is usually seconds of operator
/// time standing between an agent and an indefinite wait, so clearing it returns the most
/// blocked capacity per second of attention. An error is next — often self-recoverable on a
/// retry, but stopped until looked at. A question is the softest: it may need real thought,
/// and thinking about it unblocks nothing else meanwhile.
/// </para>
/// <para>
/// <strong>These are sub-bands, not tie-breaks.</strong> Within the Needs-You band kind sorts
/// first and age sorts only within a kind, so a question blocked twenty minutes appears
/// <em>below</em> a permission raised three minutes ago. That looks wrong at a glance and is
/// not: the operator was shown this and the alternative — age dominant, kind breaking exact
/// ties only — side by side, and chose this. TS §IV.2's oldest-first principle still governs
/// within a kind.
/// </para>
/// </remarks>
public static class AttentionOrder
{
    /// <summary>
    /// How urgently <paramref name="state"/> wants the operator. Higher is more urgent; the
    /// order is total, so any two states compare decisively.
    /// </summary>
    /// <remarks>
    /// <strong>Total, and deliberately not injective.</strong> "Compares decisively" is not
    /// "tells apart": <see cref="SessionState.Ended"/> and any unrecognised value both rank 0, on
    /// purpose, because an unrecognised state must not outrank real work. So two genuinely
    /// different states can tie here, and anything that reduces over ranks has to say what it
    /// does with a tie rather than assume none arises — <see cref="BandOf"/> puts those same two
    /// in <em>different</em> bands, so a tie broken carelessly is visible on screen.
    /// </remarks>
    public static int Rank(SessionState state) => state switch
    {
        SessionState.NeedsPermission => 6,
        SessionState.Error => 5,
        SessionState.NeedsQuestion => 4,
        SessionState.Unread => 3,
        SessionState.Working => 2,
        SessionState.Acked => 1,
        SessionState.Ended => 0,

        // An unrecognized state cannot be allowed to outrank real work.
        _ => 0,
    };

    /// <summary>The band <paramref name="state"/> is displayed in (TS §IV.2).</summary>
    /// <remarks>
    /// A coarsening of <see cref="Rank"/>, not a second ordering: the three Needs-You states
    /// keep their distinct ranks and share one band. That the two agree — a more urgent state
    /// never lands in a less urgent band — is pinned by test rather than derived, because
    /// deriving it would obscure the mapping this method exists to state plainly.
    /// </remarks>
    public static AttentionBand BandOf(SessionState state) => state switch
    {
        SessionState.NeedsPermission or SessionState.Error or SessionState.NeedsQuestion =>
            AttentionBand.NeedsYou,
        SessionState.Unread => AttentionBand.Unread,
        SessionState.Working => AttentionBand.Working,

        // TS §IV.2's Quiet band reads "Acked, idle". `SessionState` has no Idle member, and
        // T1.2 files a just-started session under Acked, so Acked carries both meanings.
        // Both sort by recency here, so Phase 1 cannot tell them apart and does not need to.
        SessionState.Acked => AttentionBand.Quiet,
        SessionState.Ended => AttentionBand.Ended,
        _ => AttentionBand.Quiet,
    };

    /// <summary>
    /// The most severe state in <paramref name="states"/>, or <see cref="SessionState.Ended"/>
    /// when there are none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one roll-up. <see cref="Group.WorstState"/> answers this question for a group's
    /// members and the tray answers it for every session at once (Impl §5.2), and they are the
    /// same question — "worst wins", over <see cref="Rank"/>, which is why the ranking is not
    /// copied a third time.
    /// </para>
    /// <para>
    /// <strong>Order-independent because ties go to the first, not because ties cannot
    /// happen.</strong> The comparison is strictly <c>&gt;</c>, so an equal rank never displaces
    /// the incumbent and the answer is the same whatever order <paramref name="states"/> arrives
    /// in. That is the whole reason, and it is worth stating exactly, because the tempting
    /// version — "<see cref="Rank"/> is total, so states can only tie when they are equal" — is
    /// <em>false</em>: <see cref="Rank"/> is not injective. <see cref="SessionState.Ended"/> and
    /// any unrecognised value both rank 0, and <see cref="BandOf"/> sorts them into different
    /// bands. Relax this to <c>&gt;=</c> and <c>[Ended, unrecognised]</c> answers differently
    /// from <c>[unrecognised, Ended]</c>, one landing in Ended and the other in Quiet.
    /// </para>
    /// <para>
    /// Nothing today can hand this an unrecognised state — the Registry only ever stores values
    /// the state machine produced — so that difference is not currently reachable. It is
    /// documented rather than defended because the two functions beside this one, both of which
    /// carry an explicit <c>_ =&gt;</c> fallback, decline to assume it never will be.
    /// </para>
    /// <para>
    /// Empty answers <see cref="SessionState.Ended"/> because it is rank 0: a dashboard with no
    /// sessions is as quiet as one whose sessions have all finished, and nothing that ranks
    /// above nothing would be true.
    /// </para>
    /// </remarks>
    /// <param name="states">The states to roll up.</param>
    /// <exception cref="ArgumentNullException"><paramref name="states"/> is null.</exception>
    public static SessionState WorstOf(IEnumerable<SessionState> states)
    {
        ArgumentNullException.ThrowIfNull(states);

        var worst = SessionState.Ended;
        var worstRank = Rank(worst);

        foreach (var state in states)
        {
            var rank = Rank(state);

            if (rank > worstRank)
            {
                worst = state;
                worstRank = rank;
            }
        }

        return worst;
    }
}
