using System.Diagnostics;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// The channel every event crosses (Impl §4).
/// </summary>
public sealed class EventPipelineTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private static Stop Event(string sessionId, DateTimeOffset stamp) => new()
    {
        SessionId = new SessionId(sessionId),
        Timestamp = stamp,
        Cwd = @"C:\w",
    };

    [Fact]
    public void An_event_written_can_be_read_back()
    {
        var pipeline = new EventPipeline(Logger.None);
        var published = Event("s-1", At);

        Assert.True(pipeline.Sink.TryPublish(published));
        Assert.True(pipeline.Reader.TryRead(out var read));
        Assert.Same(published, read);
    }

    [Fact]
    public void A_null_event_is_refused_rather_than_thrown()
    {
        var pipeline = new EventPipeline(Logger.None);

        Assert.False(pipeline.Sink.TryPublish(null!));
    }

    /// <summary>
    /// <strong>The property Impl §4 asks for by name:</strong> a burst of fifteen simultaneous
    /// events must not stall a producer. Fifteen is the scale the specs describe, so this uses
    /// far more, and asserts the wall-clock cost rather than merely that it finished.
    /// </summary>
    [Fact]
    public void A_burst_of_producers_is_never_stalled()
    {
        var pipeline = new EventPipeline(Logger.None);
        var elapsed = new long[15];

        // No consumer at all: the harshest case, since nothing is draining. All fifteen start
        // together on real threads, so the burst is genuinely simultaneous.
        var failure = Concurrently.Run(15, i =>
        {
            var clock = Stopwatch.StartNew();

            for (var n = 0; n < 100; n++)
            {
                pipeline.Sink.TryPublish(Event($"s-{i}-{n}", At));
            }

            elapsed[i] = clock.ElapsedMilliseconds;
        });

        Assert.Null(failure);
        Assert.All(
            elapsed,
            ms => Assert.True(
                ms < 1000,
                $"A producer took {ms}ms to publish 100 events. Impl §4 requires that a burst cannot stall Kestrel."));
    }

    /// <summary>
    /// Drop-oldest means <c>TryWrite</c> always succeeds, so a loss would be invisible unless
    /// the channel reports it. It does, and the report is what makes the choice survivable.
    /// </summary>
    [Fact]
    public void Overflow_drops_the_oldest_and_says_so()
    {
        var pipeline = new EventPipeline(Logger.None, capacity: 4);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(pipeline.Sink.TryPublish(Event($"s-{i}", At)));
        }

        Assert.Equal(6, pipeline.DroppedCount);

        // The four newest survive — which is the point of drop-oldest for a dashboard.
        var survivors = new List<string>();
        while (pipeline.Reader.TryRead(out var read))
        {
            survivors.Add(read.SessionId.Value);
        }

        Assert.Equal(["s-6", "s-7", "s-8", "s-9"], survivors);
    }

    [Fact]
    public void Nothing_is_dropped_below_capacity()
    {
        var pipeline = new EventPipeline(Logger.None, capacity: 64);

        for (var i = 0; i < 64; i++)
        {
            pipeline.Sink.TryPublish(Event($"s-{i}", At));
        }

        Assert.Equal(0, pipeline.DroppedCount);
    }

    [Fact]
    public void The_pipeline_needs_a_logger_and_a_positive_capacity()
    {
        Assert.Throws<ArgumentNullException>(() => new EventPipeline(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventPipeline(Logger.None, capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventPipeline(Logger.None, capacity: -1));
    }
}

/// <summary>
/// The guard that turns the single-writer invariant from a convention into something the
/// program checks (Impl §2.2, §4).
/// </summary>
public sealed class SingleWriterGuardTests
{
    [Fact]
    public void One_thread_may_enter_and_re_enter_after_leaving()
    {
        var guard = new SingleWriterGuard();

        using (guard.Enter("first"))
        {
        }

        using (guard.Enter("second"))
        {
        }

        Assert.Equal(0, guard.ViolationCount);
    }

    /// <summary>
    /// <strong>The negative case, and the one that matters.</strong> Two threads inside at once
    /// is the race the whole design exists to prevent; without this the invariant is only a
    /// comment. The second entrant is blocked deterministically rather than by timing luck: the
    /// first holds the region open until the second has tried.
    /// </summary>
    [Fact]
    public void A_second_concurrent_entrant_is_refused()
    {
        var guard = new SingleWriterGuard();
        using var secondHasTried = new ManualResetEventSlim();
        Exception? fromSecond = null;

        using (guard.Enter("the consumer loop"))
        {
            var second = new Thread(() =>
            {
                try
                {
                    using (guard.Enter("a second driver"))
                    {
                    }
                }
                catch (Exception ex)
                {
                    fromSecond = ex;
                }
                finally
                {
                    secondHasTried.Set();
                }
            });

            second.Start();
            Assert.True(secondHasTried.Wait(TimeSpan.FromSeconds(5)), "The second thread never ran.");
            second.Join();
        }

        var violation = Assert.IsType<SingleWriterViolationException>(fromSecond);
        Assert.Contains("Two threads entered", violation.Message, StringComparison.Ordinal);
        Assert.Contains("a second driver", violation.Message, StringComparison.Ordinal);
        Assert.Contains("the consumer loop", violation.Message, StringComparison.Ordinal);
        Assert.Equal(1, guard.ViolationCount);
    }

    /// <summary>After a violation the region is still usable, so one bug does not wedge the pipeline.</summary>
    [Fact]
    public void A_refused_entrant_does_not_leave_the_region_occupied()
    {
        var guard = new SingleWriterGuard();

        using (guard.Enter("holder"))
        {
            Assert.Throws<SingleWriterViolationException>(() => RefusedOnAnotherThread(guard));
        }

        using (guard.Enter("after"))
        {
        }

        Assert.Equal(1, guard.ViolationCount);
    }

    private static void RefusedOnAnotherThread(SingleWriterGuard guard)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                using (guard.Enter("intruder"))
                {
                }
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw captured;
        }
    }
}
