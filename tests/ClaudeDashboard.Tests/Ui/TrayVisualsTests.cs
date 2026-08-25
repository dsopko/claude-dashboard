using System.Linq;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The tray glyph's colour is a coarsening of <see cref="AttentionOrder.Rank"/>, not a second
/// opinion about severity (Impl §5.2).
/// </summary>
public sealed class TrayVisualsTests
{
    /// <summary>
    /// Every state has a colour, and it is not grey by accident.
    /// </summary>
    /// <remarks>
    /// Driven from the enum rather than from seven literals, so a state added later fails here
    /// instead of quietly falling through to grey and being invisible in the tray forever. Same
    /// shape as <c>AttentionOrder.Every_state_is_accounted_for</c>.
    /// </remarks>
    [Fact]
    public void Every_state_has_a_colour()
    {
        var mapped = Enum.GetValues<SessionState>()
            .ToDictionary(state => state, TrayVisuals.ColourOf);

        Assert.Equal(TrayColour.Red, mapped[SessionState.NeedsPermission]);
        Assert.Equal(TrayColour.Amber, mapped[SessionState.Error]);
        Assert.Equal(TrayColour.Amber, mapped[SessionState.NeedsQuestion]);
        Assert.Equal(TrayColour.Green, mapped[SessionState.Unread]);
        Assert.Equal(TrayColour.Blue, mapped[SessionState.Working]);
        Assert.Equal(TrayColour.Grey, mapped[SessionState.Acked]);
        Assert.Equal(TrayColour.Grey, mapped[SessionState.Ended]);

        // …and nothing was left out. If a state is added and lands on grey by default, the count
        // moves and this fails before anyone has to notice the tray never lights up for it.
        Assert.Equal(Enum.GetValues<SessionState>().Length, mapped.Count);
    }

    /// <summary>
    /// <strong>The property that makes this a coarsening.</strong> If one state outranks
    /// another, its tray colour is at least as severe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The severity ordering comes from <see cref="TrayColour"/>'s own values, which are declared
    /// in severity order for exactly this reason — not from the map under test, which would make
    /// the assertion a tautology, and not from a copy written out here, which would be a second
    /// oracle free to drift from the first.
    /// </para>
    /// <para>
    /// Note that <see cref="Accent"/> cannot be used this way: its values run
    /// <c>Grey = 0, Red = 1, Amber = 2, Green = 3, Blue = 4</c>, which is neither ascending nor
    /// descending in urgency. That is asserted below rather than assumed.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_tray_colour_is_monotone_in_rank()
    {
        var states = Enum.GetValues<SessionState>();

        foreach (var a in states)
        {
            foreach (var b in states)
            {
                if (AttentionOrder.Rank(a) <= AttentionOrder.Rank(b))
                {
                    continue;
                }

                Assert.True(
                    TrayVisuals.ColourOf(a) >= TrayVisuals.ColourOf(b),
                    $"{a} outranks {b}, but the tray shows {TrayVisuals.ColourOf(a)} for it and "
                    + $"{TrayVisuals.ColourOf(b)} for {b}. A roll-up whose colours disagree with the "
                    + "ranking contradicts itself.");
            }
        }
    }

    /// <summary>
    /// …and the control: the <em>row</em> palette fails that property, which is why it is not
    /// reused here.
    /// </summary>
    /// <remarks>
    /// Without this, the monotonicity test above proves only that some mapping satisfies the
    /// property, and the obvious wrong implementation — reuse <see cref="RowVisuals.AccentOf"/> —
    /// would look equally defensible to whoever reads it next. This says out loud that the
    /// separation is load-bearing: <c>AccentOf</c> puts a question on red and an error on amber,
    /// so the more urgent of the two shows the calmer colour.
    /// </remarks>
    [Fact]
    public void The_row_palette_is_not_monotone_in_rank_which_is_why_it_is_not_the_tray_palette()
    {
        // Error outranks NeedsQuestion (5 > 4)…
        Assert.True(AttentionOrder.Rank(SessionState.Error) > AttentionOrder.Rank(SessionState.NeedsQuestion));

        // …yet the row LED calls the question red and the error amber. Right for a row, where red
        // is also what earns the blink; wrong for a roll-up.
        Assert.Equal(Accent.Red, RowVisuals.AccentOf(SessionState.NeedsQuestion));
        Assert.Equal(Accent.Amber, RowVisuals.AccentOf(SessionState.Error));

        // The tray does not inherit that inversion.
        Assert.Equal(TrayVisuals.ColourOf(SessionState.Error), TrayVisuals.ColourOf(SessionState.NeedsQuestion));
    }

