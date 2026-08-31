using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// What an interrupted row and an interrupted tray look like (issue #28).
/// </summary>
/// <remarks>
/// <para>
/// The operator asked for grey, badged <c>INTERRUPTED</c>, out of the Working band and not
/// moving. Each of those is a separate claim about a separate method, and three of the four are
/// carried by a <c>_ =&gt;</c> arm rather than by a named case — so without these tests they hold
/// by accident, and the accident is the kind that stops holding when somebody edits the default.
/// </para>
/// <para>
/// <strong>Two of these caught real mistakes while the state was being added.</strong>
/// <see cref="An_interrupted_row_reads_ago_and_not_a_bare_duration"/> is one:
/// <c>RowVisuals.Age</c>'s default is a bare duration, which its own remark defines as "the agent
/// is busy and the clock is nobody's fault" — the exact claim this state exists to withdraw. The
/// other is <see cref="The_tray_reads_grey_for_an_interrupted_session"/>; see its remarks.
/// </para>
/// </remarks>
public sealed class SilenceVisualsTests
{
    /// <summary>The badge is the operator's word, spelled as they asked for it.</summary>
    [Fact]
    public void The_badge_reads_interrupted() =>
        Assert.Equal("INTERRUPTED", RowVisuals.BadgeOf(SessionState.Interrupted));

    /// <summary>The accent is grey — the same grey an acknowledged row reads.</summary>
    /// <remarks>
    /// Asserted against <see cref="SessionState.Acked"/> rather than against <c>Accent.Grey</c>
    /// alone, because the issue asks for "grey, as <c>SessionState.Acked</c>" and the two moving
    /// apart would be the request quietly stopping being met.
    /// </remarks>
    [Fact]
    public void The_accent_is_the_same_grey_an_acknowledged_row_reads()
    {
        Assert.Equal(Accent.Grey, RowVisuals.AccentOf(SessionState.Interrupted));
        Assert.Equal(RowVisuals.AccentOf(SessionState.Acked), RowVisuals.AccentOf(SessionState.Interrupted));
    }

    /// <summary>
    /// <strong>The row says "ago", not a bare duration.</strong>
    /// </summary>
    /// <remarks>
    /// The default arm would have given a bare duration, and <c>RowVisuals.Age</c>'s own remark
    /// defines that phrasing as <em>"the agent is busy and the clock is nobody's fault"</em> —
    /// which is precisely the claim <see cref="SessionState.Interrupted"/> exists to withdraw. It
    /// would have compiled, rendered, and read as a working row wearing a grey badge.
    ///
    /// "Waiting" was rejected: that phrasing belongs to the Needs-You band, and this state
    /// deliberately does not escalate.
    /// </remarks>
    [Fact]
    public void An_interrupted_row_reads_ago_and_not_a_bare_duration()
    {
        var age = TimeSpan.FromMinutes(12);

        Assert.Equal(RowVisuals.Age(SessionState.Acked, age), RowVisuals.Age(SessionState.Interrupted, age));
        Assert.EndsWith(" ago", RowVisuals.Age(SessionState.Interrupted, age), StringComparison.Ordinal);
        Assert.NotEqual(RowVisuals.Age(SessionState.Working, age), RowVisuals.Age(SessionState.Interrupted, age));
    }

    /// <summary>Nothing moves. Design §9: red blinks, working breathes, nothing else.</summary>
    [Fact]
    public void An_interrupted_row_does_not_move() =>
        Assert.Equal(MotionKind.None, MotionPolicy.Wanted(SessionState.Interrupted));

    /// <summary>It sits in the Quiet band, below Working, above nothing that needs the operator.</summary>
    /// <remarks>
    /// The band and the rank are asserted together because they are the two halves of "it stops
    /// claiming to be busy": the band is where the row sorts on screen, and the rank is what a
    /// group rolls up to.
    /// </remarks>
    [Fact]
    public void An_interrupted_session_is_quiet_and_ranks_below_working()
    {
        Assert.Equal(AttentionBand.Quiet, AttentionOrder.BandOf(SessionState.Interrupted));
        Assert.NotEqual(AttentionBand.Working, AttentionOrder.BandOf(SessionState.Interrupted));

        Assert.True(AttentionOrder.Rank(SessionState.Working) > AttentionOrder.Rank(SessionState.Interrupted));
        Assert.True(AttentionOrder.Rank(SessionState.Interrupted) > AttentionOrder.Rank(SessionState.Acked));

        foreach (var wants in new[]
                 {
                     SessionState.NeedsPermission,
                     SessionState.NeedsQuestion,
                     SessionState.Error,
                     SessionState.Unread,
                 })
        {
            Assert.True(
                AttentionOrder.Rank(wants) > AttentionOrder.Rank(SessionState.Interrupted),
                $"{wants} must outrank Interrupted; a stalled row must not sort above one asking for the operator.");
        }
    }

    /// <summary>
    /// <strong>The tray reads grey, and every other state keeps the colour it had.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the assertion that would have caught the tray recolour.</strong> Adding the
    /// state renumbered <see cref="AttentionOrder.Rank"/> by one, and <c>TrayVisuals.ColourOf</c>
    /// compared that rank against <em>literal</em> thresholds — <c>&gt;= 6</c>, <c>&gt;= 4</c> —
    /// with the states they covered named only in comments. Every boundary then pointed one state
    /// too low: Error would have read red, Unread amber, Working green, and an interrupted session
    /// <strong>blue</strong>, which is the one colour it must never be.
    /// </para>
    /// <para>
    /// The repair was to read each boundary out of <c>Rank</c> by name rather than by number. The
    /// general shape is a literal encoding a fact that lives somewhere else, and the comment
    /// beside it staying true while the number stopped being.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_tray_reads_grey_for_an_interrupted_session()
    {
        Assert.Equal(TrayColour.Grey, TrayVisuals.ColourOf(SessionState.Interrupted));

        // The neighbours, so a boundary that slipped is caught here rather than as a puzzle.
        Assert.Equal(TrayColour.Blue, TrayVisuals.ColourOf(SessionState.Working));
        Assert.Equal(TrayColour.Green, TrayVisuals.ColourOf(SessionState.Unread));
        Assert.Equal(TrayColour.Amber, TrayVisuals.ColourOf(SessionState.Error));
        Assert.Equal(TrayColour.Red, TrayVisuals.ColourOf(SessionState.NeedsPermission));
    }
}
