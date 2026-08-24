using ClaudeDashboard.Tests.Pipeline;
using System.Collections.Specialized;
using System.ComponentModel;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The dashboard body and header (Design Document §7, §9; Impl §5.5).
/// </summary>
/// <remarks>
/// <para>
/// Driven through the real <see cref="SessionRegistry"/> and the real
/// <see cref="SessionProjection"/> rather than by handing the view model a list. What is in doubt
/// is whether the view model reflects what the pipeline actually produces — a hand-built list
/// would test it against my idea of that, which is the thing most likely to be wrong.
/// </para>
/// <para>
/// Assertions are on the real <see cref="ObservableCollection{T}.CollectionChanged"/>, because
/// the churn-free property is a statement about what the binding raises, and only the real event
/// can say.
/// </para>
/// </remarks>
public sealed class MainViewModelTests : IDisposable
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly QueueingDispatcher _dispatcher = new();
    private readonly SessionProjection _projection;
    private readonly MainViewModel _viewModel;
    private readonly List<NotifyCollectionChangedEventArgs> _rowChanges = [];

    public MainViewModelTests()
    {
        _projection = new SessionProjection(_registry, _dispatcher);
        _viewModel = new MainViewModel(_projection, new MotionPolicy(() => false, observeChanges: false), new StubAckPublisher());
        _viewModel.Rows.CollectionChanged += (_, e) => _rowChanges.Add(e);
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        _projection.Dispose();
    }

    private void Apply(InboundEvent inboundEvent)
    {
        _registry.Apply(inboundEvent);
        _dispatcher.Pump();
    }

    private static UserPromptSubmit Prompt(
        string id,
        DateTimeOffset stamp,
        string cwd = @"C:\dev\PennCustQuote",
        string text = "run the tests") => new()
        {
            SessionId = new SessionId(id),
            Timestamp = stamp,
            Cwd = cwd,
            PromptId = "p-1",
            Prompt = text,
        };

    private static Notification Blocked(string id, DateTimeOffset stamp, string type = "permission_prompt") => new()
    {
        SessionId = new SessionId(id),
        Timestamp = stamp,
        Cwd = @"C:\dev\PennCustQuote",
        NotificationType = type,
    };

    private static Stop Finished(string id, DateTimeOffset stamp) => new()
    {
        SessionId = new SessionId(id),
        Timestamp = stamp,
        Cwd = @"C:\dev\PennCustQuote",
        PromptId = "p-1",
        LastAssistantMessage = "29 passed",
    };

    private IReadOnlyList<SessionViewModel> SessionRows => [.. _viewModel.Rows.OfType<SessionViewModel>()];

    // ---- Reflecting the pipeline ------------------------------------------------------------------

    [Fact]
    public void A_new_session_appears_as_a_row()
    {
        Apply(Prompt("s-1", At));

        var row = Assert.Single(SessionRows);
        Assert.Equal(new SessionId("s-1"), row.Id);
        Assert.Equal("run the tests", row.Prompt);
        Assert.Equal(SessionState.Working, row.State);
    }

    [Fact]
    public void A_changed_session_updates_its_row_in_place()
    {
        Apply(Prompt("s-1", At));
        var row = Assert.Single(SessionRows);

        Apply(Finished("s-1", At.AddMinutes(1)));

        Assert.Same(row, Assert.Single(SessionRows));
        Assert.Equal(SessionState.Unread, row.State);
        Assert.Equal("29 passed", row.Answer);
    }

    /// <summary>
    /// The view model holds no ordering of its own: it asks Core. Needs-You sorts by kind first
    /// and then oldest-first within a kind (TS §IV.2 as ratified), which is a rule this file
    /// could not reproduce by accident.
    /// </summary>
    [Fact]
    public void Rows_are_ordered_by_the_attention_engine_not_by_this_view_model()
    {
        _viewModel.IsGrouped = false;

        Apply(Prompt("question-old", At));
        Apply(Blocked("question-old", At.AddMinutes(1), "idle_prompt"));

        Apply(Prompt("permission-new", At.AddMinutes(2)));
        Apply(Blocked("permission-new", At.AddMinutes(20), "permission_prompt"));

        // The question has waited far longer, and still sorts below the permission.
        Assert.Equal(
            [new SessionId("permission-new"), new SessionId("question-old")],
            SessionRows.Select(row => row.Id));
    }

    [Fact]
    public void Bands_are_labelled_in_flat_view_and_only_where_they_have_sessions()
    {
        _viewModel.IsGrouped = false;

        Apply(Prompt("s-1", At));
        Apply(Prompt("s-2", At));
        Apply(Finished("s-2", At.AddMinutes(1)));

        var headers = _viewModel.Rows.OfType<BandHeaderViewModel>().ToList();

        Assert.Equal([AttentionBand.Unread, AttentionBand.Working], headers.Select(h => h.Band));
        Assert.All(headers, header => Assert.Equal(1, header.Count));
        Assert.DoesNotContain(headers, header => header.Band == AttentionBand.NeedsYou);
    }

    // ---- Grouped and flat ---------------------------------------------------------------------------

    [Fact]
    public void Grouped_is_the_default()
    {
        Assert.True(_viewModel.IsGrouped);
    }

    [Fact]
    public void The_toggle_re_projects_the_same_rows_rather_than_keeping_two_collections()
    {
        Apply(Prompt("s-1", At));
        var grouped = Assert.Single(SessionRows);
        Assert.Single(_viewModel.Rows.OfType<GroupViewModel>());

        _viewModel.IsGrouped = false;

        // The same row instance, under a band heading instead of a group heading.
        Assert.Same(grouped, Assert.Single(SessionRows));
        Assert.Empty(_viewModel.Rows.OfType<GroupViewModel>());
        Assert.Single(_viewModel.Rows.OfType<BandHeaderViewModel>());
    }

    [Fact]
    public void A_group_header_is_labelled_from_a_members_cwd_never_from_the_key()
    {
        Apply(Prompt("s-1", At, cwd: @"C:\dev\PennCustQuote"));

        var header = Assert.Single(_viewModel.Rows.OfType<GroupViewModel>());

        Assert.Equal("PennCustQuote", header.Label);
        Assert.Equal(@"C:\dev\PennCustQuote", header.Workspace);
        Assert.Equal(GroupKeyKind.Workspace, header.Kind);

        // The key is an identity: kind-prefixed, with the path case-folded. Binding it would put
        // "workspace:C:\DEV\PENNCUSTQUOTE" on screen, which is why the label comes from the cwd.
        Assert.StartsWith("workspace:", header.Key.Value, StringComparison.Ordinal);
        Assert.Contains(@"C:\DEV\PENNCUSTQUOTE", header.Key.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(header.Key.Value, header.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("workspace:", header.Label, StringComparison.Ordinal);
    }

    /// <summary>A session with no workspace is keyed on itself, so there is no path to show.</summary>
    [Fact]
    public void A_group_for_a_session_with_no_workspace_shows_no_path()
    {
        Apply(Prompt("s-1", At, cwd: string.Empty));

        var header = Assert.Single(_viewModel.Rows.OfType<GroupViewModel>());

        Assert.Equal(GroupKeyKind.Session, header.Kind);
        Assert.Null(header.Workspace);
        Assert.Equal("s-1", header.Label);
    }

    [Fact]
    public void A_group_rolls_up_to_its_worst_member_from_core()
    {
        Apply(Prompt("s-1", At));
        Apply(Prompt("s-2", At));
        Apply(Blocked("s-2", At.AddMinutes(1)));

        var header = Assert.Single(_viewModel.Rows.OfType<GroupViewModel>());

        Assert.Equal(SessionState.NeedsPermission, header.WorstState);
        Assert.Equal(2, header.SessionCount);
    }

    // ---- The counts strip -----------------------------------------------------------------------------

    [Fact]
    public void The_counts_strip_reports_each_band()
    {
        Apply(Prompt("needs", At));
        Apply(Blocked("needs", At.AddMinutes(1)));
        Apply(Prompt("unread", At));
        Apply(Finished("unread", At.AddMinutes(1)));
        Apply(Prompt("working", At));

        Assert.Equal(1, _viewModel.NeedsYouCount);
        Assert.Equal(1, _viewModel.UnreadCount);
        Assert.Equal(1, _viewModel.WorkingCount);
        Assert.Equal(0, _viewModel.QuietCount);
        Assert.Equal(0, _viewModel.EndedCount);
    }

    [Fact]
    public void The_counts_strip_follows_a_state_change()
    {
        Apply(Prompt("s-1", At));
        Assert.Equal(1, _viewModel.WorkingCount);

        Apply(Finished("s-1", At.AddMinutes(1)));

        Assert.Equal(0, _viewModel.WorkingCount);
        Assert.Equal(1, _viewModel.UnreadCount);
    }

    // ---- Churn ------------------------------------------------------------------------------------------

    /// <summary>
    /// <strong>The property T1.3 and T1.4 were built to give the binding.</strong> An unchanged
    /// projection must not touch the bound collection at all.
    /// </summary>
    /// <remarks>
    /// Asserting that the right rows are present would pass under a clear-and-refill that
    /// destroys the property entirely, so the assertion is on what must <em>not</em> happen: no
    /// collection change of any kind, and no reset in particular.
    /// </remarks>
    [Fact]
    public void An_unchanged_projection_does_not_touch_the_bound_collection()
    {
        Apply(Prompt("s-1", At));
        Apply(Prompt("s-2", At));
        _rowChanges.Clear();

        _viewModel.Refresh();
        _viewModel.Refresh();
        _viewModel.Refresh();

        Assert.Empty(_rowChanges);
    }

    /// <summary>
    /// A change to one session must not disturb the rows around it — and must not replace the
    /// row it changed either, since the view model is keyed by session id.
    /// </summary>
    [Fact]
    public void A_state_change_does_not_replace_the_rows_that_did_not_move()
    {
        _viewModel.IsGrouped = false;
        Apply(Prompt("s-1", At));
        Apply(Prompt("s-2", At.AddMinutes(1)));

        var before = SessionRows.ToList();
        _rowChanges.Clear();

        // s-1 finishes: it moves from Working up into Unread, so the sequence genuinely changes.
        Apply(Finished("s-1", At.AddMinutes(2)));

        // The same two view model instances are still the ones on screen.
        Assert.Equal(
            before.Select(row => row.Id).OrderBy(id => id.Value, StringComparer.Ordinal),
            SessionRows.Select(row => row.Id).OrderBy(id => id.Value, StringComparer.Ordinal));
        Assert.All(SessionRows, row => Assert.Contains(row, before));

        // And nothing was reset — the collection was patched.
        Assert.DoesNotContain(_rowChanges, change => change.Action == NotifyCollectionChangedAction.Reset);
    }

    /// <summary>
    /// The row survives the record being replaced, which is what keeps selection alive: the
    /// Registry hands out a new immutable <see cref="Session"/> on every change.
    /// </summary>
    [Fact]
    public void A_row_is_the_same_instance_across_many_updates()
    {
        Apply(Prompt("s-1", At));
        var row = Assert.Single(SessionRows);

        for (var i = 1; i <= 10; i++)
        {
            Apply(Blocked("s-1", At.AddMinutes(i), i % 2 == 0 ? "permission_prompt" : "idle_prompt"));
        }

        Assert.Same(row, Assert.Single(SessionRows));
    }

    [Fact]
    public void A_changed_session_raises_property_changes_on_its_row()
    {
        Apply(Prompt("s-1", At));
        var row = Assert.Single(SessionRows);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Apply(Finished("s-1", At.AddMinutes(1)));

        Assert.Contains(nameof(SessionViewModel.State), changed);
        Assert.Contains(nameof(SessionViewModel.Answer), changed);
    }

    // ---- Age ------------------------------------------------------------------------------------------------

    [Fact]
    public void Age_advances_only_when_something_drives_it()
    {
        Apply(Prompt("s-1", At));
        var row = Assert.Single(SessionRows);

        var initial = row.Age;
        _viewModel.Tick(At.AddMinutes(9));

        Assert.Equal(TimeSpan.FromMinutes(9), row.Age);
        Assert.NotEqual(initial, row.Age);
    }

    [Fact]
    public void Ticking_raises_a_property_change_for_the_age()
    {
        Apply(Prompt("s-1", At));
        var row = Assert.Single(SessionRows);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _viewModel.Tick(At.AddMinutes(3));

        Assert.Contains(nameof(SessionViewModel.Age), changed);
    }

    /// <summary>Ticking to the same instant changes nothing, so an idle tick is free.</summary>
    [Fact]
    public void Ticking_to_the_same_instant_raises_nothing()
    {
        Apply(Prompt("s-1", At));
        var row = Assert.Single(SessionRows);
        _viewModel.Tick(At.AddMinutes(3));

        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        _viewModel.Tick(At.AddMinutes(3));

        Assert.Empty(changed);
    }

    // ---- Removal and construction --------------------------------------------------------------------------------

    [Fact]
    public void Rows_disappear_when_the_projection_empties()
    {
        Apply(Prompt("s-1", At));
        Assert.NotEmpty(_viewModel.Rows);

        _projection.Sessions.Clear();
        _viewModel.Refresh();

        Assert.Empty(_viewModel.Rows);
        Assert.Equal(0, _viewModel.WorkingCount);
    }

    [Fact]
    public void Disposing_stops_following_the_projection()
    {
        _viewModel.Dispose();

        Apply(Prompt("s-1", At));

        Assert.Empty(_viewModel.Rows);
    }

    /// <summary>
    /// It needs all three collaborators, and none of them may be null.
    /// </summary>
    /// <remarks>
    /// The motion policy and the publisher used to be optional, which read as convenience and was
    /// in fact a trapdoor: this is the type the container resolves, and Microsoft DI fills an
    /// unresolvable parameter from its default rather than throwing, so a deleted registration
    /// became a running program that quietly did less. This is that rule stated for a direct
    /// caller; <c>AppHostTests</c> states the container half.
    /// </remarks>
    [Fact]
    public void The_view_model_needs_all_of_its_collaborators()
    {
        var motion = new MotionPolicy(() => false, observeChanges: false);
        var ack = new StubAckPublisher();

        Assert.Throws<ArgumentNullException>(() => new MainViewModel(null!, motion, ack));
        Assert.Throws<ArgumentNullException>(() => new MainViewModel(_projection, null!, ack));
        Assert.Throws<ArgumentNullException>(() => new MainViewModel(_projection, motion, null!));
    }
}

/// <summary>Hook text is data all the way to the binding (Impl §3.4; TS §II.5).</summary>
public sealed class SessionViewModelTextTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private static SessionViewModel Row(string prompt, string? answer = null)
    {
        var id = new SessionId("s-1");
        return new SessionViewModel(new Session
        {
            Id = id,
            State = SessionState.Unread,
            Latest = new Exchange
            {
                Prompt = prompt,
                Answer = answer,
                StartedAt = At,
                AnsweredAt = answer is null ? null : At,
            },
            Cwd = @"C:\w",
            Group = GroupKeys.ForWorkspace(@"C:\w"),
            EnteredAt = At,
            LastActivity = At,
        });
    }

    [Theory]
    [InlineData("$(rm -rf /)")]
    [InlineData("<Button Content=\"click\"/>")]
    [InlineData("{Binding Path=Something}")]
    [InlineData("<script>alert('x')</script>")]
    public void Prompt_and_answer_are_handed_back_exactly_as_they_arrived(string text)
    {
        var row = Row(text, text);

        Assert.Equal(text, row.Prompt);
        Assert.Equal(text, row.Answer);
    }

    /// <summary>
    /// The snippet is a substring and an ellipsis — the one place a transformation happens, and
    /// it neither parses nor formats.
    /// </summary>
    [Fact]
    public void A_long_prompt_is_elided_without_being_interpreted()
    {
        var text = new string('x', SessionViewModel.SnippetLength + 50);
        var row = Row(text);

        Assert.Equal(SessionViewModel.SnippetLength + 1, row.PromptSnippet.Length);
        Assert.StartsWith(text[..SessionViewModel.SnippetLength], row.PromptSnippet, StringComparison.Ordinal);
        Assert.EndsWith("…", row.PromptSnippet, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_prompt_is_not_elided_at_all()
    {
        Assert.Equal("short", Row("short").PromptSnippet);
    }

    [Theory]
    [InlineData(SessionState.Unread, true)]
    [InlineData(SessionState.NeedsPermission, true)]
    [InlineData(SessionState.Error, true)]
    [InlineData(SessionState.Working, false)]
    [InlineData(SessionState.Acked, false)]
    [InlineData(SessionState.Ended, false)]
    public void Only_rows_with_something_to_acknowledge_offer_it(SessionState state, bool expected)
    {
        var row = Row("p");
        row.Session = row.Session with { State = state };

        Assert.Equal(expected, row.CanAcknowledge);
    }

    [Fact]
    public void The_band_comes_from_core()
    {
        var row = Row("p");
        row.Session = row.Session with { State = SessionState.NeedsQuestion };

        Assert.Equal(AttentionOrder.BandOf(SessionState.NeedsQuestion), row.Band);
    }

    [Fact]
    public void An_equal_session_raises_nothing()
    {
        var row = Row("p");
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.Session = row.Session with { };

        Assert.Empty(changed);
    }

    [Fact]
    public void A_row_needs_a_session()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionViewModel(null!));
    }
}
