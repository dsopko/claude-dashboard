using ClaudeDashboard.App.Configuration;
using System.Reflection;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The manual acknowledgment tier (Design Document §4 tier 2; TS §I.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The claim is the route, not the result.</strong> "The session became Acked" passes for
/// an implementation that pokes the Registry from the dispatcher and never touches the channel at
/// all — which would work in a test run and fail in front of the operator, because T1.2b made the
/// single-writer guard mutual exclusion rather than thread affinity: a direct <c>Apply</c>
/// succeeds whenever the consumer happens to be idle. So these assert what the channel carried.
/// </para>
/// <para>
/// The end-to-end run through a live consumer is in <c>AckPipelineTests</c>; this is the row and
/// the publisher.
/// </para>
/// </remarks>
public sealed class AckTests : IDisposable
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly RegistryHarness _harness = new();
    private readonly RecordingEventSink _sink = new();
    private readonly FakeClock _clock = new();
    private readonly MainViewModel _viewModel;

    public AckTests()
    {
        _viewModel = new MainViewModel(
            _harness.Projection,
            new MotionPolicy(() => false, observeChanges: false),
            new AckPublisher(_sink, _clock, Logger.None),
            new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence());
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        _harness.Dispose();
    }

    private SessionViewModel Row(string id) =>
        _viewModel.Rows.OfType<SessionViewModel>().Single(row => row.Id.Value == id);

    private IReadOnlyList<Ack> Acks => [.. _sink.Published.OfType<Ack>()];

    private SessionViewModel GivenAnUnreadRow(string id = "finished")
    {
        var promptId = _harness.Working(id, At);
        _harness.Finished(id, At.AddMinutes(1), promptId);
        return Row(id);
    }

    // ---- What must happen ---------------------------------------------------------------------

    /// <summary>
    /// <strong>The guardrail.</strong> The click produces an event on the channel — the same one
    /// a hook would travel — and nothing else.
    /// </summary>
    [Fact]
    public void Acknowledging_publishes_an_ack_event()
    {
        var row = GivenAnUnreadRow();

        row.AcknowledgeCommand.Execute(null);

        var ack = Assert.Single(Acks);
        Assert.Equal(row.Id, ack.SessionId);
        Assert.Equal(AckSource.Manual, ack.Source);
        Assert.Equal(_clock.Now, ack.Timestamp);
    }

    /// <summary>
    /// And it publishes <em>only</em> that. A click is one event, so a row that also raised a
    /// prompt or a stop would be inventing history the operator did not make.
    /// </summary>
    [Fact]
    public void Acknowledging_publishes_nothing_else()
    {
        GivenAnUnreadRow().AcknowledgeCommand.Execute(null);

        Assert.Single(_sink.Published);
    }

    /// <summary>The row's Ack and the expanded row's are one command, so they cannot disagree.</summary>
    [Fact]
    public void The_row_and_the_expanded_row_share_one_command()
    {
        var row = GivenAnUnreadRow();
        var collapsed = row.AcknowledgeCommand;

        row.IsExpanded = true;

        Assert.Same(collapsed, row.AcknowledgeCommand);
    }

    [Theory]
    [InlineData(SessionState.Unread)]
    [InlineData(SessionState.NeedsPermission)]
    [InlineData(SessionState.NeedsQuestion)]
    [InlineData(SessionState.Error)]
    public void A_row_with_something_to_acknowledge_offers_it(SessionState state)
    {
        var row = Reach("s-1", state);

        Assert.True(row.CanAcknowledge);
        Assert.True(row.AcknowledgeCommand.CanExecute(null));
    }

    /// <summary>
    /// The command comes back when the session changes into a state that can be acknowledged —
    /// a row that was Working when the window opened must not stay disabled after it finishes.
    /// </summary>
    [Fact]
    public void The_command_follows_the_session_into_and_out_of_acknowledgeable_states()
    {
        var promptId = _harness.Working("s-1", At);
        var row = Row("s-1");
        Assert.False(row.AcknowledgeCommand.CanExecute(null));

        var changed = 0;
        row.AcknowledgeCommand.CanExecuteChanged += (_, _) => changed++;

        _harness.Finished("s-1", At.AddMinutes(1), promptId);

        Assert.True(row.AcknowledgeCommand.CanExecute(null));
        Assert.True(changed > 0, "the command must announce that it became available");
    }

    // ---- Ack all (issue #43) --------------------------------------------------------------------

    /// <summary>
    /// The flag follows the sessions: false on an empty board, false among the ineligible, true
    /// the moment one session waits, false again when it stops waiting.
    /// </summary>
    [Fact]
    public void The_flag_follows_what_is_waiting()
    {
        Assert.False(_viewModel.AnythingToAcknowledge);

        _harness.Working("busy", At);
        _harness.Quiet("done", At);

        Assert.False(_viewModel.AnythingToAcknowledge);

        _harness.Finished("s-1", At.AddMinutes(1), _harness.Working("s-1", At));

        Assert.True(_viewModel.AnythingToAcknowledge);

        _harness.Acked("s-1", At.AddMinutes(2));

        Assert.False(_viewModel.AnythingToAcknowledge);
    }

    /// <summary>The view is not the truth: the flag holds in Flat exactly as in Grouped.</summary>
    [Fact]
    public void The_flag_is_the_same_fact_in_both_views()
    {
        _harness.Finished("s-1", At.AddMinutes(1), _harness.Working("s-1", At));

        Assert.True(_viewModel.AnythingToAcknowledge);

        _viewModel.IsGrouped = false;

        Assert.True(_viewModel.AnythingToAcknowledge);
        Assert.True(_viewModel.AckAllCommand.CanExecute(null));
    }

    /// <summary>
    /// The flag holds for a member of a collapsed group, because it is computed from the
    /// sessions and never from <see cref="MainViewModel.Rows"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stated honestly: today this scenario cannot tell the two computations apart, and that is
    /// a measured fact rather than an oversight. No collapse rule can hide an eligible row —
    /// <c>An_unread_row_is_never_collapsed</c>, <c>A_blocked_row_is_never_collapsed</c> and
    /// <c>The_bands_that_hold_work_are_never_summarised_in_flat_view</c> pin exactly that, so an
    /// eligible session always has a row and a Rows-based flag would pass this test too.
    /// </para>
    /// <para>
    /// What the test does pin is the contract: collapsing a group must not move the flag, and if
    /// a future collapse rule ever does hide a waiting row — the renderer is one rule change
    /// away — this is the test that fails against the sessions-based flag's promise rather than
    /// nothing failing at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_flag_survives_a_collapsed_group()
    {
        _harness.Finished("s-1", At.AddMinutes(1), _harness.Working("s-1", At));

        foreach (var group in _viewModel.Rows.OfType<GroupViewModel>().ToList())
        {
            group.IsExpanded = false;
        }

        Assert.True(_viewModel.AnythingToAcknowledge);
    }

    /// <summary>
    /// <strong>One click, exactly the eligible set, once each.</strong>
    /// </summary>
    /// <remarks>
    /// The set is asserted, not a count: a count of four passes when the click acks the wrong
    /// four. The count is asserted beside it only to close the duplicate hole a set cannot see.
    /// </remarks>
    [Fact]
    public void The_click_publishes_exactly_the_eligible_set()
    {
        _harness.Finished("unread", At.AddMinutes(1), _harness.Working("unread", At));
        _harness.Working("perm", At);
        _harness.Blocked("perm", At.AddMinutes(1), "permission_prompt");
        _harness.Working("quest", At);
        _harness.Blocked("quest", At.AddMinutes(1), "agent_needs_input");
        _harness.Failed("err", At.AddMinutes(1), _harness.Working("err", At));
        _harness.Working("busy", At);
        _harness.Quiet("done", At);
        _harness.Started("ended", At);
        _harness.Ended("ended", At.AddMinutes(1));

        _viewModel.AckAllCommand.Execute(null);

        Assert.Equal(
            new HashSet<SessionId>
            {
                new("unread"), new("perm"), new("quest"), new("err"),
            },
            Acks.Select(ack => ack.SessionId).ToHashSet());
        Assert.Equal(4, Acks.Count);
        Assert.All(Acks, ack => Assert.Equal(AckSource.Manual, ack.Source));
        Assert.Equal(4, _sink.Published.Count);
    }

    /// <summary>
    /// Nothing at all on a quiet board — through <c>Execute</c>, which does not gate on
    /// <c>CanExecute</c>, because the body's own re-check is the claim under test.
    /// </summary>
    [Fact]
    public void The_click_publishes_nothing_when_nothing_is_waiting()
    {
        _harness.Working("busy", At);
        _harness.Quiet("done", At);

        Assert.False(_viewModel.AckAllCommand.CanExecute(null));

        _viewModel.AckAllCommand.Execute(null);

        Assert.Empty(_sink.Published);
    }

    /// <summary>
    /// The flag goes false when the Registry has applied the acks — no second click, and the
    /// command says so out loud for the button to re-query.
    /// </summary>
    [Fact]
    public void The_flag_clears_once_the_registry_has_applied_the_acks()
    {
        _harness.Finished("s-1", At.AddMinutes(1), _harness.Working("s-1", At));
        _harness.Failed("s-2", At.AddMinutes(1), _harness.Working("s-2", At));

        // The click happens after the sessions reached their states, and the publisher stamps
        // the clock's now. Left at the start, the acks would carry a timestamp BEFORE the
        // transitions and the Registry's timestamp guard would decline every one — correctly.
        _clock.Advance(TimeSpan.FromMinutes(5));

        _viewModel.AckAllCommand.Execute(null);

        // Published is not applied: the projection has heard nothing yet.
        Assert.True(_viewModel.AnythingToAcknowledge);

        var announced = 0;
        _viewModel.AckAllCommand.CanExecuteChanged += (_, _) => announced++;

        foreach (var ack in Acks.ToList())
        {
            _harness.Apply(ack);
        }

        Assert.False(_viewModel.AnythingToAcknowledge);
        Assert.False(_viewModel.AckAllCommand.CanExecute(null));
        Assert.True(announced > 0, "the command must announce that it became unavailable");
    }

    // ---- What must NOT happen ------------------------------------------------------------------

    /// <summary>
    /// A session with nothing to acknowledge does not offer the action — and invoking it anyway,
    /// which a binding cannot do but a test can, publishes nothing.
    /// </summary>
    [Theory]
    [InlineData(SessionState.Working)]
    [InlineData(SessionState.Acked)]
    [InlineData(SessionState.Ended)]
    public void A_row_with_nothing_to_acknowledge_publishes_nothing(SessionState state)
    {
        var row = Reach("s-1", state);

        Assert.False(row.CanAcknowledge);
        Assert.False(row.AcknowledgeCommand.CanExecute(null));

        row.AcknowledgeCommand.Execute(null);

        Assert.Empty(_sink.Published);
    }

    /// <summary>
    /// A row built with nowhere to send an ack still <em>offers</em> the affordance and cannot
    /// fire it: visible, and disabled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whether a session can be acknowledged is a fact about the session; whether this row was
    /// handed a publisher is a fact about how it was constructed. Collapsing the two would make
    /// the button vanish on a wiring mistake, and a missing control is the one failure an operator
    /// cannot report — there is nothing on screen to point at. So <c>CanAcknowledge</c> drives
    /// visibility and the publisher only gates the command.
    /// </para>
    /// <para>
    /// This is the only level at which a publisher can be absent at all, which is why the
    /// assertion lives here. <see cref="MainViewModel"/> requires one, so the shipped window
    /// always has somewhere to send an ack; rows are built standalone in tests, and that is what
    /// <see cref="SessionViewModel"/>'s optional form is for.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_row_with_nowhere_to_send_an_ack_offers_it_and_cannot_fire_it()
    {
        var session = Reach("s-1", SessionState.Unread).Session;

        var unwired = new SessionViewModel(session, new MotionPolicy(() => false, observeChanges: false));

        Assert.True(unwired.CanAcknowledge);
        Assert.False(unwired.AcknowledgeCommand.CanExecute(null));

        unwired.AcknowledgeCommand.Execute(null);

        Assert.Empty(_sink.Published);
    }

    /// <summary>
    /// Acknowledging one row acknowledges one session. An implementation that acked everything —
    /// or the wrong thing — passes every test that only looks at the row it clicked.
    /// </summary>
    [Fact]
    public void Acknowledging_one_row_names_only_that_session()
    {
        var first = GivenAnUnreadRow("finished-one");
        GivenAnUnreadRow("finished-two");
        GivenAnUnreadRow("finished-three");

        first.AcknowledgeCommand.Execute(null);

        var ack = Assert.Single(Acks);
        Assert.Equal(new SessionId("finished-one"), ack.SessionId);
    }

    /// <summary>
    /// A refused publish leaves the row exactly as it was and says so in the log. It must not
    /// pretend: the session is unchanged, and the projection is what says so.
    /// </summary>
    [Fact]
    public void A_refused_ack_changes_nothing()
    {
        var row = GivenAnUnreadRow("s-1");

        // A full pipeline: the bounded channel can refuse (Impl §4).
        var publisher = new AckPublisher(new RecordingEventSink { Capacity = 0 }, _clock, Logger.None);

        Assert.False(publisher.Acknowledge(row.Session));
        Assert.Equal(0, publisher.PublishedCount);
        Assert.Equal(SessionState.Unread, _harness.Registry.Sessions[row.Id].State);
        Assert.Equal(SessionState.Unread, row.State);
    }

    /// <summary>
    /// <strong>Nothing under <c>Ui</c> can reach the Registry, save the one thing that must.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule is that an ack is an event through the channel, and the architecture will not
    /// enforce it: a direct <c>Apply</c> from the dispatcher throws only when it overlaps the
    /// consumer, so it would pass a test run and fail in front of the operator. This asserts the
    /// part that <em>is</em> structural — that no view model holds, takes, or returns a
    /// <see cref="SessionRegistry"/>, and therefore has nothing to poke.
    /// </para>
    /// <para>
    /// <see cref="SessionProjection"/> is the single exception, and it is the bridge Impl §4
    /// requires: it subscribes to the Registry's change notification and marshals the snapshot it
    /// is handed onto the UI thread. It is the one type in the namespace that is not a view model,
    /// and it reads rather than writes — the direction that matters here.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_view_model_can_reach_the_registry()
    {
        var offenders = new List<string>();

        foreach (var type in typeof(MainViewModel).Assembly.GetTypes()
            .Where(candidate => candidate.Namespace == "ClaudeDashboard.App.Ui")
            .Where(candidate => candidate != typeof(SessionProjection)))
        {
            const BindingFlags All =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

            offenders.AddRange(type.GetFields(All)
                .Where(field => field.FieldType == typeof(SessionRegistry))
                .Select(field => $"{type.Name}.{field.Name} (field)"));

            offenders.AddRange(type.GetProperties(All)
                .Where(property => property.PropertyType == typeof(SessionRegistry))
                .Select(property => $"{type.Name}.{property.Name} (property)"));

            offenders.AddRange(type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All))
                .SelectMany(method => method.GetParameters()
                    .Where(parameter => parameter.ParameterType == typeof(SessionRegistry))
                    .Select(parameter => $"{type.Name}.{method.Name}({parameter.Name})")));
        }

        Assert.Empty(offenders);
    }

    /// <summary>And the ack publisher in particular holds a sink, which is the whole point.</summary>
    [Fact]
    public void The_ack_publisher_holds_a_sink_and_not_a_registry()
    {
        var parameters = typeof(AckPublisher).GetConstructors().Single().GetParameters();

        Assert.Contains(parameters, parameter => parameter.ParameterType == typeof(Core.Ports.IEventSink));
        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(SessionRegistry));
    }

    [Fact]
    public void The_publisher_needs_its_collaborators()
    {
        Assert.Throws<ArgumentNullException>(() => new AckPublisher(null!, _clock, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new AckPublisher(_sink, null!, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new AckPublisher(_sink, _clock, null!));
        Assert.Throws<ArgumentNullException>(
            () => new AckPublisher(_sink, _clock, Logger.None).Acknowledge(null!));
    }

    /// <summary>Drives a session to <paramref name="state"/> and returns its row.</summary>
    private SessionViewModel Reach(string id, SessionState state)
    {
        switch (state)
        {
            case SessionState.Working:
                _harness.Working(id, At);
                break;

            case SessionState.Unread:
                _harness.Finished(id, At.AddMinutes(1), _harness.Working(id, At));
                break;

            case SessionState.Error:
                _harness.Failed(id, At.AddMinutes(1), _harness.Working(id, At));
                break;

            case SessionState.NeedsPermission:
            case SessionState.NeedsQuestion:
                _harness.Working(id, At);
                _harness.Blocked(
                    id,
                    At.AddMinutes(1),
                    state == SessionState.NeedsPermission ? "permission_prompt" : "agent_needs_input");
                break;

            case SessionState.Acked:
                _harness.Quiet(id, At);
                break;

            case SessionState.Ended:
                _harness.Started(id, At);
                _harness.Ended(id, At.AddMinutes(1));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "No pipeline path to this state.");
        }

        // Quiet rows are collapsed behind a footer (Design Document §6), so open the group first.
        foreach (var group in _viewModel.Rows.OfType<GroupViewModel>().ToList())
        {
            group.IsExpanded = true;
        }

        var row = Row(id);
        Assert.Equal(state, row.State);
        return row;
    }
}
