using System.Globalization;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Pipeline;

/// <summary>
/// The counts the dashboard keeps, since the start and by the hour, as one snapshot that
/// <c>/state</c> reads, and an hourly summary in the log and the decisions record (T1.65, issue #76).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Built on the consumer thread, at each tick, and published like <c>StateBoard</c>.</strong>
/// <see cref="Tick"/> gathers each count from the place that already keeps it, builds one immutable
/// <see cref="HealthSnapshot"/> and swaps it in with <see cref="Volatile"/>. A request thread reads
/// <see cref="Current"/> and nothing else: never a live counter, the Registry or the sound engine.
/// The counts can therefore be up to one tick old, and <see cref="HealthSnapshot.CountedAt"/> says
/// when they were taken.
/// </para>
/// <para>
/// <strong>The hourly summary</strong> is written at the first tick after each full clock hour, in
/// UTC, and covers the time since the previous summary: one Information line and one
/// <c>HourlySummary</c> decision, with the same counts. The first one after a start is partial and
/// says so. A reader looks for "the 14:00 row", and a gap between rows shows the hours when the
/// dashboard was not running (the director's rulings of 2026-10-04). No new timer and no new
/// thread: the 15-second tick already runs.
/// </para>
/// <para>
/// Identifiers and numbers only: no title, prompt, payload or path.
/// </para>
/// </remarks>
public sealed class HealthBoard
{
    private readonly HealthSources _sources;
    private readonly ILogger _logger;
    private readonly DateTimeOffset _startedAt;
    private readonly Hosting.StartupPhases? _startup;

    private HealthSnapshot? _current;
    private DateTimeOffset _periodStart;
    private HealthCounts _atPeriodStart = HealthCounts.Zero;
    private HealthCounts? _lastHour;
    private DateTimeOffset? _lastHourFrom;
    private DateTimeOffset? _lastHourTo;
    private bool _lastHourPartial;
    private bool _first = true;

    /// <summary>Creates the board over its sources. The start time is the clock's now.</summary>
    /// <param name="sources">Where it reads the counts the consumer does not keep.</param>
    /// <param name="clock">The clock of the start time.</param>
    /// <param name="logger">Where the hourly lines go.</param>
    /// <param name="timings">The timings that would show a stall (T1.66); none when null.</param>
    /// <param name="startup">The start-up phases (T1.66); none when null.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public HealthBoard(
        HealthSources sources,
        IClock clock,
        ILogger logger,
        Timings? timings = null,
        Hosting.StartupPhases? startup = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _sources = sources;
        _logger = logger;
        _startedAt = clock.Now;
        Timings = timings;
        _startup = startup;
        _periodStart = _startedAt;
    }

    /// <summary>The timings that would show a stall (T1.66), or null in a board made without them.</summary>
    public Timings? Timings { get; }

    /// <summary>The last snapshot, or null before the first tick. Read from any thread.</summary>
    public HealthSnapshot? Current => Volatile.Read(ref _current);

    /// <summary>
    /// The counts since the start, from <paramref name="consumer"/>'s counters and the sources.
    /// The consumer thread only.
    /// </summary>
    public HealthCounts Gather(ConsumerCounts consumer) => new(
        consumer.Applied,
        consumer.Declined,
        consumer.Uncorrelated,
        _sources.Shed(),
        _sources.Lost(),
        _sources.ArchiveDropped(),
        _sources.Refused(),
        _sources.NotWritten(),
        consumer.Ticks,
        consumer.Sweeps,
        consumer.Settles);

    /// <summary>
    /// The consumer's tick: writes the hourly summary when a clock hour has passed since the last,
    /// then publishes a new snapshot. Called inside the tick's decision scope, on the consumer thread.
    /// </summary>
    /// <param name="now">The tick's instant.</param>
    /// <param name="consumer">The consumer's own counters.</param>
    /// <param name="recorder">The decisions recorder, with the tick's scope open.</param>
    public void Tick(DateTimeOffset now, ConsumerCounts consumer, DecisionRecorder? recorder)
    {
        // The all-clears, on this tick only: a figure clears a full minute after its last value over the
        // limit (T1.66 review).
        foreach (var timing in Timings?.All ?? [])
        {
            timing.CheckClear(now);
        }

        var counts = Gather(consumer);

        if (HourOf(now) > HourOf(_periodStart))
        {
            var hour = counts.Minus(_atPeriodStart);
            var partial = _first;
            var detail = hour.ToDetail();

            recorder?.HourlySummary(detail, partial);

            _logger.Information(
                "Hourly summary, {Partial:l}from {From:l} to {To:l}: {Counts:l}",
                partial ? "partial since the start, " : string.Empty,
                _periodStart.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                now.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                detail);

            // The hour's timings beside it (T1.66): the figures of the hour, which then start again.
            if (Timings is { } timings)
            {
                _logger.Information(
                    "Hourly timings, from {From:l} to {To:l}: {Timings:l}",
                    _periodStart.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                    now.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                    string.Join("; ", timings.All.Select(timing => timing.TakeHour().ToDetail())));
            }

            _lastHour = hour;
            _lastHourFrom = _periodStart;
            _lastHourTo = now;
            _lastHourPartial = partial;

            // The hour's counts start again; the counts since the start do not.
            _atPeriodStart = counts;
            _periodStart = now;
            _first = false;
        }

        Volatile.Write(ref _current, new HealthSnapshot(
            _sources.Version,
            _startedAt,
            now,
            _sources.Port,
            _sources.CanReceive,
            _sources.DatabaseAvailable(),
            _sources.SoundOutput(),
            _sources.Paused(),
            _sources.MutedUntil(),
            counts,
            counts.Minus(_atPeriodStart),
            _lastHour,
            _lastHourFrom,
            _lastHourTo,
            _lastHourPartial,
            Timings is { } measured
                ? new TimingsSnapshot([.. measured.All.Select(timing => timing.SinceStart)], _startup?.Finished)
                : null));
    }

    /// <summary>
    /// The run's worst cases, for the stop line (T1.66): <c>queueWait=2.1ms tickLateness=0ms …</c>, or
    /// null in a board made without timings.
    /// </summary>
    public string? Worst() => Timings is { } timings
        ? string.Join(" ", timings.All.Select(timing => $"{timing.Name}={TimingFigure.Show(timing.Unit, timing.SinceStart.Worst)}"))
        : null;

    /// <summary>The start of the UTC clock hour that holds <paramref name="at"/>.</summary>
    private static DateTime HourOf(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
    }
}

