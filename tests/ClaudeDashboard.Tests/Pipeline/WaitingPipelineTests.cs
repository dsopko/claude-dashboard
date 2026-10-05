using System.Globalization;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Ingress;
using Serilog;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// Waiting through the whole pipeline: raw bodies through the real mapper, consumer, Registry,
/// sound engine and decisions recorder, with every log line captured at Verbose (T1.41, issue #52).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The command is never read.</strong> Every body plants a command and a description marker. The command
/// appears nowhere downstream of the wire: not in a log line, not in a decisions row, not in the Session, not in the
/// row. The description is shown on the row by design.
/// </para>
/// <para>
/// The <c>events</c> table is not in that list, and deliberately: it keeps every body verbatim —
/// prompts, answers, and now this command — which is the archive's contract since T1.17, and a
/// change to that contract is not this task's to make. It is flagged in the status report.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class WaitingPipelineTests : IAsyncLifetime
{
    private const string Description = "zqx-description-marker-8p3 Run the test suite";
    private const string Command = BackgroundTaskReaderTests.PlantedCommand;

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RosterStore _rosters = new(new RecordingEventSink());
    private readonly CapturingSink _log = new();
    private readonly List<ArchiveRecord> _records = [];

    private Logger _logger = null!;
    private EventConsumer _consumer = null!;

    public Task InitializeAsync()
    {
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_log).CreateLogger();

        var recorder = new DecisionRecorder(_registry, _rosters, _archive, _logger);
        var engine = new SoundPolicyEngine(new RecordingSoundPlayer(), _clock, _guard, new SoundPolicyOptions(), recorder);

        _registry.SessionChanged += (_, e) =>
            engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, _rosters.Book));

        _consumer = new EventConsumer(
            _pipeline, _registry, engine, _clock, _guard, _logger, new RecordingUiTick(), _archive, _rosters,
            recorder: recorder,
            tickInterval: TimeSpan.FromHours(1));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        _logger?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <strong>An unseen task type writes a decisions row</strong> — an enum reason and a count,
    /// never the type or the description — and the Stop finishes as it would have before.
    /// </summary>
    [Fact]
    public async Task An_unseen_task_type_is_recorded_by_count_and_finishes_as_before()
    {
        Publish(Prompt("go", "p-1"));
        _clock.AdvanceMinutes(1);
        Publish(Stop("p-1", """[{"id":"x1","type":"cron","status":"running","description":"DESCRIPTION"}]"""));

        var record = await RecordWith(DecisionKind.TaskTypeUnrecognised);

        var row = Assert.Single(record.Decisions, d => d.Kind == DecisionKind.TaskTypeUnrecognised);
        Assert.Equal(nameof(TaskTypeReason.UnrecognisedType), row.Reason);
        Assert.Equal("count=1", row.Detail);

        Assert.Contains(record.Decisions, d => d.Kind == DecisionKind.StateMoved && d.ToState == nameof(SessionState.Unread));
    }

    /// <summary>
    /// <strong>The decisions record says what a prompt meant</strong>: a machine prompt is recorded
    /// as one, and the operator's own prompt on a finished row as an acknowledgment.
    /// </summary>
    [Fact]
    public async Task The_decisions_record_tells_a_machine_prompt_from_an_acknowledgment()
    {
        Publish(Prompt("go", "p-1"));
        _clock.AdvanceMinutes(1);
        Publish(Stop("p-1", "[]"));
        await RecordWith(DecisionKind.NoticePlayed);

        _clock.AdvanceMinutes(1);
        Publish(Prompt("<cross-session-message from=\"director\">next</cross-session-message>", "p-2"));
        var machine = await RecordWhere(r => r.Event is UserPromptSubmit { PromptId: "p-2" });

        _clock.AdvanceMinutes(1);
        Publish(Stop("p-2", "[]"));
        _clock.AdvanceMinutes(1);
        Publish(Prompt("now the docs", "p-3"));
        var typed = await RecordWhere(r => r.Event is UserPromptSubmit { PromptId: "p-3" });

        Assert.Equal(nameof(PromptMeaning.MachinePrompt), MovedOf(machine).Reason);
        Assert.Equal(nameof(PromptMeaning.AutoAcknowledgment), MovedOf(typed).Reason);

        static Decision MovedOf(ArchiveRecord record) =>
            Assert.Single(record.Decisions, d => d.Kind == DecisionKind.StateMoved);
    }

    /// <summary>
    /// <strong>The command appears nowhere downstream of the wire; the description appears only
    /// on the row.</strong>
    /// </summary>
    /// <remarks>
    /// Waiting entered, held through a subagent batch, left by the wake-up, and finished — every
    /// log line of the run rendered with its properties, every decisions row, the Session as a
    /// record prints it, and every string the row binds to.
    /// </remarks>
    [Fact]
    public async Task The_command_is_never_stored_shown_or_logged()
    {
        Publish(Prompt("go", "p-1"));
        _clock.AdvanceMinutes(1);
        Publish(Stop("p-1", """
            [
              {"id":"b1","type":"shell","status":"running","description":"DESCRIPTION","command":"COMMAND"},
              {"id":"x1","type":"cron","status":"running","description":"DESCRIPTION","command":"COMMAND"}
            ]
            """));
        await RecordWith(DecisionKind.TaskTypeUnrecognised);

        Assert.Equal(SessionState.Waiting, _registry.Sessions[new SessionId("s-1")].State);

        // Held through the row as the screen would show it, which is where the description belongs.
        var row = new SessionViewModel(_registry.Sessions[new SessionId("s-1")]);
        Assert.Contains(Description, row.WaitingSummary, StringComparison.Ordinal);
        Assert.Contains(row.WaitingOnLines, line => line.Description == Description);

        _clock.AdvanceMinutes(1);
        Publish(new PostToolBatch { SessionId = new SessionId("s-1"), Timestamp = _clock.Now, Cwd = @"C:\w" });
        _clock.AdvanceMinutes(1);
        Publish(Prompt("<task-notification>\n<task-id>b1</task-id>", "p-2"));
        _clock.AdvanceMinutes(1);
        Publish(Stop("p-2", "[]"));
        await RecordWhere(r => r.Event is Stop { PromptId: "p-2" });

        // Every log line of the run, message and properties.
        var lines = _log.Events
            .Select(e => e.RenderMessage(CultureInfo.InvariantCulture) + " " +
                string.Join(" ", e.Properties.Select(p => p.Value.ToString())))
            .ToList();

        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, line => line.Contains(Command, StringComparison.Ordinal));

        // Every decisions row, every field.
        var fields = _records.SelectMany(r => r.Decisions)
            .SelectMany(d => new[] { d.SessionId, d.FromState, d.ToState, d.Reason, d.Detail })
            .OfType<string>()
            .ToList();

        Assert.DoesNotContain(fields, field => field.Contains(Command, StringComparison.Ordinal));

        // The Session as a record prints it, and every string the row binds to.
        var session = _registry.Sessions[new SessionId("s-1")];
        Assert.DoesNotContain(Command, session.ToString(), StringComparison.Ordinal);

        var bound = typeof(SessionViewModel).GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.GetIndexParameters().Length == 0)
            .Select(p => (string?)p.GetValue(row))
            .OfType<string>();

        Assert.DoesNotContain(bound, text => text.Contains(Command, StringComparison.Ordinal));
    }

    // ---- Harness -------------------------------------------------------------------------------

    private void Publish(InboundEvent inboundEvent) => Assert.True(_pipeline.Sink.TryPublish(inboundEvent));

    private UserPromptSubmit Prompt(string text, string promptId) =>
        Map<UserPromptSubmit>($$"""{"hook_event_name":"UserPromptSubmit","session_id":"s-1","cwd":"C:\\w","prompt_id":"{{promptId}}","prompt":{{JsonSerializer.Serialize(text)}}}""");

    private Stop Stop(string promptId, string tasks) =>
        Map<Stop>($$"""
            {"hook_event_name":"Stop","session_id":"s-1","cwd":"C:\\w","prompt_id":"{{promptId}}","last_assistant_message":"so far","background_tasks":{{tasks.Replace("DESCRIPTION", Description, StringComparison.Ordinal).Replace("COMMAND", Command, StringComparison.Ordinal)}}}
            """);

    private T Map<T>(string body)
        where T : InboundEvent
    {
        var payload = JsonSerializer.Deserialize<HookPayload>(body, Options)!;
        return Assert.IsType<T>(new HookEventMapper(_clock).Map(payload, new PayloadJson(body)).Event);
    }

    private Task<ArchiveRecord> RecordWith(DecisionKind kind) =>
        RecordWhere(r => r.Decisions.Any(d => d.Kind == kind));

    private async Task<ArchiveRecord> RecordWhere(Func<ArchiveRecord, bool> match)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            while (_archive.Reader.TryRead(out var record))
            {
                _records.Add(record);
            }

            if (_records.FirstOrDefault(match) is { } found)
            {
                return found;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new Xunit.Sdk.XunitException("No matching record arrived.");
    }
}
