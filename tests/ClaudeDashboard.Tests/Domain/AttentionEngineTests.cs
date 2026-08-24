using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// TS §IV.2's banding and ordering — what TS calls the heart of the attention model.
/// </summary>
public sealed class AttentionEngineTests
{
    private static readonly DateTimeOffset Now = FakeClock.DefaultStart;
    private const string Dashboard = @"C:\projects\dashboard";

    /// <summary>A session in <paramref name="state"/> that entered it <paramref name="ageMinutes"/> ago.</summary>
    private static Session Aged(
        string id,
        SessionState state,
        double ageMinutes,
        string cwd = Dashboard,
        double? idleMinutes = null)
    {
        var sessionId = new SessionId(id);
        return new Session
        {
            Id = sessionId,
            State = state,
            Latest = new Exchange { Prompt = "p", StartedAt = Now.AddMinutes(-ageMinutes) },
            Cwd = cwd,
            Group = GroupKeys.ForSession(cwd, sessionId),
            EnteredAt = Now.AddMinutes(-ageMinutes),
            LastActivity = Now.AddMinutes(-(idleMinutes ?? ageMinutes)),
        };
    }

    private static IReadOnlyList<string> Ids(IEnumerable<Session> sessions) =>
        [.. sessions.Select(s => s.Id.Value)];

    private static IReadOnlyList<string> FlatIds(IEnumerable<BandedSessions> bands) =>
        [.. bands.SelectMany(b => b.Sessions).Select(s => s.Id.Value)];

    // ---- The Needs-You band: kind first, age within kind -------------------------------------

    /// <summary>
    /// <strong>This is not a bug.</strong> A question blocked twenty minutes sorts BELOW a
    /// permission raised three minutes ago, because kind sorts before age (TS §IV.3, ratified
    /// 2026-08-24). The rationale is throughput, not age: a permission is seconds of operator
    /// time standing between an agent and an indefinite wait, so clearing it returns the most
    /// blocked capacity per second of attention. The operator was shown this ordering and the
    /// age-dominant alternative side by side and chose this one. Do not "fix" it.
    /// </summary>
    [Fact]
    public void Kind_beats_age_a_twenty_minute_question_sorts_below_a_three_minute_permission()
    {
        var bands = AttentionEngine.Order(
        [
            Aged("question-20m", SessionState.NeedsQuestion, 20),
            Aged("permission-3m", SessionState.NeedsPermission, 3),
        ]);

        Assert.Equal(["permission-3m", "question-20m"], FlatIds(bands));
    }

    /// <summary>Error sits between the two, so it too outranks an older question.</summary>
    [Fact]
    public void Kind_beats_age_a_twenty_minute_question_sorts_below_a_one_minute_error()
    {
        var bands = AttentionEngine.Order(
        [
            Aged("question-20m", SessionState.NeedsQuestion, 20),
            Aged("error-1m", SessionState.Error, 1),
        ]);

        Assert.Equal(["error-1m", "question-20m"], FlatIds(bands));
    }

    /// <summary>And a permission outranks an older error.</summary>
    [Fact]
    public void Kind_beats_age_a_twenty_minute_error_sorts_below_a_one_minute_permission()
    {
        var bands = AttentionEngine.Order(
        [
            Aged("error-20m", SessionState.Error, 20),
            Aged("permission-1m", SessionState.NeedsPermission, 1),
        ]);

        Assert.Equal(["permission-1m", "error-20m"], FlatIds(bands));
    }

    /// <summary>
    /// The other half, pinned independently: TS §IV.2's oldest-first principle still governs
    /// <em>within</em> a kind. A mutation dropping either half must go red on its own.
    /// </summary>
    /// <remarks>
    /// The ids are chosen so that ordinal id order — the final tie-break — is the
    /// <em>reverse</em> of the expected result. Without that, dropping the age comparison would
    /// fall through to the id and reproduce the right answer for the wrong reason, and this
    /// test would go on passing against code that had stopped sorting by age at all.
    /// </remarks>
    [Fact]
    public void Age_governs_within_a_kind_two_permissions_sort_oldest_first()
    {
        var bands = AttentionEngine.Order(
        [
            Aged("a-newer-3m", SessionState.NeedsPermission, 3),
            Aged("z-older-12m", SessionState.NeedsPermission, 12),
        ]);

        Assert.Equal(["z-older-12m", "a-newer-3m"], FlatIds(bands));
    }

