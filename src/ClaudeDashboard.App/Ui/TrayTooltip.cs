using System.Globalization;
using System.Text;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The sentence under the tray glyph (Impl §5.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is where the coarsening is undone.</strong> The glyph merges
/// <see cref="SessionState.Error"/> and <see cref="SessionState.NeedsQuestion"/> onto amber, so
/// the tooltip is the only place the distinction survives — which is why it breaks the Needs-You
/// kinds out (<c>2 permissions · 1 error · 1 question</c>) instead of reusing the header's
/// "3 need you". Counting is Core's; this turns the counts into the sentence.
/// </para>
/// <para>
/// <strong>The mode leads.</strong> When the glyph is not telling the plain truth — paused, or
/// muted — the first words say why, because the operator's question at that moment is not "what
/// is happening" but "why is it grey" or "why is it silent".
/// </para>
/// <para>
/// <strong>"Last heard" is the last item, always</strong> (T1.61, issue #74; the operator's ruling of
/// 2026-10-03): when the dashboard last heard from Claude Code. It is information, never an alarm
/// (Design §3), so it never leads and never changes the glyph.
/// </para>
/// <para>
/// <strong>Windows cuts a tray tooltip at 127 characters</strong> (the notification area's tip holds
/// 128 characters with the terminator). Until T1.61 nothing here measured the text: it went whole to
/// the tray icon, and Windows cut whatever passed the limit, in the middle of a word if it fell
/// there. Now <see cref="MaxLength"/> is kept here: "last heard" goes first, whole, and then whole
/// items from the end, so no word is cut.
/// </para>
/// </remarks>
public static class TrayTooltip
{
    /// <summary>What is shown when nothing is worth reporting.</summary>
    public const string AllQuiet = "all quiet";

    /// <summary>What is shown while monitoring is off duty.</summary>
    public const string Paused = "paused · click to resume";

    /// <summary>The most characters Windows shows in a tray tooltip.</summary>
    public const int MaxLength = 127;

    /// <summary>Builds the tooltip.</summary>
    /// <param name="summary">The counts and the roll-up.</param>
    /// <param name="paused">Whether monitoring is off duty.</param>
    /// <param name="mutedUntil">
    /// When a global mute lapses, or null if nothing is globally muted.
    /// <see cref="DateTimeOffset.MaxValue"/> means a mute with no expiry.
    /// </param>
    /// <param name="now">
    /// The instant to measure the remaining mute against. Passed rather than read from a clock
    /// so the caller's "now" and the tooltip's agree — the countdown is recomputed on the tick,
    /// and a tooltip a second out of step with the tick it was built for would round differently
    /// on alternate ticks.
    /// </param>
    /// <param name="fault">
    /// Why the dashboard cannot hear anything, or null when it can (T1.15).
    /// </param>
    /// <param name="lastHeard">
    /// When the dashboard last heard from Claude Code, already phrased (<see cref="LastHeard"/>), or
    /// null for none. The last item, and the first left out when the text is too long.
    /// </param>
    public static string For(
        StatusSummary summary,
        bool paused = false,
        DateTimeOffset? mutedUntil = null,
        DateTimeOffset now = default,
        string? fault = null,
        string? lastHeard = null)
    {
        var rest = WithoutFault(summary, paused, mutedUntil, now);

        // The fault leads everything, including pause. Pause and mute are states the operator
        // chose seconds ago and already knows about; a dead ingress is one they cannot know
        // about, and every count behind it is a count of nothing rather than a count of zero.
        var text = string.IsNullOrEmpty(fault) ? rest : $"{fault} · {rest}";

        if (!string.IsNullOrEmpty(lastHeard) && text.Length + 3 + lastHeard.Length <= MaxLength)
        {
            return $"{text} · {lastHeard}";
        }

        return Fit(text);
    }

    /// <summary>
    /// "last heard from Claude Code 2 min ago", or "not heard from Claude Code since start" (T1.61).
    /// </summary>
    /// <param name="lastHeardAt">When the last real message was accepted, or null since start.</param>
    /// <param name="now">The tick's instant.</param>
    public static string LastHeard(DateTimeOffset? lastHeardAt, DateTimeOffset now)
    {
        if (lastHeardAt is not { } heard)
        {
            return "not heard from Claude Code since start";
        }

        var gone = now - heard;

        var ago = gone < TimeSpan.FromMinutes(1) ? "just now"
            : gone < TimeSpan.FromHours(1) ? string.Create(CultureInfo.CurrentCulture, $"{(int)gone.TotalMinutes} min ago")
            : gone < TimeSpan.FromDays(1) ? string.Create(CultureInfo.CurrentCulture, $"{(int)gone.TotalHours} h ago")
            : string.Create(CultureInfo.CurrentCulture, $"{(int)gone.TotalDays} d ago");

        return $"last heard from Claude Code {ago}";
    }

    /// <summary>Within <see cref="MaxLength"/>, by leaving out whole items from the end.</summary>
    private static string Fit(string text)
    {
        while (text.Length > MaxLength)
        {
            var cut = text.LastIndexOf(" · ", StringComparison.Ordinal);

            if (cut <= 0)
            {
                // One item longer than the limit: at the last space before it, never in a word.
                var space = text.LastIndexOf(' ', MaxLength);
                return space > 0 ? text[..space] : text[..MaxLength];
            }

            text = text[..cut];
        }

        return text;
    }

    /// <summary>The ordinary tooltip, before any fault is put in front of it.</summary>
    private static string WithoutFault(
        StatusSummary summary,
        bool paused,
        DateTimeOffset? mutedUntil,
        DateTimeOffset now)
    {
        var counts = Counts(summary);

        // Pause first: it outranks mute because it is the stronger statement, and because the
        // glyph is grey for exactly this reason. Saying "muted" while off duty would explain the
        // silence and leave the grey unexplained.
        if (paused)
        {
            return summary.IsAllQuiet ? Paused : $"{Paused} · {counts}";
        }

        if (mutedUntil is { } until)
        {
            return $"{MutedLead(until, now)} · {counts}";
        }

        return counts;
    }

    /// <summary>How the mute announces itself, with its remaining time when it has one.</summary>
    /// <remarks>
    /// Rounded <em>up</em>, so a mute with forty seconds left reads "muted 1 min" rather than
    /// "muted 0 min" — a countdown that reaches zero while still in force reads as a bug. It
    /// lapses by predicate, so the last minute simply stops being shown when it stops being true.
    /// </remarks>
    private static string MutedLead(DateTimeOffset until, DateTimeOffset now)
    {
        if (until == DateTimeOffset.MaxValue)
        {
            return "muted";
        }

        var remaining = until - now;

        if (remaining <= TimeSpan.Zero)
        {
            // The mute has lapsed but nothing has recomputed yet. Say the true thing.
            return "muted";
        }

        var minutes = (int)Math.Ceiling(remaining.TotalMinutes);

        return string.Create(CultureInfo.CurrentCulture, $"muted {minutes} min");
    }

    /// <summary>The counts, zeros omitted, or <see cref="AllQuiet"/> when there are none.</summary>
    private static string Counts(StatusSummary summary)
    {
        if (summary.IsAllQuiet)
        {
            return AllQuiet;
        }

        var parts = new StringBuilder();

        Append(parts, summary.Permissions, "permission", "permissions");
        Append(parts, summary.Errors, "error", "errors");
        Append(parts, summary.Questions, "question", "questions");
        Append(parts, summary.Unread, "unread", "unread");
        Append(parts, summary.Working, "working", "working");

        return parts.ToString();
    }

    /// <summary>Adds one count, unless it is zero.</summary>
    /// <remarks>
    /// <paramref name="one"/> and <paramref name="many"/> are the same word for "unread" and
    /// "working", which are adjectives here rather than nouns — "2 unread", not "2 unreads". They
    /// are still passed separately so the caller decides, rather than this guessing from a
    /// suffix.
    /// </remarks>
    private static void Append(StringBuilder parts, int count, string one, string many)
    {
        if (count == 0)
        {
            return;
        }

        if (parts.Length > 0)
        {
            parts.Append(" · ");
        }

        parts.Append(CultureInfo.CurrentCulture, $"{count} {(count == 1 ? one : many)}");
    }
}
