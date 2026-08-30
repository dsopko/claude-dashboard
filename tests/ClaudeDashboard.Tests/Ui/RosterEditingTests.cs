using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// Forming, remembering and editing a roster from the window's view model
/// (T1.26, issue #16 rules 1, 2, 4, 5 and 6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Rules 4 and 6 are the store's and are not re-asserted here.</strong> What these prove is
/// that the view model <em>calls</em> the store and renders what comes back — so the other group
/// losing a member is visible rather than hidden, which is a different claim from the store having
/// moved the name.
/// </para>
/// <para>
/// A real <see cref="RosterStore"/> over a recording sink, so an edit's announcement is observable
/// and nothing here depends on a running consumer.
/// </para>
/// </remarks>
public sealed class RosterEditingTests : IDisposable
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly RegistryHarness _registry = new();
    private readonly RecordingEventSink _sink = new();
    private readonly RecordingRosterPersistence _persist = new();
    private readonly RosterStore _rosters;
    private readonly MainViewModel _viewModel;

    public RosterEditingTests()
    {
        _rosters = new RosterStore(_sink);
        _viewModel = new MainViewModel(
            _registry.Projection,
            new MotionPolicy(() => false, observeChanges: false),
            new StubAckPublisher(),
            new FakeClipboard(),
            _rosters,
            _persist);
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        _registry.Dispose();
    }

    // ---- Forming ------------------------------------------------------------------------------

    /// <summary>
    /// <strong>Ticking two rows forms a group immediately</strong> (rule 1).
    /// </summary>
    /// <remarks>
    /// Immediately means before any question about remembering it: the group is a fact and the
    /// roster is a decision. The two sessions are in different directories, so a group containing
    /// both can only be the roster's doing.
    /// </remarks>
    [Fact]
    public void Ticking_two_rows_forms_a_group_at_once()
    {
        Working("s-1", "Director", @"C:\a");
        Working("s-2", "Coder", @"C:\b");

        Select("s-1", "s-2");
        _viewModel.GroupSelectedCommand.Execute(null);

        var group = Assert.Single(Groups());

        Assert.Equal(GroupKeyKind.Roster, group.Kind);
        Assert.Equal(2, group.SessionCount);
        Assert.Equal("Group", group.Label);
    }

    /// <summary>
    /// <strong>A group of one is not a group.</strong>
    /// </summary>
    /// <remarks>
    /// It would gain the settle window and the done suppression, so a single session's finished
    /// chime would be delayed for no benefit — and that chime is what this product exists to
    /// deliver. Rules 4 and 6 can still reduce a roster to one member, which is a different case and
    /// is covered below.
    /// </remarks>
    [Fact]
    public void One_ticked_row_cannot_form_a_group()
    {
        Working("s-1", "Director");

        Select("s-1");

        Assert.False(_viewModel.GroupSelectedCommand.CanExecute(null));

        _viewModel.GroupSelectedCommand.Execute(null);

        Assert.True(_rosters.Book.IsEmpty);
    }

    /// <summary>A one-member roster, which rules 4 and 6 can produce, renders as a group.</summary>
    [Fact]
    public void A_one_member_roster_still_renders()
    {
        Working("s-1", "Director");
        _rosters.Replace(RosterBook.From([("solo", ["Director"])]));

        _viewModel.Refresh();

        var group = Assert.Single(Groups());

        Assert.Equal("solo", group.Label);
        Assert.Equal(1, group.SessionCount);
    }

    /// <summary>
    /// <strong>A session with no title cannot be ticked, and the row says why.</strong>
    /// </summary>
    /// <remarks>
    /// A roster stores names and this session has none. The refusal text is asserted as well as the
    /// refusal: a row that does nothing when clicked and says nothing is indistinguishable from a
    /// bug.
    /// </remarks>
    [Fact]
    public void An_untitled_session_cannot_be_ticked_and_says_so()
    {
        Working("s-1", title: null);

        _viewModel.IsSelecting = true;

        var row = Row("s-1");

        Assert.False(row.CanSelect);
        Assert.Equal("no name to remember", row.SelectionRefusal);

        // The click a row receives in selection mode, and it must not take.
        row.IsExpanded = true;

        Assert.False(row.IsSelected);
        Assert.False(row.IsExpanded);
        Assert.Equal(0, _viewModel.SelectedCount);
    }

    /// <summary>In selection mode a row click selects and does not expand.</summary>
    /// <remarks>
    /// One gesture, one meaning at a time. Out of the mode the same click expands, which is the
    /// control that proves the mode is doing the work rather than expansion being broken.
    /// </remarks>
    [Fact]
    public void A_row_click_selects_in_the_mode_and_expands_out_of_it()
    {
        Working("s-1", "Director");

        var row = Row("s-1");

        row.IsExpanded = true;
        Assert.True(row.IsExpanded);
        Assert.False(row.IsSelected);

        row.IsExpanded = false;
        _viewModel.IsSelecting = true;

        row.IsExpanded = true;

        Assert.False(row.IsExpanded);
        Assert.True(row.IsSelected);
    }

    /// <summary>Leaving the mode drops every tick, so re-entering starts empty.</summary>
    [Fact]
    public void Cancelling_the_mode_drops_the_ticks()
    {
        Working("s-1", "Director");
        Working("s-2", "Coder");

        Select("s-1", "s-2");
        Assert.Equal(2, _viewModel.SelectedCount);

        _viewModel.CancelSelectionCommand.Execute(null);

        Assert.False(_viewModel.IsSelecting);
        Assert.Equal(0, _viewModel.SelectedCount);
        Assert.True(_rosters.Book.IsEmpty);
    }

    // ---- Remembering, and declining ------------------------------------------------------------

    /// <summary>Forming a group raises the prompt; remembering it writes the roster.</summary>
    [Fact]
    public void Remembering_writes_the_roster()
    {
        FormAGroup();

        var prompt = Assert.Single(_viewModel.Rows.OfType<RosterPromptViewModel>());

        prompt.RememberCommand.Execute(null);

        Assert.Equal(["Director", "Coder"], Assert.Single(_persist.Last).Members.ToArray());
        Assert.Empty(_viewModel.Rows.OfType<RosterPromptViewModel>());
    }

    /// <summary>The operator may rename the roster in the prompt before remembering it.</summary>
    /// <remarks>
    /// This is the ONLY typed name in the feature, and it is the roster's own label — compared
    /// against nothing. Member names are copied from rows, never typed.
    /// </remarks>
    [Fact]
    public void The_roster_can_be_renamed_in_the_prompt()
    {
        FormAGroup();

        var prompt = Assert.Single(_viewModel.Rows.OfType<RosterPromptViewModel>());
        prompt.Name = "orchestration";
        prompt.RememberCommand.Execute(null);

        Assert.Equal("orchestration", Assert.Single(_persist.Last).Name);
        Assert.Equal("orchestration", Assert.Single(Groups()).Label);
    }

    /// <summary>A roster needs a name, so an empty one cannot be remembered.</summary>
    [Fact]
    public void An_unnamed_roster_cannot_be_remembered()
    {
        FormAGroup();

        var prompt = Assert.Single(_viewModel.Rows.OfType<RosterPromptViewModel>());
        prompt.Name = "   ";

        Assert.False(prompt.RememberCommand.CanExecute(null));
    }

    /// <summary>
    /// <strong>Declining leaves the group formed and writes nothing.</strong>
    /// </summary>
    [Fact]
    public void Declining_leaves_the_group_but_persists_nothing()
    {
        FormAGroup();

        Assert.Single(_viewModel.Rows.OfType<RosterPromptViewModel>()).ForgetCommand.Execute(null);

        Assert.Empty(_persist.Remembered);
        Assert.Equal(2, Assert.Single(Groups()).SessionCount);
        Assert.Empty(_viewModel.Rows.OfType<RosterPromptViewModel>());
    }

    /// <summary>
    /// <strong>An unanswered prompt leaves exactly the state a declined one does.</strong>
    /// </summary>
    /// <remarks>
    /// This is what makes an ignorable prompt safe rather than merely convenient. The window can be
    /// used and dismissed with the prompt showing, and nothing is lost by that — so the failure mode
    /// of being ignored is not a failure. Asserted by comparing the two states rather than by
    /// describing them.
    /// </remarks>
    [Fact]
    public void An_unanswered_prompt_leaves_the_same_state_as_a_declined_one()
    {
        FormAGroup();

        var ignoredRosters = _rosters.Book.Rosters.Single();
        var ignoredPersisted = _persist.Remembered.Count;
        var ignoredMembers = Assert.Single(Groups()).SessionCount;

        Assert.Single(_viewModel.Rows.OfType<RosterPromptViewModel>()).ForgetCommand.Execute(null);

        Assert.Equal(ignoredRosters.Name, _rosters.Book.Rosters.Single().Name);
        Assert.Equal(ignoredRosters.Members.ToArray(), _rosters.Book.Rosters.Single().Members.ToArray());
        Assert.Equal(ignoredPersisted, _persist.Remembered.Count);
        Assert.Equal(ignoredMembers, Assert.Single(Groups()).SessionCount);
    }

    // ---- Editing ------------------------------------------------------------------------------

    /// <summary>
    /// <strong>Ticking a name that belongs to another roster moves it, and both groups change on
    /// screen.</strong>
    /// </summary>
    /// <remarks>
    /// Rule 4 moves the name silently — a ruling about not interrupting the operator, not about
    /// hiding the effect. The old group losing a member is asserted here because that is the half a
    /// UI could quietly fail to show.
    /// </remarks>
    [Fact]
    public void Moving_a_name_to_a_new_group_empties_the_old_one_on_screen()
    {
        Working("s-1", "Director");
        Working("s-2", "Coder");
        Working("s-3", "Reviewer");

        _rosters.Replace(RosterBook.From([("first", ["Director", "Coder"])]));
        _viewModel.Refresh();

        Assert.Equal(2, Groups().Single(g => g.Label == "first").SessionCount);

        Select("s-2", "s-3");
        _viewModel.GroupSelectedCommand.Execute(null);

        Assert.Equal(1, Groups().Single(g => g.Label == "first").SessionCount);
        Assert.Equal(2, Groups().Single(g => g.Label == "Group").SessionCount);
    }

    /// <summary>Right-click removal takes the name out of the roster (rule 5).</summary>
    [Fact]
    public void Removal_takes_the_name_out_of_the_roster()
    {
        Working("s-1", "Director");
        Working("s-2", "Coder");
        _rosters.Replace(RosterBook.From([("orchestration", ["Director", "Coder"])]));
        _viewModel.Refresh();

        _viewModel.RemoveFromGroupCommand.Execute(Row("s-1"));

        Assert.Null(_rosters.Book.RosterFor("Director"));
        Assert.Equal(["Coder"], _rosters.Book.Rosters.Single().Members.ToArray());
    }

    /// <summary>
    /// <strong>Removing the last member dissolves the group, and the screen stays coherent.</strong>
    /// </summary>
    /// <remarks>
    /// Rule 6. The session is not removed from the dashboard: it returns to its workspace group,
    /// which is why the menu item says "Remove from group". Both halves are asserted, because a UI
    /// that dropped the row entirely would satisfy "the roster is gone".
    /// </remarks>
    [Fact]
    public void Removing_the_last_member_dissolves_the_group_and_the_session_returns()
    {
        Working("s-1", "Director");
        _rosters.Replace(RosterBook.From([("solo", ["Director"])]));
        _viewModel.Refresh();

        _viewModel.RemoveFromGroupCommand.Execute(Row("s-1"));

        Assert.True(_rosters.Book.IsEmpty);

        var group = Assert.Single(Groups());

        Assert.Equal(GroupKeyKind.Workspace, group.Kind);
        Assert.Equal(1, group.SessionCount);
    }

    /// <summary>
    /// <strong>Removing a name two live sessions share moves both rows.</strong>
    /// </summary>
    /// <remarks>
    /// #16 accepts that two sessions can share a rostered name and both join. Removal is by name, so
    /// one right-click moves both — deliberately not special-cased away, because the second row
    /// moving is what the store actually did and hiding it would be the UI lying.
    /// </remarks>
    [Fact]
    public void Removing_a_shared_name_moves_both_sessions()
    {
        Working("s-1", "Director");
        Working("s-2", "Director");
        Working("s-3", "Coder");
        _rosters.Replace(RosterBook.From([("orchestration", ["Director", "Coder"])]));
        _viewModel.Refresh();

        Assert.Equal(3, Groups().Single(g => g.Label == "orchestration").SessionCount);

        _viewModel.RemoveFromGroupCommand.Execute(Row("s-1"));

        Assert.Equal(1, Groups().Single(g => g.Label == "orchestration").SessionCount);
    }

    /// <summary>A row in no roster offers nothing to remove.</summary>
    [Fact]
    public void A_row_in_no_roster_cannot_be_removed_from_one()
    {
        Working("s-1", "Director");

        Assert.False(_viewModel.RemoveFromGroupCommand.CanExecute(Row("s-1")));
        Assert.False(_viewModel.RemoveFromGroupCommand.CanExecute(null));
    }

    /// <summary>
    /// <strong>Every roster edit announces itself on the pipeline.</strong>
    /// </summary>
    /// <remarks>
    /// The consumer reads the roster book on its own thread and re-resolves only after a drain or a
    /// tick — and the tick is fifteen seconds. Without the announcement a dissolved group could go
    /// on nudging and a new one could not settle, for that whole window, with the screen already
    /// right. Asserted at every editing path, because the announcement is the store's job precisely
    /// so that no path can forget it.
    /// </remarks>
    [Fact]
    public void Every_edit_announces_itself()
    {
        Working("s-1", "Director");
        Working("s-2", "Coder");

        Select("s-1", "s-2");
        _viewModel.GroupSelectedCommand.Execute(null);

        var afterForming = _sink.Published.Count;
        Assert.True(afterForming >= 1);

        _viewModel.RemoveFromGroupCommand.Execute(Row("s-1"));

        Assert.True(_sink.Published.Count > afterForming);
        Assert.All(_sink.Published, published => Assert.IsType<ClaudeDashboard.Core.Events.RostersChanged>(published));
    }

    private void FormAGroup()
    {
        Working("s-1", "Director");
        Working("s-2", "Coder");

        Select("s-1", "s-2");
        _viewModel.GroupSelectedCommand.Execute(null);
    }

    private void Select(params string[] ids)
    {
        _viewModel.IsSelecting = true;

        foreach (var id in ids)
        {
            Row(id).IsSelected = true;
        }
    }

    private SessionViewModel Row(string id) =>
        _viewModel.Rows.OfType<SessionViewModel>().Single(row => row.Id.Value == id);

    private IReadOnlyList<GroupViewModel> Groups() =>
        [.. _viewModel.Rows.OfType<GroupViewModel>()];

    private void Working(string id, string? title, string cwd = @"C:\w") =>
        _registry.Apply(new ClaudeDashboard.Core.Events.UserPromptSubmit
        {
            SessionId = new SessionId(id),
            Timestamp = At,
            Cwd = cwd,
            PromptId = $"p-{id}",
            Prompt = "run the tests",
            SessionTitle = title,
        });
}
