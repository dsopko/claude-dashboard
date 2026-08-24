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
}
