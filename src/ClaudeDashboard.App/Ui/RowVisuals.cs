using System.Globalization;
using System.IO;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The colour a row reads as (mockups: the legend, and the <c>.r-*</c> classes).
/// </summary>
/// <remarks>
/// A coarsening of <see cref="SessionState"/> for the eye, not a second attention model. Note
/// that <see cref="SessionState.Error"/> is amber rather than red on purpose — the mockups'
/// legend spells out why: "a turn died" must read differently from "it's asking you", even
/// though both sit in the Needs-You band.
/// </remarks>
public enum Accent
{
    /// <summary>Quiet, ended, or anything unrecognised.</summary>
    Grey = 0,

    /// <summary>Blocked on the operator: a permission or a question.</summary>
    Red = 1,

    /// <summary>The turn died.</summary>
    Amber = 2,

    /// <summary>Finished and unseen.</summary>
    Green = 3,

    /// <summary>Claude is working.</summary>
    Blue = 4,
}

/// <summary>How a row says what it is and how long it has been that way (mockups: <c>.meta</c>).</summary>
public static class RowVisuals
{
    /// <summary>The colour <paramref name="state"/> reads as.</summary>
    public static Accent AccentOf(SessionState state) => state switch
    {
        SessionState.NeedsPermission or SessionState.NeedsQuestion => Accent.Red,
        SessionState.Error => Accent.Amber,
        SessionState.Unread => Accent.Green,
        SessionState.Working => Accent.Blue,
        _ => Accent.Grey,
    };

    /// <summary>
    /// The short name for a workspace path — the folder name, as a heading or a row tag.
    /// </summary>
    /// <remarks>
    /// Built from the path, never from a <see cref="GroupKey"/>: the key is an identity, with the
    /// path case-folded and the kind prefixed, so binding it would put
    /// <c>workspace:C:\DEV\PENNCUSTQUOTE</c> on screen.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="cwd"/> is null.</exception>
    public static string WorkspaceLabel(string cwd)
    {
        ArgumentNullException.ThrowIfNull(cwd);

        var name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? cwd : name;
    }

    /// <summary>The badge text, as the mockups spell it.</summary>
    public static string BadgeOf(SessionState state) => state switch
    {
        SessionState.NeedsPermission => "PERMISSION",
        SessionState.NeedsQuestion => "QUESTION",
        SessionState.Error => "ERROR",
        SessionState.Unread => "FINISHED",
        SessionState.Working => "WORKING",
        SessionState.Acked => "QUIET",

        // The operator asked for this word in issue #28. What the dashboard observed is silence;
        // see SessionState.Interrupted, which says so where anybody first meets the state.
        SessionState.Interrupted => "INTERRUPTED",
        SessionState.Ended => "ENDED",
        _ => "UNKNOWN",
    };

    /// <summary>
    /// A duration as the mockups write one: "48s", "9 min", "2h 05m".
    /// </summary>
    /// <remarks>
    /// Minutes are the working unit because that is the scale the operator thinks in — the nudge
    /// schedule is 2/5/10 minutes and a group goes stale at 15. Seconds appear only below a
    /// minute, where "0 min" would be worse than useless, and hours appear above ninety so a
    /// session left overnight does not read as "just" 640 min.
    /// </remarks>
    public static string Duration(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return string.Create(CultureInfo.CurrentCulture, $"{(int)age.TotalSeconds}s");
        }

        if (age < TimeSpan.FromMinutes(90))
        {
            return string.Create(CultureInfo.CurrentCulture, $"{(int)age.TotalMinutes} min");
        }

        return string.Create(CultureInfo.CurrentCulture, $"{(int)age.TotalHours}h {age.Minutes:00}m");
    }

    /// <summary>
    /// The age phrased for <paramref name="state"/> (mockups: "waiting 4 min", "2 min ago", "6 min").
    /// </summary>
    /// <remarks>
    /// <para>
    /// The phrasing is not decoration. "Waiting" says the agent is stopped and the clock is the
    /// operator's fault; "ago" says the work is done and the clock only measures how long it has
    /// gone unseen; a bare duration says the agent is busy and the clock is nobody's fault.
    /// </para>
    /// <para>
    /// <strong><see cref="SessionState.Interrupted"/> reads "ago", and the default would have got
    /// it wrong (issue #28).</strong> Falling through to a bare duration would have said the agent
    /// is busy — the exact claim the state exists to withdraw. "Waiting" was considered and
    /// rejected: it belongs to the Needs-You band, and this state deliberately does not escalate.
    /// So the row reads how long ago the session was last heard from, which is the only thing the
    /// dashboard actually knows about it.
    /// </para>
    /// </remarks>
    public static string Age(SessionState state, TimeSpan age) => state switch
    {
        SessionState.NeedsPermission or SessionState.NeedsQuestion =>
            string.Create(CultureInfo.CurrentCulture, $"waiting {Duration(age)}"),
        SessionState.Unread or SessionState.Acked or SessionState.Ended or SessionState.Interrupted =>
            string.Create(CultureInfo.CurrentCulture, $"{Duration(age)} ago"),
        _ => Duration(age),
    };
}
