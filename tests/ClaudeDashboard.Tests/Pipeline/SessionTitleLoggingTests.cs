using ClaudeDashboard.App.Configuration;
using System.Globalization;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// That the session title never reaches a log line, and that it would if anything logged the
/// objects carrying it (T1.24, issue #18).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two opposite assertions, and both are needed.</strong> The first half drives a real
/// ingest and proves no line the product writes contains the title. The second half proves the
/// title <em>does</em> come back through Serilog's destructuring operator, which is what puts it
/// in <c>UnprotectedTextInventory.CarriesOperatorText</c> — that list is meant to be measured
/// rather than reasoned into, and a classification nobody checked is the kind that goes stale.
/// </para>
/// <para>
/// <strong>Why a title is treated like a prompt.</strong> A session the operator did not name
/// gets a title written by a background model call summarising their first prompt, so the slot
/// can hold their words. It holds a <c>--name</c> value too, and the wire does not distinguish
/// the two, so the rule has to hold for the worst value the slot can carry.
/// </para>
/// <para>
/// The marker is a string nothing else in the product could produce, so a hit is the title and
/// not a coincidence.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class SessionTitleLoggingTests : IAsyncLifetime
{
    /// <summary>Shaped like a title a small model would write, and unique enough to grep for.</summary>
    private const string Marker = "zqx-secret-title-marker-7f3";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly FakeClock _clock = new();
    private readonly CapturingSink _sink = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);

    private Logger _logger = null!;
    private EventConsumer _consumer = null!;

    public Task InitializeAsync()
    {
        // Debug and above, so a title leaking into a routine decline line — the quietest place it
        // could go, and the one the host's file sink would discard — is still visible here.
        _logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_sink)
            .CreateLogger();

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            new SoundPolicyEngine(new RecordingSoundPlayer(), _clock, _guard, new SoundPolicyOptions()),
            _clock,
            _guard,
            _logger,
            new RecordingUiTick(),
            _archive,
            new RosterStore(new RecordingEventSink()),
            tickInterval: TimeSpan.FromMinutes(5));

        return _consumer.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        _logger?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <strong>A whole session's traffic, every event carrying the title, and not one log line
    /// mentions it.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The arrangement deliberately walks the paths that <em>do</em> log: a decline, which writes
    /// the event name and session id at Debug, and an uncorrelated completion, which writes a
    /// Warning with both prompt ids. Those are the two lines a title would most plausibly be
    /// added to by somebody making a log more useful.
    /// </para>
    /// <para>
    /// <strong>The control matters as much as the assertion.</strong> A run that logged nothing at
    /// all would pass this test while proving nothing, so the test also asserts that the two lines
    /// it is checking were actually written.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task No_log_line_anywhere_contains_the_session_title()
    {
        _pipeline.Sink.TryPublish(Prompt("s-1", At, "p-1"));
        _pipeline.Sink.TryPublish(Batch("s-1", At.AddSeconds(1)));
        _pipeline.Sink.TryPublish(Finished("s-1", At.AddSeconds(2), "p-1"));
        _pipeline.Sink.TryPublish(Prompt("s-1", At.AddSeconds(3), "p-2"));

        Assert.True(await Until(() => _consumer.AppliedCount >= 3));

        // A delayed duplicate of the first turn's completion: the Warning path.
        _pipeline.Sink.TryPublish(Finished("s-1", At.AddSeconds(4), "p-1"));

        Assert.True(await Until(() => _consumer.UncorrelatedCount >= 1));
        Assert.True(await Until(() => _consumer.DeclinedCount >= 2));

        // The control: the noisy paths ran, so the silence below is the title being withheld
        // rather than nothing having been logged.
        Assert.Contains(Rendered, line => line.Contains("declined", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(Rendered);

        var leaked = Rendered.Where(line => line.Contains(Marker, StringComparison.Ordinal)).ToList();

        Assert.True(
            leaked.Count == 0,
            $"The session title reached the log: {string.Join(" | ", leaked)}");

        // …and the session really did carry it, so the arrangement was not silently title-free.
        Assert.Equal(Marker, _registry.Sessions[new SessionId("s-1")].Title);
    }

    /// <summary>
    /// <strong>The title comes back through <c>{@Row}</c> and <c>{Event}</c>, which is why it is
    /// in the inventory.</strong>
    /// </summary>
    /// <remarks>
    /// Nothing in <c>src/</c> writes either of these templates over these objects today. This is
    /// the gap being measured rather than a live disclosure — the same distinction
    /// <c>UnprotectedTextInventory</c> draws — and it is measured because the alternative is a
    /// classification resting on somebody's recollection of how Serilog behaves.
    /// </remarks>
    [Fact]
    public void The_title_is_exposed_by_the_two_routes_the_inventory_names()
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        var session = new Session
        {
            Id = new SessionId("s-1"),
            State = SessionState.Working,
            Latest = new Exchange { Prompt = "run the tests", StartedAt = At },
            Cwd = @"C:\w",
            WorkspaceGroup = GroupKeys.ForWorkspace(@"C:\w"),
            EnteredAt = At,
            LastActivity = At,
            Title = Marker,
        };

        // The record route: a plain {Event} prints every public property of a record.
        logger.Information("Event {Event}", Prompt("s-1", At, "p-1"));

        // …and of a Session, which now carries the title directly rather than only through its
        // nested Exchange.
        logger.Information("Session {Session}", session);

        // The destructuring route: {@} reflects over the public properties of any type at all,
        // which is how a plain class in the UI assembly leaks.
        logger.Information(
            "Row {@Row}",
            new SessionViewModel(session, new MotionPolicy(() => false, observeChanges: false)));

        var lines = sink.Events
            .Select(entry => entry.RenderMessage(CultureInfo.InvariantCulture))
            .ToList();

        Assert.Equal(3, lines.Count);
        Assert.All(lines, line => Assert.Contains(Marker, line, StringComparison.Ordinal));
    }

    private IReadOnlyList<string> Rendered =>
        [.. _sink.Events.Select(entry => entry.RenderMessage(CultureInfo.InvariantCulture))];

    private static async Task<bool> Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        return condition();
    }

    private static UserPromptSubmit Prompt(string id, DateTimeOffset at, string promptId) => new()
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = @"C:\w",
        PromptId = promptId,
        Prompt = "run the tests",
        SessionTitle = Marker,
    };

    private static PostToolBatch Batch(string id, DateTimeOffset at) => new()
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = @"C:\w",
        SessionTitle = Marker,
    };

    private static Stop Finished(string id, DateTimeOffset at, string promptId) => new()
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = @"C:\w",
        PromptId = promptId,
        LastAssistantMessage = "29 passed",
        SessionTitle = Marker,
    };
}
