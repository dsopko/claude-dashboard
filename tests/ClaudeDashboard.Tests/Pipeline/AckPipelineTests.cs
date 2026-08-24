using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// Both acknowledgment tiers, end to end through the running pipeline
/// (Design Document §4; TS §I.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here calls the Registry.</strong> Every event — the prompts, the stop, and the
/// operator's click — is published to the channel and applied by the real
/// <see cref="EventConsumer"/> on its own thread, and every assertion is made on the row the
/// projection produced. That is the shape TS §I.3 requires and the shape these tests exist to
/// pin: an implementation that acknowledged by poking the Registry from the dispatcher would pass
/// a test that only checked the resulting state.
/// </para>
/// <para>
/// The consumer is the only writer, exactly as in production, so these also demonstrate that a
/// click from the UI thread and a hook from a Kestrel thread reach the Registry the same way.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class AckPipelineTests : IAsyncLifetime
{
    private const string Workspace = @"C:\dev\PennCustQuote";

    private readonly FakeClock _clock = new();
    private readonly RecordingSoundPlayer _player = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new();
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly QueueingDispatcher _dispatcher = new();

    private SessionProjection _projection = null!;
    private MainViewModel _viewModel = null!;
    private EventConsumer _consumer = null!;

    public Task InitializeAsync()
    {
        _projection = new SessionProjection(_registry, _dispatcher);
        _viewModel = new MainViewModel(
            _projection,
            new MotionPolicy(() => false, observeChanges: false),
            new AckPublisher(_pipeline.Sink, _clock, Logger.None));

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            new SoundPolicyEngine(_player, _clock),
            _clock,
            _guard,
            Logger.None,
            tickInterval: TimeSpan.FromMilliseconds(25));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        _viewModel?.Dispose();
        _projection?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Publishes into the channel, as ingress does. Nothing here touches the Registry.</summary>
    private void Publish(InboundEvent inboundEvent) =>
        Assert.True(_pipeline.Sink.TryPublish(inboundEvent), "the pipeline refused the event");

    /// <summary>
    /// Drains the UI queue until <paramref name="condition"/> holds — the consumer applies on its
    /// own thread, so this stands in for the dispatcher pumping.
    /// </summary>
    private async Task<bool> Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            _dispatcher.Pump();

            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        _dispatcher.Pump();
        return condition();
    }

    private SessionViewModel? Row(string id) =>
        _viewModel.Rows.OfType<SessionViewModel>().FirstOrDefault(row => row.Id.Value == id);

    /// <summary>
    /// Stamps with the clock, as ingress does.
    /// </summary>
    /// <remarks>
    /// <strong>One clock stamps everything.</strong> <c>HookEventMapper</c> puts <c>IClock.Now</c>
    /// on every hook as it arrives rather than trusting a payload field, and the ack publisher
    /// reads the same clock — so the Registry's stale guard (an event older than the session's
    /// last activity is declined) cannot misfire between the two. A test that stamped its events
    /// from somewhere other than its clock would be testing an arrangement production does not
    /// have; an earlier draft of this file did exactly that, and every manual ack was declined as
    /// Stale.
    /// </remarks>
    private void Prompt(string id, string promptId) => Publish(new UserPromptSubmit
    {
        SessionId = new SessionId(id),
        Timestamp = _clock.Now,
        Cwd = Workspace,
        PromptId = promptId,
        Prompt = "run the full test suite",
    });

    private void Finish(string id, string promptId) => Publish(new Stop
    {
        SessionId = new SessionId(id),
        Timestamp = _clock.Now,
        Cwd = Workspace,
        PromptId = promptId,
        LastAssistantMessage = "29 passed",
    });

    /// <summary>Gets a session to Unread through the real pipeline and returns its row.</summary>
    private async Task<SessionViewModel> GivenAnUnreadRow(string id)
    {
        Prompt(id, "p-1");
        _clock.AdvanceMinutes(1);
        Finish(id, "p-1");

        Assert.True(
            await Until(() => Row(id)?.State == SessionState.Unread),
            "the session never reached Unread through the pipeline");

        return Row(id)!;
    }

    // ---- Tier 2: manual ------------------------------------------------------------------------

    /// <summary>
    /// A click on the row's Ack reaches the Registry through the channel, and the row comes back
    /// grey — and collapsed, because a quiet row is summarised into its group's footer
    /// (Design Document §6 rule 2).
    /// </summary>
    [Fact]
    public async Task A_manual_ack_travels_the_channel_and_greys_the_row()
    {
        var row = await GivenAnUnreadRow("finished");
        Assert.Equal(Accent.Green, row.Accent);

        row.AcknowledgeCommand.Execute(null);

        Assert.True(
            await Until(() => row.State == SessionState.Acked),
            "the acknowledgment never arrived through the pipeline");

        Assert.Equal(Accent.Grey, row.Accent);
        Assert.Equal(MotionKind.None, row.Motion);
        Assert.False(row.CanAcknowledge);

        // Collapsed: the row is no longer on screen, and a footer stands for it.
        Assert.Null(Row("finished"));
        var footer = Assert.Single(_viewModel.Rows.OfType<QuietFooterViewModel>());
        Assert.Equal("+ 1 quiet", footer.Text);
    }

    /// <summary>
    /// <strong>The route, asserted rather than assumed.</strong> The consumer is the only thing
    /// that applies events, so if the click had poked the Registry directly the count of applied
    /// events would not have moved — the state would still be Acked and the test would still pass
    /// on the result alone.
    /// </summary>
    [Fact]
    public async Task The_manual_ack_was_applied_by_the_consumer()
    {
        var row = await GivenAnUnreadRow("finished");
        var appliedBefore = _consumer.AppliedCount;

        row.AcknowledgeCommand.Execute(null);
        Assert.True(await Until(() => row.State == SessionState.Acked));

        Assert.Equal(appliedBefore + 1, _consumer.AppliedCount);

        // …and the Registry recorded what caused it, which names the tier.
        var transitions = _registry.Sessions[row.Id].Transitions;
        Assert.Contains(
            transitions,
            entry => entry.To == SessionState.Acked
                && entry.Cause?.Contains("Ack (Manual)", StringComparison.Ordinal) == true);
    }

    // ---- Tier 1: automatic ----------------------------------------------------------------------

    /// <summary>
    /// The next prompt acknowledges what was waiting — no extra plumbing, as Design Document §4
    /// says. Verified through the pipeline rather than reimplemented.
    /// </summary>
    [Fact]
    public async Task A_new_prompt_auto_acks_a_prior_unread()
    {
        var row = await GivenAnUnreadRow("finished");

        _clock.AdvanceMinutes(1);
        Prompt("finished", "p-2");

        Assert.True(
            await Until(() => row.State == SessionState.Working),
            "the new prompt never arrived through the pipeline");

        // The Unread is gone: it is no longer competing for attention, and the Registry recorded
        // that the prompt is what acknowledged it.
        Assert.Equal(0, _viewModel.UnreadCount);
        Assert.Contains(
            _registry.Sessions[row.Id].Transitions,
            entry => entry.Cause?.Contains("auto-ack of Unread", StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// It acknowledges a blocked session too, not only a finished one — the operator cannot have
    /// typed the next prompt without dealing with the permission that was in the way.
    /// </summary>
    [Fact]
    public async Task A_new_prompt_auto_acks_a_blocked_session()
    {
        Prompt("blocked", "p-1");
        _clock.AdvanceMinutes(1);
        Publish(new Notification
        {
            SessionId = new SessionId("blocked"),
            Timestamp = _clock.Now,
            Cwd = Workspace,
            NotificationType = "permission_prompt",
        });

        Assert.True(await Until(() => Row("blocked")?.State == SessionState.NeedsPermission));

        _clock.AdvanceMinutes(1);
        Prompt("blocked", "p-2");

        Assert.True(await Until(() => Row("blocked")?.State == SessionState.Working));
        Assert.Equal(0, _viewModel.NeedsYouCount);
    }

    // ---- Both tiers, one path ---------------------------------------------------------------------

    /// <summary>
    /// <strong>The two tiers arrive the same way.</strong> TS §I.3's requirement, asserted: both
    /// were applied by the consumer, off one channel, and neither reached the Registry any other
    /// way. Phase 3's focus inference joins this same path.
    /// </summary>
    [Fact]
    public async Task Both_tiers_travel_the_same_pipeline()
    {
        var manual = await GivenAnUnreadRow("manual");
        var automatic = await GivenAnUnreadRow("automatic");

        var appliedBefore = _consumer.AppliedCount;

        manual.AcknowledgeCommand.Execute(null);
        Prompt("automatic", "p-2");

        Assert.True(await Until(() =>
            manual.State == SessionState.Acked && automatic.State == SessionState.Working));

        // Two events, both applied by the one writer.
        Assert.Equal(appliedBefore + 2, _consumer.AppliedCount);
        Assert.Equal(0, _viewModel.UnreadCount);
    }

    /// <summary>
    /// Acknowledging one session leaves the others exactly as they were. An implementation that
    /// acked everything passes every test that looks only at the row it clicked.
    /// </summary>
    [Fact]
    public async Task Acknowledging_one_session_leaves_the_others_alone()
    {
        var first = await GivenAnUnreadRow("finished-one");
        await GivenAnUnreadRow("finished-two");
        await GivenAnUnreadRow("finished-three");

        first.AcknowledgeCommand.Execute(null);
        Assert.True(await Until(() => first.State == SessionState.Acked));

        Assert.Equal(SessionState.Unread, _registry.Sessions[new SessionId("finished-two")].State);
        Assert.Equal(SessionState.Unread, _registry.Sessions[new SessionId("finished-three")].State);
        Assert.Equal(2, _viewModel.UnreadCount);
    }

    /// <summary>
    /// A second click on an already-acknowledged session changes nothing. The row is collapsed by
    /// then, so this is what a double-click races with — and it is harmless because the Registry
    /// declines it rather than because the UI prevented it.
    /// </summary>
    [Fact]
    public async Task Acknowledging_twice_is_harmless()
    {
        var row = await GivenAnUnreadRow("finished");

        row.AcknowledgeCommand.Execute(null);
        row.AcknowledgeCommand.Execute(null);

        Assert.True(await Until(() => row.State == SessionState.Acked));
        Assert.Equal(SessionState.Acked, _registry.Sessions[row.Id].State);
        Assert.Equal(1, _viewModel.QuietCount);
    }
}
