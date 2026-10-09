using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// Which limit goes in which slot of the window, and its colour (MOD.7, issue #133; rulings R8 and R10).
/// </summary>
/// <remarks>
/// Each threshold is tested on both sides of its line, and on the figure the window shows: a percentage is
/// judged after it is rounded, so the colour agrees with the number beside it.
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

        var slots = UsageGauge.Slots(readings);

        Assert.Equal(
            [(UsageSlot.Current, "five_hour"), (UsageSlot.Fable, "seven_day_fable")],
            slots.Select(entry => (entry.Slot, entry.Window.Kind)));
        Assert.Empty(UsageGauge.Slots(UsageReadings.Empty));
    }

    /// <summary>Two Fable kinds give the first in the order of their names, the order the readings hold.</summary>
    [Fact]
    public void Two_fable_kinds_give_the_first_in_the_order_of_their_names()
    {
        var readings = UsageReadings.Empty
            .With(Reading("seven_day_fable", 60))
            .With(Reading("fable_five_hour", 20));

        var fable = Assert.Single(UsageGauge.Slots(readings));

        Assert.Equal((UsageSlot.Fable, "fable_five_hour"), (fable.Slot, fable.Window.Kind));
    }

    private static UsageWindow Reading(string kind, double percentUsed) =>
        new(kind, percentUsed, Reset, Heard, null);
}
