using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// Holds the plan's limits as the sessions last reported them, for <c>/state</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written on request threads and read at a request</strong>, as <c>HookHealth</c> is.
/// One small lock puts the writers in a line. A reader takes one published reference and never
/// the lock.
/// </para>
/// <para>
/// <strong>Not the Registry.</strong> A limit belongs to the account, not to a session. No event,
/// no row in the history and no sound comes from a reading, and the event channel never sees one.
/// </para>
/// <para>
/// <strong>No text from a post is kept but the kind and the session id</strong>, each with a
/// bound on its length (<see cref="UsageReadings.MaxKindLength"/>,
/// <see cref="UsageReader.MaxSessionIdLength"/>).
/// </para>
/// </remarks>
public sealed class UsageBoard
{
    private readonly Lock _gate = new();
    private UsageReadings _current = UsageReadings.Empty;

    /// <summary>What was last published. Read from any thread.</summary>
    public UsageReadings Current => Volatile.Read(ref _current);

    /// <summary>A usage post was accepted at <paramref name="at"/>, with the readings it carried.</summary>
    /// <returns>
    /// The readings as this post left them. A reading of the post that is in it, by reference, is one the rule kept,
    /// so a caller can name what the post changed without a second read that another post could overtake.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="windows"/> is null.</exception>
    public UsageReadings Heard(IReadOnlyList<UsageWindow> windows, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(windows);

        lock (_gate)
        {
            var next = _current.Heard(at);

            foreach (var window in windows)
            {
                next = next.With(window);
            }

            Volatile.Write(ref _current, next);

            return next;
        }
    }

    /// <summary>
    /// The <c>usage</c> object of <c>/state</c> at <paramref name="now"/>, or null before the first
    /// post. Instants in UTC. A limit whose reset time has passed is not in it.
    /// </summary>
    public UsageEntry? Report(DateTimeOffset now)
    {
        var current = Current;

        return current.LastHeardAt is { } heard
            ? new UsageEntry(
                heard.UtcDateTime,
                [
                    .. current.At(now).Windows.Select(window => new UsageWindowEntry(
                        window.Kind,
                        window.PercentUsed,
                        window.ResetsAt?.UtcDateTime,
                        window.HeardAt.UtcDateTime,
                        window.SessionId)),
                ])
            : null;
    }
}

/// <summary><c>/state</c>'s <c>usage</c> object.</summary>
/// <param name="LastHeardAt">When a usage post last arrived, in UTC.</param>
/// <param name="Windows">The newest reading of each limit that is still open.</param>
public sealed record UsageEntry(DateTime LastHeardAt, IReadOnlyList<UsageWindowEntry> Windows);

/// <summary>One limit in <c>/state</c>.</summary>
/// <param name="Kind">The limit, as Claude Code named it.</param>
/// <param name="PercentUsed">How much of it is used.</param>
/// <param name="ResetsAt">When it resets, in UTC, or null.</param>
/// <param name="HeardAt">When the reading arrived, in UTC.</param>
/// <param name="SessionId">The session that sent it, or null.</param>
public sealed record UsageWindowEntry(
    string Kind,
    double PercentUsed,
    DateTime? ResetsAt,
    DateTime HeardAt,
    string? SessionId);
