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
/// #133; rulings R8, R10, R11 and R12).
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
            [(true, false, "24%", UsageLevel.Green), (true, true, "55%", UsageLevel.Amber), (false, false, string.Empty, UsageLevel.Green)],
            Pairs.Select(pair => (pair.IsShown, pair.HasSeparator, pair.Figure, pair.Level)));
    }

    /// <summary>
    /// <strong>A limit past its reset time leaves its slot</strong> (<see cref="UsageReadings.At"/>), and the pair
    /// after it then starts the strip, with no separator. The board still holds it: only the window lets it go.
    /// </summary>
    [Fact]
    public void A_window_past_its_reset_time_leaves_its_slot_empty()
    {
        var fiveHourReset = Noon.AddHours(3);

        _usage.Heard([Reading("five_hour", 24, fiveHourReset), Reading("seven_day", 13, Noon.AddDays(6))], Noon);
        _viewModel.Tick(Noon.AddSeconds(15));

        Assert.True(_viewModel.CurrentUsage.IsShown);

        _viewModel.Tick(fiveHourReset);

        Assert.False(_viewModel.CurrentUsage.IsShown);
        Assert.True(_viewModel.WeekUsage.IsShown);
        Assert.False(_viewModel.WeekUsage.HasSeparator);
        Assert.True(_viewModel.HasUsage);
        Assert.Equal(2, _usage.Current.Windows.Count);
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
                "Current session · 5% used · " + atToday,
                "This week · 36% used · " + onLater,
                "Fable this week · 60% used · " + onLater,
            ],
            Pairs.Select(pair => pair.HoverText));
        Assert.Equal(["5%", "36%", "60%"], Pairs.Select(pair => pair.Figure));
        Assert.Equal([false, true, true], Pairs.Select(pair => pair.HasSeparator));
    }

    /// <summary>A reading with no reset time stays (it cannot pass), and its hover text has no reset part.</summary>
    [Fact]
    public void A_reading_with_no_reset_time_has_no_resets_part()
    {
        _usage.Heard([Reading("seven_day", 13, null)], Noon);
        _viewModel.Tick(Noon.AddDays(30));

        Assert.True(_viewModel.WeekUsage.IsShown);
        Assert.Equal("This week · 13% used", _viewModel.WeekUsage.HoverText);
    }

    /// <summary>
    /// A kind with no slot is not shown, and a post with only such kinds leaves the strip away. 49.5 shows
    /// 50 and is amber: the colour is judged by the figure on screen.
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

        Assert.StartsWith("This week · 13% used", _viewModel.WeekUsage.HoverText, StringComparison.Ordinal);
    }

    private static UsageWindow Reading(string kind, double percentUsed, DateTimeOffset? resetsAt) =>
        new(kind, percentUsed, resetsAt, Noon, Session);

    private static DateTimeOffset LocalTime(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