    [Fact]
    public void Age_governs_within_a_kind_for_errors_and_questions_too()
    {
        Assert.Equal(
            ["z-older-9m", "a-newer-2m"],
            FlatIds(AttentionEngine.Order(
            [
                Aged("a-newer-2m", SessionState.Error, 2),
                Aged("z-older-9m", SessionState.Error, 9),
            ])));

        Assert.Equal(
            ["z-older-30m", "a-newer-4m"],
            FlatIds(AttentionEngine.Order(
            [
                Aged("a-newer-4m", SessionState.NeedsQuestion, 4),
                Aged("z-older-30m", SessionState.NeedsQuestion, 30),
            ])));
    }

    /// <summary>The worked example from TS §IV.2, end to end.</summary>
    [Fact]
    public void The_needs_you_band_orders_by_kind_then_age()
    {
        var bands = AttentionEngine.Order(
        [
            Aged("api-gateway", SessionState.NeedsQuestion, 12),
            Aged("web-ui", SessionState.NeedsPermission, 3),
            Aged("reports", SessionState.NeedsQuestion, 20),
            Aged("docs-site", SessionState.NeedsPermission, 12),
            Aged("billing", SessionState.Error, 8),
        ]);

        Assert.Equal(
            ["docs-site", "web-ui", "billing", "reports", "api-gateway"],
            FlatIds(bands));
    }

    // ---- The other bands ------------------------------------------------------------------------

    /// <summary>
    /// The asymmetry TS calls the heart of the model: reds oldest-first, greens newest-first.
    /// The freshest finish is the one being chased after a beep.
    /// </summary>
    [Fact]
    public void The_unread_band_is_newest_first()
    {
        var bands = AttentionEngine.Order(
        [
            Aged("unread-20m", SessionState.Unread, 20),
            Aged("unread-2m", SessionState.Unread, 2),
            Aged("unread-9m", SessionState.Unread, 9),
        ]);

        Assert.Equal(["unread-2m", "unread-9m", "unread-20m"], FlatIds(bands));
    }

    /// <summary>Directly against the Needs-You rule, so neither can be changed without the other failing.</summary>
    [Fact]
    public void Unread_and_needs_you_sort_in_opposite_directions()
    {
        var unread = FlatIds(AttentionEngine.Order(
            [Aged("old", SessionState.Unread, 20), Aged("new", SessionState.Unread, 1)]));
        var needsYou = FlatIds(AttentionEngine.Order(
            [Aged("old", SessionState.NeedsQuestion, 20), Aged("new", SessionState.NeedsQuestion, 1)]));

        Assert.Equal(["new", "old"], unread);
        Assert.Equal(["old", "new"], needsYou);
    }

    /// <summary>Ids run against the expected order, so only the activity comparison can produce it.</summary>
    [Fact]
    public void Working_orders_by_most_recent_activity()
    {
        var bands = AttentionEngine.Order(
        [
            Aged("a-stale", SessionState.Working, 30, idleMinutes: 30),
            Aged("z-busy", SessionState.Working, 30, idleMinutes: 1),
            Aged("m-middling", SessionState.Working, 30, idleMinutes: 10),
        ]);

        Assert.Equal(["z-busy", "m-middling", "a-stale"], FlatIds(bands));
    }

    [Fact]
    public void Quiet_orders_by_most_recent_activity()
    {
        var bands = AttentionEngine.Order(
        [
            Aged("a-older", SessionState.Acked, 30, idleMinutes: 30),
            Aged("z-newer", SessionState.Acked, 30, idleMinutes: 2),
        ]);

        Assert.Equal(["z-newer", "a-older"], FlatIds(bands));
    }

    [Fact]
    public void Ended_orders_by_recency()
    {
        var bands = AttentionEngine.Order(
        [
            Aged("a-long-gone", SessionState.Ended, 60, idleMinutes: 60),
            Aged("z-just-gone", SessionState.Ended, 60, idleMinutes: 1),
        ]);

        Assert.Equal(["z-just-gone", "a-long-gone"], FlatIds(bands));
    }

    // ---- Band precedence -------------------------------------------------------------------------

    [Fact]
    public void Bands_come_back_in_TS_IV_2_order()
    {
        var bands = AttentionEngine.Order(
        [
            Aged("e", SessionState.Ended, 1),
            Aged("q", SessionState.Acked, 1),
            Aged("w", SessionState.Working, 1),
            Aged("u", SessionState.Unread, 1),
            Aged("n", SessionState.NeedsQuestion, 1),
        ]);

        Assert.Equal(
            [
                AttentionBand.NeedsYou, AttentionBand.Unread, AttentionBand.Working,
                AttentionBand.Quiet, AttentionBand.Ended,
            ],
            bands.Select(b => b.Band));
        Assert.Equal(["n", "u", "w", "q", "e"], FlatIds(bands));
    }

