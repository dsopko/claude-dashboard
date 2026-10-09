namespace ClaudeDashboard.Core;

/// <summary>The three places where the window shows a plan limit, in their order (MOD.7, ruling R8).</summary>
public enum UsageSlot
{
    /// <summary>The five-hour limit, <c>five_hour</c>.</summary>
    Current,

    /// <summary>The weekly limit, <c>seven_day</c>.</summary>
    Week,

    /// <summary>A limit whose kind contains <c>fable</c>, in any case.</summary>
    Fable,
}

/// <summary>How close a limit is to used, by its whole-number figure (MOD.7, ruling R10).</summary>
public enum UsageLevel
{
    /// <summary>Below <see cref="UsageGauge.AmberFrom"/>.</summary>
    Green,

    /// <summary>From <see cref="UsageGauge.AmberFrom"/> to <see cref="UsageGauge.RedAbove"/>, both included.</summary>
    Amber,

    /// <summary>Above <see cref="UsageGauge.RedAbove"/>.</summary>
    Red,
}

/// <summary>
/// Which limit goes in which slot, and how close each one is to used: the question that every interface asks
/// of <see cref="UsageReadings"/> before it shows them (MOD.7, issue #133).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The rule is here and the words are not.</strong> The window says "Current", "Week" and "Fable", and
/// it paints green, amber and red; a second interface may use other words and other colours. What it must not
/// do is put the line between amber and red somewhere else, or show a kind that the window does not show. So
/// the slots and the thresholds are in Core, and the words, the brushes and the layout are in App.
/// </para>
/// <para>
/// <strong>Information, and still no alarm.</strong> A level changes a colour. It plays no sound, shows no
/// notice and changes no state.
/// </para>
/// <para>
/// <strong>The figure shown is the share remaining; the colour is judged by the share used</strong> (MOD.8, ruling
/// R13). So "40% remaining" is amber, as "60% used" was, and nothing that was green turns amber.
/// </para>
/// <para>
/// <strong>A slot is live or fresh</strong> (ruling R14). Its newest reading is live until its reset time; at and
/// after it, the slot is fresh, 100% remaining and green, until a newer reading replaces it. A kind never reported
/// has no slot. <see cref="UsageReadings.At"/>, which leaves a passed limit out, is <c>/state</c>'s view and not this
/// one.
/// </para>
/// </remarks>
public static class UsageGauge
{
    /// <summary>The lowest whole-number figure that is amber: 50.</summary>
    public const int AmberFrom = 50;

    /// <summary>The highest whole-number figure that is still amber: 90. Above it is red.</summary>
    public const int RedAbove = 90;

    /// <summary>The kind of the five-hour limit, as Claude Code names it.</summary>
    public const string FiveHourKind = "five_hour";

    /// <summary>The kind of the weekly limit, as Claude Code names it.</summary>
    public const string SevenDayKind = "seven_day";

    /// <summary>The text that puts a kind in the Fable slot, compared without case.</summary>
    /// <remarks>
    /// Claude Code's name for this kind is not known on 2026-10-09. When it sends a kind that contains this
    /// text, the kind appears; nothing else is built for it.
    /// </remarks>
    public const string FableText = "fable";

    /// <summary>
    /// The slot of <paramref name="kind"/>, or null when the window does not show it: a gateway's
    /// <c>spend_limit</c>, or a name this build has not seen.
    /// </summary>
    /// <remarks>
    /// The two known kinds are compared exactly, and before the Fable text, so a kind such as
    /// <c>five_hour_fable</c> is a Fable kind and <c>five_hour</c> is not.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="kind"/> is null.</exception>
    public static UsageSlot? SlotOf(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);

        if (string.Equals(kind, FiveHourKind, StringComparison.Ordinal))
        {
            return UsageSlot.Current;
        }

        if (string.Equals(kind, SevenDayKind, StringComparison.Ordinal))
        {
            return UsageSlot.Week;
        }

        return kind.Contains(FableText, StringComparison.OrdinalIgnoreCase) ? UsageSlot.Fable : null;
    }

    /// <summary>
    /// The used figure, which the colour is judged by: <paramref name="percentUsed"/> rounded to a whole number, half away from
    /// zero, so 49.5 is 50.
    /// </summary>
    public static double FigureOf(double percentUsed) => Math.Round(percentUsed, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The share remaining the window shows (ruling R13): <c>100</c> less the used figure rounded as
    /// <see cref="FigureOf"/> rounds it, so 40.4 used is 60 and 49.5 used is 50. A used figure above 100 is 0: a
    /// negative share is not a share.
    /// </summary>
    public static double RemainingOf(double percentUsed) => Math.Clamp(100 - FigureOf(percentUsed), 0, 100);

    /// <summary>
    /// Whether <paramref name="window"/> is fresh at <paramref name="now"/>: its reset time is at or before
    /// <paramref name="now"/> (ruling R14). A reading with no reset time is never fresh.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
    public static bool IsFresh(UsageWindow window, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(window);

        return window.ResetsAt is { } reset && reset <= now;
    }

    /// <summary>
    /// The level of <paramref name="percentUsed"/>, judged by its whole-number figure (<see cref="FigureOf"/>):
    /// the colour then changes where the figure shown changes: 50% remaining is amber, 51% green, 10% amber, 9% red.
    /// </summary>
    public static UsageLevel LevelOf(double percentUsed)
    {
        var figure = FigureOf(percentUsed);

        return figure > RedAbove ? UsageLevel.Red
            : figure >= AmberFrom ? UsageLevel.Amber
            : UsageLevel.Green;
    }

    /// <summary>
    /// The reading in each slot at <paramref name="now"/>, in the slots' order, each live or fresh; a slot whose kind
    /// was never reported is absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Give it the held readings, not <see cref="UsageReadings.At"/>:</strong> a limit past its reset time is
    /// a fresh slot here (ruling R14), and <c>At</c> would leave it out.
    /// </para>
    /// <para>
    /// Two kinds that both contain the Fable text give the first in the order of their names, which is the
    /// order <see cref="UsageReadings.Windows"/> holds them in.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="readings"/> is null.</exception>
    public static IReadOnlyList<UsageSlotReading> Slots(UsageReadings readings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var bySlot = new SortedDictionary<UsageSlot, UsageWindow>();

        foreach (var window in readings.Windows)
        {
            if (SlotOf(window.Kind) is { } slot)
            {
                bySlot.TryAdd(slot, window);
            }
        }

        return [.. bySlot.Select(pair => new UsageSlotReading(pair.Key, pair.Value, IsFresh(pair.Value, now)))];
    }
}

/// <summary>
/// What one slot shows at an instant (MOD.8, rulings R13 and R14): its newest reading, and whether its reset time has
/// passed.
/// </summary>
/// <param name="Slot">The slot.</param>
/// <param name="Window">The slot's newest reading.</param>
/// <param name="IsFresh">Whether the reading's reset time has passed: the slot then shows 100% remaining, green.</param>
public sealed record UsageSlotReading(UsageSlot Slot, UsageWindow Window, bool IsFresh)
{
    /// <summary>The share remaining shown: 100 when fresh, else <see cref="UsageGauge.RemainingOf"/>.</summary>
    public double Remaining => IsFresh ? 100 : UsageGauge.RemainingOf(Window.PercentUsed);

    /// <summary>The colour: green when fresh, else <see cref="UsageGauge.LevelOf"/> of the share used.</summary>
    public UsageLevel Level => IsFresh ? UsageLevel.Green : UsageGauge.LevelOf(Window.PercentUsed);
}
