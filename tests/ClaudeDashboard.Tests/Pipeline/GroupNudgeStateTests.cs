using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// <strong><c>/state</c> follows a roster group's reminder with no session change</strong> (T1.84, issue #58): a
/// finished member reads no time and <c>Group</c>, then the group's time at the settle, then no time once the group's
/// one nudge has played.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The real consumer, in real time.</strong> Nothing here builds the report by hand. The settle is seen by the
/// consumer's settle wake, which builds the report again (T1.83); the group's nudge is fired by the consumer's tick,
/// and only <see cref="SoundPolicyEngine.NudgeScheduleAdvanced"/> builds the report after it. No event is posted after
/// the last finish, and the test counts the Registry's changes to show it.
/// </para>
/// <para>
/// <strong>Short times, so the test takes seconds.</strong> The shipped settle window (1.5 s), a reminder 2 s after the
/// settle, and a tick of 100 ms. The bounds below are a tick and the dispatcher's lateness, with room.
/// </para>
/// </remarks>
public sealed class GroupNudgeStateTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan UnreadAfter = TimeSpan.FromSeconds(2);

    /// <summary>How late after its instant each step may be seen: a tick, and the timer's own lateness, with room.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromMilliseconds(600);

    private static readonly SessionId[] Members = [new("s-1"), new("s-2")];

    [Fact]
    public async Task A_finished_member_reads_the_group_time_at_the_settle_and_none_after_the_group_nudge()
    {
        var clock = new SystemClock();
        var guard = new SingleWriterGuard();
        var registry = new SessionRegistry(new SingleWriterGuard());
        var pipeline = new EventPipeline(Logger.None);
        var archive = new EventArchive(Logger.None);
        var rosters = new RosterStore(pipeline.Sink, RosterBook.From([("orchestration", ["Coder", "Reviewer"])]), clock);
        var recorder = new DecisionRecorder(registry, rosters, archive, Logger.None);
        var engine = new SoundPolicyEngine(
            new RecordingSoundPlayer(), clock, guard, new SoundPolicyOptions { UnreadNudgeAfter = UnreadAfter }, recorder);

        // As AppHost wires it: the engine hears each change first, then the board subscribes.
        registry.SessionChanged += (_, e) => engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, rosters.Book));
        using var board = new StateBoard(registry, engine, clock, Logger.None, rosters);

        var changes = 0;
        registry.SessionChanged += (_, _) => Interlocked.Increment(ref changes);

        using var consumer = new EventConsumer(
            pipeline, registry, engine, clock, guard, Logger.None, new UiTick(new QueueingDispatcher()), archive, rosters, recorder,
            tickInterval: Tick,
            state: board);
        await consumer.StartAsync(CancellationToken.None);

        try
        {
            Publish(pipeline, Prompt("s-1", "Coder", "p-1", clock.Now));
            Publish(pipeline, Prompt("s-2", "Reviewer", "p-2", clock.Now));
            Publish(pipeline, Finished("s-1", "p-1", clock.Now));
            var lastStop = clock.Now;
            Publish(pipeline, Finished("s-2", "p-2", lastStop));
            var settle = lastStop + RosterSettle.DefaultWindow;

            // Before the settle: both finished, no time, the group's.
            Assert.True(
                await Until(() => Members.All(id => Is(board, id, null, NudgeOwner.Group)), settle),
                $"Before the settle the members did not read no time and Group: {Read(board)}.");
            var applied = Volatile.Read(ref changes);

            // At the settle: the group's time, with no session change.
            Assert.True(
                await Until(() => Members.All(id => Entry(board, id) is { NextNudgeAt: not null, NudgeBy: NudgeOwner.Group }), settle + Bound),
                $"Within {Bound.TotalMilliseconds} ms of the settle the members did not read the group's time: {Read(board)}.");
            var due = Entry(board, Members[0])!.NextNudgeAt!.Value;
            output.WriteLine($"SETTLE: the group's time is {(due - settle).TotalMilliseconds:F0} ms after the settle deadline");
            Assert.InRange(due - settle, UnreadAfter, UnreadAfter + Bound);
            Assert.Equal(due, Entry(board, Members[1])!.NextNudgeAt);

            // After the group's nudge: no time, still the group's, with no session change.
            Assert.True(
                await Until(() => Members.All(id => Is(board, id, null, NudgeOwner.Group)), due + Bound),
                $"Within {Bound.TotalMilliseconds} ms of the group's nudge the members did not read no time: {Read(board)}.");
            output.WriteLine($"NUDGE: no time read {(clock.Now - due).TotalMilliseconds:F0} ms after the group's time");
            Assert.True(clock.Now >= due, "The time went before the group's nudge was due.");

            Assert.Equal(applied, Volatile.Read(ref changes));
        }
        finally
        {
            await consumer.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds or the clock passes <paramref name="giveUp"/>.</summary>
    private static async Task<bool> Until(Func<bool> condition, DateTimeOffset giveUp)
    {
        while (DateTimeOffset.UtcNow <= giveUp)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    private static SessionStateEntry? Entry(StateBoard board, SessionId id) =>
        board.Current.Sessions.FirstOrDefault(entry => entry.Id == id.Value);

    private static bool Is(StateBoard board, SessionId id, DateTimeOffset? at, NudgeOwner by) =>
        Entry(board, id) is { } entry && entry.State == SessionState.Unread && entry.NextNudgeAt == at && entry.NudgeBy == by;

    private static string Read(StateBoard board) =>
        string.Join("; ", board.Current.Sessions.Select(entry => $"{entry.Id} {entry.State} {entry.NextNudgeAt:O} {entry.NudgeBy}"));

    private static void Publish(EventPipeline pipeline, InboundEvent inboundEvent) =>
        Assert.True(pipeline.Sink.TryPublish(inboundEvent));

    private static UserPromptSubmit Prompt(string id, string title, string promptId, DateTimeOffset at) => new()
    {
        SessionId = new SessionId(id), Timestamp = at, Cwd = @"C:\w\" + id, PromptId = promptId, Prompt = "go", SessionTitle = title,
    };

    private static Stop Finished(string id, string promptId, DateTimeOffset at) => new()
    {
        SessionId = new SessionId(id), Timestamp = at, Cwd = @"C:\w\" + id, PromptId = promptId, LastAssistantMessage = "done",
    };
}
