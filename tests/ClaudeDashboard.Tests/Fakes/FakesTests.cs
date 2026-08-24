using System.Diagnostics.CodeAnalysis;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// Exercises each Phase 1 fake the way the task that depends on it will, so a fake that is
/// unusable is caught here rather than in the middle of T1.2, T1.5, T1.8, or T1.9.
/// </summary>
public sealed class FakeClockTests
{
    [Fact]
    public void Starts_at_a_known_instant()
    {
        Assert.Equal(FakeClock.DefaultStart, new FakeClock().Now);
        Assert.Equal(
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new FakeClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)).Now);
    }

    [Fact]
    public void Advance_moves_time_forward_and_returns_the_new_instant()
    {
        var clock = new FakeClock();

        var after = clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(FakeClock.DefaultStart.AddMinutes(2), after);
        Assert.Equal(after, clock.Now);
    }

    [Fact]
    public void Advance_accumulates()
    {
        var clock = new FakeClock();

        clock.AdvanceMinutes(2);
        clock.AdvanceMinutes(5);
        clock.AdvanceMinutes(10);

        Assert.Equal(FakeClock.DefaultStart.AddMinutes(17), clock.Now);
    }

    /// <summary>
    /// A test that subtracts where it meant to add would silently un-fire a nudge, so the
    /// fake refuses rather than helping create that bug.
    /// </summary>
    [Fact]
    public void Advance_refuses_to_go_backwards()
    {
        var clock = new FakeClock();

        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromSeconds(-1)));
        Assert.Equal(FakeClock.DefaultStart, clock.Now);
    }

    [Fact]
    public void Now_is_settable_including_backwards()
    {
        var clock = new FakeClock { Now = FakeClock.DefaultStart.AddHours(-1) };

        Assert.Equal(FakeClock.DefaultStart.AddHours(-1), clock.Now);
    }

    /// <summary>
    /// The shape T1.5 will use: walk the widening 2 → 5 → 10 minute schedule with no waiting,
    /// asserting only that the clock supports being driven that way.
    /// </summary>
    [Fact]
    [SuppressMessage("Performance", "CA1859", Justification = "The interface-typed local is the point of the test: it proves the fake is usable through the port, which is how every consuming task will hold it.")]
    public void Supports_stepping_a_widening_schedule_without_waiting()
    {
        var clock = new FakeClock();
        IClock port = clock;
        var start = port.Now;

        var readings = new List<TimeSpan>();
        foreach (var minutes in new[] { 2d, 5d, 10d })
        {
            clock.AdvanceMinutes(minutes);
            readings.Add(port.Now - start);
        }

        Assert.Equal(
            [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(17)],
            readings);
    }
}

public sealed class RecordingSoundPlayerTests
{
    [Fact]
    [SuppressMessage("Performance", "CA1859", Justification = "The interface-typed local is the point of the test: it proves the fake is usable through the port, which is how every consuming task will hold it.")]
    public void Records_what_it_was_asked_to_play()
    {
        var player = new RecordingSoundPlayer();
        ISoundPlayer port = player;

        port.Play(SoundId.Finished, 1.0, TimeSpan.Zero);

        var call = Assert.Single(player.Played);
        Assert.Equal(SoundId.Finished, call.Sound);
        Assert.Equal(1.0, call.Gain);
        Assert.Equal(TimeSpan.Zero, call.Fade);
        Assert.Equal(call, player.Last);
    }

    [Fact]
    public void Records_calls_in_order()
    {
        var player = new RecordingSoundPlayer();

        player.Play(SoundId.Permission, 1.0, TimeSpan.Zero);
        player.Play(SoundId.Finished, 0.6, TimeSpan.FromMilliseconds(150));

        Assert.Equal([SoundId.Permission, SoundId.Finished], player.Played.Select(p => p.Sound));
    }

    /// <summary>
    /// The assertion T1.5 makes: the same sound, at falling gain, is a widening nudge rather
    /// than a second notice (TS §IV.5 — "never louder, never faster").
    /// </summary>
    [Fact]
    public void Exposes_gains_for_asserting_a_softening_nudge()
    {
        var player = new RecordingSoundPlayer();

        player.Play(SoundId.Question, 1.0, TimeSpan.Zero);
        player.Play(SoundId.Question, 0.6, TimeSpan.FromMilliseconds(150));
        player.Play(SoundId.Question, 0.4, TimeSpan.FromMilliseconds(150));

        Assert.Equal([1.0, 0.6, 0.4], player.Gains);
        Assert.Equal(3, player.PlayedOf(SoundId.Question).Count);
        Assert.Empty(player.PlayedOf(SoundId.Error));
    }