/// <summary>Where the board reads what the consumer does not count itself (T1.65).</summary>
/// <remarks>Each is read on the consumer thread, from a value its owner publishes.</remarks>
public sealed record HealthSources
{
    /// <summary>The informational version.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>The port ingress bound, or null.</summary>
    public int? Port { get; init; }

    /// <summary>Whether ingress can receive messages from Claude Code.</summary>
    public bool CanReceive { get; init; }

    /// <summary>Noise the event channel shed at its capacity (T1.58).</summary>
    public Func<long> Shed { get; init; } = () => 0;

    /// <summary>Events the event channel lost at its hard limit (T1.58).</summary>
    public Func<long> Lost { get; init; } = () => 0;

    /// <summary>Records the archive channel dropped because the disk could not keep up.</summary>
    public Func<long> ArchiveDropped { get; init; } = () => 0;

    /// <summary>Posts refused for a wrong or missing token (T1.61).</summary>
    public Func<long> Refused { get; init; } = () => 0;

    /// <summary>
    /// Records the store did not write: the archive writer's refused count, which is the store's
    /// failed writes and the records it lost inside the retry minute, since the start. The store's
    /// own <c>LostCount</c> starts again at each recovery, so it is not a count since the start.
    /// </summary>
    public Func<long> NotWritten { get; init; } = () => 0;

    /// <summary>Whether the last write succeeded: null before the first.</summary>
    public Func<bool?> DatabaseAvailable { get; init; } = () => null;

    /// <summary>Whether a sound output device is bound.</summary>
    public Func<bool> SoundOutput { get; init; } = () => true;

    /// <summary>Whether monitoring is paused.</summary>
    public Func<bool> Paused { get; init; } = () => false;

    /// <summary>When a global mute lapses, or null.</summary>
    public Func<DateTimeOffset?> MutedUntil { get; init; } = () => null;
}

/// <summary>The consumer's own counters, as one value (T1.65).</summary>
public readonly record struct ConsumerCounts(
    long Applied,
    long Declined,
    long Uncorrelated,
    long Ticks,
    long Sweeps,
    long Settles);

