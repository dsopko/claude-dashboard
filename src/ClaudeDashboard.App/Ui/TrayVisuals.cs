using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The tray glyph's five colours, in <strong>severity order</strong>: a larger value is more
/// urgent (Impl §5.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a second enum on purpose, and its ordering is the reason.</strong> The row
/// LED's <see cref="Accent"/> numbers its members <c>Grey = 0, Red = 1, Amber = 2, Green = 3,
/// Blue = 4</c> — ascending runs <em>descending</em> in urgency, except Grey, which is the least
/// urgent of all and sorts first. Neither <c>&lt;</c> nor <c>&gt;</c> on <see cref="Accent"/> is
/// the tray's precedence, so anything comparing those values is either inverted or accidentally
/// right on some rows and not others. Here the numbers <em>are</em> the precedence, declared
/// once, and everything that needs to compare tray colours compares these.
/// </para>
/// <para>
/// Declared here rather than restated wherever it is needed — including in tests. A second copy
/// of a severity order is the exact drift <see cref="AttentionOrder"/> exists to have ended, and
/// a copy living in a test file is worse than one in production code, because then it is the
/// oracle that drifts and nothing is left to notice.
/// </para>
/// </remarks>
public enum TrayColour
{
    /// <summary>All quiet: nothing wants the operator.</summary>
    Grey = 0,

    /// <summary>Something is working.</summary>
    Blue = 1,

    /// <summary>Something finished and has not been seen.</summary>
    Green = 2,

    /// <summary>Something died, or is asking a question.</summary>
    Amber = 3,

    /// <summary>Something is asking for permission — only the operator can give it.</summary>
    Red = 4,
}

/// <summary>
/// What the tray glyph shows for a given roll-up (Impl §5.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Derived from <see cref="AttentionOrder.Rank"/>, not from a second state table.</strong>
/// Impl §5.2 is explicit that the tray palette is deliberately not the row palette:
/// <see cref="RowVisuals.AccentOf"/> puts <see cref="SessionState.NeedsQuestion"/> on
/// <see cref="Accent.Red"/>, which is right for a row — the LED says what that one session is,
/// and red is what earns the blink — and wrong for a roll-up, because it is
/// <strong>not monotone in rank</strong>: Permission 6 → Red, Error 5 → Amber, Question 4 → Red.
/// A roll-up built on a non-monotone palette contradicts itself, and the case it gets wrong is
/// the one the operator ratified: a lone session asking a question would burn red in the tray
/// while the ranking says it is the least urgent thing that needs them.
/// </para>
/// <para>
/// Mapping rank thresholds instead makes this a genuine <em>coarsening</em> — five colours for
/// seven states, order preserved — which is a different kind of object from a second opinion
/// about severity. The property that tells them apart is monotonicity, and it is asserted.
/// </para>
/// <para>
/// The consequence is intended and visible: a lone question shows <strong>amber in the tray</strong>
/// and <strong>red, blinking, in its row</strong>. The tray triages — how urgently should I look?
/// The row diagnoses — what is it doing?
/// </para>
/// </remarks>
public static class TrayVisuals
{
    /// <summary>The glyph colour for a rolled-up state.</summary>
    /// <remarks>
    /// <para>
    /// Expressed as thresholds on <see cref="AttentionOrder.Rank"/> so that the mapping cannot
    /// disagree with the ranking about which of two states is worse. Every rank below
    /// <see cref="SessionState.Working"/>'s is grey, which is why an Ended session and an empty
    /// dashboard look the same and why neither can be told from a quiet one.
    /// </para>
    /// <para>
    /// <strong>THE THRESHOLDS ARE NAMED STATES, NOT LITERAL NUMBERS, AND THAT MATTERS (issue #28).</strong>
    /// They were literals — <c>&gt;= 6</c>, <c>&gt;= 4</c> — with the states they covered written
    /// beside them in comments. Adding <see cref="SessionState.Interrupted"/> renumbered the ranks
    /// by one, and every literal then pointed at the state above the one it named: Error would have
    /// read red, Unread amber, Working green, and an interrupted session <em>blue</em>. The comment
    /// naming each boundary stayed true while the number under it stopped being.
    /// </para>
    /// <para>
    /// Reading the boundary out of <see cref="AttentionOrder.Rank"/> keeps the coarsening exact
    /// under any future renumbering, and costs nothing: the ranks are compile-time constants in
    /// every sense that matters here.
    /// </para>
    /// </remarks>
    /// <param name="worst">The most severe state across the sessions being summarised.</param>
    public static TrayColour ColourOf(SessionState worst)
    {
        var rank = AttentionOrder.Rank(worst);

        return rank switch
        {
            _ when rank >= AttentionOrder.Rank(SessionState.NeedsPermission) => TrayColour.Red,

            // Error and NeedsQuestion merged onto one colour, per Impl §5.2.
            _ when rank >= AttentionOrder.Rank(SessionState.NeedsQuestion) => TrayColour.Amber,
            _ when rank >= AttentionOrder.Rank(SessionState.Unread) => TrayColour.Green,
            _ when rank >= AttentionOrder.Rank(SessionState.Working) => TrayColour.Blue,

            // Interrupted, Acked, Ended, and anything unrecognised.
            _ => TrayColour.Grey,
        };
    }
}
