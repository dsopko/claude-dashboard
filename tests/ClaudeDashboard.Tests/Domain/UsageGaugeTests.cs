using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// Which limit goes in which slot of the window, its colour, the share remaining and the fresh slot (MOD.7, issue
/// #133; MOD.8, issues #144 and #145; rulings R8, R10, R13 and R14).
/// </summary>
/// <remarks>
/// Each threshold is tested on both sides of its line, on the rounded used figure: a percentage is judged after it
/// is rounded, so the colour changes where the figure shown changes.
/// </remarks>
public sealed class UsageGaugeTests
{
    private static readonly DateTimeOffset Heard = new(2026, 10, 8, 19, 43, 48, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = new(2026, 10, 14, 13, 0, 0, TimeSpan.Zero);

    // ---- The levels (R10) ------------------------------------------------------------------------

    /// <summary>The two lines are pinned at their values: amber from 50, red above 90.</summary>
    [Fact]
    public void The_lines_are_fifty_and_ninety() =>
        Assert.Equal((50, 90), (UsageGauge.AmberFrom, UsageGauge.RedAbove));

    /// <summary>Every figure below 50 is green, 0 and 49 included.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(49)]
    [InlineData(49.4)]
    public void Below_fifty_is_green(double percentUsed) =>
        Assert.Equal(UsageLevel.Green, UsageGauge.LevelOf(percentUsed));

    /// <summary>50 and 90 are both amber, and so is every figure between them.</summary>
    [Theory]
    [InlineData(50)]
    [InlineData(70)]
    [InlineData(90)]
    [InlineData(90.4)]
    public void Fifty_and_ninety_are_amber(double percentUsed) =>
        Assert.Equal(UsageLevel.Amber, UsageGauge.LevelOf(percentUsed));

    /// <summary>Every figure above 90 is red, above 100 included (a limit that is exceeded).</summary>
    [Theory]
    [InlineData(91)]
    [InlineData(90.5)]
    [InlineData(100)]
    [InlineData(250)]
    public void Above_ninety_is_red(double percentUsed) =>
        Assert.Equal(UsageLevel.Red, UsageGauge.LevelOf(percentUsed));

    /// <summary>
    /// <strong>A half rounds away from zero,</strong> so 49.5 shows 50 and is amber, and 90.5 shows 91 and is
    /// red. The default rounding of .NET (to even) would show 50 for 49.5 but 90 for 90.5.
    /// </summary>
    [Fact]
    public void Half_rounds_away_from_zero_so_forty_nine_and_a_half_is_amber()
    {
        Assert.Equal(50, UsageGauge.FigureOf(49.5));
        Assert.Equal(UsageLevel.Amber, UsageGauge.LevelOf(49.5));
        Assert.Equal(91, UsageGauge.FigureOf(90.5));
        Assert.Equal(UsageLevel.Red, UsageGauge.LevelOf(90.5));
        Assert.Equal(5, UsageGauge.FigureOf(4.5));
        Assert.Equal(36, UsageGauge.FigureOf(36.4));
    }

    // ---- The slots (R8) --------------------------------------------------------------------------

    /// <summary><c>five_hour</c> is the Current slot.</summary>
    [Fact]
    public void Five_hour_is_the_current_slot() =>
        Assert.Equal(UsageSlot.Current, UsageGauge.SlotOf("five_hour"));

    /// <summary><c>seven_day</c> is the Week slot.</summary>
    [Fact]
    public void Seven_day_is_the_week_slot() =>
        Assert.Equal(UsageSlot.Week, UsageGauge.SlotOf("seven_day"));

    /// <summary>
    /// A kind that contains <c>fable</c>, in any case, is the Fable slot. The names are made up: Claude Code's
    /// name for this kind is not known.
    /// </summary>
    [Theory]
    [InlineData("seven_day_fable")]
    [InlineData("SEVEN_DAY_FABLE")]
    [InlineData("Fable")]
    [InlineData("five_hour_fable")]
    public void A_kind_whose_name_contains_fable_is_the_fable_slot(string kind) =>
        Assert.Equal(UsageSlot.Fable, UsageGauge.SlotOf(kind));

    /// <summary>A kind that is none of the three has no slot, and the window does not show it.</summary>
    [Theory]
    [InlineData("spend_limit")]
    [InlineData("seven_day_opus")]
    [InlineData("FIVE_HOUR")]
    [InlineData("five_hour ")]
    [InlineData("fabl")]
    public void Any_other_kind_has_no_slot(string kind) =>
        Assert.Null(UsageGauge.SlotOf(kind));

    /// <summary>
    /// The readings in their slots: in the slots' order, whatever the order of the kinds' names; a kind with no
    /// slot is left out; a slot with no reading is absent.
    /// </summary>
    [Fact]
    public void The_slots_are_in_their_order_and_an_empty_one_is_absent()
    {
        var readings = UsageReadings.Empty
            .With(Reading("spend_limit", 80))
            .With(Reading("seven_day_fable", 60))
            .With(Reading("five_hour", 5));

        var slots = UsageGauge.Slots(readings, Heard);

        Assert.Equal(
            [(UsageSlot.Current, "five_hour"), (UsageSlot.Fable, "seven_day_fable")],
            slots.Select(entry => (entry.Slot, entry.Window.Kind)));
        Assert.Empty(UsageGauge.Slots(UsageReadings.Empty, Heard));
    }

    /// <summary>Two Fable kinds give the first in the order of their names, the order the readings hold.</summary>
    [Fact]
    public void Two_fable_kinds_give_the_first_in_the_order_of_their_names()
    {
        var readings = UsageReadings.Empty
            .With(Reading("seven_day_fable", 60))
            .With(Reading("fable_five_hour", 20));

        var fable = Assert.Single(UsageGauge.Slots(readings, Heard));

        Assert.Equal((UsageSlot.Fable, "fable_five_hour"), (fable.Slot, fable.Window.Kind));
    }

    // ---- The share remaining (MOD.8, R13) --------------------------------------------------------

    /// <summary>
    /// <strong>The figure is the share remaining:</strong> 100 less the used figure, so 40.4 used shows 60 and 5
    /// used shows 95. The colour is still judged by the share used.
    /// </summary>
    [Theory]
    [InlineData(40.4, 60, UsageLevel.Green)]
    [InlineData(5, 95, UsageLevel.Green)]
    [InlineData(60, 40, UsageLevel.Amber)]
    [InlineData(0, 100, UsageLevel.Green)]
    [InlineData(100, 0, UsageLevel.Red)]
    public void The_figure_is_the_share_remaining(double percentUsed, double remaining, UsageLevel level)
    {
        Assert.Equal(remaining, UsageGauge.RemainingOf(percentUsed));
        Assert.Equal(level, UsageGauge.LevelOf(percentUsed));

        var slot = Assert.Single(UsageGauge.Slots(UsageReadings.Empty.With(Reading("seven_day", percentUsed)), Heard));

        Assert.Equal((remaining, level, false), (slot.Remaining, slot.Level, slot.IsFresh));
    }

    /// <summary>
    /// <strong>The used share is rounded first, half away from zero, then taken from 100:</strong> 49.5 used is 50
    /// used, so 50 remaining, amber; 50.5 used is 51, so 49 remaining. Taken from 100 first, 50.5 remaining would
    /// round to 51.
    /// </summary>
    [Fact]
    public void Half_rounds_away_from_zero_then_subtracts()
    {
        Assert.Equal(50, UsageGauge.RemainingOf(49.5));
        Assert.Equal(UsageLevel.Amber, UsageGauge.LevelOf(49.5));
        Assert.Equal(49, UsageGauge.RemainingOf(50.5));
        Assert.Equal(9, UsageGauge.RemainingOf(90.5));
        Assert.Equal(UsageLevel.Red, UsageGauge.LevelOf(90.5));
    }

    /// <summary>
    /// <strong>A used share above 100 shows 0 remaining, in red</strong>: a negative share is not a share. A gateway's
    /// spend limit can pass 100.
    /// </summary>
    [Theory]
    [InlineData(100.4)]
    [InlineData(150)]
    [InlineData(10_000)]
    public void Above_one_hundred_used_shows_zero_remaining_red(double percentUsed)
    {
        Assert.Equal(0, UsageGauge.RemainingOf(percentUsed));
        Assert.Equal(UsageLevel.Red, UsageGauge.LevelOf(percentUsed));
    }

    // ---- Fresh at the reset (MOD.8, R14) ---------------------------------------------------------

    /// <summary>
    /// <strong>A reading past its reset time is a fresh slot, 100% remaining, green</strong> (R14), at the reset
    /// time itself and after it, whatever it showed; one second before, it is live. <c>UsageReadings.At</c> would
    /// leave it out: the slot keeps it.
    /// </summary>
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(86_400, true)]
    public void A_reading_past_its_reset_time_is_a_fresh_slot_at_one_hundred_green(int secondsAfterReset, bool fresh)
    {
        var readings = UsageReadings.Empty.With(Reading("five_hour", 97));
        var now = Reset.AddSeconds(secondsAfterReset);

        var slot = Assert.Single(UsageGauge.Slots(readings, now));

        Assert.Equal(fresh, slot.IsFresh);
        Assert.Equal(fresh, UsageGauge.IsFresh(slot.Window, now));
        Assert.Equal(fresh ? (100, UsageLevel.Green) : (3, UsageLevel.Red), (slot.Remaining, slot.Level));
        Assert.Equal(97, slot.Window.PercentUsed);
    }

    /// <summary><strong>A reading with no reset time is live</strong> at any instant, as it was before MOD.8.</summary>
    [Fact]
    public void A_reading_with_no_reset_time_is_live()
    {
        var readings = UsageReadings.Empty.With(new UsageWindow("seven_day", 70, null, Heard, null));

        var slot = Assert.Single(UsageGauge.Slots(readings, DateTimeOffset.MaxValue));

        Assert.False(slot.IsFresh);
        Assert.Equal((30, UsageLevel.Amber), (slot.Remaining, slot.Level));
    }

    /// <summary>
    /// <strong>A kind never reported has no slot</strong>, at any instant: fresh is for a slot that had a reading,
    /// not for one that never did.
    /// </summary>
    [Fact]
    public void A_kind_never_reported_has_no_slot()
    {
        var readings = UsageReadings.Empty.With(Reading("five_hour", 5));

        foreach (var now in new[] { Heard, Reset, Reset.AddDays(30) })
        {
            Assert.Equal([UsageSlot.Current], UsageGauge.Slots(readings, now).Select(slot => slot.Slot));
        }
    }

    private static UsageWindow Reading(string kind, double percentUsed) =>
        new(kind, percentUsed, Reset, Heard, null);
}
