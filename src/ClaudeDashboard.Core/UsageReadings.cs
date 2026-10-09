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
/// <strong>Information, never an alarm.</strong> Nothing here has a threshold. The lines between
/// green, amber and red are in <see cref="UsageGauge"/> (ruling R10). A limit that is almost used
/// plays no sound and shows no notice.
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
    /// <para>
    /// <strong>The rule is <see cref="RefusalOf"/></strong>: this changes nothing exactly when it answers a reason,
    /// so what the log file says about a reading and what was done with it cannot disagree (MOD.8, ruling R15).
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is null.</exception>
    public UsageReadings With(UsageWindow reading)
    {
        if (RefusalOf(reading) is not null)
        {
            return this;
        }

        var held = HeldOf(reading.Kind);

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
    /// Why <see cref="With"/> would change nothing for <paramref name="reading"/>, or null when it would keep it
    /// (MOD.8, ruling R15). The checks are made in this order, so a reading with no kind is never judged by its
    /// percentage.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is null.</exception>
    public UsageRefusal? RefusalOf(UsageWindow reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        if (string.IsNullOrEmpty(reading.Kind))
        {
            return UsageRefusal.NoKind;
        }

        if (reading.Kind.Length > MaxKindLength)
        {
            return UsageRefusal.KindTooLong;
        }

        if (!double.IsFinite(reading.PercentUsed) || reading.PercentUsed < 0)
        {
            return UsageRefusal.NotAPercentage;
        }

        var held = HeldOf(reading.Kind);

        if (held is { ResetsAt: { } heldReset } && reading.ResetsAt is { } readReset && readReset < heldReset)
        {
            return UsageRefusal.OlderWindow;
        }

        if (held is null && Windows.Count >= MaxKinds)
        {
            return UsageRefusal.TooManyKinds;
        }

        return null;
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

    private UsageWindow? HeldOf(string kind) =>
        Windows.FirstOrDefault(window => string.Equals(window.Kind, kind, StringComparison.Ordinal));
}

/// <summary>Why <see cref="UsageReadings.With"/> changed nothing for a reading (MOD.8, ruling R15).</summary>
public enum UsageRefusal
{
    /// <summary>The reading has no kind.</summary>
    NoKind = 1,

    /// <summary>The kind is longer than <see cref="UsageReadings.MaxKindLength"/>.</summary>
    KindTooLong,

    /// <summary>The percentage is negative or not a number.</summary>
    NotAPercentage,

    /// <summary>The reading's reset time is earlier than the held reading's: it was made in a window that ended.</summary>
    OlderWindow,

    /// <summary>A new kind, while <see cref="UsageReadings.MaxKinds"/> are held.</summary>
    TooManyKinds,
}

/// <summary>The words for a <see cref="UsageRefusal"/>, so every interface and the log file say the same.</summary>
public static class UsageRefusals
{
    /// <summary>
    /// The reason as a clause that follows the reading it is about: "five_hour 2% with reset …, which is older than
    /// the window held".
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="refusal"/> is not a defined value.</exception>
    public static string Clause(UsageRefusal refusal) => refusal switch
    {
        UsageRefusal.NoKind => "which has no kind",
        UsageRefusal.KindTooLong => $"whose kind is longer than {UsageReadings.MaxKindLength} characters",
        UsageRefusal.NotAPercentage => "whose percentage is negative or not a number",
        UsageRefusal.OlderWindow => "which is older than the window held",
        UsageRefusal.TooManyKinds => $"a new kind while {UsageReadings.MaxKinds} are held",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null),
    };

    /// <summary>
    /// Whether a reading refused for <paramref name="refusal"/> may be named by its kind: false when the kind is
    /// absent or too long, so a line never holds a kind the readings would not hold.
    /// </summary>
    public static bool NamesItsKind(UsageRefusal? refusal) =>
        refusal is not (UsageRefusal.NoKind or UsageRefusal.KindTooLong);
}
