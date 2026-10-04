using System.Globalization;
using Serilog;

namespace ClaudeDashboard.App.Pipeline;

/// <summary>What a timing measures: a time, or a number of records.</summary>
public enum TimingUnit
{
    /// <summary>A time, kept in <see cref="TimeSpan"/> ticks and shown in milliseconds.</summary>
    Milliseconds = 1,

    /// <summary>A number of records.</summary>
    Records = 2,
}

/// <summary>
/// One timing that would show a stall (T1.66, issue #86): a count, a total, a worst case and a
/// limit, since the start and for the present hour.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written on the thread that measures, without a lock.</strong> Every field is changed with
/// <see cref="Interlocked"/>, so the UI thread, the self-test's thread and the consumer can each
/// record without waiting, and the consumer reads a copy at its tick. The figures exist to catch a
/// stall, not to measure speed (#86).
/// </para>
/// <para>
/// <strong>Warn once.</strong> One Warning when a value crosses the limit, one Information line
/// when a value is back under it, and nothing for each value in between. Never a line for each event.
/// </para>
/// </remarks>
public sealed class Timing
{
    private readonly ILogger _logger;
    private readonly string _meaning;

    private long _count;
    private long _total;
    private long _worst;
    private long _hourCount;
    private long _hourTotal;
    private long _hourWorst;
    private int _over;

    /// <summary>Creates a timing.</summary>
    /// <param name="name">The identifier, as <c>/state</c> and the lines name it.</param>
    /// <param name="unit">What the values are.</param>
    /// <param name="limit">The value above which it warns, in the unit's storage (ticks or records).</param>
    /// <param name="meaning">What a warning means, for the Warning line.</param>
    /// <param name="logger">Where the warning and its all-clear go.</param>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    public Timing(string name, TimingUnit unit, long limit, string meaning, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(meaning);
        ArgumentNullException.ThrowIfNull(logger);

        Name = name;
        Unit = unit;
        Limit = limit;
        _meaning = meaning;
        _logger = logger;
    }

    /// <summary>The identifier.</summary>
    public string Name { get; }

    /// <summary>What the values are.</summary>
    public TimingUnit Unit { get; }

    /// <summary>The value above which it warns.</summary>
    public long Limit { get; }

    /// <summary>Records a time. Any thread; never waits.</summary>
    public void Record(TimeSpan value) => Record(value.Ticks);

    /// <summary>Records a value in the unit's storage. Any thread; never waits.</summary>
    public void Record(long value)
    {
        if (value < 0)
        {
            value = 0;
        }

        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _total, value);
        Raise(ref _worst, value);

        Interlocked.Increment(ref _hourCount);
        Interlocked.Add(ref _hourTotal, value);
        Raise(ref _hourWorst, value);

        if (value > Limit)
        {
            if (Interlocked.Exchange(ref _over, 1) == 0)
            {
                _logger.Warning(
                    "The {Timing:l} was {Value:l}, over its limit of {Limit:l}. {Meaning:l} The next line on this says when it is back under.",
                    Name,
                    Show(value),
                    Show(Limit),
                    _meaning);
            }
        }
        else if (Interlocked.Exchange(ref _over, 0) == 1)
        {
            _logger.Information(
                "The {Timing:l} is back under its limit of {Limit:l}: {Value:l}.",
                Name,
                Show(Limit),
                Show(value));
        }
    }

    /// <summary>A copy of the figure since the start. Any thread.</summary>
    public TimingFigure SinceStart => new(
        Name,
        Unit,
        Interlocked.Read(ref _count),
        Interlocked.Read(ref _total),
        Interlocked.Read(ref _worst),
        Limit);

    /// <summary>
    /// The figure for the hour so far, and a fresh hour after it. The consumer, at the hourly summary.
    /// A value recorded on another thread at the same moment lands in one hour or the next.
    /// </summary>
    public TimingFigure TakeHour() => new(
        Name,
        Unit,
        Interlocked.Exchange(ref _hourCount, 0),
        Interlocked.Exchange(ref _hourTotal, 0),
        Interlocked.Exchange(ref _hourWorst, 0),
        Limit);

    private string Show(long value) => TimingFigure.Show(Unit, value);

    private static void Raise(ref long worst, long value)
    {
        var seen = Interlocked.Read(ref worst);

        while (value > seen)
        {
            var was = Interlocked.CompareExchange(ref worst, value, seen);

            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }
}

