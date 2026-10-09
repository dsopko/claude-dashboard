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
    private IReadOnlyList<string>? _lastKinds;

    /// <summary>What was last published. Read from any thread.</summary>
    public UsageReadings Current => Volatile.Read(ref _current);

    /// <summary>A usage post was accepted at <paramref name="at"/>, with the readings it carried.</summary>
    /// <returns>
    /// The readings as this post left them. A reading of the post that is in it, by reference, is one the rule kept,
    /// so a caller can name what the post changed without a second read that another post could overtake.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="windows"/> is null.</exception>
    public UsageReadings Heard(IReadOnlyList<UsageWindow> windows, DateTimeOffset at) => Accept(windows, at).Readings;

    /// <summary>
    /// A usage post was accepted at <paramref name="at"/>: the readings as it left them, what became of each reading
    /// it carried, and the kinds the post before it carried (MOD.8, ruling R15).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>All in one step under the lock</strong>, so two posts at once each compare with the post that came
    /// before it, and a reading's outcome is the one the rule applied, not one a later read would see.
    /// </para>
    /// <para>
    /// <strong>The previous post is any session's</strong>, from the dashboard's start: a limit belongs to the
    /// account. A post with no reading in it counts, and carries no kind.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="windows"/> is null.</exception>
    public UsagePost Accept(IReadOnlyList<UsageWindow> windows, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(windows);

        lock (_gate)
        {
            var next = _current.Heard(at);
            var outcomes = new List<UsageOutcome>(windows.Count);

            foreach (var window in windows)
            {
                var refusal = next.RefusalOf(window);

                outcomes.Add(new UsageOutcome(window, refusal));
                next = next.With(window);
            }

            // A kind the readings could never hold is not a kind the post carries: no line names it.
            IReadOnlyList<string> carried =
            [
                .. outcomes
                    .Where(outcome => UsageRefusals.NamesItsKind(outcome.Refusal))
                    .Select(outcome => outcome.Reading.Kind)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ];

            var previous = _lastKinds;
            _lastKinds = carried;

            Volatile.Write(ref _current, next);

            return new UsagePost(next, outcomes, carried, previous);
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

/// <summary>What one accepted usage post did (MOD.8, ruling R15), from <see cref="UsageBoard.Accept"/>.</summary>
/// <param name="Readings">The readings as the post left them.</param>
/// <param name="Outcomes">Each reading the post carried, in its order, with why it was not kept, or null.</param>
/// <param name="Kinds">
/// The kinds the post carried, in ordinal order, without a kind the readings could never hold (none, or too long).
/// </param>
/// <param name="PreviousKinds">The kinds the post before it carried, or null for the first since the start.</param>
public sealed record UsagePost(
    UsageReadings Readings,
    IReadOnlyList<UsageOutcome> Outcomes,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string>? PreviousKinds);

/// <summary>One reading of a usage post, and why the board did not keep it, or null when it did.</summary>
/// <param name="Reading">The reading as the post sent it. Only a kept or a nameable one's kind is ever shown.</param>
/// <param name="Refusal">Why it was not kept, from <see cref="UsageReadings.RefusalOf"/>; null when it was.</param>
public sealed record UsageOutcome(UsageWindow Reading, UsageRefusal? Refusal);

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
