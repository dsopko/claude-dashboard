namespace ClaudeDashboard.App.Pipeline;

/// <summary>
/// Detects a second thread entering the region that must have exactly one writer
/// (Impl §2.2, §4).
/// </summary>
/// <remarks>
/// <para>
/// The Registry and the sound-policy engine hold no locks, on the stated assumption that one
/// thread mutates them. Until this task nothing existed that could break that; the pipeline is
/// the first code in a position to, and if it does the symptom is a data race — intermittent,
/// unreproducible, and invisible in a green suite.
/// </para>
/// <para>
/// <strong>Why a guard rather than a comment.</strong> "Only the consumer calls this" is an
/// enumeration of the callers someone thought of. It stays true only while everyone who adds a
/// caller happens to know. This turns the invariant into something the program checks: two
/// threads inside at once is no longer a race that corrupts state quietly, it is an exception
/// on the second thread naming both, at the moment it happens.
/// </para>
/// <para>
/// It detects rather than prevents, and the distinction is honest: nothing here can stop a
/// caller invoking the Registry directly. What it can do is ensure that every path which is
/// <em>supposed</em> to be serialized fails loudly the first time it is not, instead of on a
/// customer's machine six weeks later.
/// </para>
/// </remarks>
public sealed class SingleWriterGuard
{
    private int _occupied;
    private int _ownerThreadId;
    private string? _ownerReason;

    /// <summary>How many violations have been observed. Diagnostic only.</summary>
    public int ViolationCount { get; private set; }

    /// <summary>
    /// Enters the single-writer region.
    /// </summary>
    /// <param name="reason">What is being done, so a violation names both sides.</param>
    /// <returns>A scope that leaves the region when disposed.</returns>
    /// <exception cref="SingleWriterViolationException">Another thread is already inside.</exception>
    public Scope Enter(string reason)
    {
        if (Interlocked.CompareExchange(ref _occupied, 1, 0) != 0)
        {
            ViolationCount++;

            throw new SingleWriterViolationException(
                $"Two threads entered the single-writer region at once: thread {_ownerThreadId} is " +
                $"'{_ownerReason}', thread {Environment.CurrentManagedThreadId} is '{reason}'. The " +
                "Registry and the sound engine are lock-free on the assumption that this cannot happen " +
                "(Impl §2.2, §4); the event consumer's read loop and its nudge tick must share one loop.");
        }

        _ownerThreadId = Environment.CurrentManagedThreadId;
        _ownerReason = reason;

        return new Scope(this);
    }

    private void Leave()
    {
        _ownerReason = null;
        _ownerThreadId = 0;
        Volatile.Write(ref _occupied, 0);
    }

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
