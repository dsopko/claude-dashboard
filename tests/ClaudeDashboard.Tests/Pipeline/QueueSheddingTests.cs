using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// A full queue sheds only events that change nothing (T1.58, issue #3, the operator's ruling of
/// 2026-10-03).
/// </summary>
public sealed class QueueSheddingTests
{
    private static readonly SessionId Id = new("s1");
    private const string Cwd = @"C:\work";

    private readonly FakeClock _clock = new();

    private UserPromptSubmit Prompt(string id = "s1") => new()
    {
        SessionId = new SessionId(id), Timestamp = _clock.Now, Cwd = Cwd, PromptId = "p-1", Prompt = "go",
    };

    private Notification Notified(string type, string id = "s1") => new()
    {
        SessionId = new SessionId(id), Timestamp = _clock.Now, Cwd = Cwd, NotificationType = type,
    };

    private PostToolBatch Batch(string id = "s1") => new()
    {
        SessionId = new SessionId(id), Timestamp = _clock.Now, Cwd = Cwd,
    };

    private Stop Stopped(string id) => new()
    {
        SessionId = new SessionId(id), Timestamp = _clock.Now, Cwd = Cwd,
    };

    private static List<InboundEvent> Drain(EventPipeline pipeline)
    {
        var read = new List<InboundEvent>();

        while (pipeline.Reader.TryRead(out var inboundEvent))
        {
            read.Add(inboundEvent);
        }

        return read;
    }

    /// <summary>
    /// <strong>Issue #3's reproduction, at capacity 2.</strong> The permission prompt is delivered
    /// and applied, its sound plays, and a tool batch is shed instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The permission and the first tool batch are written (0 and 1 queued, below 2); the second
    /// tool batch is noise at the capacity, so it is shed. Under drop-oldest, before T1.58, the third
    /// event pushed the permission out, NeedsPermission never happened, and no sound played.
    /// </para>
    /// <para>
    /// <strong>The session ends in Working, and that is right</strong> (the director's ruling of
    /// 2026-10-03): the written tool batch resumes a blocked session (TS §II.2). What #3 is about is
    /// the permission prompt being lost, and here it is not.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_permission_prompt_survives_a_full_queue_and_a_tool_batch_is_shed()
    {
        var pipeline = new EventPipeline(Logger.None, capacity: 2, clock: _clock);
        var registry = new SessionRegistry(new SingleWriterGuard());
        var player = new RecordingSoundPlayer();
        var engine = new SoundPolicyEngine(player, _clock, new SingleWriterGuard(), new SoundPolicyOptions());
        registry.SessionChanged += (_, e) => engine.OnSessionChanged(e.Session, e.Session.WorkspaceGroup);

        var dropped = new List<(InboundEvent Event, PipelineDrop Why)>();
        pipeline.Dropped = (inboundEvent, why) => dropped.Add((inboundEvent, why));

        Assert.True(pipeline.Sink.TryPublish(Prompt()));
        registry.Apply(Assert.Single(Drain(pipeline)));
        Assert.Equal(SessionState.Working, registry.Sessions[Id].State);

        Assert.True(pipeline.Sink.TryPublish(Notified("permission_prompt")));
        Assert.True(pipeline.Sink.TryPublish(Batch()));
        Assert.True(pipeline.Sink.TryPublish(Batch()));

        foreach (var inboundEvent in Drain(pipeline))
        {
            registry.Apply(inboundEvent);
        }

        var shed = Assert.Single(dropped);
        Assert.IsType<PostToolBatch>(shed.Event);
        Assert.Equal(PipelineDrop.Shed, shed.Why);

        Assert.Contains(
            registry.Sessions[Id].Transitions,
            transition => transition.From == SessionState.Working && transition.To == SessionState.NeedsPermission);
        Assert.Contains(player.Played, played => played.Sound == SoundId.Permission);

        Assert.Equal(SessionState.Working, registry.Sessions[Id].State);
    }

