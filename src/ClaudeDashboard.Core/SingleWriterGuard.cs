namespace ClaudeDashboard.Core;

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
/// <strong>It prevents rather than merely detects.</strong> The occupancy claim is a single
/// <see cref="Interlocked.CompareExchange(ref object, object, object)"/>, so a second thread
/// throws <em>before</em> touching anything the first is working on.
/// </para>
/// <para>
/// <strong>Coverage is structural, not opt-in.</strong> The mutators enter it themselves —
/// <see cref="SessionRegistry.Apply"/> and every mutating method on
/// <see cref="SoundPolicyEngine"/> — so a caller cannot miss the guard by not knowing about it.
/// Wrapping call sites instead would have made the protected set a list of the callers someone
/// thought of, which is the shape of assumption this whole design keeps having to correct.
/// </para>
/// <para>
/// <strong>What it does not cover: reads.</strong> A thread reading
/// <see cref="SessionRegistry.Sessions"/> — a live view, not a snapshot — or the sound engine's
/// query methods while the writer is mid-mutation is still racing, and no guard here can catch
/// it: the Registry hands back a collection whose enumeration happens later, outside any scope
/// this type could hold. Reads remain what they already were, documented as belonging to the
/// consumer thread. Guarding them would also refuse two concurrent readers, which are safe, and
/// making the guard distinguish them would make it a reader-writer lock — something that
/// <em>waits</em>, turning a loud bug into a silent stall.
/// </para>
/// </remarks>
public sealed class SingleWriterGuard
{
    private Occupant? _occupant;
    private int _violationCount;
    private int _depth;

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
        var me = Environment.CurrentManagedThreadId;
        var mine = new Occupant(me, reason);

        // Occupancy and identity are claimed by the same atomic write, so there is no window in
        // which the region is held by a thread that has not yet said who it is. Recording them
        // separately would let a second thread arrive between the two and report thread 0 with
        // no reason — the exception would fire correctly and then say nothing useful.
        var existing = Interlocked.CompareExchange(ref _occupant, mine, null);

        if (existing is null)
        {
            _depth = 1;
            return new Scope(this);
        }

        if (existing.ThreadId == me)
        {
            // Re-entrant by the owning thread, which is safe by definition — the invariant is
            // one *thread*, not one call. This is not a corner case: applying an event raises a
            // change notification, and the handler for it enters the sound engine, so the
            // ordinary path is already nested two deep. Only this thread can see or change
            // _depth while it owns the region, so a plain int is correct here.
            _depth++;
            return new Scope(this);
        }

        // Interlocked, because a plain increment inside the type whose job is catching data
        // races would be one.
        Interlocked.Increment(ref _violationCount);

        throw new SingleWriterViolationException(
            $"Two threads entered the single-writer region at once: thread {existing.ThreadId} is " +
            $"'{existing.Reason}', thread {mine.ThreadId} is '{mine.Reason}'. The Registry and the " +
            "sound engine are lock-free on the assumption that this cannot happen (Impl §2.2, §4); " +
            "every mutation must reach them on the event consumer's thread, which means through " +
            "the event channel (Impl §4).");
    }

    private void Leave()
    {
        if (_depth <= 0)
        {
            // A scope disposed twice, or one belonging to an entry that threw. Either way there
            // is nothing to release, and decrementing further would hand the region away while
            // its owner is still inside.
            return;
        }

        if (--_depth == 0)
        {
            Interlocked.Exchange(ref _occupant, null);
        }
    }

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