/// <summary>One timing's figure, as a copy (T1.66).</summary>
/// <param name="Name">The identifier.</param>
/// <param name="Unit">What the values are.</param>
/// <param name="Count">How many values.</param>
/// <param name="Total">Their sum, in the unit's storage.</param>
/// <param name="Worst">The largest, in the unit's storage.</param>
/// <param name="Limit">The value above which it warns.</param>
public sealed record TimingFigure(string Name, TimingUnit Unit, long Count, long Total, long Worst, long Limit)
{
    /// <summary>The average, in milliseconds or records; 0 with no values.</summary>
    public double Average => Count == 0 ? 0 : Convert(Unit, Total) / Count;

    /// <summary>The worst, in milliseconds or records.</summary>
    public double WorstShown => Convert(Unit, Worst);

    /// <summary>The limit, in milliseconds or records.</summary>
    public double LimitShown => Convert(Unit, Limit);

    /// <summary>The figure as identifiers and numbers: <c>queueWait n=12 avg=0.4ms worst=2.1ms</c>.</summary>
    public string ToDetail() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Name} n={Count} avg={Format(Unit, Average)} worst={Show(Unit, Worst)}");

    /// <summary>A stored value as it is shown: <c>2.1ms</c>, or <c>37</c> records.</summary>
    public static string Show(TimingUnit unit, long value) => Format(unit, Convert(unit, value));

    private static double Convert(TimingUnit unit, long value) =>
        unit == TimingUnit.Milliseconds ? TimeSpan.FromTicks(value).TotalMilliseconds : value;

    private static string Format(TimingUnit unit, double value) =>
        unit == TimingUnit.Milliseconds
            ? string.Create(CultureInfo.InvariantCulture, $"{value:0.#}ms")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#}");
}

/// <summary>
/// The six measured timings of #86, with its limits (T1.66). The seventh, the start-up phases, is
/// <see cref="Hosting.StartupPhases"/>.
/// </summary>
public sealed class Timings
{
    /// <summary>Creates the six timings.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
    public Timings(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        QueueWait = new Timing(
            "queueWait", TimingUnit.Milliseconds, TimeSpan.FromSeconds(1).Ticks,
            "The one thread that applies events is behind or stuck: rows and sounds are late.", logger);
        TickLateness = new Timing(
            "tickLateness", TimingUnit.Milliseconds, TimeSpan.FromSeconds(5).Ticks,
            "Something blocked the thread that applies events: nudges and the silence sweep are late too.", logger);
        ApplyTime = new Timing(
            "applyTime", TimingUnit.Milliseconds, TimeSpan.FromMilliseconds(50).Ticks,
            "Applying one event to the sessions took too long: a rule got slow.", logger);
        ArchiveBacklog = new Timing(
            "archiveBacklog", TimingUnit.Records, 512,
            "The disk is slow: records are dropped at 1,024.", logger);
        UiHop = new Timing(
            "uiHop", TimingUnit.Milliseconds, TimeSpan.FromMilliseconds(500).Ticks,
            "The window's thread is stalled: the rows on screen are late.", logger);
        HookRoundTrip = new Timing(
            "hookRoundTrip", TimingUnit.Milliseconds, TimeSpan.FromSeconds(1).Ticks,
            "The hook script, curl or a firewall is slow: each message from Claude Code costs this.", logger);
    }

    /// <summary>Arrival to apply: the event's <c>Timestamp</c>, stamped at arrival, to the consumer's clock.</summary>
    public Timing QueueWait { get; }

    /// <summary>When the tick was due to when it ran.</summary>
    public Timing TickLateness { get; }

    /// <summary>One <c>SessionRegistry.Apply</c>.</summary>
    public Timing ApplyTime { get; }

    /// <summary>The archive channel's count at each hand-off.</summary>
    public Timing ArchiveBacklog { get; }

    /// <summary>A post to the window's dispatcher until the posted work runs.</summary>
    public Timing UiHop { get; }

    /// <summary>The self-test's round trip (T1.61).</summary>
    public Timing HookRoundTrip { get; }

    /// <summary>The six, in #86's order.</summary>
    public IReadOnlyList<Timing> All => [QueueWait, TickLateness, ApplyTime, ArchiveBacklog, UiHop, HookRoundTrip];
}