    /// <summary>
    /// <strong>At the capacity, every event that can change the board is written</strong>, and only
    /// noise is shed: a tool batch and an idle notification.
    /// </summary>
    [Fact]
    public void At_capacity_only_noise_is_shed()
    {
        var pipeline = new EventPipeline(Logger.None, capacity: 2, clock: _clock);
        Assert.True(pipeline.Sink.TryPublish(Stopped("a")));
        Assert.True(pipeline.Sink.TryPublish(Stopped("b")));

        InboundEvent[] written =
        [
            Stopped("c"),
            new Ack { SessionId = new SessionId("d"), Timestamp = _clock.Now, Cwd = Cwd, Source = AckSource.Manual },
            new SoundCommand { SessionId = default, Timestamp = _clock.Now, Cwd = string.Empty, Kind = SoundCommandKind.MuteAll },
            Notified("permission_prompt", "e"),
            Notified("agent_needs_input", "f"),
        ];

        foreach (var inboundEvent in written)
        {
            Assert.True(pipeline.Sink.TryPublish(inboundEvent));
        }

        Assert.True(pipeline.Sink.TryPublish(Batch("g")));
        Assert.True(pipeline.Sink.TryPublish(Notified("idle_prompt", "h")));

        var read = Drain(pipeline);

        Assert.Equal(["a", "b", "c", "d", string.Empty, "e", "f"], read.Select(e => e.SessionId.Value));
        Assert.Equal(2, pipeline.ShedCount);
        Assert.Equal(0, pipeline.DroppedCount);
    }

    /// <summary>The events written come out in the order they went in; nothing queued is removed.</summary>
    [Fact]
    public void Written_events_keep_their_order()
    {
        var pipeline = new EventPipeline(Logger.None, capacity: 3, clock: _clock);

        for (var i = 0; i < 10; i++)
        {
            pipeline.Sink.TryPublish(Stopped($"s-{i}"));
            pipeline.Sink.TryPublish(Batch($"noise-{i}"));
        }

        var read = Drain(pipeline).Select(e => e.SessionId.Value).ToList();

        Assert.Equal(Enumerable.Range(0, 10).Select(i => $"s-{i}"), read.Where(id => id.StartsWith("s-", StringComparison.Ordinal)));
        Assert.Equal(read.OrderBy(id => read.IndexOf(id)), read);
        Assert.Equal(0, pipeline.DroppedCount);
    }

    /// <summary>
    /// <strong>The hard limit drops the oldest, and the second notice shows</strong>, and stays.
    /// </summary>
    [Fact]
    public void The_hard_limit_drops_the_oldest_and_shows_the_events_lost_notice()
    {
        var pipeline = new EventPipeline(Logger.None, capacity: 2, hardLimit: 4, clock: _clock);
        var lost = new EventsLostNotice(() => pipeline.DroppedCount);

        lost.Tick(_clock.Now);
        Assert.False(lost.IsShown);

        for (var i = 0; i < 6; i++)
        {
            Assert.True(pipeline.Sink.TryPublish(Stopped($"s-{i}")));
        }

        Assert.Equal(2, pipeline.DroppedCount);
        Assert.Equal(["s-2", "s-3", "s-4", "s-5"], Drain(pipeline).Select(e => e.SessionId.Value));

        lost.Tick(_clock.Now);
        Assert.True(lost.IsShown);
        Assert.Equal(EventsLostNotice.WindowText, lost.Text);
        Assert.Equal(EventsLostNotice.TrayShort, lost.TrayText);

        _clock.Advance(TimeSpan.FromHours(3));
        lost.Tick(_clock.Now);
        Assert.True(lost.IsShown, "The events-lost notice stays until the next start.");
    }

