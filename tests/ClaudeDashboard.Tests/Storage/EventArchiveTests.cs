using System.Diagnostics;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// The channel that keeps the disk away from the Registry's only writer (Impl §4; T1.17).
/// </summary>
public sealed class EventArchiveTests
{
    private static Serilog.Core.Logger Logger(RecordingLogSink sink) =>
        new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    /// <summary>An event as the consumer now hands it over: a record with no decisions.</summary>
    private static ArchiveRecord Record(ClaudeDashboard.Core.Events.InboundEvent inboundEvent) => new(inboundEvent, []);

    /// <summary>
    /// <strong>Handing an event over cannot block, even with nothing draining.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The criterion the whole class exists for. The caller is the single writer of the Registry
    /// and the sound engine; if this could wait, a stalled disk would stop the dashboard seeing
    /// events, and the symptom would be indistinguishable from a dead dashboard.
    /// </para>
    /// <para>
    /// <strong>What this measures and what it does not.</strong> It times the offer path with no
    /// reader attached and the channel driven past its capacity — so it measures the hand-over,
    /// including the drop path, which is the part that must never wait. It says nothing about how
    /// fast the disk is, because no disk is involved: a rate for a path that never touches the
    /// disk would measure nothing about the disk. Disk speed is <see cref="SqliteEventStoreTests"/>'s
    /// business, on the other side of this channel, which is exactly the point of there being a
    /// channel.
    /// </para>
    /// </remarks>
    [Fact]
    public void Handing_an_event_over_never_waits_even_with_nothing_draining()
    {
        var archive = new EventArchive(Serilog.Core.Logger.None, capacity: 8);

        // Ten times the capacity, so most of these are drops rather than enqueues.
        var clock = Stopwatch.StartNew();

        for (var i = 0; i < 80; i++)
        {
            archive.TryArchive(Record(TestEvents.Hook($$"""{"n":{{i}}}""")));
        }

        clock.Stop();

        Assert.Equal(80, archive.OfferedCount);
        Assert.Equal(72, archive.DroppedCount);

        // Generous by three orders of magnitude against what this costs, so it fails on "it
        // waited" and never on "the machine was busy".
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(2),
            $"eighty hand-overs took {clock.Elapsed.TotalMilliseconds:F0}ms; this path must not wait on anything");
    }

    /// <summary>The oldest goes, so what survives is a contiguous recent window.</summary>
    /// <remarks>
    /// Either policy leaves a gap; this one puts the gap at the far end, because recent history is
    /// what anyone would search. Asserting <em>which</em> events survived is what distinguishes
    /// drop-oldest from drop-newest — a count alone would pass for both.
    /// </remarks>
    [Fact]
    public void The_oldest_events_are_the_ones_dropped()
    {
        var archive = new EventArchive(Serilog.Core.Logger.None, capacity: 4);

        for (var i = 0; i < 10; i++)
        {
            archive.TryArchive(Record(TestEvents.Hook($$"""{"n":{{i}}}""")));
        }

        var survived = new List<string>();

        while (archive.Reader.TryRead(out var kept))
        {
            survived.Add(kept.Event!.Payload.Reveal());
        }

        Assert.Equal(
            ["""{"n":6}""", """{"n":7}""", """{"n":8}""", """{"n":9}"""],
            survived);
    }

    /// <summary>An empty record — no event, no decisions — is refused; anything else lands.</summary>
    /// <remarks>
    /// <para>
    /// <strong>This test's predecessor asserted the opposite for the Ack, and T1.37 is why it
    /// turned.</strong> The old rule skipped any event with an empty payload, which kept the Ack
    /// out of the table — and the Ack's absence was exactly the hole issue #48's investigation
    /// fell into: an acknowledgment came and went with no durable trace. The Ack now rides a
    /// record with its decision rows; what the recorder keeps out of the events table — sound
    /// commands, roster wakes — it keeps out by building the record with a null event, not by
    /// this channel judging payloads.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_empty_record_is_refused_and_an_ack_is_not()
    {
        var archive = new EventArchive(Serilog.Core.Logger.None);

        Assert.False(archive.TryArchive(new ArchiveRecord(null, [])));
        Assert.Equal(0, archive.OfferedCount);
        Assert.False(archive.Reader.TryRead(out _));

        // The Ack, empty payload and all, with its decisions.
        Assert.True(archive.TryArchive(new ArchiveRecord(
            TestEvents.Synthetic(),
            [new Decision(DateTimeOffset.UnixEpoch, "s-1", DecisionKind.AckApplied, Reason: "Manual")])));
        Assert.Equal(1, archive.OfferedCount);

        // And a tick's decisions with no event at all.
        Assert.True(archive.TryArchive(new ArchiveRecord(
            null,
            [new Decision(DateTimeOffset.UnixEpoch, null, DecisionKind.MuteExpired)])));
        Assert.Equal(2, archive.OfferedCount);
    }

    // ---- The gap is never silent ---------------------------------------------------------------

    /// <summary>A run that dropped nothing says nothing.</summary>
    [Fact]
    public void A_clean_run_reports_no_gap()
    {
        var log = new RecordingLogSink();
        var archive = new EventArchive(Logger(log), capacity: 16);

        archive.TryArchive(Record(TestEvents.Hook("{}")));
        archive.ReportDrops();

        Assert.DoesNotContain(log.Events, entry => entry.Level >= LogEventLevel.Warning);
    }

    /// <summary>A run that dropped says so, once, with the numbers.</summary>
    /// <remarks>
    /// <strong>A history with holes that says nothing about them is worse than no history</strong>
    /// — it invites conclusions from an absence that was never a fact about the sessions. The
    /// count is the difference between "the operator ran nothing that morning" and "the dashboard
    /// could not keep up".
    /// </remarks>
    [Fact]
    public void A_run_that_dropped_events_says_so_with_the_count()
    {
        var log = new RecordingLogSink();
        var archive = new EventArchive(Logger(log), capacity: 4);

        for (var i = 0; i < 20; i++)
        {
            archive.TryArchive(Record(TestEvents.Hook($$"""{"n":{{i}}}""")));
        }

        archive.ReportDrops();

        var warning = Assert.Single(log.Events, entry => entry.Level == LogEventLevel.Warning);
        var rendered = warning.RenderMessage(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Contains("16", rendered, StringComparison.Ordinal);
        Assert.Contains("20", rendered, StringComparison.Ordinal);
        Assert.Contains("gaps", rendered, StringComparison.Ordinal);
    }

    // ---- Construction ---------------------------------------------------------------------------

    [Fact]
    public void It_needs_a_logger() =>
        Assert.Throws<ArgumentNullException>(() => new EventArchive(null!));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_capacity_that_is_not_a_capacity_is_refused(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventArchive(Serilog.Core.Logger.None, capacity));

    [Fact]
    public void It_refuses_a_null_event() =>
        Assert.Throws<ArgumentNullException>(() => new EventArchive(Serilog.Core.Logger.None).TryArchive(null!));

    [Fact]
    public void Completing_twice_is_harmless()
    {
        var archive = new EventArchive(Serilog.Core.Logger.None);

        archive.Complete();
        archive.Complete();

        Assert.False(archive.TryArchive(Record(TestEvents.Hook("{}"))));
    }
}
