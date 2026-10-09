using System.Globalization;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Pipeline;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The plan's usage in the view model: what the three pairs of the usage strip say, and when (MOD.7, issue
/// #133; MOD.8, issues #144 and #145; rulings R8, R10 to R14).
/// </summary>
/// <remarks>
/// <para>
/// The readings go on a real <see cref="UsageBoard"/>, as <c>/usage</c> puts them there, and the view model reads
/// it at a tick, as the UI tick drives it. Nothing tells the view model that a post arrived.
/// </para>
/// <para>
/// <strong>The hover text is in the machine's own zone and culture,</strong> as the operator sees it. So each
/// expected text is built with the same local conversion and the same short time pattern, and each instant is
/// chosen in local time, so "today" is today in every zone.
/// </para>
/// </remarks>
public sealed class UsageFigureTests : IDisposable
{
    private const string Session = "ab86443d-84b5-4342-85b4-a13111a93020";

    /// <summary>Noon, local time, on the day of the guide's real body: far from midnight in every zone.</summary>
    private static readonly DateTimeOffset Noon = LocalTime(2026, 10, 8, 12, 0);

    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly QueueingDispatcher _dispatcher = new();
    private readonly SessionProjection _projection;
    private readonly UsageBoard _usage = new();
    private readonly MainViewModel _viewModel;

    public UsageFigureTests()
    {
        _projection = new SessionProjection(_registry, _dispatcher);
        _viewModel = new MainViewModel(_projection, new MotionPolicy(() => false, observeChanges: false), new StubAckPublisher(), new FakeClipboard(), new RosterStore(new RecordingEventSink()), new RecordingRosterPersistence(), _usage);
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        _projection.Dispose();
    }

    private UsageFigure[] Pairs => [_viewModel.CurrentUsage, _viewModel.WeekUsage, _viewModel.FableUsage];

    /// <summary>Before the first post, no pair is shown, and the strip is not there (R8).</summary>
    [Fact]
    public void No_reading_means_no_usage_strip()
    {
        _viewModel.Tick(Noon);

        Assert.False(_viewModel.HasUsage);
        Assert.All(Pairs, pair => Assert.False(pair.IsShown));
    }

    /// <summary>
    /// <strong>A post shows at the next tick</strong> (R12): nothing tells the view model that it arrived. Each
    /// pair gives its figure and its colour, and only a pair with a shown pair before it has a separator.
    /// </summary>
    [Fact]
    public void A_reading_shows_at_the_next_tick()
    {
        _viewModel.Tick(Noon);

        _usage.Heard([Reading("five_hour", 24, Noon.AddHours(3)), Reading("seven_day", 55, Noon.AddDays(6))], Noon);

        Assert.False(_viewModel.HasUsage);

        _viewModel.Tick(Noon.AddSeconds(15));

        Assert.True(_viewModel.HasUsage);
        Assert.Equal(
            [(true, false, "76%", UsageLevel.Green), (true, true, "45%", UsageLevel.Amber), (false, false, string.Empty, UsageLevel.Green)],
            Pairs.Select(pair => (pair.IsShown, pair.HasSeparator, pair.Figure, pair.Level)));
    }

    /// <summary>
    /// <strong>A slot past its reset time shows fresh</strong> (R14): at the tick at its reset time it shows 100% in
    /// green, whatever it showed before, and stays in its place, so the pair after it keeps its separator. The board
    /// still holds the old reading, and <c>/state</c>'s view (<see cref="UsageReadings.At"/>) still leaves it out.
    /// </summary>
    [Fact]
    public void A_slot_past_its_reset_time_shows_fresh()
    {
        var fiveHourReset = Noon.AddHours(3);

        _usage.Heard([Reading("five_hour", 97, fiveHourReset), Reading("seven_day", 13, Noon.AddDays(6))], Noon);
        _viewModel.Tick(Noon.AddSeconds(15));

        Assert.Equal(("3%", UsageLevel.Red), (_viewModel.CurrentUsage.Figure, _viewModel.CurrentUsage.Level));

        _viewModel.Tick(fiveHourReset);

        Assert.True(_viewModel.CurrentUsage.IsShown);
        Assert.Equal(("100%", UsageLevel.Green), (_viewModel.CurrentUsage.Figure, _viewModel.CurrentUsage.Level));
        Assert.True(_viewModel.WeekUsage.HasSeparator);
        Assert.Equal("87%", _viewModel.WeekUsage.Figure);
        Assert.True(_viewModel.HasUsage);
        Assert.Equal(97, _usage.Current.Windows.Single(window => window.Kind == "five_hour").PercentUsed);
        Assert.DoesNotContain(_usage.Current.At(fiveHourReset).Windows, window => window.Kind == "five_hour");
    }

