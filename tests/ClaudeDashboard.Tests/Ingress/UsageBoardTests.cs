using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Ingress;

/// <summary>
/// The holder of the plan's limits, written on request threads and read at a request (issue #133, MOD.4).
/// </summary>
/// <remarks>
/// The rule is Core's <see cref="UsageReadings"/>, tested on its own. These tests hold what the board adds: that a
/// post is heard with or without a reading, that the rule is applied to each reading of a post, that writers on many
/// threads lose nothing, and the <c>usage</c> object it reports at an instant.
/// </remarks>
public sealed class UsageBoardTests
{
    private static readonly DateTimeOffset Heard = new(2026, 10, 8, 19, 43, 48, TimeSpan.Zero);
    private static readonly DateTimeOffset FiveHourReset = new(2026, 10, 8, 23, 10, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SevenDayReset = new(2026, 10, 14, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Before_the_first_post_nothing_is_held_and_the_report_is_null()
    {
        var board = new UsageBoard();

        Assert.Same(UsageReadings.Empty, board.Current);
        Assert.Null(board.Report(Heard));
    }

    /// <summary>A post with no reading is heard: its instant is kept, and no reading is made.</summary>
    [Fact]
    public void A_post_with_no_reading_is_heard()
    {
        var board = new UsageBoard();

        board.Heard([], Heard);

        Assert.Equal(Heard, board.Current.LastHeardAt);
        Assert.Empty(board.Current.Windows);
        Assert.Empty(board.Report(Heard)!.Windows);
    }

    /// <summary>
    /// Each reading of a post goes through the rule: a reading of an older window, a second one of a kind in the same
    /// post, and a reading that cannot be true are handled as <see cref="UsageReadings.With"/> handles them.
    /// </summary>
    [Fact]
    public void Each_reading_of_a_post_is_applied_by_the_rule()
    {
        var board = new UsageBoard();
        board.Heard([Reading("five_hour", 24, FiveHourReset)], Heard);

        board.Heard(
            [
                Reading("five_hour", 90, FiveHourReset.AddHours(-5)),
                Reading("seven_day", 13, SevenDayReset),
                Reading("seven_day", 14, SevenDayReset),
                Reading("seven_day", double.NaN, SevenDayReset),
            ],
            Heard.AddMinutes(1));

        Assert.Equal(Heard.AddMinutes(1), board.Current.LastHeardAt);
        Assert.Equal([("five_hour", 24.0), ("seven_day", 14.0)], board.Current.Windows.Select(window => (window.Kind, window.PercentUsed)));
    }

    /// <summary>
    /// <strong>The <c>usage</c> object:</strong> instants in UTC, the readings in the order of their kinds, and a limit
    /// whose reset time has passed left out, while the board still holds it.
    /// </summary>
    [Fact]
    public void The_report_gives_instants_in_utc_and_leaves_out_a_limit_past_its_reset_time()
    {
        var board = new UsageBoard();
        var eastern = TimeSpan.FromHours(-4);
        board.Heard(
            [
                Reading("seven_day", 13, SevenDayReset.ToOffset(eastern), Heard.ToOffset(eastern)),
                Reading("five_hour", 24, FiveHourReset.ToOffset(eastern), Heard.ToOffset(eastern)),
            ],
            Heard.ToOffset(eastern));

        var report = board.Report(FiveHourReset.AddMinutes(-1))!;

        Assert.Equal(Heard.UtcDateTime, report.LastHeardAt);
        Assert.Equal(DateTimeKind.Utc, report.LastHeardAt.Kind);
        Assert.Equal(["five_hour", "seven_day"], report.Windows.Select(window => window.Kind));
        Assert.All(report.Windows, window =>
        {
            Assert.Equal(DateTimeKind.Utc, window.ResetsAt!.Value.Kind);
            Assert.Equal(DateTimeKind.Utc, window.HeardAt.Kind);
        });
        Assert.Equal(FiveHourReset.UtcDateTime, report.Windows[0].ResetsAt);

        var later = board.Report(FiveHourReset)!;

        Assert.Equal("seven_day", Assert.Single(later.Windows).Kind);
        Assert.Equal(2, board.Current.Windows.Count);
    }

    /// <summary>
    /// <strong>Writers on many threads lose nothing</strong>: eight kinds posted at once from eight threads, fifty
    /// times each, leave all eight held, each at its last reading.
    /// </summary>
    [Fact]
    public async Task Writers_on_many_threads_lose_no_reading()
    {
        var board = new UsageBoard();

        await Task.WhenAll(Enumerable.Range(1, UsageReadings.MaxKinds).Select(n => Task.Run(() =>
        {
            for (var i = 1; i <= 50; i++)
            {
                board.Heard([Reading($"kind_{n}", i, FiveHourReset)], Heard);
            }
        })));

        Assert.Equal(UsageReadings.MaxKinds, board.Current.Windows.Count);
        Assert.All(board.Current.Windows, window => Assert.Equal(50, window.PercentUsed));
    }

    private static UsageWindow Reading(string kind, double percentUsed, DateTimeOffset resetsAt, DateTimeOffset? heardAt = null) =>
        new(kind, percentUsed, resetsAt, heardAt ?? Heard, "ab86443d-84b5-4342-85b4-a13111a93020");
}
