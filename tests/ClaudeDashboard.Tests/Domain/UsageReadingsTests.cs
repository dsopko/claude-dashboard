using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// What the dashboard believes about the plan's limits: the newest reading of each kind (issue #133, MOD.3).
/// </summary>
/// <remarks>
/// <para>
/// The rule is the table "What the dashboard keeps" of the Usage Mod Development Guide, "The server endpoint". Each
/// row of it is a test here, under the name of the lab kit's check for that row.
/// </para>
/// <para>
/// The readings are those of the guide's real body: <c>five_hour</c> and <c>seven_day</c>, each with a reset time.
/// Every instant is given to the rule as an argument; there is no clock in it.
/// </para>
/// </remarks>
public sealed class UsageReadingsTests
{
    private const string FiveHour = "five_hour";
    private const string SevenDay = "seven_day";
    private const string Session = "ab86443d-84b5-4342-85b4-a13111a93020";

    private static readonly DateTimeOffset Heard = new(2026, 10, 8, 19, 43, 48, TimeSpan.Zero);
    private static readonly DateTimeOffset FiveHourReset = new(2026, 10, 8, 23, 10, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SevenDayReset = new(2026, 10, 14, 13, 0, 0, TimeSpan.Zero);

    // ---- The bounds ------------------------------------------------------------------------------

    /// <summary>
    /// <strong>At most eight kinds are held, and the number itself is pinned.</strong> Claude Code declares three
    /// kinds; eight leaves room for kinds this build does not know, and a sender cannot make the held value grow
    /// without end.
    /// </summary>
    [Fact]
    public void The_most_kinds_held_is_eight() =>
        Assert.Equal(8, UsageReadings.MaxKinds);

    /// <summary>
    /// <strong>The longest kind kept is 64 characters, and the number itself is pinned</strong>: a kind of 64 is
    /// held, and one of 65 is not.
    /// </summary>
    [Fact]
    public void The_longest_kind_kept_is_sixty_four_characters()
    {
        Assert.Equal(64, UsageReadings.MaxKindLength);

        var longest = new string('k', 64);
        var readings = UsageReadings.Empty.With(Reading(longest, 10, FiveHourReset));

        Assert.Equal(longest, Assert.Single(readings.Windows).Kind);
        Assert.Same(readings, readings.With(Reading(new string('k', 65), 10, FiveHourReset)));
    }

    // ---- The rule, row by row --------------------------------------------------------------------

    /// <summary>A reading of a kind replaces the held reading of that kind, with its time and its session.</summary>
    [Fact]
    public void The_newest_reading_of_a_kind_replaces_the_one_held()
    {
        var held = UsageReadings.Empty.With(Reading(FiveHour, 24, FiveHourReset, Heard, "s-old"));
        var newer = Reading(FiveHour, 47, FiveHourReset, Heard.AddMinutes(30), Session);

        var readings = held.With(newer);

        Assert.Same(newer, Assert.Single(readings.Windows));
    }

    /// <summary>
    /// <strong>Also when it shows less:</strong> a limit that was raised lowers the percentage inside one window.
    /// </summary>
    [Fact]
    public void A_newer_reading_that_shows_less_still_replaces_the_one_held()
    {
        var held = UsageReadings.Empty.With(Reading(FiveHour, 47, FiveHourReset));
        var lower = Reading(FiveHour, 12, FiveHourReset, Heard.AddMinutes(5));

        var readings = held.With(lower);

        Assert.Equal(12, Assert.Single(readings.Windows).PercentUsed);
    }

    /// <summary>
    /// <strong>A reading of an older window that arrives late changes nothing.</strong> A window is known by its reset
    /// time, and this one's is earlier than the held one's.
    /// </summary>
    [Fact]
    public void A_reading_of_an_older_window_changes_nothing()
    {
        var held = UsageReadings.Empty.With(Reading(FiveHour, 3, FiveHourReset));
        var late = Reading(FiveHour, 96, FiveHourReset.AddHours(-5), Heard.AddMinutes(1));

        Assert.Same(held, held.With(late));
        Assert.Equal(3, Assert.Single(held.Windows).PercentUsed);
    }

    /// <summary>A reading of the next window replaces the held one, though it shows less: the window was reset.</summary>
    [Fact]
    public void A_reading_of_a_newer_window_replaces_the_one_held()
    {
        var held = UsageReadings.Empty.With(Reading(FiveHour, 96, FiveHourReset));
        var next = Reading(FiveHour, 2, FiveHourReset.AddHours(5), Heard.AddHours(4));

        var readings = held.With(next);

        Assert.Same(next, Assert.Single(readings.Windows));
    }

    /// <summary>
    /// <strong>A newer reading with no reset time replaces one that had a reset time</strong>, and stays when the held
    /// one's reset time passes.
    /// </summary>
    /// <remarks>
    /// The director's ruling of 2026-10-08 on a case the guide is silent on: the newest reading of a kind replaces the
    /// one held, as the guide's first row says, because the older-window row needs an earlier reset time, and here
    /// there is none to compare. The newest reading is the truth Claude Code last gave, and refusing it for a missing
    /// reset time would throw away real data to guard against a rarer case. That rarer case is then accepted: a late
    /// reading of an older window replaces the held one, because nothing is left to compare it with.
    /// </remarks>
    [Fact]
    public void A_newer_reading_with_no_reset_time_replaces_one_that_had_a_reset_time()
    {
        var held = UsageReadings.Empty.With(Reading(FiveHour, 40, FiveHourReset));
        var newer = Reading(FiveHour, 42, resetsAt: null, Heard.AddMinutes(5));

        var readings = held.With(newer);

        Assert.Same(newer, Assert.Single(readings.Windows));
        Assert.Same(newer, Assert.Single(readings.At(FiveHourReset.AddHours(1)).Windows));

        var late = Reading(FiveHour, 96, FiveHourReset.AddHours(-5), Heard.AddMinutes(6));

        Assert.Same(late, Assert.Single(readings.With(late).Windows));
    }

    /// <summary>A post that carries one kind leaves the held reading of each other kind as it was.</summary>
    [Fact]
    public void A_kind_that_a_post_does_not_carry_is_left_alone()
    {
        var sevenDay = Reading(SevenDay, 13, SevenDayReset);
        var held = UsageReadings.Empty.With(Reading(FiveHour, 24, FiveHourReset)).With(sevenDay);

        var readings = held.Heard(Heard.AddMinutes(10)).With(Reading(FiveHour, 25, FiveHourReset, Heard.AddMinutes(10)));

        Assert.Equal([FiveHour, SevenDay], readings.Windows.Select(window => window.Kind));
        Assert.Same(sevenDay, readings.Windows[1]);
    }

    /// <summary>
    /// <strong>A limit whose reset time has passed is left out when the state is read</strong>: its percentage is no
    /// longer true. At the reset time itself it is out; a moment before, it is in. The other limits stay, and so does
    /// the time of the last post.
    /// </summary>
    [Fact]
    public void A_limit_whose_reset_time_has_passed_is_left_out()
    {
        var readings = UsageReadings.Empty
            .Heard(Heard)
            .With(Reading(FiveHour, 24, FiveHourReset))
            .With(Reading(SevenDay, 13, SevenDayReset));

        Assert.Equal([FiveHour, SevenDay], readings.At(FiveHourReset.AddTicks(-1)).Windows.Select(window => window.Kind));

        var atReset = readings.At(FiveHourReset);

        Assert.Equal(SevenDay, Assert.Single(atReset.Windows).Kind);
        Assert.Equal(SevenDay, Assert.Single(readings.At(FiveHourReset.AddHours(1)).Windows).Kind);
        Assert.Equal(Heard, atReset.LastHeardAt);
    }

    /// <summary>A reading with no reset time stays, however late the state is read, until a newer one replaces it.</summary>
    [Fact]
    public void A_limit_with_no_reset_time_stays()
    {
        var readings = UsageReadings.Empty.With(Reading(FiveHour, 24, resetsAt: null));

        Assert.Equal(FiveHour, Assert.Single(readings.At(Heard.AddYears(1)).Windows).Kind);

        var replaced = readings.With(Reading(FiveHour, 30, resetsAt: null, Heard.AddMinutes(1)));

        Assert.Equal(30, Assert.Single(replaced.Windows).PercentUsed);
    }

    /// <summary>
    /// <strong>A kind this build does not know is kept, as text</strong>, and compared with nothing. A gateway's spend
    /// limit can pass 100.
    /// </summary>
    [Fact]
    public void A_kind_this_build_does_not_know_is_kept()
    {
        var readings = UsageReadings.Empty.With(Reading("weekly_<b>opus</b>", 112.5, SevenDayReset));

        var window = Assert.Single(readings.Windows);
        Assert.Equal("weekly_<b>opus</b>", window.Kind);
        Assert.Equal(112.5, window.PercentUsed);
    }

    /// <summary>
    /// <strong>With eight kinds held, a ninth is not held</strong>, and a reading of a held kind still replaces the one
    /// held.
    /// </summary>
    [Fact]
    public void A_ninth_kind_is_not_held_and_a_held_kind_still_moves()
    {
        var full = Enumerable.Range(1, UsageReadings.MaxKinds)
            .Aggregate(UsageReadings.Empty, (readings, n) => readings.With(Reading($"kind_{n}", n, FiveHourReset)));

        Assert.Equal(8, full.Windows.Count);
        Assert.Same(full, full.With(Reading("kind_9", 9, FiveHourReset)));

        var moved = full.With(Reading("kind_3", 33, FiveHourReset, Heard.AddMinutes(1)));

        Assert.Equal(8, moved.Windows.Count);
        Assert.Equal(33, moved.Windows.Single(window => window.Kind == "kind_3").PercentUsed);
    }

    /// <summary>
    /// <strong>A reading that cannot be true changes nothing</strong>: no kind, a kind of more than 64 characters, or a
    /// percentage that is negative or not a finite number. The held reading of that kind stays.
    /// </summary>
    [Fact]
    public void A_reading_that_cannot_be_true_changes_nothing()
    {
        var held = UsageReadings.Empty.With(Reading(FiveHour, 24, FiveHourReset));

        UsageWindow[] untrue =
        [
            Reading(string.Empty, 10, FiveHourReset),
            Reading(new string('k', UsageReadings.MaxKindLength + 1), 10, FiveHourReset),
            Reading(FiveHour, -1, FiveHourReset),
            Reading(FiveHour, double.NaN, FiveHourReset),
            Reading(FiveHour, double.PositiveInfinity, FiveHourReset),
            Reading(FiveHour, double.NegativeInfinity, FiveHourReset),
        ];

        Assert.All(untrue, reading => Assert.Same(held, held.With(reading)));
    }

    /// <summary>The readings are in the ordinal order of their kinds, whatever order they arrived in.</summary>
    [Fact]
    public void The_readings_are_in_the_order_of_their_kinds()
    {
        var readings = new[] { SevenDay, "spend_limit", FiveHour, "Z_gateway" }
            .Aggregate(UsageReadings.Empty, (held, kind) => held.With(Reading(kind, 1, SevenDayReset)));

        Assert.Equal(["Z_gateway", FiveHour, SevenDay, "spend_limit"], readings.Windows.Select(window => window.Kind));
    }

    /// <summary>
    /// <strong>Immutable</strong>: <see cref="UsageReadings.Heard"/>, <see cref="UsageReadings.With"/> and
    /// <see cref="UsageReadings.At"/> answer a new value, and a value read before is as it was. The holder swaps one
    /// reference, so a reader never sees half a change.
    /// </summary>
    [Fact]
    public void Applying_a_reading_changes_no_value_that_was_read_before()
    {
        var before = UsageReadings.Empty.Heard(Heard).With(Reading(FiveHour, 24, FiveHourReset));
        var windows = before.Windows;
        var seen = windows.ToArray();

        _ = before.Heard(Heard.AddMinutes(1)).With(Reading(FiveHour, 47, FiveHourReset)).With(Reading(SevenDay, 13, SevenDayReset));
        _ = before.At(FiveHourReset.AddHours(1));

        Assert.Equal(Heard, before.LastHeardAt);
        Assert.Same(windows, before.Windows);
        Assert.Equal(seen, before.Windows);
        Assert.Null(UsageReadings.Empty.LastHeardAt);
        Assert.Empty(UsageReadings.Empty.Windows);
    }

    private static UsageWindow Reading(
        string kind,
        double percentUsed,
        DateTimeOffset? resetsAt,
        DateTimeOffset? heardAt = null,
        string? sessionId = Session) =>
        new(kind, percentUsed, resetsAt, heardAt ?? Heard, sessionId);
}
