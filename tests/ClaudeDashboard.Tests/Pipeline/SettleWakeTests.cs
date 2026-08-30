using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// The consumer loop's two deadlines: the ordinary tick, and a roster group due to settle
/// (T1.25, issue #16).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this needed changing at all.</strong> <c>DefaultTickInterval</c> is fifteen seconds
/// and the settle window is one and a half, so a settle evaluated only on the tick would deliver
/// the group's finished state — and its chime — up to fifteen seconds after the work finished.
/// Every test would have passed, because tests drive the clock directly and never wait on the
/// loop.
/// </para>
/// <para>
/// <strong>The old behaviour is asserted as carefully as the new one.</strong> This is the spine of
/// the pipeline: a change that only proves the settle now works has not proved the tick still does.
/// </para>
/// </remarks>
public sealed class SettleWakeTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    /// <summary>With nothing pending, the wait is exactly the time left until the next tick.</summary>
    [Fact]
    public void With_no_settle_pending_the_wait_is_the_ordinary_tick()
    {
        var nextTick = At + EventConsumer.DefaultTickInterval;

        Assert.Equal(EventConsumer.DefaultTickInterval, EventConsumer.WaitFor(At, nextTick, settleDue: null));
    }

    /// <summary>
    /// <strong>A pending deadline precedes the tick; it does not displace it.</strong>
    /// </summary>
    /// <remarks>
    /// The wait shortens to the settle, and the tick deadline itself is untouched — the caller
    /// advances that only when it actually ticks. So a run of settles cannot postpone the tick, and
    /// cannot bring it forward either.
    /// </remarks>
    [Fact]
    public void A_pending_settle_shortens_the_wait_without_moving_the_tick()
    {
        var nextTick = At + EventConsumer.DefaultTickInterval;
        var settleDue = At + TimeSpan.FromSeconds(1.5);

        Assert.Equal(TimeSpan.FromSeconds(1.5), EventConsumer.WaitFor(At, nextTick, settleDue));

        // The tick deadline is an input, not a result: the same call with no settle still returns
        // the full interval, so nothing here has consumed or moved it.
        Assert.Equal(EventConsumer.DefaultTickInterval, EventConsumer.WaitFor(At, nextTick, settleDue: null));
    }

    /// <summary>A deadline beyond the next tick does not extend the wait.</summary>
    /// <remarks>
    /// The tick has to keep its cadence: ages on screen and the nudge ladder both depend on it, and
    /// a settle that pushed it out would freeze both for as long as the group stayed quiet.
    /// </remarks>
    [Fact]
    public void A_settle_after_the_next_tick_does_not_extend_the_wait()
    {
        var nextTick = At + EventConsumer.DefaultTickInterval;
        var settleDue = At + TimeSpan.FromMinutes(5);

        Assert.Equal(EventConsumer.DefaultTickInterval, EventConsumer.WaitFor(At, nextTick, settleDue));
    }

    /// <summary>
    /// A past deadline produces the floor rather than a zero-length wait.
    /// </summary>
    /// <remarks>
    /// <strong>This asserts one call of a pure function, and that is all it ever asserted.</strong>
    /// It was once named for the loop and remarked as though the floor removed a busy loop. The
    /// floor does not remove one — it sets its frequency. What the loop does after a deadline has
    /// passed is <c>SettleSpinTests</c>'s, and it had to be, because no assertion on this function
    /// could ever have caught it.
    /// </remarks>
    [Fact]
    public void A_past_deadline_produces_the_floor_rather_than_a_zero_wait()
    {
        var nextTick = At + EventConsumer.DefaultTickInterval;

        Assert.Equal(EventConsumer.MinimumWait, EventConsumer.WaitFor(At, nextTick, At - TimeSpan.FromMinutes(1)));
        Assert.Equal(EventConsumer.MinimumWait, EventConsumer.WaitFor(At, At - TimeSpan.FromMinutes(1), null));
    }

    /// <summary>The floor is far below the settle window, so it can never blunt it.</summary>
    /// <remarks>
    /// Asserted rather than assumed: a floor that crept up to the size of the window would silently
    /// reinstate the very latency this whole mechanism exists to remove.
    /// </remarks>
    [Fact]
    public void The_floor_is_far_below_the_settle_window()
    {
        Assert.True(EventConsumer.MinimumWait * 10 < ClaudeDashboard.Core.RosterSettle.DefaultWindow);
    }
}
