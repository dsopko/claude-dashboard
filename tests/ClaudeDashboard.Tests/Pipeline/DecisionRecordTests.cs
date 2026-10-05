using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// Every decision the dashboard makes while handling an event or a tick becomes a row in the
/// record beside the event that caused it (T1.37, issue #48).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Driven through the real pipeline, read off the real channel.</strong> Each test posts
/// events the way ingress does and reads the <see cref="ArchiveRecord"/>s the consumer hands to
/// the archive — the same records the writer persists. The store's half of the contract (one
/// transaction, the <c>event_id</c> join) is <c>DecisionTableTests</c>'.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class DecisionRecordTests : IAsyncLifetime
{
    /// <summary>A recognisable text for the prompt, the title and the body of the planted events.</summary>
    private const string Marker = "zqx-operator-text-marker-7f4";

    private const string Cwd = @"C:\projects\dashboard";
    private static readonly SessionId Id = new("s-1");

    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RecordingSoundPlayer _player = new();
    private readonly RosterStore _rosters = new(new RecordingEventSink());
    private readonly List<ArchiveRecord> _records = [];

    private DecisionRecorder _recorder = null!;
    private EventConsumer _consumer = null!;

    public Task InitializeAsync()
    {
        _recorder = new DecisionRecorder(_registry, _rosters, _archive, Logger.None);

        var engine = new SoundPolicyEngine(
            _player, _clock, _guard, new SoundPolicyOptions(), _recorder);

        // The live composition's own subscription, mirrored (AppHost; ReplaySwitch does the
        // same): without it no notice ever plays, and the sound rows would pass vacuously.
        _registry.SessionChanged += (_, e) =>
            engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, _rosters.Book));

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            engine,
            _clock,
            _guard,
            Logger.None,
            new RecordingUiTick(),
            _archive,
            _rosters,
            recorder: _recorder,
            tickInterval: TimeSpan.FromMilliseconds(20),
            silenceThreshold: TimeSpan.FromMinutes(10));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        return Task.CompletedTask;
    }

    // ---- The rows an event produces -----------------------------------------------------------

    /// <summary>A session's first event rides with a SessionAdded row.</summary>
    [Fact]
    public async Task A_new_session_is_recorded_added_beside_its_event()
    {
        Publish(Prompt());

        var record = await RecordWith(DecisionKind.SessionAdded);

        Assert.IsType<UserPromptSubmit>(record.Event);
        var added = Row(record, DecisionKind.SessionAdded);

        Assert.Equal(Id.Value, added.SessionId);
        Assert.Equal(nameof(SessionState.Working), added.ToState);
    }

    /// <summary>The state move and the notice it caused are one record.</summary>
    [Fact]
    public async Task A_state_move_and_its_notice_share_the_record()
    {
        Publish(Prompt());
        await RecordWith(DecisionKind.SessionAdded);

        _clock.AdvanceMinutes(1);
        Publish(new Stop { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, LastAssistantMessage = Marker });

        var record = await RecordWith(DecisionKind.StateMoved);

        Assert.IsType<Stop>(record.Event);

        var moved = Row(record, DecisionKind.StateMoved);
        Assert.Equal(nameof(SessionState.Working), moved.FromState);
        Assert.Equal(nameof(SessionState.Unread), moved.ToState);

        var notice = Row(record, DecisionKind.NoticePlayed);
        Assert.Equal(Id.Value, notice.SessionId);
        Assert.Equal(SoundId.Finished.ToString(), notice.Reason);
    }

    /// <summary>A stale event is declined, and the decline says why.</summary>
    [Fact]
    public async Task A_stale_event_is_recorded_declined()
    {
        Publish(Prompt());
        await RecordWith(DecisionKind.SessionAdded);

        // Older than the session's last-seen: the Registry's timestamp guard declines it.
        Publish(new Notification
        {
            SessionId = Id,
            Timestamp = _clock.Now - TimeSpan.FromMinutes(5),
            Cwd = Cwd,
            NotificationType = "permission_prompt",
        });

        var record = await RecordWith(DecisionKind.EventDeclined);

        Assert.IsType<Notification>(record.Event);

        var declined = Row(record, DecisionKind.EventDeclined);
        Assert.Equal(nameof(SessionState.Working), declined.FromState);
        Assert.Equal(nameof(ApplyOutcome.Stale), declined.Reason);
    }

    /// <summary>The Ack is archived — its absence was exactly issue #48's hole — with its rows.</summary>
    [Fact]
    public async Task An_ack_is_archived_and_recorded_applied()
    {
        Publish(Prompt());
        _clock.AdvanceMinutes(1);
        Publish(new Stop { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd });
        await RecordWith(DecisionKind.StateMoved);

        _clock.AdvanceMinutes(1);
        Publish(new Ack { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Source = AckSource.Manual });

        var record = await RecordWith(DecisionKind.AckApplied);

        Assert.IsType<Ack>(record.Event);
        Assert.Equal(nameof(AckSource.Manual), Row(record, DecisionKind.AckApplied).Reason);

        var moved = Row(record, DecisionKind.StateMoved);
        Assert.Equal(nameof(SessionState.Unread), moved.FromState);
        Assert.Equal(nameof(SessionState.Acked), moved.ToState);
    }

    /// <summary>A declined ack gets its own kind, so the join finds unanswered clicks.</summary>
    [Fact]
    public async Task A_stale_ack_is_recorded_declined_as_an_ack()
    {
        Publish(Prompt());
        await RecordWith(DecisionKind.SessionAdded);

        Publish(new Ack
        {
            SessionId = Id,
            Timestamp = _clock.Now - TimeSpan.FromMinutes(5),
            Cwd = Cwd,
            Source = AckSource.Manual,
        });

        var record = await RecordWith(DecisionKind.AckDeclined);

        Assert.IsType<Ack>(record.Event);
        Assert.Equal(nameof(ApplyOutcome.Stale), Row(record, DecisionKind.AckDeclined).Reason);
    }

    /// <summary>A SessionStart reviving an ended session is a refresh, not an add.</summary>
    /// <remarks>
    /// A <c>SessionStart</c> that changes nothing — same state, same cwd — is declined as
    /// Ignored and recorded as such; the refresh row is for the starts that DO something, a
    /// revival being the clearest. One session id, two lives, and the record says so without
    /// pretending a second session appeared.
    /// </remarks>
    [Fact]
    public async Task A_session_start_reviving_an_ended_session_is_recorded_refreshed()
    {
        Publish(Prompt());
        _clock.AdvanceMinutes(1);
        Publish(new SessionEnd { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Reason = "clear" });
        await RecordWith(DecisionKind.SessionEnded);

        _clock.AdvanceMinutes(1);
        Publish(new SessionStart { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Source = "resume" });

        var record = await RecordWith(DecisionKind.SessionRefreshed);

        Assert.IsType<SessionStart>(record.Event);
        Assert.Equal("resume", Row(record, DecisionKind.SessionRefreshed).Reason);
    }

    /// <summary>An unknown source is recorded as "other", never passed through.</summary>
    /// <remarks>
    /// The source is a matcher by contract, but it arrives as payload text and nothing stops a
    /// payload from carrying anything there. The row names the known five by their wire
    /// spelling and folds everything else to one word (T1.24).
    /// </remarks>
    [Fact]
    public async Task An_unknown_session_start_source_is_recorded_as_other()
    {
        Publish(Prompt());
        _clock.AdvanceMinutes(1);
        Publish(new SessionEnd { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Reason = "clear" });
        await RecordWith(DecisionKind.SessionEnded);

        _clock.AdvanceMinutes(1);
        Publish(new SessionStart { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Source = Marker });

        var record = await RecordWith(DecisionKind.SessionRefreshed);

        Assert.Equal("other", Row(record, DecisionKind.SessionRefreshed).Reason);
    }

    /// <summary>The session ending is its own kind, so removal work can find it later.</summary>
    [Fact]
    public async Task A_session_end_is_recorded_ended()
    {
        Publish(Prompt());
        await RecordWith(DecisionKind.SessionAdded);

        _clock.AdvanceMinutes(1);
        Publish(new SessionEnd { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Reason = "clear" });

        var record = await RecordWith(DecisionKind.SessionEnded);

        var ended = Row(record, DecisionKind.SessionEnded);
        Assert.Equal(nameof(SessionState.Working), ended.FromState);
        Assert.Equal(nameof(SessionState.Ended), ended.ToState);
    }

    /// <summary>A moved cwd re-derives the group, and the row carries both keys.</summary>
    [Fact]
    public async Task A_moved_cwd_is_recorded_as_the_group_rederiving()
    {
        Publish(Prompt());
        await RecordWith(DecisionKind.SessionAdded);

        _clock.AdvanceMinutes(1);
        Publish(new CwdChanged { SessionId = Id, Timestamp = _clock.Now, Cwd = @"C:\projects\elsewhere" });

        var record = await RecordWith(DecisionKind.GroupRederived);

        var rederived = Row(record, DecisionKind.GroupRederived);
        Assert.Contains("from=", rederived.Detail, StringComparison.Ordinal);
        Assert.Contains("to=", rederived.Detail, StringComparison.Ordinal);
    }

    // ---- The rows a tick produces -------------------------------------------------------------

    /// <summary>The silence sweep's row rides a tick record, which carries no event.</summary>
    [Fact]
    public async Task The_silence_sweep_writes_a_tick_record_with_no_event()
    {
        Publish(Prompt());
        await RecordWith(DecisionKind.SessionAdded);

        _clock.AdvanceMinutes(11);

        var record = await RecordWith(DecisionKind.SilenceSwept);

        Assert.Null(record.Event);

        var swept = Row(record, DecisionKind.SilenceSwept);
        Assert.Equal(Id.Value, swept.SessionId);
        Assert.Equal(nameof(SessionState.Working), swept.FromState);
        Assert.Equal(nameof(SessionState.Interrupted), swept.ToState);
        Assert.Equal(SilenceWatch.Cause, swept.Reason);
        Assert.Contains("silentMinutes=11", swept.Detail, StringComparison.Ordinal);
    }

    /// <summary>A nudge row carries its rung and how long the operator has been waited on.</summary>
    [Fact]
    public async Task A_nudge_carries_its_rung_and_the_wait()
    {
        Publish(Prompt());
        await RecordWith(DecisionKind.SessionAdded);

        _clock.AdvanceMinutes(1);
        Publish(new Notification
        {
            SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, NotificationType = "permission_prompt",
        });
        await RecordWith(DecisionKind.StateMoved);

        // Past the ladder's first rung (2 minutes); the consumer's own tick evaluates it.
        _clock.AdvanceMinutes(3);

        var record = await RecordWith(DecisionKind.NudgePlayed);

        Assert.Null(record.Event);

        var nudge = Row(record, DecisionKind.NudgePlayed);
        Assert.Equal(Id.Value, nudge.SessionId);
        Assert.Equal(SoundId.Permission.ToString(), nudge.Reason);
        Assert.Contains("rung=", nudge.Detail, StringComparison.Ordinal);
        Assert.Contains("waitedMinutes=3", nudge.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>With no output, the notice is recorded dropped, and never played</strong> (T1.55,
    /// issue #72). Through the real pipeline and the real engine: the record the archive receives.
    /// </summary>
    [Fact]
    public async Task With_no_output_a_due_notice_is_recorded_dropped_and_not_played()
    {
        _player.Outcome = SoundOutcome.NoOutput;

        Publish(Prompt());
        await RecordWith(DecisionKind.SessionAdded);

        _clock.AdvanceMinutes(1);
        Publish(new Stop { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, LastAssistantMessage = Marker });

        var record = await RecordWith(DecisionKind.SoundDropped);

        var dropped = Row(record, DecisionKind.SoundDropped);
        Assert.Equal(Id.Value, dropped.SessionId);
        Assert.Equal(nameof(SoundOutcome.NoOutput), dropped.Reason);
        Assert.Equal($"kind=Notice sound={SoundId.Finished}", dropped.Detail);

        Assert.DoesNotContain(_records, r => r.Decisions.Any(d => d.Kind == DecisionKind.NoticePlayed));
    }

    // ---- Mutes --------------------------------------------------------------------------------

    /// <summary>The mute is recorded, and what it silenced is recorded suppressed, not lost.</summary>
    [Fact]
    public async Task A_mute_is_recorded_and_so_is_what_it_suppressed()
    {
        Publish(new SoundCommand { SessionId = default, Timestamp = _clock.Now, Cwd = "", Kind = SoundCommandKind.MuteAll });

        var muted = await RecordWith(DecisionKind.MuteApplied);

        // A synthetic channel rider: decisions only, no events row.
        Assert.Null(muted.Event);
        Assert.Equal(nameof(SoundCommandKind.MuteAll), Row(muted, DecisionKind.MuteApplied).Reason);

        _clock.AdvanceMinutes(1);
        Publish(Prompt());
        _clock.AdvanceMinutes(1);
        Publish(new Stop { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd });

        var record = await RecordWith(DecisionKind.NoticeSuppressed);

        var suppressed = Row(record, DecisionKind.NoticeSuppressed);
        Assert.Equal(Id.Value, suppressed.SessionId);
        Assert.Equal(nameof(SuppressionReason.AllMuted), suppressed.Reason);
        Assert.Contains($"sound={SoundId.Finished}", suppressed.Detail, StringComparison.Ordinal);

        Assert.Empty(_player.Played);
        Assert.DoesNotContain(_records, r => r.Decisions.Any(d => d.Kind == DecisionKind.NoticePlayed));
    }

    /// <summary>
    /// A timed mute's lapse produces no event by design, so the tick records it — "a predicate,
    /// not a timer" made visible.
    /// </summary>
    [Fact]
    public async Task A_timed_mute_lapsing_is_recorded_on_the_tick()
    {
        var until = _clock.Now + TimeSpan.FromMinutes(1);

        Publish(new SoundCommand
        {
            SessionId = default, Timestamp = _clock.Now, Cwd = "", Kind = SoundCommandKind.MuteAll, Until = until,
        });
        await RecordWith(DecisionKind.MuteApplied);

        _clock.AdvanceMinutes(2);

        var record = await RecordWith(DecisionKind.MuteExpired);

        Assert.Null(record.Event);
        Assert.Contains($"until={until:o}", Row(record, DecisionKind.MuteExpired).Detail, StringComparison.Ordinal);
    }

    // ---- Synthetic riders and rows born on other threads --------------------------------------

    /// <summary>A roster edit is a decisions-only record: no payload is worth an events row.</summary>
    [Fact]
    public async Task A_roster_edit_is_a_decisions_only_record()
    {
        Publish(new RostersChanged { SessionId = default, Timestamp = _clock.Now, Cwd = "" });

        var record = await RecordWith(DecisionKind.RosterEdited);

        Assert.Null(record.Event);
    }

    /// <summary>
    /// Decisions born off the consumer thread ride their own record with no event — never an
    /// unrelated event's, whose <c>event_id</c> would be a false attribution.
    /// </summary>
    /// <remarks>
    /// These three kinds are born on other threads in the product: the drop on whichever thread
    /// hit the full channel, the tray light on the Dispatcher, the surfaced window on a Kestrel
    /// thread. <c>AppHost</c> wires each source to <see cref="IDecisionLog.External"/>; this
    /// asserts what the recorder does with whatever arrives there.
    /// </remarks>
    [Fact]
    public async Task External_decisions_ride_their_own_record_and_no_events_row()
    {
        _recorder.External(new Decision(_clock.Now, Id.Value, DecisionKind.EventDropped, Reason: "pipeline"));
        _recorder.External(new Decision(_clock.Now, Id.Value, DecisionKind.TrayLightChanged, FromState: "Green", ToState: "Red"));
        _recorder.External(new Decision(_clock.Now, null, DecisionKind.WindowSurfaced));

        // The next scope carries them out; an ordinary event opens one.
        Publish(Prompt());

        var external = await RecordWith(DecisionKind.EventDropped);

        Assert.Null(external.Event);
        Assert.Equal("pipeline", Row(external, DecisionKind.EventDropped).Reason);
        Assert.Equal("Red", Row(external, DecisionKind.TrayLightChanged).ToState);
        _ = Row(external, DecisionKind.WindowSurfaced);

        // And the event that carried them out still has its own record, without them.
        var added = await RecordWith(DecisionKind.SessionAdded);
        Assert.NotNull(added.Event);
        Assert.DoesNotContain(added.Decisions, d => d.Kind == DecisionKind.EventDropped);
    }

    // ---- What no row may carry ----------------------------------------------------------------

    // ---- Harness ------------------------------------------------------------------------------

    private UserPromptSubmit Prompt() => new()
    {
        SessionId = Id,
        Timestamp = _clock.Now,
        Cwd = Cwd,
        PromptId = "p-1",
        Prompt = Marker,
        SessionTitle = Marker,
        Payload = new PayloadJson($$"""{"prompt":"{{Marker}}"}"""),
    };

    private void Publish(InboundEvent inboundEvent) => Assert.True(_pipeline.Sink.TryPublish(inboundEvent));

    /// <summary>Drains the archive channel until a record carrying the kind appears.</summary>
    private async Task<ArchiveRecord> RecordWith(DecisionKind kind)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            while (_archive.Reader.TryRead(out var record))
            {
                _records.Add(record);
            }

            if (_records.FirstOrDefault(r => r.Decisions.Any(d => d.Kind == kind)) is { } found)
            {
                return found;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new Xunit.Sdk.XunitException($"No record carrying {kind} arrived.");
    }

    private static Decision Row(ArchiveRecord record, DecisionKind kind) =>
        Assert.Single(record.Decisions, d => d.Kind == kind);
}
