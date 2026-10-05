namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// What the dashboard knows about the path from Claude Code (T1.61, issue #74): when it last heard
/// a message, how many posts it refused, and how the last self-test went.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written on request threads and the self-test's thread, read on the UI tick and by
/// <c>/state</c>.</strong> The last-heard instant is one <see cref="long"/> of ticks, published with
/// <see cref="Interlocked"/>. The refusals and the self-test sit behind one small lock, because each
/// is more than one value. Nothing here waits on a disk, a process or the UI.
/// </para>
/// <para>
/// <strong>Last heard is information, never an alarm</strong> (Design §3). A long gap changes no
/// colour, plays no sound and raises no notice: an absence of activity must never make anything
/// louder. It is a fact to read when the board looks too quiet.
/// </para>
/// <para>
/// <strong>No text from a post is kept here.</strong> A refused post is not trusted, and nothing here needs the
/// text of an accepted one. This holds instants, counts, and the self-test's own value.
/// </para>
/// </remarks>
public sealed class HookHealth
{
    /// <summary>
    /// The self-test's own event name. Not a Claude Code event, and not in
    /// <see cref="HookEventNames.Accepted"/>: ingress takes it out before the mapper.
    /// </summary>
    public const string SelfTestEventName = "ClaudeDashboardSelfTest";

    /// <summary>The JSON field that carries the self-test's one-time value.</summary>
    public const string SelfTestValueField = "self_test";

    /// <summary>
    /// How many refusals within <see cref="RefusalWindow"/> show the notice: three.
    /// </summary>
    /// <remarks>
    /// <strong>One refusal after a restart is normal</strong> (event flow §11): a hook can read
    /// <c>listening.txt</c> a moment before a restart replaces it, and post the old token once. Two
    /// is still a race with one restart. Three in ten minutes is a token that keeps failing (the
    /// operator's ruling of 2026-10-03 on #74).
    /// </remarks>
    public const int RefusalsToShow = 3;