    /// <summary>
    /// <strong>The fell-behind notice shows after a shed, and clears five minutes after the last
    /// one</strong>, on the tick, under a fake clock.
    /// </summary>
    [Fact]
    public void The_fell_behind_notice_clears_five_minutes_after_the_last_shed()
    {
        var pipeline = new EventPipeline(Logger.None, capacity: 1, clock: _clock);
        var behind = new FellBehindNotice(() => pipeline.LastShedAt);

        pipeline.Sink.TryPublish(Stopped("a"));
        behind.Tick(_clock.Now);
        Assert.False(behind.IsShown);

        pipeline.Sink.TryPublish(Batch());
        behind.Tick(_clock.Now);
        Assert.True(behind.IsShown);
        Assert.Equal(FellBehindNotice.WindowText, behind.Text);
        Assert.Equal(FellBehindNotice.TrayShort, behind.TrayText);

        // A second shed two minutes later restarts the five minutes.
        _clock.Advance(TimeSpan.FromMinutes(2));
        pipeline.Sink.TryPublish(Batch());

        _clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        behind.Tick(_clock.Now);
        Assert.True(behind.IsShown);

        _clock.Advance(TimeSpan.FromSeconds(1));
        behind.Tick(_clock.Now);
        Assert.False(behind.IsShown);
    }

    /// <summary>
    /// <strong>The noise list and <see cref="SessionRegistry.TargetOf(NotificationKind)"/> agree</strong>,
    /// and the list is pinned here: a change to either fails this test.
    /// </summary>
    [Fact]
    public void The_noise_list_is_the_notifications_that_move_no_state()
    {
        foreach (var kind in Enum.GetValues<NotificationKind>())
        {
            var notification = Notified(kind.ToWireValue() ?? "something_new");

            Assert.Equal(SessionRegistry.TargetOf(kind) is null, PipelineNoise.Is(notification));
        }

        Assert.Equal(
            [NotificationKind.Unknown, NotificationKind.IdlePrompt, NotificationKind.AgentCompleted],
            Enum.GetValues<NotificationKind>().Where(kind => SessionRegistry.TargetOf(kind) is null));

        Assert.True(PipelineNoise.Is(Batch()));

        InboundEvent[] never =
        [
            Prompt(),
            Stopped("a"),
            Notified("permission_prompt"),
            Notified("agent_needs_input"),
            new StopFailure { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, ErrorKind = "rate_limit" },
            new SessionEnd { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Reason = "clear" },
            new SessionStart { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Source = "startup" },
            new CwdChanged { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd },
            new Ack { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Source = AckSource.Manual },
            new SoundCommand { SessionId = default, Timestamp = _clock.Now, Cwd = string.Empty, Kind = SoundCommandKind.MuteAll },
            new RostersChanged { SessionId = default, Timestamp = _clock.Now, Cwd = string.Empty },
        ];

        Assert.All(never, inboundEvent => Assert.False(PipelineNoise.Is(inboundEvent), inboundEvent.HookEventName));
    }

    /// <summary>
    /// <strong>One Warning and one Information line for a burst of 10,000 shed events</strong>, and
    /// no line for each.
    /// </summary>
    [Fact]
    public void A_burst_of_shedding_writes_one_warning_and_one_recovery_line()
    {
        var sink = new RecordingLogSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var pipeline = new EventPipeline(logger, capacity: 1, clock: _clock);

        pipeline.Sink.TryPublish(Stopped("a"));

        for (var i = 0; i < 10_000; i++)
        {
            Assert.True(pipeline.Sink.TryPublish(Batch()));
        }

        pipeline.NoteDrained();
        Assert.Single(sink.Events);

        Drain(pipeline);
        pipeline.NoteDrained();
        pipeline.NoteDrained();

        var warning = Assert.Single(sink.Events, entry => entry.Level == LogEventLevel.Warning);
        var recovered = Assert.Single(sink.Events, entry => entry.Level == LogEventLevel.Information);
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal("10000", recovered.Properties["Shed"].ToString());
        Assert.Equal("0", recovered.Properties["Lost"].ToString());
        Assert.Equal(10_000, pipeline.ShedCount);
        _ = warning;
    }
}
