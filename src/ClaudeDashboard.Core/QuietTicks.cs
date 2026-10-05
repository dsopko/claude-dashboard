using System.Collections.Immutable;
using ClaudeDashboard.Core.Events;

namespace ClaudeDashboard.Core;

/// <summary>
/// A watchdog tick that finds nothing makes no sound (T1.44, issue #56).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A tick is identified by structure, not by keywords.</strong> Every <c>Stop</c> lists the
/// session's scheduled jobs in <c>session_crons</c>, each with its exact prompt. A cron firing
/// arrives as a <c>UserPromptSubmit</c> whose prompt exactly equals one of the prompts the
/// session's previous Stop listed — 203 of 203 watchdog ticks on the operator's archive. A typed
/// prompt that happens to match a cron's text is not a tick unless that Stop listed the cron.
/// </para>
/// <para>
/// <strong>Quiet is the agent's claim, and the burden of proof is on it.</strong> A tick is quiet
/// when its Stop's reply, trimmed of surrounding whitespace, is exactly <see cref="Sentinel"/>. The
/// cron prompt opts in by asking for it. Anything else — other text, the sentinel with anything
/// around it, a Resurface, a Progress Update — beeps and displays as today. So the failure mode is
/// always one extra beep, never a silenced escalation. Counting tool calls was rejected by the
/// operator: a Resurface is terminal text with no tool call.
/// </para>
/// <para>
/// The reply is compared as data, and never interpreted. Measured on the operator's
/// archive (2026-09-27): no real reply had leading or trailing whitespace, and the one-word replies
/// ended in punctuation — so "WATCHDOG-QUIET." is not quiet, and the guide says so.
/// </para>
/// </remarks>
public static class QuietTicks
{
    /// <summary>The reply that marks a tick quiet. Opt-in, per cron.</summary>
    public const string Sentinel = "WATCHDOG-QUIET";

    /// <summary>
    /// The line a cron prompt carries to opt in — word for word what the user guide and the
    /// Execution Plan's watchdog instruction carry.
    /// </summary>
    public const string OptInLine =
        "If nothing is overdue and you took no action, reply with exactly WATCHDOG-QUIET with no punctuation, quotes or formatting, and nothing else.";

    /// <summary>Whether <paramref name="reply"/> is exactly the sentinel, after trimming surrounding whitespace.</summary>
    public static bool IsQuietReply(string? reply) =>
        reply is not null && string.Equals(reply.Trim(), Sentinel, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="prompt"/> is one of <paramref name="session"/>'s own scheduled jobs firing.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static bool IsTick(Session session, UserPromptSubmit prompt)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(prompt);

        return session.ScheduledPrompts.Contains(prompt.Prompt);
    }

    /// <summary>
    /// Whether <paramref name="stop"/> ends a tick of <paramref name="session"/>'s own scheduled job
    /// quietly, so the row goes back to what it showed before the tick.
    /// </summary>
    /// <remarks>The one rule the Registry applies and the decisions record reports.</remarks>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static bool IsQuiet(Session session, Stop stop)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(stop);

        return session.PreTick is not null && IsQuietReply(stop.LastAssistantMessage);
    }
}

/// <summary>
/// The prompts of a session's scheduled jobs, as its latest <c>Stop</c> listed them (T1.44).
/// </summary>
/// <remarks>
/// Prompt text, so it is kept only to be compared — ordinal, exact — and never printed: this type
/// exposes no string, and its <see cref="ToString"/> says how many there are and nothing more, so a
/// <see cref="Session"/> that carries it prints no cron's words. Value equality, so a Session still
/// compares by value.
/// </remarks>
public sealed class ScheduledPrompts : IEquatable<ScheduledPrompts>
{
    /// <summary>No scheduled jobs.</summary>
    public static readonly ScheduledPrompts None = new(ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal));

    private readonly ImmutableHashSet<string> _prompts;

    private ScheduledPrompts(ImmutableHashSet<string> prompts) => _prompts = prompts;

    /// <summary>How many there are.</summary>
    public int Count => _prompts.Count;

    /// <summary>The set a Stop listed.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="prompts"/> is null.</exception>
    public static ScheduledPrompts Of(IEnumerable<string> prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);

        var set = prompts.ToImmutableHashSet(StringComparer.Ordinal);

        return set.IsEmpty ? None : new ScheduledPrompts(set);
    }

    /// <summary>Whether <paramref name="prompt"/> is exactly one of them.</summary>
    public bool Contains(string prompt) => prompt is not null && _prompts.Contains(prompt);

    /// <inheritdoc/>
    public bool Equals(ScheduledPrompts? other) =>
        other is not null && (ReferenceEquals(this, other) || _prompts.SetEquals(other._prompts));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ScheduledPrompts);

    /// <inheritdoc/>
    public override int GetHashCode() => _prompts.Count;

    /// <summary>How many, and never what they say.</summary>
    public override string ToString() => $"{Count} scheduled prompt(s)";
}

/// <summary>
/// What a row showed before a scheduled job's tick began — what a quiet tick puts back (T1.44).
/// </summary>
/// <param name="State">The state before the tick.</param>
/// <param name="Latest">The exchange before the tick: "Claude answered" stays the real answer.</param>
/// <param name="EnteredAt">When that state was entered; restored, so the nudge ladder is not reset.</param>
/// <param name="ErrorKind">The error kind before the tick, if any.</param>
/// <param name="WaitingOn">What the session was waiting on before the tick, if it was Waiting.</param>
/// <param name="ClockAnchor">The row clock's anchor before the tick (T1.47); restored, so the row reads the same age.</param>
public sealed record TickSnapshot(
    SessionState State,
    Exchange Latest,
    DateTimeOffset EnteredAt,
    string? ErrorKind,
    WaitingTasks WaitingOn,
    DateTimeOffset? ClockAnchor = null);
