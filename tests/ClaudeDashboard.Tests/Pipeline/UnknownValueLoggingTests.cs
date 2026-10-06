using System.Globalization;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// The log file says when an unknown value arrives, and traces each event at Debug (T1.79, issue #9).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The operator's design:</strong> the database is the full record. At the normal level the log file says
/// what someone should notice, and an unknown value is that: one Information line the first time, for each event,
/// field and value. At Debug it writes one line for each event, with its name, session, type and outcome.
/// </para>
/// <para>
/// The consumer and the recorder log to one sink at Debug, so each test can tell the levels apart. Each wait is on
/// the consumer's counts, and the consumer writes a line before it counts.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class UnknownValueLoggingTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly FakeClock _clock = new();
    private readonly CapturingSink _sink = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RosterStore _rosters = new(new RecordingEventSink());
    private readonly List<ArchiveRecord> _records = [];

    private Logger _logger = null!;
    private EventConsumer _consumer = null!;
    private int _published;

    public Task InitializeAsync()
    {
        _logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(_sink).CreateLogger();

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            new SoundPolicyEngine(new RecordingSoundPlayer(), _clock, _guard, new SoundPolicyOptions()),
            _clock,
            _guard,
            _logger,
            new RecordingUiTick(),
            _archive,
            _rosters,
            recorder: new DecisionRecorder(_registry, _rosters, _archive, _logger),
            tickInterval: TimeSpan.FromMinutes(5));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        _logger?.Dispose();
        return Task.CompletedTask;
    }

    // ---- The normal level: one line for each new unknown value -------------------------------------

    /// <summary>
    /// Two arrivals of one unknown value write one Information line, not two; a second unknown value writes its own.
    /// For each field read into a fixed list.
    /// </summary>
    [Theory]
    [InlineData("notification", "Claude Code sent a Notification of type \"{0}\", which this build does not know. It changed nothing.")]
    [InlineData("start", "Claude Code sent a SessionStart with source \"{0}\", which this build does not know. The value changed nothing; the event was handled as usual.")]
    [InlineData("failure", "Claude Code sent a StopFailure with error \"{0}\", which this build does not know. The value changed nothing; the event was handled as usual.")]
    [InlineData("end", "Claude Code sent a SessionEnd with reason \"{0}\", which this build does not know. The value changed nothing; the event was handled as usual.")]
    [InlineData("task", "Claude Code sent a Stop with a running background task of type \"{0}\", which this build does not know. The dashboard does not wait for that task.")]
    public async Task One_unknown_value_writes_one_line_and_a_second_value_its_own(string field, string line)
    {
        Publish(With(field, "first_new_value", "s-1", 0));
        Publish(With(field, "first_new_value", "s-2", 1));
        Publish(With(field, "second_new_value", "s-3", 2));
        await Handled();

        Assert.Equal(
            [string.Format(CultureInfo.InvariantCulture, line, "first_new_value"), string.Format(CultureInfo.InvariantCulture, line, "second_new_value")],
            Unknowns());
    }

    /// <summary>A known value that the dashboard ignores on purpose is not news: no Information line.</summary>
    [Theory]
    [InlineData("idle_prompt")]
    [InlineData("agent_completed")]
    public async Task A_known_value_ignored_on_purpose_writes_no_line(string type)
    {
        Publish(Notified("s-1", type, 0));
        await Handled();

        Assert.Empty(Unknowns());
        Assert.DoesNotContain(Above(LogEventLevel.Debug), text => !text.StartsWith("Event consumer started", StringComparison.Ordinal));
    }

    /// <summary>
    /// At the normal level, a run with only known values writes what it wrote before T1.79: the consumer's start line,
    /// and nothing for any event. Each known value of each field, applied or declined.
    /// </summary>
    [Fact]
    public async Task At_the_normal_level_known_values_write_no_line()
    {
        Publish(new SessionStart { SessionId = new SessionId("s-1"), Timestamp = At, Cwd = @"C:\w", Source = "startup" });
        Publish(Prompt("s-1", 1, "p-1"));
        Publish(Notified("s-1", "permission_prompt", 2));
        Publish(Notified("s-1", "idle_prompt", 3));
        Publish(Notified("s-1", "agent_completed", 4));
        Publish(Notified("s-1", "agent_needs_input", 5));
        Publish(new Stop
        {
            SessionId = new SessionId("s-1"), Timestamp = At.AddSeconds(6), Cwd = @"C:\w", PromptId = "p-1",
            BackgroundTasks = [new BackgroundTask("b1", BackgroundTaskKind.Shell, "build")],
        });
        Publish(new StopFailure { SessionId = new SessionId("s-1"), Timestamp = At.AddSeconds(7), Cwd = @"C:\w", ErrorKind = "rate_limit" });
        Publish(new SessionEnd { SessionId = new SessionId("s-1"), Timestamp = At.AddSeconds(8), Cwd = @"C:\w", Reason = "clear" });
        await Handled();

        var normal = Assert.Single(Above(LogEventLevel.Debug));
        Assert.StartsWith("Event consumer started", normal, StringComparison.Ordinal);
    }

    /// <summary>A value of 10,000 characters is cut in the line, and the line stays one line.</summary>
    [Fact]
    public async Task A_long_value_is_cut_in_the_line()
    {
        Publish(Notified("s-1", new string('q', 10_000), 0));
        Publish(Notified("s-2", "two\nlines", 1));
        await Handled();

        var lines = Unknowns();

        Assert.Equal(2, lines.Count);
        Assert.Contains("\"" + new string('q', EventValues.MaxLength) + "…\"", lines[0], StringComparison.Ordinal);
        Assert.True(lines[0].Length < EventValues.MaxLength + 200, $"The line is {lines[0].Length} characters.");
        Assert.Contains("\"two\\nlines\"", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain('\n', lines[1]);

        // The Debug line and the decline row are cut the same way.
        Assert.All(Traces(), line => Assert.True(line.Length < EventValues.MaxLength + 200, $"The line is {line.Length} characters."));
        var detail = (await Declines(2)).Select(row => row.Detail!).ToList();
        Assert.Equal("type=" + new string('q', EventValues.MaxLength) + "…", detail[0]);
        Assert.Equal("type=two\\nlines", detail[1]);
    }

    // ---- The Debug level: one line for each event --------------------------------------------------

    /// <summary>
    /// At Debug, each event writes one line with its name, session, type and outcome, applied or declined; the
    /// decision lines of T1.52 stay.
    /// </summary>
    [Fact]
    public async Task At_Debug_each_event_writes_one_line_with_its_name_session_type_and_outcome()
    {
        Publish(Prompt("s-1", 0, "p-1"));
        Publish(Notified("s-1", "permission_prompt", 1));
        Publish(Notified("s-1", "idle_prompt", 2));
        Publish(Notified("s-1", "quota_auto_resume_stale", 3));
        await Handled();

        Assert.Equal(
            [
                "Event UserPromptSubmit session=\"s-1\" type=\"-\" applied",
                "Event Notification session=\"s-1\" type=\"permission_prompt\" applied",
                "Event Notification session=\"s-1\" type=\"idle_prompt\" declined reason=Ignored",
                "Event Notification session=\"s-1\" type=\"quota_auto_resume_stale\" declined reason=Ignored",
            ],
            Traces());

        Assert.Contains(_sink.AtLevel(LogEventLevel.Debug), e => Render(e).StartsWith("Decision SessionAdded", StringComparison.Ordinal));
    }

    // ---- The database: the type in the decline row ------------------------------------------------

    /// <summary>The <c>EventDeclined</c> row of an unknown notification has <c>type=&lt;value&gt;</c> in its detail.</summary>
    [Fact]
    public async Task The_decline_row_of_an_unknown_notification_carries_its_type()
    {
        Publish(Prompt("s-1", 0, "p-1"));
        Publish(Notified("s-1", "quota_auto_resume_stale", 1));
        Publish(Notified("s-1", "idle_prompt", 2));
        await Handled();

        var declines = await Declines(2);

        Assert.Equal(["type=quota_auto_resume_stale", "type=idle_prompt"], declines.Select(row => row.Detail ?? "(null)").ToArray());
        Assert.All(declines, row => Assert.Equal(nameof(ApplyOutcome.Ignored), row.Reason));
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private void Publish(InboundEvent inboundEvent)
    {
        Assert.True(_pipeline.Sink.TryPublish(inboundEvent));
        _published++;
    }

    /// <summary>Waits until the consumer has applied or declined each event published.</summary>
    private async Task Handled()
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (_consumer.AppliedCount + _consumer.DeclinedCount >= _published)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException(
            $"{_published} events were published; {_consumer.AppliedCount + _consumer.DeclinedCount} were handled.");
    }

    /// <summary>The <c>EventDeclined</c> rows, in order, once there are <paramref name="count"/> of them.</summary>
    private async Task<List<Decision>> Declines(int count)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            while (_archive.Reader.TryRead(out var record))
            {
                _records.Add(record);
            }

            var rows = _records.SelectMany(record => record.Decisions).Where(row => row.Kind == DecisionKind.EventDeclined).ToList();

            if (rows.Count >= count)
            {
                return rows;
            }

            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException($"Fewer than {count} EventDeclined rows arrived.");
    }

    private static string Render(LogEvent logEvent) => logEvent.RenderMessage(CultureInfo.InvariantCulture);

    /// <summary>The Information lines that name an unknown value, in order.</summary>
    private List<string> Unknowns() =>
        [.. _sink.AtLevel(LogEventLevel.Information).Select(Render).Where(text => text.Contains("which this build does not know", StringComparison.Ordinal))];

    /// <summary>Every line above Debug, in order.</summary>
    private List<string> Above(LogEventLevel level) =>
        [.. _sink.Events.Where(e => e.Level > level).Select(Render)];

    /// <summary>The per-event Debug lines, in order.</summary>
    private List<string> Traces() =>
        [.. _sink.AtLevel(LogEventLevel.Debug).Select(Render).Where(text => text.StartsWith("Event ", StringComparison.Ordinal))];

    /// <summary>An event that carries <paramref name="value"/> in the field named.</summary>
    private static InboundEvent With(string field, string value, string session, int second)
    {
        var id = new SessionId(session);
        var at = At.AddSeconds(second);

        return field switch
        {
            "notification" => Notified(session, value, second),
            "start" => new SessionStart { SessionId = id, Timestamp = at, Cwd = @"C:\w", Source = value },
            "failure" => new StopFailure { SessionId = id, Timestamp = at, Cwd = @"C:\w", ErrorKind = value },
            "end" => new SessionEnd { SessionId = id, Timestamp = at, Cwd = @"C:\w", Reason = value },
            "task" => new Stop
            {
                SessionId = id, Timestamp = at, Cwd = @"C:\w",
                UnrecognisedBackgroundTasks = 1, UnrecognisedBackgroundTaskTypes = [value],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };
    }

    private static Notification Notified(string session, string type, int second) => new()
    {
        SessionId = new SessionId(session), Timestamp = At.AddSeconds(second), Cwd = @"C:\w", NotificationType = type,
    };

    private static UserPromptSubmit Prompt(string session, int second, string promptId) => new()
    {
        SessionId = new SessionId(session), Timestamp = At.AddSeconds(second), Cwd = @"C:\w", PromptId = promptId, Prompt = "go",
    };
}
