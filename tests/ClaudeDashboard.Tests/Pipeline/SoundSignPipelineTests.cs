using System.Collections.Concurrent;
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
/// Which row a group's sound marks, through the real consumer's settle pass (the T1.67 review,
/// must-fix 1): the Registry, the roster store, the roster watch and the engine, wired as
/// <c>AppHost</c> wires them, under a fake clock. No settle is called by hand.
/// </summary>
/// <remarks>
/// The first version let the engine find the member in its own copy of each session's group, which
/// changes only on a session change. A roster formed over finished sessions then played the group's
/// sound and marked no row. These tests reach the settle the way the product does: an event, or a
/// roster edit, wakes the consumer, and its pass resolves the groups as they stand.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class SoundSignPipelineTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;
    private static readonly RosterBook Orchestration = RosterBook.From([("orchestration", ["Coder", "Reviewer"])]);

    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RecordingSoundPlayer _player = new();
    private readonly ConcurrentQueue<SoundMarkedEventArgs> _marks = new();
    private RosterStore _rosters = null!;
    private EventConsumer _consumer = null!;

    /// <summary>Each test starts the consumer once, with the roster book it begins with.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <strong>A roster formed over two finished sessions</strong> marks no row: each finish played its own
    /// notice and marked its own row, and the group that the edit forms has nothing new to announce, so it
    /// plays nothing and marks nothing (T1.72, issue #107, the operator's ruling). This test asserted a third
    /// sound and a mark on the member that finished last before that ruling; the mark rule itself is held by
    /// the two tests below, where the group does play.
    /// </summary>
    [Fact]
    public async Task A_roster_formed_over_finished_sessions_marks_no_row()
    {
        await Start(RosterBook.Empty);

        Publish(Prompt("s-1", "Coder", "p-1", At));
        Publish(Prompt("s-2", "Reviewer", "p-2", At));
        Publish(Finished("s-1", "p-1", At.AddSeconds(60)));
        Publish(Finished("s-2", "p-2", At.AddSeconds(120)));

        Assert.True(await Until(() => _marks.Count == 2));
        Assert.Equal(["s-1", "s-2"], _marks.Select(mark => mark.Session.Value));

        _clock.Now = At.AddMinutes(5);
        _rosters.Replace(Orchestration);

        Assert.True(await Until(() => _consumer.SettledCount == 1), Seen());
        await Task.Delay(100);

        Assert.Equal(2, _marks.Count);
        Assert.Equal(2, _player.PlayedOf(SoundId.Finished).Count);
    }

    /// <summary>
    /// <strong>A member that ended last</strong> still gets the sign: its end is what settled the group,
    /// so it set off the sound (the operator's ruling on #99, the T1.67 review).
    /// </summary>
    [Fact]
    public async Task A_member_that_ended_last_gets_the_sign()
    {
        await Start(Orchestration);

        Publish(Prompt("s-1", "Coder", "p-1", At));
        Publish(Prompt("s-2", "Reviewer", "p-2", At));
        Publish(Finished("s-1", "p-1", At.AddSeconds(60)));

        Assert.True(await Until(() => _consumer.AppliedCount == 3));

        // A member's finish belongs to the group, and the group still has a member at work.
        Assert.Empty(_marks);
        Assert.Equal(0, _consumer.SettledCount);

        _clock.Now = At.AddMinutes(5);
        Publish(new SessionEnd { SessionId = new SessionId("s-2"), Timestamp = At.AddSeconds(120), Cwd = @"C:\w", Reason = "logout" });

        Assert.True(await Until(() => _consumer.SettledCount == 1 && _marks.Count == 1), Seen());
        Assert.Equal(new SessionId("s-2"), Assert.Single(_marks).Session);
        Assert.Single(_player.PlayedOf(SoundId.Finished));
    }

    /// <summary>
    /// The ordinary case through the same pass: a roster that exists before its members finish marks
    /// the member that finished last, and nothing before the settle.
    /// </summary>
    [Fact]
    public async Task A_roster_that_settles_marks_the_member_that_finished_last()
    {
        await Start(Orchestration);

        Publish(Prompt("s-1", "Coder", "p-1", At));
        Publish(Prompt("s-2", "Reviewer", "p-2", At));
        Publish(Finished("s-2", "p-2", At.AddSeconds(60)));

        _clock.Now = At.AddMinutes(5);
        Publish(Finished("s-1", "p-1", At.AddSeconds(90)));

        Assert.True(await Until(() => _consumer.SettledCount == 1 && _marks.Count == 1), Seen());
        Assert.Equal(new SessionId("s-1"), Assert.Single(_marks).Session);
    }

    private Task Start(RosterBook book)
    {
        _rosters = new RosterStore(_pipeline.Sink, book, _clock);

        var engine = new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions());
        engine.SoundMarked += (_, e) => _marks.Enqueue(e);

        // As AppHost wires it: the engine hears each change with the session's effective group.
        _registry.SessionChanged += (_, e) => engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, _rosters.Book));

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
            recorder: TestDecisions.For(_registry, _archive),
            tickInterval: TimeSpan.FromMinutes(15));

        return _consumer.StartAsync(CancellationToken.None);
    }

    /// <summary>What the pass did, for a failure message: the settles, the finished plays and the marks.</summary>
    private string Seen() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"settled={_consumer.SettledCount} finishedPlays={_player.PlayedOf(SoundId.Finished).Count} marks=[{string.Join(", ", _marks.Select(mark => mark.Session.Value))}]");

    private void Publish(InboundEvent inboundEvent) => Assert.True(_pipeline.Sink.TryPublish(inboundEvent));

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

    private static UserPromptSubmit Prompt(string id, string title, string promptId, DateTimeOffset at) => new()
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = @"C:\w",
        PromptId = promptId,
        Prompt = "run the tests",
        SessionTitle = title,
    };

    private static Stop Finished(string id, string promptId, DateTimeOffset at) => new()
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = @"C:\w",
        PromptId = promptId,
        LastAssistantMessage = "29 passed",
    };
}