    /// <summary>The window the refusals are counted in, and how long the notice stays after the last.</summary>
    public static readonly TimeSpan RefusalWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The least time between two <c>HookRefused</c> rows: one second (the operator's ruling of
    /// 2026-10-04 on #74).
    /// </summary>
    /// <remarks>
    /// A flood of refused posts wrote a row for each one, about 100 MB a minute in the review's
    /// probe. Now the refusals in between are counted, and the next row carries the count
    /// (<c>refused=37</c>). A lone refusal still writes its row at once, and the tick writes what a
    /// flood that stopped left over, so no refusal goes unrecorded while the dashboard runs. At a stop,
    /// the refusals counted since the last row are not written: at most one tick's worth, 15 seconds
    /// (T1.62, from the T1.61 review; a flush at the stop was not chosen, because the decisions of a
    /// stop would have to pass the consumer after it has drained).
    /// </remarks>
    public static readonly TimeSpan RefusedRowEvery = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _recentRefusals = new();

    private long _lastHeardTicks;
    private long _refusedCount;
    private DateTimeOffset? _refusedShownUntil;
    private DateTimeOffset? _lastRefusedRowAt;
    private long _refusedSinceRow;
    private string? _pendingTest;
    private TaskCompletionSource<DateTimeOffset>? _arrival;
    private SelfTestResult? _lastSelfTest;

    /// <summary>
    /// Told when a <c>HookRefused</c> row is due, with its time and the refusals it counts: at most
    /// once each <see cref="RefusedRowEvery"/>, on the request thread or the tick. The decisions
    /// recorder writes the row. Set once at composition.
    /// </summary>
    public Action<DateTimeOffset, long>? RefusedPost { get; set; }

    /// <summary>When the last real message was accepted, or null since start. Read from any thread.</summary>
    public DateTimeOffset? LastHeardAt =>
        Interlocked.Read(ref _lastHeardTicks) is var ticks and not 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <summary>How many posts were refused since start. Read from any thread; #76 reads it.</summary>
    public long RefusedCount => Interlocked.Read(ref _refusedCount);

    /// <summary>The last self-test, or null before the first has finished.</summary>
    public SelfTestResult? LastSelfTest
    {
        get
        {
            lock (_gate)
            {
                return _lastSelfTest;
            }
        }
    }

    /// <summary>A real message from Claude Code was accepted. Never the self-test, never a refused post.</summary>
    public void Heard(DateTimeOffset at) => Interlocked.Exchange(ref _lastHeardTicks, at.UtcTicks);

    /// <summary>A post was refused: its token did not match.</summary>
    /// <remarks>
    /// Three within <see cref="RefusalWindow"/> show the notice, and each later refusal while it
    /// shows keeps it for another <see cref="RefusalWindow"/>: it clears ten minutes after the last.
    /// </remarks>
    public void Refused(DateTimeOffset at)
    {
        Interlocked.Increment(ref _refusedCount);

        long due;

        lock (_gate)
        {
            _recentRefusals.Enqueue(at);

            while (_recentRefusals.Count > 0 && at - _recentRefusals.Peek() >= RefusalWindow)
            {
                _recentRefusals.Dequeue();
            }

            // At most RefusalsToShow instants (T1.61 review): the newest three decide whether three fell in
            // the window, so older ones add nothing. A flood of refused posts then costs three instants of
            // memory, not one for each post. Degrade, never crash.
            while (_recentRefusals.Count > RefusalsToShow)
            {
                _recentRefusals.Dequeue();
            }

            var shown = _refusedShownUntil is { } until && at < until;

            if (shown || _recentRefusals.Count >= RefusalsToShow)
            {
                _refusedShownUntil = at + RefusalWindow;
            }

            _refusedSinceRow++;
            due = RowDue(at);
        }

        if (due > 0)
        {
            RefusedPost?.Invoke(at, due);
        }
    }

    /// <summary>
    /// On the tick: writes the refusals a flood that stopped left uncounted, as one last row, once a
    /// second has passed since the last row.
    /// </summary>
    public void FlushRefusals(DateTimeOffset now)
    {
        long due;

        lock (_gate)
        {
            due = _refusedSinceRow > 0 ? RowDue(now) : 0;
        }

        if (due > 0)
        {
            RefusedPost?.Invoke(now, due);
        }
    }

    /// <summary>
    /// The count for a row due at <paramref name="at"/>, or 0 if the last row was less than
    /// <see cref="RefusedRowEvery"/> ago. Called under the lock.
    /// </summary>
    private long RowDue(DateTimeOffset at)
    {
        if (_lastRefusedRowAt is { } last && at - last < RefusedRowEvery)
        {
            return 0;
        }

        var due = _refusedSinceRow;
        _refusedSinceRow = 0;
        _lastRefusedRowAt = at;

        return due;
    }

    /// <summary>How many refusal instants are held now: never more than <see cref="RefusalsToShow"/>.</summary>
    internal int HeldRefusals
    {
        get
        {
            lock (_gate)
            {
                return _recentRefusals.Count;
            }
        }
    }

    /// <summary>Whether the refused notice shows at <paramref name="now"/>.</summary>
    public bool RefusalsShowAt(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _refusedShownUntil is { } until && now < until;
        }
    }

    /// <summary>
    /// Starts waiting for a self-test with this one-time value. Returns the task that completes
    /// with the arrival time. A test already waiting is replaced.
    /// </summary>
    internal Task<DateTimeOffset> Expect(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        lock (_gate)
        {
            _pendingTest = value;
            _arrival = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);

            return _arrival.Task;
        }
    }

    /// <summary>
    /// A self-test post arrived. Returns whether it carried the value being waited for. A stale or
    /// forged value is answered like any post and changes nothing.
    /// </summary>
    public bool TestArrived(string? value, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (value is null || _pendingTest is null || !string.Equals(value, _pendingTest, StringComparison.Ordinal))
            {
                return false;
            }

            _pendingTest = null;
            _arrival?.TrySetResult(at);

            return true;
        }
    }

    /// <summary>Records how a self-test ended.</summary>
    internal void Finished(SelfTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            _pendingTest = null;
            _lastSelfTest = result;
        }
    }

    /// <summary>The <c>health</c> object of <c>/state</c>: instants in UTC, no text.</summary>
    public HealthEntry Report()
    {
        var test = LastSelfTest;

        return new HealthEntry(
            LastHeardAt?.UtcDateTime,
            test is null ? null : new SelfTestEntry(test.Passed, test.RoundTripMs, test.At.UtcDateTime));
    }
}

