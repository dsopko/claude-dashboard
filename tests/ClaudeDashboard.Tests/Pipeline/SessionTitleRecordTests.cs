using System.Globalization;
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
/// Each row with a session holds the session's name, and each decision row its full path, as the
/// Registry holds them after the event is applied (T1.69, issue #98). Through the real consumer, the
/// real recorder and the real sound engine, under a fake clock; the records the archive receives.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class SessionTitleRecordTests : IAsyncLifetime
{
    /// <summary>A name no other text contains, so a search for it finds only the name.</summary>
    private const string Title = "zqx-session-name-marker-3c1";

    private const string Cwd = @"C:\projects\payments-api";
    private static readonly SessionId Id = new("s-1");

    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RecordingSoundPlayer _player = new();
    private readonly RosterStore _rosters = new(new RecordingEventSink());
    private readonly RecordingLogSink _log = new();
    private readonly List<ArchiveRecord> _records = [];

    private Logger _logger = null!;
    private DecisionRecorder _recorder = null!;
    private EventConsumer _consumer = null!;

    public Task InitializeAsync()
    {
        _logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_log).CreateLogger();
        _recorder = new DecisionRecorder(_registry, _rosters, _archive, _logger);

        var engine = new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions(), _recorder);

        _registry.SessionChanged += (_, e) =>
            engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, _rosters.Book));

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            engine,
            _clock,
            _guard,
            _logger,
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
        _logger?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The event row holds the session's name, and each decision row the name and the full path. A
    /// session with no name stores NULL for its name, and still its path.
    /// </summary>
    [Fact]
    public async Task Event_and_decision_rows_hold_the_name_and_the_path()
    {
        Publish(Prompt(Id, Title));
        var named = await RecordWith(DecisionKind.SessionAdded, Id.Value);

        Assert.Equal(Title, named.EventSessionTitle);
        Assert.All(named.Decisions, decision => Assert.Equal((Title, Cwd), (decision.SessionTitle, decision.Cwd)));

        var unnamed = new SessionId("s-2");
        Publish(Prompt(unnamed, title: null));
        var record = await RecordWith(DecisionKind.SessionAdded, unnamed.Value);

        Assert.Null(record.EventSessionTitle);
        Assert.All(record.Decisions, decision => Assert.Equal((null, Cwd), (decision.SessionTitle, decision.Cwd)));
    }

    /// <summary>
    /// <strong>A decision made by the clock</strong> (a reminder, "went quiet") has no event, and still
    /// holds the name and the full path: it reads the Registry, not an event.
    /// </summary>
    [Fact]
    public async Task A_decision_made_by_the_clock_holds_the_name_and_the_path()
    {
        Publish(Prompt(Id, Title));
        await RecordWith(DecisionKind.SessionAdded, Id.Value);

        _clock.AdvanceMinutes(1);
        Publish(new Notification { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, NotificationType = "permission_prompt" });
        await RecordWith(DecisionKind.StateMoved, Id.Value);

        _clock.AdvanceMinutes(3);
        var nudge = Row(await RecordWith(DecisionKind.NudgePlayed, Id.Value), DecisionKind.NudgePlayed);

        Assert.Equal((Title, Cwd), (nudge.SessionTitle, nudge.Cwd));

        // Back to work, then quiet past the threshold: the sweep's row, on the tick.
        _clock.AdvanceMinutes(1);
        Publish(Prompt(Id, title: null, promptId: "p-2"));
        _clock.AdvanceMinutes(11);
        var swept = await RecordWith(DecisionKind.SilenceSwept, Id.Value);

        Assert.Null(swept.Event);
        Assert.Equal((Title, Cwd), (Row(swept, DecisionKind.SilenceSwept).SessionTitle, Row(swept, DecisionKind.SilenceSwept).Cwd));
    }

    /// <summary>
    /// <strong>A renamed session:</strong> rows before the rename hold the old name, and rows from the
    /// rename on, its own row included, hold the new one.
    /// </summary>
    [Fact]
    public async Task A_rename_changes_the_name_from_its_own_row_on()
    {
        Publish(Prompt(Id, "Old name"));
        var before = await RecordWith(DecisionKind.SessionAdded, Id.Value);

        _clock.AdvanceMinutes(1);
        Publish(new PostToolBatch { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, SessionTitle = "New name" });
        var rename = await RecordWithEvent<PostToolBatch>();

        _clock.AdvanceMinutes(1);
        Publish(new Notification { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, NotificationType = "permission_prompt" });
        var after = await RecordWith(DecisionKind.StateMoved, Id.Value, notBefore: rename);

        Assert.Equal("Old name", before.EventSessionTitle);
        Assert.All(before.Decisions, decision => Assert.Equal("Old name", decision.SessionTitle));
        Assert.Equal("New name", rename.EventSessionTitle);
        Assert.All(rename.Decisions, decision => Assert.Equal("New name", decision.SessionTitle));
        Assert.Equal("New name", after.EventSessionTitle);
        Assert.All(after.Decisions.Where(decision => decision.SessionId == Id.Value), decision => Assert.Equal("New name", decision.SessionTitle));
    }

    /// <summary>
    /// <strong>A decision about a session the Registry does not hold</strong> takes the name and the path
    /// from its event. A decision from another thread about such a session has no event: NULL.
    /// </summary>
    [Fact]
    public async Task A_session_the_registry_does_not_hold_takes_the_name_from_its_event()
    {
        var stranger = new SessionId("s-unknown");

        Publish(new Ack { SessionId = stranger, Timestamp = _clock.Now, Cwd = @"C:\elsewhere", Source = AckSource.Manual, SessionTitle = "Stranger" });
        var record = await RecordWith(DecisionKind.AckDeclined, stranger.Value);

        Assert.False(_registry.Sessions.ContainsKey(stranger));
        Assert.Equal("Stranger", record.EventSessionTitle);
        Assert.Equal(("Stranger", @"C:\elsewhere"), (Row(record, DecisionKind.AckDeclined).SessionTitle, Row(record, DecisionKind.AckDeclined).Cwd));

        _recorder.External(new Decision(_clock.Now, "s-gone", DecisionKind.EventDropped, Reason: "pipeline"));
        Publish(Prompt(Id, Title));
        var dropped = Row(await RecordWith(DecisionKind.EventDropped, "s-gone"), DecisionKind.EventDropped);

        Assert.Equal<(string?, string?)>((null, null), (dropped.SessionTitle, dropped.Cwd));
    }

    private static UserPromptSubmit Prompt(SessionId id, string? title, string promptId = "p-1") => new()
    {
        SessionId = id,
        Timestamp = DateTimeOffset.MinValue,
        Cwd = Cwd,
        PromptId = promptId,
        Prompt = "run the tests",
        SessionTitle = title,
    };

    private void Publish(InboundEvent inboundEvent)
    {
        if (inboundEvent.Timestamp == DateTimeOffset.MinValue)
        {
            inboundEvent = inboundEvent with { Timestamp = _clock.Now };
        }

        Assert.True(_pipeline.Sink.TryPublish(inboundEvent));
    }

    /// <summary>Drains the archive until a record carries the kind for the session, after an earlier record.</summary>
    private async Task<ArchiveRecord> RecordWith(DecisionKind kind, string sessionId, ArchiveRecord? notBefore = null)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            Drain();

            var start = notBefore is null ? 0 : _records.IndexOf(notBefore) + 1;

            if (_records.Skip(start).FirstOrDefault(r => r.Decisions.Any(d => d.Kind == kind && d.SessionId == sessionId)) is { } found)
            {
                return found;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new Xunit.Sdk.XunitException($"No record carrying {kind} for {sessionId} arrived.");
    }

    private async Task<ArchiveRecord> RecordWithEvent<T>()
        where T : InboundEvent
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            Drain();

            if (_records.FirstOrDefault(r => r.Event is T) is { } found)
            {
                return found;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new Xunit.Sdk.XunitException($"No record carrying a {typeof(T).Name} arrived.");
    }

    private void Drain()
    {
        while (_archive.Reader.TryRead(out var record))
        {
            _records.Add(record);
        }
    }

    private static Decision Row(ArchiveRecord record, DecisionKind kind) =>
        Assert.Single(record.Decisions, d => d.Kind == kind);
}
