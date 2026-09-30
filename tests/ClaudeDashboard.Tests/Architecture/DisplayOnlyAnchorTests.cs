using System.IO;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Ui;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// Only the row's clock reads <see cref="Session.ClockAnchor"/> (T1.47, issue #59, and the rulings of
/// 2026-09-29). The sort order, the nudge ladder and the roster settle still read
/// <see cref="Session.EnteredAt"/> — the line T1.40 drew.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a scan, and not only behaviour.</strong> A move of the sort or the nudge ladder to the
/// row's anchor is not always visible, and where it is visible no behaviour test here builds the
/// path. In Unread the anchor and <see cref="Session.EnteredAt"/> are one instant, so a move there
/// cannot be seen. In NeedsYou it can: a session reached from Waiting or Unread keeps an answered
/// <see cref="Session.Latest"/>, so its <see cref="Exchange.AnsweredAt"/> is earlier than its
/// <see cref="Session.EnteredAt"/>, and a sort or a permission nudge moved to the answer would change
/// what the operator sees and hears. The first review planted exactly that (Pd2, Pe2), and only this
/// scan caught it. So the three files are held to <see cref="Session.EnteredAt"/>, and may name
/// neither the answer, nor the row's anchor, nor <see cref="Session.LastHeardAt"/> — which the sweep
/// reads for itself in <c>SessionRegistry</c>, and which reaches the row only through the anchor.
/// </para>
/// <para>
/// <strong>The settle is held by behaviour too</strong>, because there the difference is common: an
/// acknowledged member of a roster group finished long before its ack, and the group's quiet-since
/// instant must be the ack — the last thing that happened to the group.
/// </para>
/// </remarks>
public sealed class DisplayOnlyAnchorTests
{
    [Theory]
    [InlineData("AttentionEngine.cs")]
    [InlineData("SoundPolicyEngine.cs")]
    [InlineData("RosterSettle.cs")]
    public void The_sort_the_nudge_ladder_and_the_settle_read_the_time_in_state_and_never_the_rows_anchor(string file)
    {
        var code = GuardScan.CodeOnly(File.ReadAllText(
            Path.Combine(RepoLayout.Root.FullName, "src", "ClaudeDashboard.Core", file)));

        Assert.Contains("EnteredAt", code, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "AnsweredAt", "ClockAnchor", "LastHeardAt" })
        {
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_roster_group_is_quiet_since_its_last_ack_not_its_last_finish()
    {
        var at = FakeClock.DefaultStart;
        using var harness = new RegistryHarness();

        harness.Finished("early", at.AddMinutes(1), harness.Working("early", at));
        harness.Finished("late", at.AddMinutes(10), harness.Working("late", at));
        harness.Acked("early", at.AddMinutes(30));

        var members = harness.Registry.Sessions.Values.ToList();
        var early = members.Single(member => member.Id.Value == "early");

        Assert.Equal(SessionState.Acked, early.State);
        Assert.Equal(at.AddMinutes(1), early.ClockAnchor);

        var group = new Group(new GroupKey("roster:pair"), members);

        Assert.Equal(at.AddMinutes(30), RosterSettle.QuietSince(group));
    }
}
