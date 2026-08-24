namespace ClaudeDashboard.App.Pipeline;

/// <summary>
/// Enforces that exactly one thread is inside the region that mutates the Registry and the
/// sound engine (Impl §2.2, §4).
/// </summary>
/// <remarks>
/// <para>
/// The Registry and the sound-policy engine hold no locks, on the stated assumption that one
/// thread mutates them. Until T1.9 nothing existed that could break that; the pipeline is the
/// first code in a position to, and if it does the symptom is a data race — intermittent,
/// unreproducible, and invisible in a green suite.
/// </para>
/// <para>
/// <strong>Why a guard rather than a comment.</strong> "Only the consumer calls this" is an
/// enumeration of the callers someone thought of. It stays true only while everyone who adds a
/// caller happens to know. This makes the invariant something the program checks.
/// </para>
/// <para>
/// <strong>It prevents rather than merely detects, for every path through
/// <see cref="Enter"/>.</strong> The occupancy claim is a single
/// <see cref="Interlocked.CompareExchange(ref object, object, object)"/>, so a second thread
/// throws <em>before</em> touching anything the first is working on. What it cannot do is stop
/// a caller that bypasses it and reaches the Registry directly — which is why moving the guard
/// inside Core, where the mutators could enter it themselves, would close the last gap.
/// </para>
/// </remarks>
public sealed class SingleWriterGuard
{
    private Occupant? _occupant;
    private int _violationCount;

    /// <summary>How many violations have been observed. Diagnostic only.</summary>
    public int ViolationCount => Volatile.Read(ref _violationCount);

    /// <summary>
    /// Enters the single-writer region.
    /// </summary>
    /// <param name="reason">What is being done, so a violation names both sides.</param>
    /// <returns>A scope that leaves the region when disposed.</returns>
    /// <exception cref="SingleWriterViolationException">Another thread is already inside.</exception>
    public Scope Enter(string reason)
    {
        var mine = new Occupant(Environment.CurrentManagedThreadId, reason);

        // Occupancy and identity are claimed by the same atomic write, so there is no window in
        // which the region is held by a thread that has not yet said who it is. Recording them
        // separately would let a second thread arrive between the two and report thread 0 with
        // no reason — the exception would fire correctly and then say nothing useful.
        var existing = Interlocked.CompareExchange(ref _occupant, mine, null);

        if (existing is not null)
        {
            // Interlocked, because a plain increment inside the type whose job is catching data
            // races would be one.
            Interlocked.Increment(ref _violationCount);

            throw new SingleWriterViolationException(
                $"Two threads entered the single-writer region at once: thread {existing.ThreadId} is " +
                $"'{existing.Reason}', thread {mine.ThreadId} is '{mine.Reason}'. The Registry and the " +
                "sound engine are lock-free on the assumption that this cannot happen (Impl §2.2, §4); " +
                "the event consumer's read loop and its nudge tick must share one loop.");
        }

        return new Scope(this);
    }

    private void Leave() => Interlocked.Exchange(ref _occupant, null);

    /// <summary>Who holds the region, and what for.</summary>
    private sealed record Occupant(int ThreadId, string Reason);

    /// <summary>Occupancy of the single-writer region; disposing leaves it.</summary>
    public readonly struct Scope(SingleWriterGuard guard) : IDisposable
    {
        /// <summary>Leaves the region.</summary>
        public void Dispose() => guard?.Leave();
    }
}

/// <summary>Thrown when two threads enter the single-writer region at once.</summary>
public sealed class SingleWriterViolationException : InvalidOperationException
{
    /// <inheritdoc/>
    public SingleWriterViolationException(string message)
        : base(message)
    {
    }

    /// <inheritdoc/>
    public SingleWriterViolationException()
    {
    }

    /// <inheritdoc/>
    public SingleWriterViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
