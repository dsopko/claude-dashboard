using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// Space, staleness and overflow (Design Document §6), in the order §6 puts them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every rule here is one an over-eager implementation satisfies.</strong> "The quiet
/// group collapsed" passes if everything collapses; "the unread row is present" passes if nothing
/// ever collapses. So each test says what must still be there, or what must not have gone.
/// </para>
/// <para>
/// The clock is supplied rather than read: staleness advances only when something drives it, for
/// the same reason ages do (T1.9 owns the only periodic loop).
/// </para>
/// </remarks>
public sealed class CollapseTests : IDisposable
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;
    private const string Other = @"C:\dev\ClaudeBeeps";

    private readonly RegistryHarness _harness = new();
    private readonly MainViewModel _viewModel;

    public CollapseTests()
    {
        _viewModel = new MainViewModel(
            _harness.Projection,
            new MotionPolicy(() => false, observeChanges: false),
            new StubAckPublisher(),
            new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), new UsageBoard());
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        _harness.Dispose();
    }

    private IReadOnlyList<SessionViewModel> SessionRows => [.. _viewModel.Rows.OfType<SessionViewModel>()];

    private IReadOnlyList<string> Ids => [.. SessionRows.Select(row => row.Id.Value)];

    private GroupViewModel Group(string workspace) =>
        _viewModel.Rows.OfType<GroupViewModel>().Single(header => header.Workspace == workspace);

    // ---- Rule 1: a stale group costs one row -------------------------------------------------

    [Fact]
    public void A_group_quiet_for_long_enough_collapses_to_one_line()
    {
        _harness.Quiet("s-1", At);
        _harness.Quiet("s-2", At);

        _viewModel.Tick(At + MainViewModel.DefaultStaleAfter);

        var header = Assert.Single(_viewModel.Rows.OfType<GroupViewModel>());
        Assert.True(header.IsStale);
        Assert.Equal(2, header.SessionCount);
        Assert.Equal("idle 15 min", header.IdleText);

        // One row, and it is the heading: no member rows, and no "+ 2 quiet" footer either.
        Assert.Same(header, Assert.Single(_viewModel.Rows));
    }

    /// <summary>
    /// The same group a minute earlier is not stale. Without this, "it collapsed" would pass for
    /// an implementation that collapses every quiet group the moment it goes quiet.
    /// </summary>
    [Fact]
    public void A_group_quiet_for_less_than_that_does_not_collapse()
    {
        _harness.Quiet("s-1", At);
        _harness.Quiet("s-2", At);

        _viewModel.Tick(At + MainViewModel.DefaultStaleAfter - TimeSpan.FromMinutes(1));

        var header = Assert.Single(_viewModel.Rows.OfType<GroupViewModel>());
        Assert.False(header.IsStale);
        Assert.Single(_viewModel.Rows.OfType<QuietFooterViewModel>());
    }

    /// <summary>
    /// Idle time is measured from the group's last activity, so a group with anything live in it
    /// never goes stale however long ago it started.
    /// </summary>
    [Fact]
    public void A_group_with_live_work_never_goes_stale()
    {
        _harness.Quiet("quiet-one", At);
        _harness.Working("busy", At);

        _viewModel.Tick(At + TimeSpan.FromHours(3));

        var header = Assert.Single(_viewModel.Rows.OfType<GroupViewModel>());
        Assert.False(header.IsStale);
        Assert.Equal(["busy"], Ids);
    }

    /// <summary>Design Document §6 rule 1: a stale group "never pushes active work down".</summary>
    [Fact]
    public void A_stale_group_sinks_below_the_active_one()
    {
        _harness.Quiet("stale", At, Other);
        _harness.Working("live", At.AddMinutes(20));

        _viewModel.Tick(At + TimeSpan.FromMinutes(30));

        var stale = Group(Other);
        var live = Group(RegistryHarness.Workspace);

        Assert.True(stale.IsStale);
        Assert.False(live.IsStale);
        Assert.True(
            _viewModel.Rows.IndexOf(live) < _viewModel.Rows.IndexOf(stale),
            "the stale group must sit below the group with live work");

        // …and below its rows, not merely below its heading.
        Assert.True(_viewModel.Rows.IndexOf(SessionRows.Single()) < _viewModel.Rows.IndexOf(stale));
    }

    [Fact]
    public void A_stale_group_opens_when_asked()
    {
        _harness.Quiet("s-1", At);
        _harness.Quiet("s-2", At);
        _viewModel.Tick(At + TimeSpan.FromMinutes(38));

        Group(RegistryHarness.Workspace).IsExpanded = true;

        Assert.Equal(["s-1", "s-2"], Ids.Order(StringComparer.Ordinal));
        Assert.Empty(_viewModel.Rows.OfType<QuietFooterViewModel>());
    }

    // ---- Rule 2: acked rows collapse inside their group ----------------------------------------

    [Fact]
    public void Acked_rows_collapse_to_a_footer_and_the_live_ones_stay()
    {
        _harness.Working("busy", At);
        _harness.Quiet("seen-1", At);
        _harness.Quiet("seen-2", At);
        _harness.Quiet("seen-3", At);

        var footer = Assert.Single(_viewModel.Rows.OfType<QuietFooterViewModel>());

        Assert.Equal("+ 3 quiet", footer.Text);
        Assert.Equal(["busy"], Ids);

        // The footer stands after the rows it summarises, not before them.
        Assert.True(_viewModel.Rows.IndexOf(SessionRows.Single()) < _viewModel.Rows.IndexOf(footer));
    }

    [Fact]
    public void A_group_with_nothing_quiet_gets_no_footer()
    {
        _harness.Working("busy", At);

        Assert.Empty(_viewModel.Rows.OfType<QuietFooterViewModel>());
        Assert.Equal(["busy"], Ids);
    }

    [Fact]
    public void The_footer_opens_the_rows_it_stands_for()
    {
        _harness.Working("busy", At);
        _harness.Quiet("seen", At);

        var footer = Assert.Single(_viewModel.Rows.OfType<QuietFooterViewModel>());
        Assert.Same(Group(RegistryHarness.Workspace), footer.Owner);

        Group(RegistryHarness.Workspace).IsExpanded = true;

        Assert.Equal(["busy", "seen"], Ids.Order(StringComparer.Ordinal));
        Assert.Empty(_viewModel.Rows.OfType<QuietFooterViewModel>());
    }

    /// <summary>An ended session is quiet too — it is a dim line, not a row competing for space.</summary>
    [Fact]
    public void An_ended_session_collapses_with_the_quiet_ones()
    {
        _harness.Working("busy", At);
        _harness.Started("gone", At);
        _harness.Ended("gone", At.AddMinutes(1));

        var footer = Assert.Single(_viewModel.Rows.OfType<QuietFooterViewModel>());

        Assert.Equal("+ 1 quiet", footer.Text);
        Assert.Equal(["busy"], Ids);
    }

    // ---- Rule 3: unread rows are never summarised away ------------------------------------------

    /// <summary>
    /// The rule the whole tool turns on. §6 says rule 3 replaced an earlier idea precisely
    /// because that idea "would hide the thing the tool exists to surface".
    /// </summary>
    [Fact]
    public void An_unread_row_is_never_collapsed()
    {
        var promptId = _harness.Working("finished", At);
        _harness.Finished("finished", At.AddMinutes(1), promptId);
        _harness.Quiet("seen-1", At);
        _harness.Quiet("seen-2", At);

        // Long past every staleness threshold, and with the quiet ones collapsed around it.
        _viewModel.Tick(At + TimeSpan.FromHours(4));

        var row = Assert.Single(SessionRows);
        Assert.Equal(SessionState.Unread, row.State);
        Assert.Equal("+ 2 quiet", Assert.Single(_viewModel.Rows.OfType<QuietFooterViewModel>()).Text);
    }

    /// <summary>
    /// And its group does not go stale either, however long the unread result has sat there —
    /// collapsing the group would hide the row by another route.
    /// </summary>
    [Fact]
    public void A_group_holding_an_unread_result_never_goes_stale()
    {
        var promptId = _harness.Working("finished", At);
        _harness.Finished("finished", At.AddMinutes(1), promptId);
        _harness.Quiet("seen", At);

        _viewModel.Tick(At + TimeSpan.FromHours(4));

        Assert.False(Group(RegistryHarness.Workspace).IsStale);
        Assert.Equal(["finished"], Ids);
    }

    [Theory]
    [InlineData("permission_prompt")]
    [InlineData("agent_needs_input")]
    public void A_blocked_row_is_never_collapsed(string notification)
    {
        _harness.Working("blocked", At);
        _harness.Blocked("blocked", At.AddMinutes(1), notification);
        _harness.Quiet("seen", At);

        _viewModel.Tick(At + TimeSpan.FromHours(4));

        Assert.Equal(["blocked"], Ids);
        Assert.False(Group(RegistryHarness.Workspace).IsStale);
    }

    /// <summary>
    /// A ticked row that goes quiet stays on screen with its mark (issue #46). Before this rule the
    /// row left the view behind the footer and was still counted, so the header said "2 chosen"
    /// with one mark showing, and "Group these" grouped a session the operator could not see.
    /// </summary>
    [Fact]
    public void A_ticked_row_is_never_collapsed()
    {
        TwoTickedFinishedRows();

        // One of them is dealt with while ticked; it is now a quiet row.
        _harness.Acked("a", At.AddMinutes(2));

        Assert.Equal(SessionState.Acked, Row("a").State);
        Assert.Equal(["b", "a"], Ids);
        Assert.True(Row("a").IsSelected);
        Assert.True(Row("b").IsSelected);
        Assert.Equal(2, _viewModel.SelectedCount);
        Assert.True(_viewModel.GroupSelectedCommand.CanExecute(null));
        Assert.Empty(_viewModel.Rows.OfType<QuietFooterViewModel>());
    }

    /// <summary>Unticking the quiet row lets it collapse as it would have.</summary>
    [Fact]
    public void An_unticked_quiet_row_collapses_again()
    {
        TwoTickedFinishedRows();
        _harness.Acked("a", At.AddMinutes(2));

        Row("a").IsSelected = false;

        Assert.Equal(["b"], Ids);
        Assert.Equal("+ 1 quiet", Assert.Single(_viewModel.Rows.OfType<QuietFooterViewModel>()).Text);
        Assert.Equal(1, _viewModel.SelectedCount);
    }

    /// <summary>Leaving the mode drops every tick, so the quiet row collapses then.</summary>
    [Fact]
    public void Leaving_selection_mode_lets_the_quiet_row_collapse()
    {
        TwoTickedFinishedRows();
        _harness.Acked("a", At.AddMinutes(2));

        _viewModel.IsSelecting = false;

        Assert.Equal(["b"], Ids);
        Assert.Equal("+ 1 quiet", Assert.Single(_viewModel.Rows.OfType<QuietFooterViewModel>()).Text);
        Assert.Equal(0, _viewModel.SelectedCount);
    }

    /// <summary>
    /// And a group whose every member is quiet does not go stale while one of them is ticked —
    /// rule 1 would hide the row by another route.
    /// </summary>
    [Fact]
    public void A_group_holding_a_ticked_row_never_goes_stale()
    {
        TwoTickedFinishedRows();
        _harness.Acked("a", At.AddMinutes(2));
        _harness.Acked("b", At.AddMinutes(2));

        _viewModel.Tick(At + TimeSpan.FromHours(4));

        Assert.False(Group(RegistryHarness.Workspace).IsStale);
        Assert.Equal(["a", "b"], Ids.Order());

        _viewModel.IsSelecting = false;

        Assert.True(Group(RegistryHarness.Workspace).IsStale);
        Assert.Empty(SessionRows);
    }

    /// <summary>The same exemption in flat view: the ticked row stands above the band's one line.</summary>
    [Fact]
    public void A_ticked_row_is_never_summarised_in_flat_view()
    {
        _viewModel.IsGrouped = false;
        TwoTickedFinishedRows();
        _harness.Quiet("seen", At);

        _harness.Acked("a", At.AddMinutes(2));

        Assert.Equal(["b", "a"], Ids);
        Assert.True(Row("a").IsSelected);
        Assert.Equal(2, _viewModel.SelectedCount);
        Assert.Equal("1 quiet session", Assert.Single(_viewModel.Rows.OfType<QuietFooterViewModel>()).Text);

        _viewModel.IsSelecting = false;

        Assert.Equal(["b"], Ids);
        Assert.Equal("2 quiet sessions", Assert.Single(_viewModel.Rows.OfType<QuietFooterViewModel>()).Text);
    }

    /// <summary>
    /// Two finished rows, both with a title (a row with no title cannot be ticked), both ticked in
    /// selection mode. "a" is the one the tests then deal with.
    /// </summary>
    private void TwoTickedFinishedRows()
    {
        var a = _harness.Working("a", At, title: "Session A");
        _harness.Finished("a", At.AddMinutes(1), a, title: "Session A");
        var b = _harness.Working("b", At, title: "Session B");
        _harness.Finished("b", At.AddMinutes(1), b, title: "Session B");

        _viewModel.IsSelecting = true;
        Row("a").IsSelected = true;
        Row("b").IsSelected = true;

        Assert.Equal(2, _viewModel.SelectedCount);
    }

    private SessionViewModel Row(string id) => SessionRows.Single(row => row.Id.Value == id);

    // ---- Flat view --------------------------------------------------------------------------------

    /// <summary>The mockups' flat view: the Quiet band is one line, never a list of grey rows.</summary>
    [Fact]
    public void The_quiet_band_is_summarised_in_flat_view()
    {
        _viewModel.IsGrouped = false;
        _harness.Working("busy", At);
        _harness.Quiet("seen-1", At);
        _harness.Quiet("seen-2", At);

        var footer = Assert.Single(_viewModel.Rows.OfType<QuietFooterViewModel>());

        Assert.Equal("2 quiet sessions", footer.Text);
        Assert.Equal(["busy"], Ids);
    }

    /// <summary>
    /// …and the bands that hold work are not summarised, which is what stops "the quiet band
    /// collapsed" from passing for an implementation that collapses every band.
    /// </summary>
    [Fact]
    public void The_bands_that_hold_work_are_never_summarised_in_flat_view()
    {
        _viewModel.IsGrouped = false;
        var promptId = _harness.Working("finished", At);
        _harness.Finished("finished", At.AddMinutes(1), promptId);
        _harness.Working("busy", At);
        _harness.Working("blocked", At);
        _harness.Blocked("blocked", At.AddMinutes(1));

        _viewModel.Tick(At + TimeSpan.FromHours(4));

        Assert.Equal(["blocked", "busy", "finished"], Ids.Order(StringComparer.Ordinal));
        Assert.Empty(_viewModel.Rows.OfType<QuietFooterViewModel>());

        var collapsible = _viewModel.Rows.OfType<BandHeaderViewModel>()
            .Where(header => header.IsCollapsible)
            .ToList();
        Assert.Empty(collapsible);
    }

    [Fact]
    public void The_quiet_band_opens_when_asked()
    {
        _viewModel.IsGrouped = false;
        _harness.Quiet("seen", At);

        var band = _viewModel.Rows.OfType<BandHeaderViewModel>()
            .Single(header => header.Band == AttentionBand.Quiet);
        band.IsExpanded = true;

        Assert.Equal(["seen"], Ids);
        Assert.Empty(_viewModel.Rows.OfType<QuietFooterViewModel>());
    }

    // ---- What collapsing must not cost ---------------------------------------------------------------

    /// <summary>
    /// The counts strip counts sessions, not rows. A collapsed group still reports what is inside
    /// it — otherwise collapsing would quietly change the numbers the operator steers by.
    /// </summary>
    [Fact]
    public void Collapsing_does_not_change_the_counts()
    {
        _harness.Quiet("seen-1", At);
        _harness.Quiet("seen-2", At);
        _harness.Working("busy", At, Other);

        _viewModel.Tick(At + TimeSpan.FromHours(1));

        Assert.True(Group(RegistryHarness.Workspace).IsStale);
        Assert.Equal(2, _viewModel.QuietCount);
        Assert.Equal(1, _viewModel.WorkingCount);
    }

    /// <summary>
    /// A row hidden behind a footer keeps its instance and its age. Rebuilding it on expand would
    /// show the age it had when it was collapsed, and would lose whatever the operator had open.
    /// </summary>
    [Fact]
    public void A_hidden_row_keeps_its_identity_and_its_age()
    {
        _harness.Working("busy", At);
        _harness.Quiet("seen", At);
        var group = Group(RegistryHarness.Workspace);

        group.IsExpanded = true;
        var hidden = SessionRows.Single(row => row.Id.Value == "seen");
        hidden.IsExpanded = true;

        group.IsExpanded = false;
        Assert.DoesNotContain(hidden, SessionRows);

        _viewModel.Tick(At.AddMinutes(9));
        group.IsExpanded = true;

        var reappeared = SessionRows.Single(row => row.Id.Value == "seen");
        Assert.Same(hidden, reappeared);
        Assert.True(reappeared.IsExpanded);
        Assert.Equal(TimeSpan.FromMinutes(9), reappeared.Age);
    }

    /// <summary>Nothing is stale before a tick arrives, so the window opens showing everything.</summary>
    [Fact]
    public void Nothing_is_stale_until_the_clock_has_been_supplied()
    {
        _harness.Quiet("s-1", At);

        Assert.False(Group(RegistryHarness.Workspace).IsStale);
    }

    /// <summary>The threshold is a setting, not a constant baked into the rule.</summary>
    [Fact]
    public void The_staleness_threshold_can_be_changed()
    {
        _harness.Quiet("s-1", At);
        _viewModel.Tick(At.AddMinutes(5));
        Assert.False(Group(RegistryHarness.Workspace).IsStale);

        _viewModel.StaleAfter = TimeSpan.FromMinutes(4);

        Assert.True(Group(RegistryHarness.Workspace).IsStale);
    }
}
