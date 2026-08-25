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

/// <summary>Where an acknowledgment came from (TS §IV.1, "Ack*").</summary>
public enum AckSource
{
    /// <summary>The operator clicked the row's acknowledge affordance (T1.12).</summary>
    Manual = 1,

    /// <summary>Focus inference observed the operator looking at the session (Phase 3).</summary>
    InferredFocus = 2,
}

/// <summary>
/// A synthetic acknowledgment — the operator has seen this session's result (TS §IV.1;
/// Impl §4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not a Claude Code hook.</strong> Every other variant here is normalized from a
/// hook payload; this one originates inside the dashboard, from the acknowledge affordance
/// (T1.12) or from focus inference (Phase 3). It is an <see cref="InboundEvent"/> because
/// Impl §4 routes synthetic acks through the <em>same</em> channel as hook events, so that
/// every ack source travels one path (TS §I.3) and the Registry stays single-writer.
/// </para>
/// <para>
/// TS §IV.1's third ack source — a new <c>UserPromptSubmit</c> in the session — does
/// <em>not</em> produce one of these. That auto-ack is folded into the
/// <see cref="UserPromptSubmit"/> transition itself, because the prompt is the proof the
/// operator saw the previous result.
/// </para>
/// <para>
/// <strong>Ingress must never map a wire payload to this variant.</strong>
/// <see cref="HookEventName"/> is a local discriminator, not a value Claude Code sends; a
/// hook that could name it would let anything able to reach the loopback endpoint forge an
/// acknowledgment and silence a session that genuinely needs the operator.
/// </para>
/// </remarks>
public sealed record Ack : InboundEvent
{
    /// <inheritdoc/>
    public override string HookEventName => "Ack";

    /// <summary>Which acknowledgment source raised this.</summary>
    public required AckSource Source { get; init; }
}

/// <summary>Which global sound mode the operator asked for (Impl §5.2).</summary>
public enum SoundCommandKind
{
    /// <summary>Silence everything, optionally until an expiry. The glyph stays truthful.</summary>
    MuteAll = 1,

    /// <summary>Let everything be heard again.</summary>
    UnmuteAll = 2,

    /// <summary>Go off duty: silence everything, and grey the glyph, until the operator resumes.</summary>
    PauseMonitoring = 3,

    /// <summary>Come back on duty.</summary>
    ResumeMonitoring = 4,
}

/// <summary>
/// The operator changed a global sound mode from the tray menu (Impl §5.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is an event rather than a method call.</strong> The tray menu runs on the
/// Dispatcher, and <see cref="SoundPolicyEngine"/>'s mutators enter the single-writer region.
/// T1.2b made that region mutual exclusion rather than thread affinity, so calling a mutator
/// straight from a click <em>succeeds</em> whenever the consumer happens to be idle and throws
/// only when the two overlap — which is to say it passes in testing and fails in front of the
/// operator. Sending it down the Channel puts the change on the consumer thread with everything
/// else, in order.
/// </para>
/// <para>
/// That is a preference about <em>ordering</em>, not a race being fixed: the guard would catch
/// an overlap loudly if one happened. The stronger reason is T1.7's — the dispatcher exception
/// handler marks every fault handled on the premise that the domain never mutates on the
/// Dispatcher, so a mutation there would quietly undermine a decision taken elsewhere.
/// </para>
/// <para>
/// <strong>It names no session.</strong> These are global, so <see cref="InboundEvent.SessionId"/>
/// is left default and reads <see cref="SessionId.IsEmpty"/>. The Registry never sees one of
/// these — the consumer routes it to the sound engine — so no session state is implied by the
/// gap.
/// </para>
/// </remarks>
public sealed record SoundCommand : InboundEvent
{
    /// <inheritdoc/>
    public override string HookEventName => "SoundCommand";

    /// <summary>Which mode the operator asked for.</summary>
    public required SoundCommandKind Kind { get; init; }

    /// <summary>
    /// When a <see cref="SoundCommandKind.MuteAll"/> lapses; null mutes with no expiry. Ignored
    /// by every other kind.
    /// </summary>
    public DateTimeOffset? Until { get; init; }
}

/// <summary>
/// A batch of tool calls finished and the agent is about to make its next model call — so the
/// turn is <em>running</em> (TS §IV.1, addition of 2026-08-25; Impl §9.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this event exists at all.</strong> <see cref="SessionState.Working"/> was only
/// ever entered from <see cref="UserPromptSubmit"/>, so once a session left it for a Needs-You
/// state the only road back was the turn ending. The operator answered a permission, Claude
/// carried on, and the row stayed red at the top of Needs You for the rest of the turn —
/// claiming to be blocked on someone who had already unblocked it (issue #2).
/// </para>
/// <para>
/// <strong>Resumption is inferred, because nothing announces it.</strong> There is no
/// <c>PermissionGranted</c> hook: <c>PermissionRequest</c> fires when a decision is needed and
/// <c>PermissionDenied</c> when auto mode denies one, and approval fires nothing at all. So the
/// evidence has to be the session doing work again, and this is that — the agent is between
/// model calls, therefore executing.
/// </para>
/// <para>
/// <strong>Once per batch, not once per tool</strong>, which is what makes it affordable at
/// fifteen concurrent sessions; <c>PostToolUse</c> would carry more traffic than every other
/// hook combined. And it is deliberately the general fix rather than a permission-specific one:
/// it recovers a resolved question and an errored turn that retries, both of which a
/// permission-shaped signal would have missed.
/// </para>
/// <para>
/// <strong>It carries none of the batch's payload.</strong> <c>tool_calls</c> and
/// <c>batch_id</c> are deliberately not read — the common fields carry everything this needs,
/// and <c>tool_input</c> in particular is user content that has no business in the domain or in
/// a log. The event's whole meaning is that it happened.
/// </para>
/// </remarks>
public sealed record PostToolBatch : InboundEvent
{
    /// <inheritdoc/>
    public override string HookEventName => "PostToolBatch";
}
