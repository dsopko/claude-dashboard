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
    /// The figure the window shows: <paramref name="percentUsed"/> rounded to a whole number, half away from
    /// zero, so 49.5 is 50.
    /// </summary>
    public static double FigureOf(double percentUsed) => Math.Round(percentUsed, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The level of <paramref name="percentUsed"/>, judged by its whole-number figure (<see cref="FigureOf"/>):
    /// the colour then agrees with the number beside it.
    /// </summary>
    public static UsageLevel LevelOf(double percentUsed)
    {
        var figure = FigureOf(percentUsed);

        return figure > RedAbove ? UsageLevel.Red
            : figure >= AmberFrom ? UsageLevel.Amber
            : UsageLevel.Green;
    }

    /// <summary>
    /// The reading in each slot, in the slots' order; a slot with no reading is absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Give it <see cref="UsageReadings.At"/>, not the held readings,</strong> so a limit past its reset
    /// time leaves its slot empty: its percentage is no longer true.
    /// </para>
    /// <para>
    /// Two kinds that both contain the Fable text give the first in the order of their names, which is the
    /// order <see cref="UsageReadings.Windows"/> holds them in.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="readings"/> is null.</exception>
    public static IReadOnlyList<(UsageSlot Slot, UsageWindow Window)> Slots(UsageReadings readings)
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

        return [.. bySlot.Select(pair => (pair.Key, pair.Value))];
    }
}
