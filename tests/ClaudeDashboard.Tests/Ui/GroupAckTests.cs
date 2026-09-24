using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The orchestration's one Ack: a roster group acknowledged at its header, its members carrying
/// none, and a working-directory group untouched (T1.36, issue #47).
/// </summary>
/// <remarks>
/// <para>
/// Every roster assertion here has a cwd twin, because the risk this task carries is not that
/// the roster gains an Ack — it is that the working-directory groups quietly lose theirs. A cwd
/// group is a filing convenience: several unrelated sessions in one repository must keep their
/// own Acks, or one of them finishing hides behind another still working.
/// </para>
/// <para>
/// The same harness shape as <c>AckTests</c>: a real <see cref="AckPublisher"/> over a recording
/// sink, a real Registry, and rosters seeded through the real <see cref="RosterStore"/> — so
/// what is asserted is what the pipeline produces, not a hand-built fiction.
/// </para>
/// </remarks>
public sealed class GroupAckTests : IDisposable
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly RegistryHarness _harness = new();
    private readonly RecordingEventSink _sink = new();
    private readonly FakeClock _clock = new();
    private readonly RosterStore _rosters;
    private readonly MainViewModel _viewModel;

    public GroupAckTests()
    {
        _rosters = new RosterStore(new RecordingEventSink());
        _viewModel = new MainViewModel(
            _harness.Projection,
            new MotionPolicy(() => false, observeChanges: false),
            new AckPublisher(_sink, _clock, Logger.None),
            new FakeClipboard(),
            _rosters,
            new RecordingRosterPersistence());
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        _harness.Dispose();
    }

    private void FormRoster(string name, params string[] titles)
    {
        _rosters.Replace(RosterBook.From([(name, titles)]));
        _viewModel.Refresh();
    }

    private GroupViewModel Header(string label) =>
        _viewModel.Rows.OfType<GroupViewModel>().Single(group => group.Label == label);

    private SessionViewModel Row(string id) =>
        _viewModel.Rows.OfType<SessionViewModel>().Single(row => row.Id.Value == id);

    private IReadOnlyList<Ack> Acks => [.. _sink.Published.OfType<Ack>()];

    /// <summary>An Unread session carrying <paramref name="title"/>.</summary>
    private void Finished(string id, string title) =>
        _harness.Finished(id, At.AddMinutes(1), _harness.Working(id, At, title: title), title: title);

    // ---- The header's Ack ---------------------------------------------------------------------

    /// <summary>
    /// A roster header offers the Ack when a member waits; a cwd header never does, whatever its
    /// members' states.
    /// </summary>
    [Fact]
    public void A_roster_header_offers_the_ack_and_a_cwd_header_never_does()
    {
        Finished("r-1", "alpha");
        _harness.Working("r-2", At, title: "beta");
        FormRoster("orchestration", "alpha", "beta");

        // The cwd twin, in the same window: an unread session in an ordinary workspace group.
        Finished("w-1", "gamma");

        Assert.True(Header("orchestration").CanAcknowledge);
        Assert.True(Header("orchestration").CanRaiseAck);

        var cwd = _viewModel.Rows.OfType<GroupViewModel>().Single(group => group.Label != "orchestration");

        Assert.NotEqual(GroupKeyKind.Roster, cwd.Kind);
        Assert.False(cwd.CanAcknowledge);
        Assert.False(cwd.AcknowledgeCommand.CanExecute(null));
    }

    /// <summary>With every member quiet, the roster header offers nothing.</summary>
    [Fact]
    public void A_quiet_roster_offers_nothing()
    {
        _harness.Working("r-1", At, title: "alpha");
        _harness.Working("r-2", At, title: "beta");
        FormRoster("orchestration", "alpha", "beta");

        Assert.False(Header("orchestration").CanAcknowledge);
    }

    /// <summary>
    /// <strong>The flag reads the members, not the settled display state.</strong>
    /// </summary>
    /// <remarks>
    /// The distinguisher that makes "any member's state" load-bearing rather than a phrasing:
    /// while the roster settle window is open, <see cref="RosterSettle.StateOf"/> — and so the
    /// header's <see cref="GroupViewModel.WorstState"/> — reads Working even though a member is
    /// already Unread. The clock here has never ticked, so the settle is still pending, the
    /// display says Working, and the Ack must be offered anyway: the member is eligible the
    /// moment it is eligible, and a flag read off the display would hide the button for exactly
    /// the settle window.
    /// </remarks>
    [Fact]
    public void The_flag_reads_members_not_the_settled_display_state()
    {
        Finished("r-1", "alpha");
        _harness.Working("r-2", At, title: "beta");
        FormRoster("orchestration", "alpha", "beta");

        var header = Header("orchestration");

        Assert.Equal(SessionState.Working, header.WorstState);
        Assert.False(Acknowledgment.Applies(header.WorstState));
        Assert.True(header.CanAcknowledge);
    }

    // ---- The click ----------------------------------------------------------------------------

    /// <summary>
    /// The click publishes exactly the eligible members, once each — and nothing for a waiting
    /// session OUTSIDE the roster, which is the scoping half of the claim.
    /// </summary>
    [Fact]
    public void The_click_acks_exactly_the_eligible_members()
    {
        Finished("r-unread", "alpha");
        _harness.Failed("r-err", At.AddMinutes(1), _harness.Working("r-err", At, title: "beta"));
        _harness.Working("r-busy", At, title: "gamma");
        var donePrompt = _harness.Working("r-done", At, title: "delta");
        _harness.Finished("r-done", At.AddSeconds(30), donePrompt, title: "delta");
        _harness.Acked("r-done", At.AddMinutes(1));
        FormRoster("orchestration", "alpha", "beta", "gamma", "delta");

        // Eligible, and deliberately not a member: the group's Ack must not reach it.
        Finished("outside", "epsilon");

        Header("orchestration").AcknowledgeCommand.Execute(null);

        Assert.Equal(
            new HashSet<SessionId> { new("r-unread"), new("r-err") },
            Acks.Select(ack => ack.SessionId).ToHashSet());
        Assert.Equal(2, Acks.Count);
        Assert.All(Acks, ack => Assert.Equal(AckSource.Manual, ack.Source));
    }

    /// <summary>Nothing at all when no member is eligible — through Execute, which does not gate.</summary>
    [Fact]
    public void The_click_publishes_nothing_when_no_member_waits()
    {
        _harness.Working("r-1", At, title: "alpha");
        FormRoster("orchestration", "alpha");

        Header("orchestration").AcknowledgeCommand.Execute(null);

        Assert.Empty(_sink.Published);
    }

    /// <summary>
    /// The round trip: after the Registry applies the acks, the header offers nothing and the
    /// group's display state reads Acked.
    /// </summary>
    /// <remarks>
    /// The clock is advanced before the click for T1.34's recorded reason: the publisher stamps
    /// the clock's now, and un-advanced it stamps BEFORE the members' transitions, which the
    /// Registry's timestamp guard rightly declines.
    /// </remarks>
    [Fact]
    public void The_round_trip_clears_the_header_and_the_group_reads_acked()
    {
        Finished("r-1", "alpha");
        Finished("r-2", "beta");
        FormRoster("orchestration", "alpha", "beta");

        _clock.Advance(TimeSpan.FromMinutes(5));

        Header("orchestration").AcknowledgeCommand.Execute(null);

        foreach (var ack in Acks.ToList())
        {
            _harness.Apply(ack);
        }

        Assert.False(Header("orchestration").CanAcknowledge);
        Assert.Equal(SessionState.Acked, Header("orchestration").WorstState);
    }

    // ---- What is unchanged --------------------------------------------------------------------

    /// <summary>Ack all still counts roster members as eligible sessions.</summary>
    [Fact]
    public void Ack_all_still_counts_roster_members()
    {
        Finished("r-1", "alpha");
        FormRoster("orchestration", "alpha");
        Finished("outside", "epsilon");

        Assert.True(_viewModel.AnythingToAcknowledge);

        _viewModel.AckAllCommand.Execute(null);

        Assert.Equal(
            new HashSet<SessionId> { new("r-1"), new("outside") },
            Acks.Select(ack => ack.SessionId).ToHashSet());
    }

    /// <summary>
    /// Tier 1 is untouched: a prompt submitted in a member session still clears that member on
    /// its own.
    /// </summary>
    [Fact]
    public void A_prompt_in_a_member_session_still_clears_it()
    {
        Finished("r-1", "alpha");
        FormRoster("orchestration", "alpha");

        Assert.True(Header("orchestration").CanAcknowledge);

        _harness.Working("r-1", At.AddMinutes(2), title: "alpha");

        Assert.Equal(SessionState.Working, Row("r-1").State);
        Assert.False(Header("orchestration").CanAcknowledge);
    }

    // ---- The member rows ----------------------------------------------------------------------

    /// <summary>
    /// A roster member shows no Ack of its own; the same session in a cwd group, and in Flat,
    /// does — and a Refresh that moves it between the three retells it.
    /// </summary>
    [Fact]
    public void A_member_hides_its_own_ack_and_gets_it_back_outside_the_roster()
    {
        Finished("s-1", "alpha");
        FormRoster("orchestration", "alpha");

        Assert.True(Row("s-1").CanAcknowledge);
        Assert.True(Row("s-1").IsRosterMember);
        Assert.False(Row("s-1").ShowsOwnAck);

        // Flat view: no rosters, its own Ack again.
        _viewModel.IsGrouped = false;
        Assert.False(Row("s-1").IsRosterMember);
        Assert.True(Row("s-1").ShowsOwnAck);

        // Back to Grouped, roster dissolved: a cwd group member keeps its own.
        _viewModel.IsGrouped = true;
        _rosters.Replace(RosterBook.From([]));
        _viewModel.Refresh();

        Assert.False(Row("s-1").IsRosterMember);
        Assert.True(Row("s-1").ShowsOwnAck);
    }
}