    /// <summary>
    /// <strong>The fresh hover names the reset that passed</strong> (R14): "Reset", past, at the time when it was
    /// today, or with the day's name when it was not; then that the next reset time comes with the next reading.
    /// </summary>
    [Fact]
    public void The_fresh_hover_names_the_reset_that_passed()
    {
        var today = Noon.AddHours(-2).AddMinutes(-40);
        var yesterday = Noon.AddDays(-1);

        _usage.Heard([Reading("five_hour", 60, today), Reading("seven_day", 99, yesterday)], Noon.AddDays(-2));
        _viewModel.Tick(Noon);

        var todayLocal = TimeZoneInfo.ConvertTime(today, TimeZoneInfo.Local);
        var yesterdayLocal = TimeZoneInfo.ConvertTime(yesterday, TimeZoneInfo.Local);

        Assert.Equal(
            "Current session · 100% remaining · Reset at " + todayLocal.ToString("t", CultureInfo.CurrentCulture)
                + "; the next reset time comes with the next reading",
            _viewModel.CurrentUsage.HoverText);
        Assert.Equal(
            "This week · 100% remaining · Reset " + yesterdayLocal.ToString("dddd", CultureInfo.CurrentCulture) + " "
                + yesterdayLocal.ToString("t", CultureInfo.CurrentCulture) + "; the next reset time comes with the next reading",
            _viewModel.WeekUsage.HoverText);
    }

    /// <summary>
    /// <strong>The next reading replaces a fresh slot</strong> (R14), at the next tick, also when it shows less than
    /// 100: the slot is live again, with its new reset time.
    /// </summary>
    [Fact]
    public void The_next_reading_replaces_a_fresh_slot()
    {
        var firstReset = Noon.AddHours(-1);
        var nextReset = Noon.AddHours(4);

        _usage.Heard([Reading("five_hour", 80, firstReset)], Noon.AddHours(-3));
        _viewModel.Tick(Noon);

        Assert.Equal("100%", _viewModel.CurrentUsage.Figure);

        _usage.Heard([Reading("five_hour", 2, nextReset)], Noon.AddSeconds(5));
        _viewModel.Tick(Noon.AddSeconds(15));

        var nextLocal = TimeZoneInfo.ConvertTime(nextReset, TimeZoneInfo.Local);

        Assert.Equal(("98%", UsageLevel.Green), (_viewModel.CurrentUsage.Figure, _viewModel.CurrentUsage.Level));
        Assert.Equal(
            "Current session · 98% remaining · Resets at " + nextLocal.ToString("t", CultureInfo.CurrentCulture),
            _viewModel.CurrentUsage.HoverText);
    }

