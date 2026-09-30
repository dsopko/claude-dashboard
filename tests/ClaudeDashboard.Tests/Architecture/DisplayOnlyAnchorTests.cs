using System.IO;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Ui;

namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// Only the row's clock reads the finish (T1.47, issue #59). The sort order, the nudge ladder and
/// the roster settle still read <see cref="Session.EnteredAt"/> — the line T1.40 drew.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a scan, and not only behaviour.</strong> For the sort and the nudge ladder the new
/// anchor cannot be seen from outside. They read <see cref="Session.EnteredAt"/> in NeedsYou and
/// Unread, where the row's clock is also <see cref="Session.EnteredAt"/> or the same instant as the
/// finish; and they sort Quiet and Ended by <see cref="Session.LastActivity"/>. A move to the finish
/// there would pass every behaviour test while quietly changing what the code depends on. So the
/// three files are held to reading <see cref="Session.EnteredAt"/> and never
/// <see cref="Exchange.AnsweredAt"/>.
/// </para>
/// <para>
/// <strong>The settle can be seen, so it is held both ways.</strong> An acknowledged member of a
/// roster group finished long before its ack, and the group's quiet-since instant must be the ack:
/// that is the last thing that happened to the group.
/// </para>
/// </remarks>
public sealed class DisplayOnlyAnchorTests
{
    [Theory]
    [InlineData("AttentionEngine.cs")]
    [InlineData("SoundPolicyEngine.cs")]
    [InlineData("RosterSettle.cs")]
    public void The_sort_the_nudge_ladder_and_the_settle_read_the_time_in_state_and_never_the_finish(string file)
    {
        var code = GuardScan.CodeOnly(File.ReadAllText(
            Path.Combine(RepoLayout.Root.FullName, "src", "ClaudeDashboard.Core", file)));

        Assert.Contains("EnteredAt", code, StringComparison.Ordinal);
        Assert.DoesNotContain("AnsweredAt", code, StringComparison.Ordinal);
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
        Assert.Equal(at.AddMinutes(1), early.Latest.AnsweredAt);

        var group = new Group(new GroupKey("roster:pair"), members);

        Assert.Equal(at.AddMinutes(30), RosterSettle.QuietSince(group));
    }
}
