using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The sentence under the glyph (Impl §5.2).
/// </summary>
public sealed class TrayTooltipTests
{
    private const string Fault = "port 52789 taken · not receiving hooks";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    // ---- The ingress fault, which leads everything (T1.15) ----------------------------------------

    /// <summary>
    /// A dashboard that cannot hear anything says so before it says anything else.
    /// </summary>
    /// <remarks>
    /// "All quiet" and "I am deaf" are the same sentence otherwise, and the second one is the
    /// only one the operator cannot work out for themselves.
    /// </remarks>
    [Fact]
    public void A_fault_leads_the_tooltip()
    {
        var tooltip = TrayTooltip.For(Summary(), fault: Fault);

        Assert.StartsWith(Fault, tooltip, StringComparison.Ordinal);
        Assert.Contains(TrayTooltip.AllQuiet, tooltip, StringComparison.Ordinal);
    }

    /// <summary>The counts survive behind the fault; they are still true of what did arrive.</summary>
    [Fact]
    public void A_fault_does_not_replace_the_counts()
    {
        var tooltip = TrayTooltip.For(Summary(permissions: 2, working: 1), fault: Fault);

        Assert.StartsWith(Fault, tooltip, StringComparison.Ordinal);
        Assert.Contains("2 permissions", tooltip, StringComparison.Ordinal);
        Assert.Contains("1 working", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fault outranks pause, which until now led everything.
    /// </summary>
    /// <remarks>
    /// Pause is a state the operator chose seconds ago and already knows about. A dead ingress is
    /// one they cannot know about, and it is the stronger claim: paused means the glyph is not
    /// telling the truth, and deaf means the counts behind it are not either.
    /// </remarks>
    [Fact]
    public void A_fault_outranks_pause_and_mute()
    {
        var paused = TrayTooltip.For(Summary(), paused: true, fault: Fault);
        var muted = TrayTooltip.For(Summary(), mutedUntil: At.AddMinutes(10), now: At, fault: Fault);

        Assert.StartsWith(Fault, paused, StringComparison.Ordinal);
        Assert.Contains(TrayTooltip.Paused, paused, StringComparison.Ordinal);

        Assert.StartsWith(Fault, muted, StringComparison.Ordinal);
        Assert.Contains("muted 10 min", muted, StringComparison.Ordinal);
    }

    /// <summary>
    /// No fault means the tooltip is exactly what it was. The control for all of the above.
    /// </summary>
    /// <remarks>
    /// Asserted as equality against the same call without the parameter, rather than as "does not
    /// contain a fault": a build that always prefixed something would satisfy the weaker form
    /// whenever the something happened not to be the word this test looked for.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Without_a_fault_the_tooltip_is_unchanged(string? fault)
    {
        Assert.Equal(
            TrayTooltip.For(Summary(permissions: 1), paused: true),
            TrayTooltip.For(Summary(permissions: 1), paused: true, fault: fault));
    }

    /// <summary>Impl §5.2's example, exactly as written.</summary>
    [Fact]
    public void It_reads_the_way_the_spec_writes_it()
    {
        Assert.Equal(
            "2 permissions · 1 error · 1 question · 2 unread · 3 working",
            TrayTooltip.For(Summary(permissions: 2, errors: 1, questions: 1, unread: 2, working: 3)));
    }

    /// <summary>Zero counts are omitted rather than shown as "0 errors".</summary>
    [Fact]
    public void Zero_counts_are_omitted()
    {
        Assert.Equal("1 permission · 4 working", TrayTooltip.For(Summary(permissions: 1, working: 4)));
        Assert.Equal("2 unread", TrayTooltip.For(Summary(unread: 2)));
    }

    /// <summary>
    /// Singular and plural, including the two that are adjectives and do not take an "s".
    /// </summary>
    [Fact]
    public void It_is_plural_correct()
    {
        Assert.Equal(
            "1 permission · 1 error · 1 question · 1 unread · 1 working",
            TrayTooltip.For(Summary(permissions: 1, errors: 1, questions: 1, unread: 1, working: 1)));

        Assert.Equal(
            "2 permissions · 2 errors · 2 questions · 2 unread · 2 working",
            TrayTooltip.For(Summary(permissions: 2, errors: 2, questions: 2, unread: 2, working: 2)));
    }

    /// <summary>Nothing at all reads as words, not as an empty string.</summary>
    [Fact]
    public void All_quiet_when_every_count_is_zero()
    {
        Assert.Equal("all quiet", TrayTooltip.For(Summary()));
    }

    /// <summary>
    /// <strong>The Needs-You kinds are broken out.</strong> The glyph merges error and question
    /// onto amber, so this is the only place the difference survives.
    /// </summary>
    /// <remarks>
    /// Asserted as a difference rather than as a format: two dashboards that show the same
    /// amber glyph must not produce the same tooltip, which is what "the distinction stays
    /// available where there is room to render it" means.
    /// </remarks>
    [Fact]
    public void An_error_and_a_question_read_differently_though_the_glyph_cannot_tell_them_apart()
    {
        var error = Summary(errors: 1);
        var question = Summary(questions: 1);

        Assert.Equal(
            TrayVisuals.ColourOf(error.Worst),
            TrayVisuals.ColourOf(question.Worst));

        Assert.NotEqual(TrayTooltip.For(error), TrayTooltip.For(question));
        Assert.Equal("1 error", TrayTooltip.For(error));
        Assert.Equal("1 question", TrayTooltip.For(question));
    }

    /// <summary>Paused leads, and says how to undo itself.</summary>
    [Fact]
    public void Paused_leads_with_the_mode()
    {
        Assert.Equal("paused · click to resume", TrayTooltip.For(Summary(), paused: true));

        Assert.StartsWith(
            "paused · click to resume",
            TrayTooltip.For(Summary(errors: 2), paused: true),
            StringComparison.Ordinal);
    }

    /// <summary>Muted leads with its remaining time, and the counts follow.</summary>
    [Fact]
    public void Muted_leads_with_the_remaining_time()
    {
        Assert.Equal(
            "muted 24 min · 1 error",
            TrayTooltip.For(Summary(errors: 1), mutedUntil: At.AddMinutes(24), now: At));
    }

    /// <summary>A mute with no expiry says so rather than counting down from infinity.</summary>
    [Fact]
    public void An_indefinite_mute_has_no_countdown()
    {
        Assert.Equal(
            "muted · 1 working",
            TrayTooltip.For(Summary(working: 1), mutedUntil: DateTimeOffset.MaxValue, now: At));
    }

    /// <summary>
    /// Pause outranks mute, because pause is why the glyph is grey.
    /// </summary>
    /// <remarks>
    /// Both can be in force at once — the operator can mute and then go off duty. Saying "muted"
    /// there would explain the silence and leave the grey glyph unexplained, which is the one
    /// thing the operator is looking at.
    /// </remarks>
    [Fact]
    public void Pause_wins_over_mute()
    {
        Assert.StartsWith(
            "paused",
            TrayTooltip.For(Summary(errors: 1), paused: true, mutedUntil: At.AddMinutes(10), now: At),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The countdown rounds up, so it never reads zero while the mute is still in force.
    /// </summary>
    /// <remarks>
    /// Forty seconds left is "1 min", not "0 min". A countdown that hits zero and keeps muting
    /// reads as a bug, and the operator's next move would be to click something.
    /// </remarks>
    [Fact]
    public void The_countdown_rounds_up()
    {
        Assert.StartsWith(
            "muted 1 min",
            TrayTooltip.For(Summary(), mutedUntil: At.AddSeconds(40), now: At),
            StringComparison.Ordinal);

        Assert.StartsWith(
            "muted 30 min",
            TrayTooltip.For(Summary(), mutedUntil: At.AddMinutes(30), now: At),
            StringComparison.Ordinal);
    }

    private static StatusSummary Summary(
        int permissions = 0,
        int errors = 0,
        int questions = 0,
        int unread = 0,
        int working = 0)
    {
        var worst = AttentionOrder.WorstOf(
        [
            permissions > 0 ? SessionState.NeedsPermission : SessionState.Ended,
            errors > 0 ? SessionState.Error : SessionState.Ended,
            questions > 0 ? SessionState.NeedsQuestion : SessionState.Ended,
            unread > 0 ? SessionState.Unread : SessionState.Ended,
            working > 0 ? SessionState.Working : SessionState.Ended,
        ]);

        return new StatusSummary
        {
            Worst = worst,
            Permissions = permissions,
            Errors = errors,
            Questions = questions,
            Unread = unread,
            Working = working,
        };
    }
}