    /// <summary>
    /// <strong>The hover says remaining</strong> (R13), with the figure the caption shows: 40.4 used is "60%
    /// remaining", never "used".
    /// </summary>
    [Fact]
    public void The_hover_says_remaining()
    {
        var reset = Noon.AddDays(6);

        _usage.Heard([Reading("seven_day", 40.4, reset)], Noon);
        _viewModel.Tick(Noon.AddSeconds(15));

        var resetLocal = TimeZoneInfo.ConvertTime(reset, TimeZoneInfo.Local);

        Assert.Equal("60%", _viewModel.WeekUsage.Figure);
        Assert.Equal(
            "This week · 60% remaining · Resets " + resetLocal.ToString("dddd", CultureInfo.CurrentCulture) + " "
                + resetLocal.ToString("t", CultureInfo.CurrentCulture),
            _viewModel.WeekUsage.HoverText);
        Assert.DoesNotContain("used", _viewModel.WeekUsage.HoverText, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The hover text names the window, the figure and the reset time</strong> (R11): "Resets at" and the
    /// time when the reset is today, and the day's full name before the time otherwise. A made-up Fable kind
    /// fills the third slot.
    /// </summary>
    [Fact]
    public void The_hover_text_names_the_window_the_figure_and_the_reset_time()
    {
        var today = Noon.AddHours(3).AddMinutes(20);
        var later = Noon.AddDays(6).AddHours(-3);

        _usage.Heard(
            [Reading("five_hour", 5, today), Reading("seven_day", 36.4, later), Reading("seven_day_fable", 60, later)],
            Noon);
        _viewModel.Tick(Noon.AddSeconds(15));

        var todayLocal = TimeZoneInfo.ConvertTime(today, TimeZoneInfo.Local);
        var laterLocal = TimeZoneInfo.ConvertTime(later, TimeZoneInfo.Local);
        var atToday = "Resets at " + todayLocal.ToString("t", CultureInfo.CurrentCulture);
        var onLater = "Resets " + laterLocal.ToString("dddd", CultureInfo.CurrentCulture) + " " + laterLocal.ToString("t", CultureInfo.CurrentCulture);

        Assert.Equal(
            [
                "Current session · 95% remaining · " + atToday,
                "This week · 64% remaining · " + onLater,
                "Fable this week · 40% remaining · " + onLater,
            ],
            Pairs.Select(pair => pair.HoverText));
        Assert.Equal(["95%", "64%", "40%"], Pairs.Select(pair => pair.Figure));
        Assert.Equal([UsageLevel.Green, UsageLevel.Green, UsageLevel.Amber], Pairs.Select(pair => pair.Level));
        Assert.Equal([false, true, true], Pairs.Select(pair => pair.HasSeparator));
    }

    /// <summary>A reading with no reset time stays live (it cannot pass), and its hover text has no reset part.</summary>
    [Fact]
    public void A_reading_with_no_reset_time_has_no_resets_part()
    {
        _usage.Heard([Reading("seven_day", 13, null)], Noon);
        _viewModel.Tick(Noon.AddDays(30));

        Assert.True(_viewModel.WeekUsage.IsShown);
        Assert.Equal("This week · 87% remaining", _viewModel.WeekUsage.HoverText);
    }

    /// <summary>
    /// A kind with no slot is not shown, and a post with only such kinds leaves the strip away. 49.5 used is 50 used,
    /// so 50% remaining, amber: the colour is judged by the rounded used figure.
    /// </summary>
    [Fact]
    public void Only_the_three_slots_are_shown_and_each_figure_is_rounded()
    {
        _usage.Heard([Reading("spend_limit", 80, Noon.AddDays(1))], Noon);
        _viewModel.Tick(Noon.AddSeconds(15));

        Assert.False(_viewModel.HasUsage);

        _usage.Heard([Reading("five_hour", 49.5, Noon.AddHours(1))], Noon.AddMinutes(1));
        _viewModel.Tick(Noon.AddSeconds(30));

        Assert.Equal(("50%", UsageLevel.Amber), (_viewModel.CurrentUsage.Figure, _viewModel.CurrentUsage.Level));
    }

    /// <summary>
    /// <strong>Degrade, never crash:</strong> a reset time at either end of the calendar has a local time in no
    /// zone on one side of UTC, and the conversion throws. The hover text then has no reset part.
    /// </summary>
    [Fact]
    public void A_reset_time_at_the_end_of_the_calendar_does_not_throw()
    {
        var ends = new[] { DateTimeOffset.MaxValue, DateTimeOffset.MinValue };

        Assert.All(ends, end => UsageFigure.ResetsText(end, Noon));

        _usage.Heard([Reading("seven_day", 13, DateTimeOffset.MaxValue)], Noon);
        _viewModel.Tick(Noon.AddSeconds(15));

        Assert.StartsWith("This week · 87% remaining", _viewModel.WeekUsage.HoverText, StringComparison.Ordinal);

        // A reset at the start of the calendar has passed: the slot is fresh. Its reset part is there only in a zone
        // where that instant has a local time.
        _usage.Heard([Reading("five_hour", 13, DateTimeOffset.MinValue)], Noon);
        _viewModel.Tick(Noon.AddSeconds(30));

        Assert.StartsWith("Current session · 100% remaining", _viewModel.CurrentUsage.HoverText, StringComparison.Ordinal);
        Assert.EndsWith("; " + UsageFigure.NextReadingText, _viewModel.CurrentUsage.HoverText, StringComparison.Ordinal);
    }

    private static UsageWindow Reading(string kind, double percentUsed, DateTimeOffset? resetsAt) =>
        new(kind, percentUsed, resetsAt, Noon, Session);

    private static DateTimeOffset LocalTime(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
