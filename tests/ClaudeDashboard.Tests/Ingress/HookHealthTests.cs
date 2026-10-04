using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Ui;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Ingress;

/// <summary>
/// The refused notice, "last heard" and the tooltip's length, without a host (T1.61, issue #74).
/// </summary>
public sealed class HookHealthTests
{
    private static readonly DateTimeOffset Start = FakeClock.DefaultStart;

    private static RefusedNotice ShownAt(HookHealth health, DateTimeOffset now)
    {
        var notice = new RefusedNotice(health);
        notice.Tick(now);
        return notice;
    }

    // ---- Refused messages ---------------------------------------------------------------------

    /// <summary>Two refusals, as around a restart, show nothing; a third within ten minutes does.</summary>
    [Fact]
    public void Two_refusals_show_nothing_and_three_in_ten_minutes_show_the_notice()
    {
        var health = new HookHealth();

        health.Refused(Start);
        health.Refused(Start + TimeSpan.FromMinutes(1));

        Assert.False(ShownAt(health, Start + TimeSpan.FromMinutes(1)).IsShown);

        health.Refused(Start + TimeSpan.FromMinutes(9));
        var notice = ShownAt(health, Start + TimeSpan.FromMinutes(9));

        Assert.True(notice.IsShown);
        Assert.Equal(RefusedNotice.WindowText, notice.Text);
        Assert.Equal(RefusedNotice.TrayShort, notice.TrayText);
        Assert.Equal(3, health.RefusedCount);
    }

    /// <summary>
    /// The notice clears on the tick ten minutes after the last refusal, and a refusal while it
    /// shows keeps it ten minutes more.
    /// </summary>
    [Fact]
    public void The_refused_notice_clears_ten_minutes_after_the_last_refusal()
    {
        var clock = new FakeClock(Start);
        var health = new HookHealth();
        var notice = new RefusedNotice(health);

        health.Refused(Start);
        health.Refused(Start + TimeSpan.FromMinutes(1));
        health.Refused(Start + TimeSpan.FromMinutes(2));

        clock.Now = Start + TimeSpan.FromMinutes(2);
        notice.Tick(clock.Now);
        Assert.True(notice.IsShown);

        clock.Now = Start + TimeSpan.FromMinutes(11.9);
        notice.Tick(clock.Now);
        Assert.True(notice.IsShown);

        clock.Now = Start + TimeSpan.FromMinutes(12);
        notice.Tick(clock.Now);
        Assert.False(notice.IsShown);

        // Shown again by three more, then kept by a fourth.
        health.Refused(Start + TimeSpan.FromMinutes(20));
        health.Refused(Start + TimeSpan.FromMinutes(21));
        health.Refused(Start + TimeSpan.FromMinutes(22));
        health.Refused(Start + TimeSpan.FromMinutes(30));

        notice.Tick(Start + TimeSpan.FromMinutes(39));
        Assert.True(notice.IsShown);

        notice.Tick(Start + TimeSpan.FromMinutes(40));
        Assert.False(notice.IsShown);
    }

    /// <summary>Three refusals spread over more than ten minutes are not a token that keeps failing.</summary>
    [Fact]
    public void Three_refusals_spread_over_more_than_ten_minutes_show_nothing()
    {
        var health = new HookHealth();

        health.Refused(Start);
        health.Refused(Start + TimeSpan.FromMinutes(6));
        health.Refused(Start + TimeSpan.FromMinutes(12));

        Assert.False(ShownAt(health, Start + TimeSpan.FromMinutes(12)).IsShown);
        Assert.Equal(3, health.RefusedCount);
    }

    // ---- HookRefused rows: at most one a second (the ruling of 2026-10-04) ----------------------

    /// <summary>
    /// A burst of 1,000 refusals within one second writes one row at once, for the first, and the
    /// tick writes the other 999 as one last row. No refusal goes unrecorded while the dashboard runs.
    /// </summary>
    [Fact]
    public void A_burst_of_a_thousand_refusals_writes_one_row_and_the_tick_the_rest()
    {
        var health = new HookHealth();
        var rows = new List<(DateTimeOffset At, long Count)>();
        health.RefusedPost = (at, count) => rows.Add((at, count));

        for (var i = 0; i < 1_000; i++)
        {
            health.Refused(Start + TimeSpan.FromMilliseconds(i * 0.9));
        }

        Assert.Equal([(Start, 1L)], rows);

        // The tray's tick, through the notice: what the burst left is one last row.
        new RefusedNotice(health).Tick(Start + TimeSpan.FromSeconds(15));

        Assert.Equal([(Start, 1L), (Start + TimeSpan.FromSeconds(15), 999L)], rows);
        Assert.Equal(1_000, rows.Sum(row => row.Count));
        Assert.Equal(1_000, health.RefusedCount);
    }

