namespace ClaudeDashboard.Core;

/// <summary>One plan limit, as a Claude Code session last reported it.</summary>
/// <param name="Kind">
/// Which limit: <c>five_hour</c>, <c>seven_day</c>, or a value this build does not know. It is
/// data: nothing switches on it.
/// </param>
/// <param name="PercentUsed">
/// How much of the limit is used: 0 to 100, and above 100 on a spend limit that is exceeded.
/// </param>
/// <param name="ResetsAt">When the limit resets, or null when the reading gave no time.</param>
/// <param name="HeardAt">When the dashboard received the reading.</param>
/// <param name="SessionId">The session that sent the reading, or null.</param>
public sealed record UsageWindow(
    string Kind,
    double PercentUsed,
    DateTimeOffset? ResetsAt,
    DateTimeOffset HeardAt,
    string? SessionId);

/// <summary>
/// What the dashboard believes about the plan's limits: the newest reading of each kind.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Immutable.</strong> <see cref="Heard"/>, <see cref="With"/> and <see cref="At"/> answer a
/// new value and change nothing. The holder swaps one reference, so a reader never sees half a change.
/// </para>
/// <para>
/// <strong>A limit belongs to the account, not to a session.</strong> Each session of one account
/// reports the same limits. So the newest reading of a kind wins, whichever session sent it.
/// </para>
/// <para>
/// <strong>Information, never an alarm.</strong> Nothing here has a threshold. A limit that is
/// almost used changes no colour and plays no sound until a ruling says that it does.
/// </para>
/// </remarks>
public sealed record UsageReadings
{
    /// <summary>How many kinds are held: eight. A sender cannot make this value grow without end.</summary>
    public const int MaxKinds = 8;

    /// <summary>The longest kind that is kept: 64 characters.</summary>
    public const int MaxKindLength = 64;

    /// <summary>Nothing heard yet.</summary>
    public static UsageReadings Empty { get; } = new();

    /// <summary>When a usage post last arrived, with or without a reading in it. Null before the first.</summary>
    public DateTimeOffset? LastHeardAt { get; init; }

    /// <summary>The newest reading of each kind, in the order of the kinds' names.</summary>
    public IReadOnlyList<UsageWindow> Windows { get; init; } = [];

    /// <summary>A usage post arrived at <paramref name="at"/>.</summary>
    public UsageReadings Heard(DateTimeOffset at) => this with { LastHeardAt = at };

    /// <summary>These readings with <paramref name="reading"/> applied.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The newest reading of a kind replaces the one held</strong>, also when it shows less:
    /// a limit that was raised lowers the percentage inside one window.
    /// </para>
    /// <para>
    /// <strong>But a reading of an older window changes nothing.</strong> A window is known by its
    /// reset time. A reading whose reset time is earlier than the held one's was made in a window
    /// that has ended, and arrived late.
    /// </para>
    /// <para>
    /// <strong>A reading that cannot be true changes nothing</strong>: no kind, a kind longer than
    /// <see cref="MaxKindLength"/>, or a percentage that is negative or not a number. So does a new
    /// kind when <see cref="MaxKinds"/> are held.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is null.</exception>
    public UsageReadings With(UsageWindow reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        if (reading.Kind is not { Length: > 0 and <= MaxKindLength }
            || !double.IsFinite(reading.PercentUsed)
            || reading.PercentUsed < 0)
        {
            return this;
        }

        var held = Windows.FirstOrDefault(window => string.Equals(window.Kind, reading.Kind, StringComparison.Ordinal));

        if (held is { ResetsAt: { } heldReset } && reading.ResetsAt is { } readReset && readReset < heldReset)
        {
            return this;
        }

        if (held is null && Windows.Count >= MaxKinds)
        {
            return this;
        }

        return this with
        {
            Windows =
            [
                .. Windows
                    .Where(window => !ReferenceEquals(window, held))
                    .Append(reading)
                    .OrderBy(window => window.Kind, StringComparer.Ordinal),
            ],
        };
    }

    /// <summary>
    /// What stands at <paramref name="now"/>: a limit whose reset time has passed is left out,
    /// because its percentage is no longer true. A limit with no reset time stays.
    /// </summary>
    public UsageReadings At(DateTimeOffset now) =>
        Windows.All(window => Open(window, now))
            ? this
            : this with { Windows = [.. Windows.Where(window => Open(window, now))] };

    private static bool Open(UsageWindow window, DateTimeOffset now) =>
        window.ResetsAt is not { } reset || reset > now;
}
