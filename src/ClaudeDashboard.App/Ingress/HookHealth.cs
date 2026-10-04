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
/// <strong>No text from a post is kept here.</strong> A refused post is not trusted, and an accepted
/// one carries the operator's words. This holds instants, counts, and the self-test's own value.
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

    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _recentRefusals = new();

    private long _lastHeardTicks;
    private long _refusedCount;
    private DateTimeOffset? _refusedShownUntil;
    private string? _pendingTest;
    private TaskCompletionSource<DateTimeOffset>? _arrival;
    private SelfTestResult? _lastSelfTest;

    /// <summary>
    /// Told on the request thread for each refused post (the decisions recorder writes
    /// <c>HookRefused</c>). Set once at composition.
    /// </summary>
    public Action<DateTimeOffset>? RefusedPost { get; set; }

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

        lock (_gate)
        {
            _recentRefusals.Enqueue(at);

            while (_recentRefusals.Count > 0 && at - _recentRefusals.Peek() >= RefusalWindow)
            {
                _recentRefusals.Dequeue();
            }

            var shown = _refusedShownUntil is { } until && at < until;

            if (shown || _recentRefusals.Count >= RefusalsToShow)
            {
                _refusedShownUntil = at + RefusalWindow;
            }
        }

        RefusedPost?.Invoke(at);
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

/// <summary><c>/state</c>'s <c>health</c> object (T1.61). #76 adds to it.</summary>
/// <param name="LastHeardAt">When the last real message was accepted, in UTC, or null since start.</param>
/// <param name="SelfTest">The last self-test, or null before the first has finished.</param>
public sealed record HealthEntry(DateTime? LastHeardAt, SelfTestEntry? SelfTest);

/// <summary>The self-test in <c>/state</c>.</summary>
/// <param name="Passed">Whether the test message arrived.</param>
/// <param name="RoundTripMs">The round trip in milliseconds, or null.</param>
/// <param name="At">When it ended, in UTC.</param>
public sealed record SelfTestEntry(bool Passed, long? RoundTripMs, DateTime At);