    /// <summary>A refusal a second or more after the last row carries the count of those in between.</summary>
    [Fact]
    public void The_next_row_carries_the_refusals_in_between()
    {
        var health = new HookHealth();
        var rows = new List<(DateTimeOffset At, long Count)>();
        health.RefusedPost = (at, count) => rows.Add((at, count));

        for (var i = 0; i < 37; i++)
        {
            health.Refused(Start + TimeSpan.FromMilliseconds(i * 10));
        }

        health.Refused(Start + TimeSpan.FromSeconds(1));

        Assert.Equal([(Start, 1L), (Start + TimeSpan.FromSeconds(1), 37L)], rows);
    }

    /// <summary>Refusals two seconds apart write one row each, each for one refusal.</summary>
    [Fact]
    public void Refusals_two_seconds_apart_write_one_row_each()
    {
        var health = new HookHealth();
        var rows = new List<(DateTimeOffset At, long Count)>();
        health.RefusedPost = (at, count) => rows.Add((at, count));

        health.Refused(Start);
        health.Refused(Start + TimeSpan.FromSeconds(2));
        health.Refused(Start + TimeSpan.FromSeconds(4));

        Assert.Equal(
            [(Start, 1L), (Start + TimeSpan.FromSeconds(2), 1L), (Start + TimeSpan.FromSeconds(4), 1L)],
            rows);
    }

    /// <summary>The tick writes nothing when nothing is left, or within a second of the last row.</summary>
    [Fact]
    public void The_tick_writes_only_a_remainder_and_keeps_to_one_row_a_second()
    {
        var health = new HookHealth();
        var rows = new List<(DateTimeOffset At, long Count)>();
        health.RefusedPost = (at, count) => rows.Add((at, count));

        health.FlushRefusals(Start);
        Assert.Empty(rows);

        health.Refused(Start);
        health.Refused(Start + TimeSpan.FromMilliseconds(100));

        health.FlushRefusals(Start + TimeSpan.FromMilliseconds(500));
        Assert.Equal([(Start, 1L)], rows);

        health.FlushRefusals(Start + TimeSpan.FromSeconds(1));
        health.FlushRefusals(Start + TimeSpan.FromSeconds(16));
        Assert.Equal([(Start, 1L), (Start + TimeSpan.FromSeconds(1), 1L)], rows);
    }

    /// <summary>
    /// A flood of refused posts holds at most <see cref="HookHealth.RefusalsToShow"/> instants, and
    /// the notice still shows and clears as before (T1.61 review: 154,746 held before the fix).
    /// </summary>
    [Fact]
    public void A_flood_of_refusals_holds_at_most_three_and_shows_and_clears_as_before()
    {
        var health = new HookHealth();
        var notice = new RefusedNotice(health);
        var last = Start;

        for (var i = 0; i < 200_000; i++)
        {
            last = Start + TimeSpan.FromMilliseconds(i);
            health.Refused(last);

            Assert.True(health.HeldRefusals <= HookHealth.RefusalsToShow, $"{health.HeldRefusals} held after {i + 1}.");
        }

        Assert.Equal(200_000, health.RefusedCount);

        notice.Tick(last);
        Assert.True(notice.IsShown);

        notice.Tick(last + HookHealth.RefusalWindow - TimeSpan.FromSeconds(1));
        Assert.True(notice.IsShown);

        notice.Tick(last + HookHealth.RefusalWindow);
        Assert.False(notice.IsShown);
    }

    // ---- The self-test's one-time value -------------------------------------------------------

    /// <summary>
    /// Only the value the test waits for completes it: another value, or none, is answered and
    /// changes nothing.
    /// </summary>
    [Fact]
    public async Task Only_the_awaited_value_completes_the_self_test()
    {
        var health = new HookHealth();
        var arrival = health.Expect("the-value");

        Assert.False(health.TestArrived("another-value", Start));
        Assert.False(health.TestArrived(null, Start));
        Assert.False(arrival.IsCompleted);

        Assert.True(health.TestArrived("the-value", Start + TimeSpan.FromSeconds(1)));
        Assert.True(arrival.IsCompletedSuccessfully);
        Assert.Equal(Start + TimeSpan.FromSeconds(1), await arrival);

        // Once arrived, the same value is not accepted twice.
        Assert.False(health.TestArrived("the-value", Start + TimeSpan.FromSeconds(2)));
    }

    // ---- Last heard ---------------------------------------------------------------------------