/// <summary>Why a self-test failed, where it can be known.</summary>
public enum SelfTestCause
{
    /// <summary>It passed.</summary>
    None = 0,

    /// <summary>The script is not where Claude Code runs it from.</summary>
    ScriptMissing = 1,

    /// <summary><c>curl.exe</c> is not in <c>System32</c>, so the script cannot post.</summary>
    CurlMissing = 2,

    /// <summary>The script ran and nothing arrived in time.</summary>
    NothingArrived = 3,

    /// <summary>The script could not be started.</summary>
    CouldNotRun = 4,
}

/// <summary>How a self-test ended (T1.61).</summary>
/// <param name="Passed">Whether the test message arrived in time.</param>
/// <param name="RoundTripMs">From starting the script to the arrival, or null if it did not arrive.</param>
/// <param name="At">When the test ended.</param>
/// <param name="Cause">Why it failed, or <see cref="SelfTestCause.None"/>.</param>
public sealed record SelfTestResult(bool Passed, long? RoundTripMs, DateTimeOffset At, SelfTestCause Cause);

/// <summary><c>/state</c>'s <c>health</c> object (T1.61, T1.65).</summary>
/// <remarks>
/// <c>lastHeardAt</c> and <c>selfTest</c> are read from <see cref="HookHealth"/> at the request. The
/// other members are the consumer's last published snapshot (<see cref="Pipeline.HealthBoard"/>), up
/// to one tick old, and null before the first tick. T1.66 added its timings as one more member, and
/// none of these moved. Identifiers and numbers only.
/// </remarks>
/// <param name="LastHeardAt">When the last real message was accepted, in UTC, or null since start.</param>
/// <param name="SelfTest">The last self-test, or null before the first has finished.</param>
public sealed record HealthEntry(DateTime? LastHeardAt, SelfTestEntry? SelfTest)
{
    /// <summary>The informational version.</summary>
    public string? Version { get; init; }

    /// <summary>When the dashboard started, in UTC.</summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>The tick that took these counts, in UTC: they can be up to one tick old.</summary>
    public DateTime? CountedAt { get; init; }

    /// <summary>The port ingress bound, and whether it can receive messages.</summary>
    public IngressEntry? Ingress { get; init; }

    /// <summary>Whether the history database is being written.</summary>
    public DatabaseState? Database { get; init; }

    /// <summary>Whether a sound output device is bound.</summary>
    public bool? SoundOutput { get; init; }

    /// <summary>The pause and mute modes.</summary>
    public ModesEntry? Modes { get; init; }

    /// <summary>The counts since the start, for the present hour, and for the last summary.</summary>
    public CountsEntry? Counts { get; init; }

    /// <summary>The timings that would show a stall, since the start, and the start-up phases (T1.66).</summary>
    public TimingsEntry? Timings { get; init; }

    /// <summary>This entry with the consumer's snapshot, or as it is when there is none yet.</summary>
    public HealthEntry With(Pipeline.HealthSnapshot? snapshot) => snapshot is null
        ? this
        : this with
        {
            Version = snapshot.Version,
            StartedAt = snapshot.StartedAt.UtcDateTime,
            CountedAt = snapshot.CountedAt.UtcDateTime,
            Ingress = new IngressEntry(snapshot.Port, snapshot.CanReceive),
            Database = snapshot.DatabaseAvailable switch
            {
                true => DatabaseState.Writing,
                false => DatabaseState.NotWriting,
                null => DatabaseState.NotYetKnown,
            },
            SoundOutput = snapshot.SoundOutput,
            Modes = new ModesEntry(snapshot.Paused, snapshot.MutedUntil?.UtcDateTime),
            Counts = new CountsEntry(
                snapshot.SinceStart,
                snapshot.ThisHour,
                snapshot is { LastHour: { } hour, LastHourFrom: { } from, LastHourTo: { } to }
                    ? new LastHourEntry(from.UtcDateTime, to.UtcDateTime, snapshot.LastHourPartial, hour)
                    : null),
            Timings = snapshot.Timings is { } timings ? TimingsEntry.From(timings) : null,
        };
}