    /// <summary>
    /// Band precedence beats everything within a band: the least urgent Needs-You session still
    /// outranks the most urgent Unread one.
    /// </summary>
    [Fact]
    public void A_band_always_outranks_the_band_below_it()
    {
        var bands = AttentionEngine.Order(
        [
            Aged("unread-fresh", SessionState.Unread, 0.1),
            Aged("question-ancient", SessionState.NeedsQuestion, 600),
        ]);

        Assert.Equal(["question-ancient", "unread-fresh"], FlatIds(bands));
    }

    [Fact]
    public void The_three_needs_you_states_share_one_band()
    {
        var bands = AttentionEngine.Order(
        [
            Aged("p", SessionState.NeedsPermission, 1),
            Aged("e", SessionState.Error, 1),
            Aged("q", SessionState.NeedsQuestion, 1),
        ]);

        var band = Assert.Single(bands);
        Assert.Equal(AttentionBand.NeedsYou, band.Band);
        Assert.Equal(["p", "e", "q"], Ids(band.Sessions));
    }

    [Fact]
    public void An_empty_band_is_omitted_rather_than_returned_empty()
    {
        var bands = AttentionEngine.Order([Aged("w", SessionState.Working, 1)]);

        var band = Assert.Single(bands);
        Assert.Equal(AttentionBand.Working, band.Band);
        Assert.All(bands, b => Assert.NotEmpty(b.Sessions));
    }

    [Fact]
    public void No_sessions_produce_no_bands()
    {
        Assert.Empty(AttentionEngine.Order([]));
    }

    // ---- Determinism ------------------------------------------------------------------------------

    /// <summary>
    /// The ordering is total: two sessions alike in everything the band sorts by still order
    /// decisively, by id. Nothing is left to whatever an unstable sort happens to do.
    /// </summary>
    [Fact]
    public void Sessions_alike_in_every_sort_key_still_order_decisively()
    {
        Session[] sessions =
        [
            Aged("s-2", SessionState.Working, 5),
            Aged("s-1", SessionState.Working, 5),
            Aged("s-3", SessionState.Working, 5),
        ];

        Assert.Equal(["s-1", "s-2", "s-3"], FlatIds(AttentionEngine.Order(sessions)));
        Assert.Equal(["s-1", "s-2", "s-3"], FlatIds(AttentionEngine.Order(sessions.Reverse())));
    }

    [Fact]
    public void The_result_does_not_depend_on_input_order()
    {
        Session[] sessions =
        [
            Aged("a", SessionState.NeedsQuestion, 20),
            Aged("b", SessionState.NeedsPermission, 3),
            Aged("c", SessionState.Unread, 5),
            Aged("d", SessionState.Working, 5),
            Aged("e", SessionState.Error, 8),
        ];

        Assert.Equal(AttentionEngine.Order(sessions), AttentionEngine.Order(sessions.Reverse()));
    }

    /// <summary>The churn-free property T1.4 established, preserved through banding.</summary>
    [Fact]
    public void An_unchanged_result_compares_equal_to_the_one_it_replaces()
    {
        Session[] sessions = [Aged("a", SessionState.Unread, 5), Aged("b", SessionState.Working, 5)];

        Assert.Equal(AttentionEngine.Order(sessions), AttentionEngine.Order(sessions));
        Assert.NotEqual(
            AttentionEngine.Order(sessions),
            AttentionEngine.Order([Aged("a", SessionState.Error, 5), Aged("b", SessionState.Working, 5)]));
    }