/// <summary>The counts the dashboard reports, since a moment (T1.65).</summary>
/// <param name="Applied">Events that changed a session.</param>
/// <param name="Declined">Events the Registry refused: repeats, stale, nothing to do.</param>
/// <param name="Uncorrelated">A <c>Stop</c> that matched no prompt.</param>
/// <param name="Shed">Noise the event channel shed.</param>
/// <param name="Lost">Events the event channel lost at its hard limit.</param>
/// <param name="ArchiveDropped">Records the archive channel dropped.</param>
/// <param name="Refused">Posts refused for their token.</param>
/// <param name="NotWritten">Records the store did not write.</param>
/// <param name="Ticks">Ticks of the consumer's loop.</param>
/// <param name="Sweeps">Sessions the silence sweep moved.</param>
/// <param name="Settles">Roster groups that settled.</param>
public sealed record HealthCounts(
    long Applied,
    long Declined,
    long Uncorrelated,
    long Shed,
    long Lost,
    long ArchiveDropped,
    long Refused,
    long NotWritten,
    long Ticks,
    long Sweeps,
    long Settles)
{
    /// <summary>All zero.</summary>
    public static HealthCounts Zero { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>These counts less <paramref name="earlier"/>: the counts of the time between.</summary>
    public HealthCounts Minus(HealthCounts earlier)
    {
        ArgumentNullException.ThrowIfNull(earlier);

        return new(
            Applied - earlier.Applied,
            Declined - earlier.Declined,
            Uncorrelated - earlier.Uncorrelated,
            Shed - earlier.Shed,
            Lost - earlier.Lost,
            ArchiveDropped - earlier.ArchiveDropped,
            Refused - earlier.Refused,
            NotWritten - earlier.NotWritten,
            Ticks - earlier.Ticks,
            Sweeps - earlier.Sweeps,
            Settles - earlier.Settles);
    }

    /// <summary>
    /// The counts as <c>key=value</c> identifiers, the form of the hourly line, the
    /// <c>HourlySummary</c> row's detail and the stop line.
    /// </summary>
    public string ToDetail() => string.Create(
        CultureInfo.InvariantCulture,
        $"applied={Applied} declined={Declined} uncorrelated={Uncorrelated} shed={Shed} lost={Lost} " +
        $"archiveDropped={ArchiveDropped} refused={Refused} notWritten={NotWritten} ticks={Ticks} " +
        $"sweeps={Sweeps} settles={Settles}");
}

/// <summary>
/// What <c>/state</c>'s <c>health</c> object shows from the consumer: one immutable value per tick
/// (T1.65). T1.66 added its timings as the last member; no member moved.
/// </summary>
/// <param name="Version">The informational version.</param>
/// <param name="StartedAt">When this board was made, at the host's start.</param>
/// <param name="CountedAt">The tick that took these counts.</param>
/// <param name="Port">The port ingress bound, or null.</param>
/// <param name="CanReceive">Whether ingress can receive messages.</param>
/// <param name="DatabaseAvailable">Whether the last write succeeded; null before the first.</param>
/// <param name="SoundOutput">Whether a sound output device is bound.</param>
/// <param name="Paused">Whether monitoring is paused.</param>
/// <param name="MutedUntil">When a global mute lapses, or null.</param>
/// <param name="SinceStart">The counts since the start.</param>
/// <param name="ThisHour">The counts since the last summary (or the start).</param>
/// <param name="LastHour">The counts of the last summary, or null before the first.</param>
/// <param name="LastHourFrom">Where the last summary began.</param>
/// <param name="LastHourTo">Where it ended.</param>
/// <param name="LastHourPartial">Whether the last summary was the partial first one.</param>
/// <param name="Timings">The timings since the start and the start-up phases (T1.66), or null.</param>
public sealed record HealthSnapshot(
    string Version,
    DateTimeOffset StartedAt,
    DateTimeOffset CountedAt,
    int? Port,
    bool CanReceive,
    bool? DatabaseAvailable,
    bool SoundOutput,
    bool Paused,
    DateTimeOffset? MutedUntil,
    HealthCounts SinceStart,
    HealthCounts ThisHour,
    HealthCounts? LastHour,
    DateTimeOffset? LastHourFrom,
    DateTimeOffset? LastHourTo,
    bool LastHourPartial,
    TimingsSnapshot? Timings = null);

/// <summary>The timings in the snapshot (T1.66).</summary>
/// <param name="Figures">The six measured timings since the start, in #86's order.</param>
/// <param name="Startup">The start-up phases, or null before the start has logged them.</param>
public sealed record TimingsSnapshot(IReadOnlyList<TimingFigure> Figures, IReadOnlyList<Hosting.StartupPhase>? Startup);