/// <summary>The timings in <c>health</c> (T1.66, issue #86): each since the start.</summary>
/// <param name="QueueWait">Arrival to apply.</param>
/// <param name="TickLateness">When the tick was due to when it ran.</param>
/// <param name="ApplyTime">One <c>SessionRegistry.Apply</c>.</param>
/// <param name="ArchiveBacklog">The archive channel's count at each hand-off.</param>
/// <param name="UiHop">A post to the window's dispatcher until the posted work runs.</param>
/// <param name="HookRoundTrip">The self-test's round trip.</param>
/// <param name="Startup">The start-up phases, or null before the start has logged them.</param>
public sealed record TimingsEntry(
    TimingEntry QueueWait,
    TimingEntry TickLateness,
    TimingEntry ApplyTime,
    TimingEntry ArchiveBacklog,
    TimingEntry UiHop,
    TimingEntry HookRoundTrip,
    IReadOnlyList<Hosting.StartupPhase>? Startup)
{
    /// <summary>The entry for a snapshot's timings, in #86's order.</summary>
    public static TimingsEntry From(Pipeline.TimingsSnapshot timings)
    {
        ArgumentNullException.ThrowIfNull(timings);

        var figures = timings.Figures.Select(TimingEntry.From).ToList();

        return new TimingsEntry(figures[0], figures[1], figures[2], figures[3], figures[4], figures[5], timings.Startup);
    }
}

/// <summary>One timing in <c>health</c>: count, average, worst and limit, in its unit.</summary>
/// <param name="Count">How many values.</param>
/// <param name="Average">Their average, in milliseconds or records.</param>
/// <param name="Worst">The largest.</param>
/// <param name="Limit">The value above which it warns.</param>
/// <param name="Unit"><c>Milliseconds</c> or <c>Records</c>.</param>
/// <param name="Skipped">Values that could not be measured, such as an event with no arrival instant.</param>
public sealed record TimingEntry(long Count, double Average, double Worst, double Limit, Pipeline.TimingUnit Unit, long Skipped)
{
    /// <summary>The entry for one figure.</summary>
    public static TimingEntry From(Pipeline.TimingFigure figure)
    {
        ArgumentNullException.ThrowIfNull(figure);

        return new TimingEntry(figure.Count, figure.Average, figure.WorstShown, figure.LimitShown, figure.Unit, figure.Skipped);
    }
}

/// <summary>Ingress in <c>health</c>.</summary>
/// <param name="Port">The port bound, or null when it could not bind.</param>
/// <param name="Receiving">Whether it can receive messages from Claude Code.</param>
public sealed record IngressEntry(int? Port, bool Receiving);

/// <summary>The history database in <c>health</c>.</summary>
public enum DatabaseState
{
    /// <summary>Nothing has been written yet.</summary>
    NotYetKnown = 0,

    /// <summary>The last write succeeded.</summary>
    Writing = 1,

    /// <summary>The last write failed (T1.54's notice shows).</summary>
    NotWriting = 2,
}

/// <summary>The modes in <c>health</c>.</summary>
/// <param name="Paused">Whether monitoring is paused.</param>
/// <param name="MutedUntil">When a global mute lapses, in UTC, or null.</param>
public sealed record ModesEntry(bool Paused, DateTime? MutedUntil);

/// <summary>The counts in <c>health</c>.</summary>
/// <param name="SinceStart">Since the start.</param>
/// <param name="ThisHour">Since the last hourly summary, or the start.</param>
/// <param name="LastHour">The last hourly summary, or null before the first.</param>
public sealed record CountsEntry(Pipeline.HealthCounts SinceStart, Pipeline.HealthCounts ThisHour, LastHourEntry? LastHour);

/// <summary>The last hourly summary in <c>health</c>.</summary>
/// <param name="From">Where it began, in UTC.</param>
/// <param name="To">Where it ended, in UTC.</param>
/// <param name="Partial">Whether it was the first after a start, from the start.</param>
/// <param name="Counts">Its counts.</param>
public sealed record LastHourEntry(DateTime From, DateTime To, bool Partial, Pipeline.HealthCounts Counts);

/// <summary>The self-test in <c>/state</c>.</summary>
/// <param name="Passed">Whether the test message arrived.</param>
/// <param name="RoundTripMs">The round trip in milliseconds, or null.</param>
/// <param name="At">When it ended, in UTC.</param>
public sealed record SelfTestEntry(bool Passed, long? RoundTripMs, DateTime At);
