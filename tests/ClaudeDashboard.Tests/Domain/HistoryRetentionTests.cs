using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// The rule that decides how long the history keeps its rows: Claude Code's <c>cleanupPeriodDays</c>,
/// judged as Claude Code judges it (T1.68, issue #102). Pure: no file and no clock.
/// </summary>
public sealed class HistoryRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A whole number of 1 or more keeps that many days.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(3650)]
    public void A_whole_number_of_one_or_more_keeps_that_many_days(int days)
    {
        Assert.Equal(new RetentionRule(RetentionCause.Setting, days), HistoryRetention.Decide(CleanupPeriodRead.Of(days), Now));
    }

    /// <summary>30.0 is the number 30, as Claude Code reads it.</summary>
    [Fact]
    public void A_whole_number_written_with_a_fraction_part_is_that_number()
    {
        Assert.Equal(new RetentionRule(RetentionCause.Setting, 30), HistoryRetention.Decide(CleanupPeriodRead.Of(30.0m), Now));
    }

    /// <summary>No key in a readable file: Claude Code's default, 30 days.</summary>
    [Fact]
    public void No_key_keeps_thirty_days()
    {
        Assert.Equal(new RetentionRule(RetentionCause.Default, 30), HistoryRetention.Decide(CleanupPeriodRead.Absent, Now));
    }

    /// <summary>A file that could not be read or parsed deletes nothing.</summary>
    [Fact]
    public void A_file_not_read_deletes_nothing()
    {
        Assert.Equal(new RetentionRule(RetentionCause.NotRead, null), HistoryRetention.Decide(CleanupPeriodRead.NotRead, Now));
    }

    /// <summary>Zero, a negative number and a fraction are not valid, and delete nothing.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("0.5")]
    [InlineData("-99999999")]
    public void A_number_that_is_not_a_whole_number_of_one_or_more_deletes_nothing(string value)
    {
        var number = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(new RetentionRule(RetentionCause.NotValid, null), HistoryRetention.Decide(CleanupPeriodRead.Of(number), Now));
    }

    /// <summary>Text, <c>true</c>, <c>null</c>, and a negative number too large to hold are not valid.</summary>
    [Theory]
    [InlineData(CleanupPeriodKind.NotANumber)]
    [InlineData(CleanupPeriodKind.HugeNegativeNumber)]
    public void What_is_not_a_usable_number_deletes_nothing(CleanupPeriodKind kind)
    {
        Assert.Equal(new RetentionRule(RetentionCause.NotValid, null), HistoryRetention.Decide(new CleanupPeriodRead(kind), Now));
    }

    /// <summary>
    /// <strong>The operator's value, 99999</strong>, counts back to 1752: valid, and it keeps everything
    /// in practice. 99999999 cannot be counted back from now, and deletes nothing; nor can a number
    /// larger than a whole number holds, or one too large to read at all.
    /// </summary>
    [Fact]
    public void The_operators_value_is_kept_and_one_too_large_to_count_back_deletes_nothing()
    {
        Assert.Equal(new RetentionRule(RetentionCause.Setting, 99999), HistoryRetention.Decide(CleanupPeriodRead.Of(99999), Now));
        Assert.True(Now - TimeSpan.FromDays(99999) > DateTimeOffset.MinValue);

        Assert.Equal(new RetentionRule(RetentionCause.TooLarge, null), HistoryRetention.Decide(CleanupPeriodRead.Of(99999999), Now));
        Assert.Equal(new RetentionRule(RetentionCause.TooLarge, null), HistoryRetention.Decide(CleanupPeriodRead.Of(10_000_000_000m), Now));
        Assert.Equal(new RetentionRule(RetentionCause.TooLarge, null), HistoryRetention.Decide(new CleanupPeriodRead(CleanupPeriodKind.HugeNumber), Now));
    }

    /// <summary>The limit is read from the instant: the last day that can be counted back is kept as given.</summary>
    [Fact]
    public void The_edge_of_the_calendar_is_judged_against_the_instant()
    {
        var reach = (int)Math.Floor((Now - DateTimeOffset.MinValue).TotalDays);

        Assert.Equal(new RetentionRule(RetentionCause.Setting, reach), HistoryRetention.Decide(CleanupPeriodRead.Of(reach), Now));
        Assert.Equal(new RetentionRule(RetentionCause.TooLarge, null), HistoryRetention.Decide(CleanupPeriodRead.Of(reach + 1), Now));
    }
}