    [Fact]
    public void Order_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => AttentionEngine.Order(null!));
        Assert.Throws<ArgumentException>(() => AttentionEngine.Order([Aged("a", SessionState.Working, 1), null!]));
    }

    // ---- The grouped view ---------------------------------------------------------------------------

    private static Group GroupOf(string cwd, params Session[] members) =>
        new(GroupKeys.ForWorkspace(cwd), members);

    /// <summary>
    /// TS §IV.2's "this ordering runs within each group" includes band precedence, not only
    /// the within-band rules: a group holding an ended session and a blocked one must list the
    /// blocked one first.
    /// </summary>
    /// <remarks>
    /// This is the only test of band precedence that reaches <c>Compare</c>'s band branch.
    /// The flat view never depends on it — <see cref="AttentionEngine.Order"/> re-bands the
    /// sorted sequence and orders the bands itself — so reversing that branch changes nothing
    /// there, while silently inverting every group's member list.
    ///
    /// The ids run against the answer: <c>a-ended</c> precedes <c>b-needs</c> ordinally, so the
    /// final id tie-break would produce the wrong order and only band precedence can produce
    /// the right one.
    /// </remarks>
    [Fact]
    public void Band_precedence_holds_within_a_groups_member_list()
    {
        var ordered = AttentionEngine.OrderGroups(
        [
            GroupOf(
                Dashboard,
                Aged("a-ended", SessionState.Ended, 1),
                Aged("b-needs", SessionState.NeedsPermission, 1)),
        ]).Single();

        Assert.Equal(
            [SessionState.NeedsPermission, SessionState.Ended],
            ordered.Members.Select(m => m.State));
    }

    /// <summary>TS §IV.2: in grouped view the ordering runs within each group.</summary>
    [Fact]
    public void Ordering_runs_within_each_group()
    {
        var groups = AttentionEngine.OrderGroups(
        [
            GroupOf(
                Dashboard,
                Aged("question-20m", SessionState.NeedsQuestion, 20, Dashboard),
                Aged("permission-3m", SessionState.NeedsPermission, 3, Dashboard)),
        ]);

        Assert.Equal(["permission-3m", "question-20m"], Ids(Assert.Single(groups).Members));
    }

    /// <summary>
    /// TS §IV.2: groups are ordered by their most urgent member, so active groups float up.
    /// The keys run against the expected order, so the group-key tie-break cannot produce it.
    /// </summary>
    [Fact]
    public void Groups_are_ordered_by_their_most_urgent_member()
    {
        var groups = AttentionEngine.OrderGroups(
        [
            GroupOf(@"C:\a-quiet", Aged("q", SessionState.Acked, 1, @"C:\a-quiet")),
            GroupOf(@"C:\z-blocked", Aged("p", SessionState.NeedsPermission, 1, @"C:\z-blocked")),
            GroupOf(@"C:\m-done", Aged("u", SessionState.Unread, 1, @"C:\m-done")),
        ]);

        Assert.Equal(
            [SessionState.NeedsPermission, SessionState.Unread, SessionState.Acked],
            groups.Select(g => g.WorstState));
    }

    /// <summary>A group's urgency is its worst member's, not its average or its first member's.</summary>
    [Fact]
    public void A_single_urgent_member_lifts_its_whole_group()
    {
        var groups = AttentionEngine.OrderGroups(
        [
            GroupOf(@"C:\busy", Aged("w1", SessionState.Working, 1, @"C:\busy"), Aged("w2", SessionState.Working, 1, @"C:\busy")),
            GroupOf(
                @"C:\mixed",
                Aged("m1", SessionState.Acked, 1, @"C:\mixed"),
                Aged("m2", SessionState.Error, 1, @"C:\mixed")),
        ]);

        Assert.Equal(GroupKeys.ForWorkspace(@"C:\mixed"), groups[0].Key);
    }

    /// <summary>
    /// TS §IV.2 names latest activity as the tie-break between equally urgent groups. The keys
    /// run against the expected order, so the key tie-break below it cannot produce the result.
    /// </summary>
    [Fact]
    public void Equally_urgent_groups_break_the_tie_on_latest_activity()
    {
        var groups = AttentionEngine.OrderGroups(
        [
            GroupOf(@"C:\a-stale", Aged("s", SessionState.Working, 5, @"C:\a-stale", idleMinutes: 30)),
            GroupOf(@"C:\z-fresh", Aged("f", SessionState.Working, 5, @"C:\z-fresh", idleMinutes: 1)),
        ]);

        Assert.Equal(
            [GroupKeys.ForWorkspace(@"C:\z-fresh"), GroupKeys.ForWorkspace(@"C:\a-stale")],
            groups.Select(g => g.Key));
    }

    [Fact]
    public void Grouped_ordering_is_deterministic()
    {
        Group[] groups =
        [
            GroupOf(@"C:\a", Aged("a", SessionState.Working, 5, @"C:\a", idleMinutes: 5)),
            GroupOf(@"C:\b", Aged("b", SessionState.Working, 5, @"C:\b", idleMinutes: 5)),
            GroupOf(@"C:\c", Aged("c", SessionState.Working, 5, @"C:\c", idleMinutes: 5)),
        ];

        Assert.Equal(AttentionEngine.OrderGroups(groups), AttentionEngine.OrderGroups(groups.Reverse()));
    }

    [Fact]
    public void OrderGroups_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => AttentionEngine.OrderGroups(null!));
        Assert.Throws<ArgumentException>(() =>
            AttentionEngine.OrderGroups([GroupOf(Dashboard, Aged("a", SessionState.Working, 1)), null!]));
    }

    [Fact]
    public void No_groups_produce_no_groups()
    {
        Assert.Empty(AttentionEngine.OrderGroups([]));
    }
}
