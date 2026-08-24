using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// The ratified severity order (TS §IV.2, §IV.3) — the single ranking both the attention
/// engine and <see cref="Group.WorstState"/> consume.
/// </summary>
public sealed class AttentionOrderTests
{
    private static readonly SessionState[] MostToLeastUrgent =
    [
        SessionState.NeedsPermission,
        SessionState.Error,
        SessionState.NeedsQuestion,
        SessionState.Unread,
        SessionState.Working,
        SessionState.Acked,
        SessionState.Ended,
    ];

    /// <summary>
    /// Permission &gt; Error &gt; Question &gt; Unread &gt; Working &gt; Quiet &gt; Ended,
    /// as ratified by the operator on 2026-08-24. The two corrections it embodies: Error now
    /// outranks Question, and the two Needs-You states are no longer tied.
    /// </summary>
    [Fact]
    public void Ranks_the_ratified_order_strictly()
    {
        var ranks = MostToLeastUrgent.Select(AttentionOrder.Rank).ToList();

        Assert.Equal(ranks.OrderByDescending(r => r), ranks);
        Assert.Equal(MostToLeastUrgent.Length, ranks.Distinct().Count());
    }

    [Fact]
    public void Error_outranks_a_question()
    {
        Assert.True(AttentionOrder.Rank(SessionState.Error) > AttentionOrder.Rank(SessionState.NeedsQuestion));
    }

    [Fact]
    public void A_permission_outranks_an_error()
    {
        Assert.True(AttentionOrder.Rank(SessionState.NeedsPermission) > AttentionOrder.Rank(SessionState.Error));
    }

    /// <summary>The two Needs-You states are distinct ranks, not one rank shared.</summary>
    [Fact]
    public void A_permission_and_a_question_do_not_share_a_rank()
    {
        Assert.NotEqual(
            AttentionOrder.Rank(SessionState.NeedsPermission),
            AttentionOrder.Rank(SessionState.NeedsQuestion));
    }

    [Theory]
    [InlineData(SessionState.NeedsPermission, AttentionBand.NeedsYou)]
    [InlineData(SessionState.Error, AttentionBand.NeedsYou)]
    [InlineData(SessionState.NeedsQuestion, AttentionBand.NeedsYou)]
    [InlineData(SessionState.Unread, AttentionBand.Unread)]
    [InlineData(SessionState.Working, AttentionBand.Working)]
    [InlineData(SessionState.Acked, AttentionBand.Quiet)]
    [InlineData(SessionState.Ended, AttentionBand.Ended)]
    public void Bands_each_state_per_TS_IV_2(SessionState state, AttentionBand expected)
    {
        Assert.Equal(expected, AttentionOrder.BandOf(state));
    }

    /// <summary>
    /// The band mapping is a coarsening of the rank, not a second ordering: a more urgent state
    /// must never land in a less urgent band. Pinned rather than derived, so the two cannot
    /// drift apart the way TS §IV.2 and §IV.3 once did.
    /// </summary>
    [Fact]
    public void Banding_never_contradicts_ranking()
    {
        foreach (var more in MostToLeastUrgent)
        {
            foreach (var less in MostToLeastUrgent)
            {
                if (AttentionOrder.Rank(more) > AttentionOrder.Rank(less))
                {
                    Assert.True(
                        AttentionOrder.BandOf(more) >= AttentionOrder.BandOf(less),
                        $"{more} outranks {less} but bands below it.");
                }
            }
        }
    }

    /// <summary>Every declared state is ranked and banded; none falls through to a default.</summary>
    [Fact]
    public void Every_state_is_accounted_for()
    {
        Assert.Equal(
            Enum.GetValues<SessionState>().Order().ToList(),
            MostToLeastUrgent.Order().ToList());
    }
}
