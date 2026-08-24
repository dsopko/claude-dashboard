namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// Runs work on dedicated threads that are released together.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Dedicated threads, not the thread pool.</strong> A concurrency test that never
/// achieves concurrency passes for the wrong reason forever, and <c>Task.Run</c> gives no
/// guarantee that N tasks are ever in flight at once — the pool decides, and under load it may
/// run them one after another. A rendezvous barrier over pool work is worse than useless: it
/// deadlocks whenever the pool schedules fewer workers than the barrier is waiting for.
/// </para>
/// <para>
/// A thread per participant makes the overlap real, and the barrier makes it simultaneous: no
/// thread does any work until every thread is ready to.
/// </para>
/// </remarks>
internal static class Concurrently
{
    /// <summary>
    /// Runs <paramref name="work"/> on <paramref name="threads"/> real threads, all released at
    /// the same instant, and waits for them.
    /// </summary>
    /// <param name="threads">How many participants.</param>
    /// <param name="work">The work, given its participant index.</param>
    /// <returns>Whatever any participant threw, or null.</returns>
    public static Exception? Run(int threads, Action<int> work)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        ArgumentNullException.ThrowIfNull(work);

        using var allReady = new Barrier(threads);
        var failures = new Exception?[threads];
        var running = new Thread[threads];

        for (var i = 0; i < threads; i++)
        {
            var index = i;
            running[i] = new Thread(() =>
            {
                try
                {
                    // Nobody proceeds until everybody has arrived.
                    allReady.SignalAndWait();
                    work(index);
                }
                catch (Exception ex)
                {
                    failures[index] = ex;
                }
            })
            {
                IsBackground = true,
                Name = $"concurrent-{index}",
            };
        }

        foreach (var thread in running)
        {
            thread.Start();
        }

        foreach (var thread in running)
        {
            Assert.True(
                thread.Join(TimeSpan.FromSeconds(30)),
                $"Thread {thread.Name} did not finish; the test would otherwise hang.");
        }

        return failures.FirstOrDefault(failure => failure is not null);
    }
}
