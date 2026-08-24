using System.Diagnostics.CodeAnalysis;

namespace ClaudeDashboard.Core.Events;

/// <summary>
/// <c>SessionStart</c> — a session begins or resumes; create or refresh a Registry entry.
/// Matchers: <c>startup</c>, <c>resume</c>, <c>fork</c> (Impl §9.1).
/// </summary>
public sealed record SessionStart : InboundEvent
{
    /// <inheritdoc/>
    public override string HookEventName => "SessionStart";

    /// <summary>The raw <c>source</c> matcher, verbatim. Null when the payload carried none.</summary>
    public string? Source { get; init; }

    /// <summary>The parsed <see cref="Source"/>, or <see cref="SessionStartSource.Unknown"/>.</summary>
    public SessionStartSource ParsedSource => SessionStartSources.Parse(Source);

    /// <summary>
    /// Claude Code's <c>session_title</c>, or null. Display text — data, never instruction.
    /// </summary>
    public string? SessionTitle { get; init; }
}

/// <summary>
/// <c>UserPromptSubmit</c> — the operator submitted a prompt. Takes no matcher; always fires
/// (Impl §9.1). Drives → <see cref="SessionState.Working"/> and the auto-ack of any prior
/// Unread/Needs-You state, both of which are T1.2's.
/// </summary>
public sealed record UserPromptSubmit : InboundEvent
{
    private readonly string _prompt = string.Empty;

    /// <inheritdoc/>
    public override string HookEventName => "UserPromptSubmit";

    /// <summary>
    /// The submitted text (<c>prompt</c>), verbatim — the session's context line. This
    /// arriving inline is why a row identifies itself without scrollback scraping (TS §II.2).
    /// May be empty; never null.
    /// </summary>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public required string Prompt
    {
        get => _prompt;
        init => _prompt = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>
/// <c>Notification</c> — Claude wants the operator. Matchers: <c>permission_prompt</c>,
/// <c>idle_prompt</c>, <c>agent_needs_input</c>, <c>agent_completed</c> (Impl §9.1).
/// Pure observation: this event carries no decision control (Impl §9.1 notes; §3.3).
/// </summary>
public sealed record Notification : InboundEvent
{
    private readonly string _notificationType = string.Empty;

    /// <inheritdoc/>
    public override string HookEventName => "Notification";

    /// <summary>The raw notification type matcher, verbatim. May be empty; never null.</summary>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public required string NotificationType
    {
        get => _notificationType;
        init => _notificationType = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The parsed <see cref="NotificationType"/>, or <see cref="NotificationKind.Unknown"/>.</summary>
    public NotificationKind Kind => NotificationKinds.Parse(NotificationType);
}

/// <summary>
/// <c>Stop</c> — Claude finished responding. Takes no matcher; always fires (Impl §9.1).
/// Drives → <see cref="SessionState.Unread"/>, which is T1.2's.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification =
        "The variant names mirror Claude Code's hook_event_name values one-for-one, which is " +
        "what lets ingress dispatch on the wire discriminator and keeps the hierarchy readable " +
        "against Impl §9.1. 'Stop' collides with a Visual Basic keyword, but this is an internal " +
        "domain assembly with no cross-language consumers, so the collision costs nothing and " +
        "renaming would break the mapping the spec is written in.")]
public sealed record Stop : InboundEvent
{
    /// <inheritdoc/>
    public override string HookEventName => "Stop";

    /// <summary>
    /// The final answer text (<c>last_assistant_message</c>), verbatim, or null when the
    /// payload omitted it. Preferred over the transcript, which lags the live turn
    /// (TS §II.2; Impl §9.1) — this arriving inline is why an expanded row can show the
    /// answer beside the question.
    /// </summary>
    public string? LastAssistantMessage { get; init; }
}

/// <summary>
/// <c>StopFailure</c> — the turn died on an error. Matchers: <c>rate_limit</c>,
/// <c>overloaded</c>, <c>authentication_failed</c>, … (Impl §9.1 — an open-ended list).
/// Drives → <see cref="SessionState.Error"/>, which is T1.2's.
/// </summary>
public sealed record StopFailure : InboundEvent
{
    private readonly string _errorKind = string.Empty;

    /// <inheritdoc/>
    public override string HookEventName => "StopFailure";

    /// <summary>
    /// The raw error type matcher, verbatim. Preserved as a string because Impl §9.1's list is
    /// open-ended: an unrecognized kind must still reach the operator intact. May be empty;
    /// never null.
    /// </summary>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public required string ErrorKind
    {
        get => _errorKind;
        init => _errorKind = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The parsed <see cref="ErrorKind"/>, or <see cref="StopFailureKind.Unknown"/>.</summary>
    public StopFailureKind Kind => StopFailureKinds.Parse(ErrorKind);
}

/// <summary>
/// <c>SessionEnd</c> — the session terminated. Matchers: <c>clear</c>, <c>resume</c>,
/// <c>logout</c>, <c>prompt_input_exit</c>, <c>other</c> (Impl §9.1). Drives →
/// <see cref="SessionState.Ended"/> and scheduled removal, both T1.2's.
/// </summary>
public sealed record SessionEnd : InboundEvent
{
    private readonly string _reason = string.Empty;

    /// <inheritdoc/>
    public override string HookEventName => "SessionEnd";

    /// <summary>The raw end reason matcher, verbatim. May be empty; never null.</summary>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public required string Reason
    {
        get => _reason;
        init => _reason = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The parsed <see cref="Reason"/>, or <see cref="SessionEndReason.Unknown"/>.</summary>
    public SessionEndReason ParsedReason => SessionEndReasons.Parse(Reason);
}

/// <summary>
/// <c>CwdChanged</c> — the session's working directory moved, so its group is re-derived
/// (TS §IV.3; Impl §9.1, where it is marked optional). The new directory is
/// <see cref="InboundEvent.Cwd"/>; re-deriving the group is T1.4's.
/// </summary>
public sealed record CwdChanged : InboundEvent
{
    /// <inheritdoc/>
    public override string HookEventName => "CwdChanged";
}