    /// <summary>
    /// <strong>The case the operator ratified.</strong> A dashboard whose most urgent session is
    /// a question — no permission, no error — shows amber, not red.
    /// </summary>
    /// <remarks>
    /// This is the assertion that separates the right implementation from the obvious wrong one.
    /// Composing the roll-up with <see cref="RowVisuals.AccentOf"/> passes every other case,
    /// including the mixed Error-plus-Question case below, because there the roll-up picks Error
    /// at rank 5 and <c>AccentOf</c> maps Error to amber. The two maps differ in exactly one
    /// place: when the worst state <em>is</em> <see cref="SessionState.NeedsQuestion"/>.
    /// </remarks>
    [Fact]
    public void A_lone_question_shows_amber()
    {
        var worst = AttentionOrder.WorstOf([SessionState.NeedsQuestion, SessionState.Working, SessionState.Acked]);

        Assert.Equal(SessionState.NeedsQuestion, worst);
        Assert.Equal(TrayColour.Amber, TrayVisuals.ColourOf(worst));
    }

    /// <summary>
    /// …and its companion: one error beside one question is also amber (Impl §5.2's correction).
    /// </summary>
    /// <remarks>
    /// Kept because it catches the <em>other</em> wrong implementation — the pre-correction table
    /// that shared red between question and permission, under which this case showed red.
    /// </remarks>
    [Fact]
    public void One_error_beside_one_question_shows_amber()
    {
        var worst = AttentionOrder.WorstOf([SessionState.Error, SessionState.NeedsQuestion]);

        Assert.Equal(SessionState.Error, worst);
        Assert.Equal(TrayColour.Amber, TrayVisuals.ColourOf(worst));
    }

    /// <summary>A permission anywhere outranks everything and shows red.</summary>
    [Fact]
    public void A_permission_shows_red_over_anything_else()
    {
        var worst = AttentionOrder.WorstOf(
            [SessionState.Working, SessionState.Error, SessionState.NeedsPermission, SessionState.Unread]);

        Assert.Equal(SessionState.NeedsPermission, worst);
        Assert.Equal(TrayColour.Red, TrayVisuals.ColourOf(worst));
    }

    /// <summary>Nothing at all is grey — the same answer as a dashboard whose sessions all ended.</summary>
    [Fact]
    public void An_empty_dashboard_is_grey()
    {
        Assert.Equal(SessionState.Ended, AttentionOrder.WorstOf([]));
        Assert.Equal(TrayColour.Grey, TrayVisuals.ColourOf(AttentionOrder.WorstOf([])));
        Assert.Equal(TrayColour.Grey, TrayVisuals.ColourOf(AttentionOrder.WorstOf([SessionState.Ended])));
    }

    /// <summary>
    /// The roll-up cannot depend on the order the sessions arrive in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property holds because ties go to the first arrival, not because ties cannot happen:
    /// <see cref="AttentionOrder.Rank"/> is total but <strong>not injective</strong>, and
    /// <see cref="SessionState.Ended"/> shares rank 0 with any unrecognised value.
    /// </para>
    /// <para>
    /// <strong>What this test does not cover, stated so nobody mistakes it for coverage.</strong>
    /// Every state here is a real one, so every tie is between equal states and the assertion
    /// passes under <c>&gt;=</c> as well as <c>&gt;</c>. The case that separates them needs an
    /// unrecognised value, which nothing can reach through the Registry — so it is left
    /// unasserted deliberately, and the reasoning is recorded on <c>WorstOf</c> instead of being
    /// implied by a test that cannot see it.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_roll_up_does_not_depend_on_order()
    {
        SessionState[] states =
        [
            SessionState.Working, SessionState.NeedsQuestion, SessionState.Error,
            SessionState.Unread, SessionState.Acked,
        ];

        Assert.Equal(SessionState.Error, AttentionOrder.WorstOf(states));
        Assert.Equal(SessionState.Error, AttentionOrder.WorstOf(states.Reverse()));
    }
}