    /// <summary>The phrase for each gap: since start, just now, minutes, hours, days.</summary>
    [Theory]
    [InlineData(null, "not heard from Claude Code since start")]
    [InlineData(0.0, "last heard from Claude Code just now")]
    [InlineData(0.9, "last heard from Claude Code just now")]
    [InlineData(2.0, "last heard from Claude Code 2 min ago")]
    [InlineData(12.5, "last heard from Claude Code 12 min ago")]
    [InlineData(180.0, "last heard from Claude Code 3 h ago")]
    [InlineData(2.0 * 24 * 60, "last heard from Claude Code 2 d ago")]
    public void Last_heard_is_phrased_by_the_gap(double? minutesAgo, string expected)
    {
        DateTimeOffset? heard = minutesAgo is { } minutes ? Start - TimeSpan.FromMinutes(minutes) : null;

        Assert.Equal(expected, TrayTooltip.LastHeard(heard, Start));
    }

    /// <summary>
    /// The tray says "not heard", then "just now", then "12 min ago", as the last item, and the gap
    /// changes no colour and records no light change.
    /// </summary>
    [Fact]
    public void Last_heard_is_the_last_item_and_never_an_alarm()
    {
        var clock = new FakeClock(Start);
        var health = new HookHealth();
        var decisions = new RecordingDecisionLog();
        using var registry = new RegistryHarness();

        using var tray = new TrayViewModel(
            registry.Projection,
            new SettableSoundModes(),
            new RecordingEventSink(),
            clock,
            IngressStatus.Healthy(DashboardSettings.IngressPortBase),
            Logger.None,
            decisions,
            health: health);

        var colour = tray.Colour;
        Assert.Equal("all quiet · not heard from Claude Code since start", tray.Tooltip);

        health.Heard(clock.Now);
        tray.Tick(clock.Now);
        Assert.Equal("all quiet · last heard from Claude Code just now", tray.Tooltip);

        clock.Now += TimeSpan.FromMinutes(12);
        tray.Tick(clock.Now);
        Assert.Equal("all quiet · last heard from Claude Code 12 min ago", tray.Tooltip);

        clock.Now += TimeSpan.FromDays(3);
        tray.Tick(clock.Now);
        Assert.EndsWith("3 d ago", tray.Tooltip, StringComparison.Ordinal);

        Assert.Equal(colour, tray.Colour);
        Assert.DoesNotContain(decisions.Rows, row => row.Kind == DecisionKind.TrayLightChanged);
    }

    // ---- The tooltip's length -----------------------------------------------------------------

    /// <summary>
    /// With every notice active the tooltip stays within Windows' 127 characters. "Last heard" goes
    /// first and whole, then whole items from the end, so no word is cut.
    /// </summary>
    [Fact]
    public void With_every_notice_the_tooltip_fits_and_drops_last_heard_whole()
    {
        string[] notices =
        [
            IngressStatus.PinnedPortTaken(52961).TrayText!,
            HookNotice.PluginDisabledShort,
            SelfTestNotice.TrayShort,
            RefusedNotice.TrayShort,
            HistoryNotice.TrayShort,
            SoundDeviceNotice.TrayShort,
            SettingsNotice.TrayShort,
            FellBehindNotice.TrayShort,
            EventsLostNotice.TrayShort,
        ];

        var fault = string.Join(" · ", notices);
        var summary = new StatusSummary { Permissions = 2, Errors = 1, Questions = 1, Unread = 3, Working = 4, Worst = SessionState.NeedsPermission };
        var lastHeard = TrayTooltip.LastHeard(Start - TimeSpan.FromMinutes(2), Start);

        var tooltip = TrayTooltip.For(summary, now: Start, fault: fault, lastHeard: lastHeard);
        var full = $"{fault} · {TrayTooltip.For(summary, now: Start)} · {lastHeard}";

        Assert.True(full.Length > TrayTooltip.MaxLength, "The case is meant to be over the limit.");
        Assert.True(tooltip.Length <= TrayTooltip.MaxLength, $"{tooltip.Length} characters: {tooltip}");
        Assert.DoesNotContain("heard", tooltip, StringComparison.Ordinal);

        // Whole items only: what is left is the full text's own leading items.
        Assert.StartsWith(tooltip, full, StringComparison.Ordinal);
        Assert.StartsWith(" · ", full[tooltip.Length..], StringComparison.Ordinal);
    }

    /// <summary>A tooltip that fits keeps "last heard" as its last item.</summary>
    [Fact]
    public void A_tooltip_that_fits_ends_with_last_heard()
    {
        var tooltip = TrayTooltip.For(
            new StatusSummary { Permissions = 0, Errors = 0, Questions = 0, Unread = 0, Working = 1, Worst = SessionState.Working },
            now: Start,
            fault: RefusedNotice.TrayShort,
            lastHeard: TrayTooltip.LastHeard(null, Start));

        Assert.Equal("messages refused · 1 working · not heard from Claude Code since start", tooltip);
    }

    /// <summary>Records each decision handed to it.</summary>
    private sealed class RecordingDecisionLog : IDecisionLog
    {
        public List<Decision> Rows { get; } = [];

        public void External(Decision decision) => Rows.Add(decision);
    }
}