    [Fact]
    public void Clear_forgets_earlier_calls()
    {
        var player = new RecordingSoundPlayer();
        player.Play(SoundId.Error, 1.0, TimeSpan.Zero);

        player.Clear();

        Assert.Empty(player.Played);
        Assert.Null(player.Last);
    }

    [Fact]
    public void Records_nothing_until_asked_to_play()
    {
        var player = new RecordingSoundPlayer();

        Assert.Empty(player.Played);
        Assert.Null(player.Last);
    }
}

public sealed class RecordingEventSinkTests
{
    private static Stop AnEvent(string sessionId = "s-1") => new()
    {
        SessionId = new SessionId(sessionId),
        Timestamp = FakeClock.DefaultStart,
        Cwd = @"C:\projects\dashboard",
    };

    [Fact]
    [SuppressMessage("Performance", "CA1859", Justification = "The interface-typed local is the point of the test: it proves the fake is usable through the port, which is how every consuming task will hold it.")]
    public void Accepts_and_records_an_event()
    {
        var sink = new RecordingEventSink();
        IEventSink port = sink;
        var published = AnEvent();

        Assert.True(port.TryPublish(published));

        Assert.Same(published, Assert.Single(sink.Published));
        Assert.Same(published, sink.Last);
        Assert.Equal(0, sink.RefusedCount);
    }

    [Fact]
    public void Records_events_in_order()
    {
        var sink = new RecordingEventSink();

        sink.TryPublish(AnEvent("s-1"));
        sink.TryPublish(AnEvent("s-2"));

        Assert.Equal(
            [new SessionId("s-1"), new SessionId("s-2")],
            sink.Published.Select(e => e.SessionId));
    }

    /// <summary>
    /// The bounded channel behind the real sink can fill (Impl §4), so the refusing branch
    /// has to be reachable — ingress logs the drop and still answers 200 (Impl §3.3).
    /// </summary>
    [Fact]
    public void Refuses_once_full_without_throwing()
    {
        var sink = new RecordingEventSink { Capacity = 1 };

        Assert.True(sink.TryPublish(AnEvent("s-1")));
        Assert.False(sink.TryPublish(AnEvent("s-2")));

        Assert.Single(sink.Published);
        Assert.Equal(1, sink.RefusedCount);
    }

    [Fact]
    public void Is_unbounded_by_default()
    {
        var sink = new RecordingEventSink();

        for (var i = 0; i < 50; i++)
        {
            Assert.True(sink.TryPublish(AnEvent($"s-{i}")));
        }

        Assert.Equal(50, sink.Published.Count);
        Assert.Equal(0, sink.RefusedCount);
    }

    /// <summary>The shape T1.8's payload-mapping tests want: filter to the variant under test.</summary>
    [Fact]
    public void Filters_recorded_events_by_variant()
    {
        var sink = new RecordingEventSink();
        sink.TryPublish(AnEvent());
        sink.TryPublish(new CwdChanged
        {
            SessionId = new SessionId("s-1"),
            Timestamp = FakeClock.DefaultStart,
            Cwd = @"C:\elsewhere",
        });

        Assert.Single(sink.PublishedOf<Stop>());
        Assert.Single(sink.PublishedOf<CwdChanged>());
        Assert.Empty(sink.PublishedOf<UserPromptSubmit>());
    }

    [Fact]
    public void Clear_forgets_events_and_refusals()
    {
        var sink = new RecordingEventSink { Capacity = 0 };
        sink.TryPublish(AnEvent());

        sink.Clear();

        Assert.Empty(sink.Published);
        Assert.Equal(0, sink.RefusedCount);
    }
}

/// <summary>
/// The stub publisher, exercised the way the tests that must supply one will use it.
/// </summary>
public sealed class StubAckPublisherTests
{
    [Fact]
    public void It_accepts_by_default_and_records_what_it_was_asked()
    {
        var publisher = new StubAckPublisher();
        var session = AnySession();

        Assert.True(publisher.Acknowledge(session));
        Assert.Equal(session.Id, Assert.Single(publisher.Asked));
    }

    [Fact]
    public void It_can_be_told_to_refuse()
    {
        var publisher = new StubAckPublisher { Accepts = false };

        Assert.False(publisher.Acknowledge(AnySession()));
        Assert.Single(publisher.Asked);
    }

    [Fact]
    public void It_needs_a_session()
    {
        Assert.Throws<ArgumentNullException>(() => new StubAckPublisher().Acknowledge(null!));
    }

    private static Session AnySession()
    {
        var registry = new SessionRegistry();
        var id = new SessionId("s-1");

        registry.Apply(new UserPromptSubmit
        {
            SessionId = id,
            Timestamp = FakeClock.DefaultStart,
            Cwd = @"C:\dev\PennCustQuote",
            PromptId = "p-1",
            Prompt = "run the tests",
        });

        return registry.Sessions[id];
    }
}
