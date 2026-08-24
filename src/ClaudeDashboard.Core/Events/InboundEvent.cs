namespace ClaudeDashboard.Core.Events;

/// <summary>
/// A Claude Code hook payload, normalized into the internal event the pipeline applies
/// (Impl §2.1, §3.2, §9.1). One derived record per consumed event.
/// </summary>
/// <remarks>
/// Every text field on every variant is <strong>data, never instruction</strong> (TS §II.5;
/// Execution Plan Part 1): stored and rendered verbatim, never executed or interpreted.
///
/// Delivery is at-least-once and can reorder (TS §I.2), which is why
/// <see cref="Timestamp"/> is required on every variant — the Registry's stale-drop guard
/// (T1.2) has nothing to compare against without it.
///
/// The hierarchy is deliberately closed: the constructor is private-protected, so every
/// variant lives in this assembly and an exhaustive <c>switch</c> over them is complete.
/// </remarks>
public abstract record InboundEvent
{
    private readonly string _cwd = string.Empty;

    private protected InboundEvent()
    {
    }

    /// <summary>
    /// The <c>hook_event_name</c> this variant was normalized from, spelled exactly as
    /// Claude Code sends it. This is the wire discriminator ingress dispatches on (T1.8).
    /// </summary>
    public abstract string HookEventName { get; }

    /// <summary>Claude Code's <c>session_id</c> — present on every event, and the Registry key (TS §II.3).</summary>
    public required SessionId SessionId { get; init; }

    /// <summary>
    /// When the event happened, for the Registry's timestamp guard (Impl §2.2). Supplied by
    /// ingress from <c>IClock</c> at receipt, since hook payloads carry no timestamp of their own.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The session's working directory (<c>cwd</c>), the Phase 1 grouping key (TS §IV.3).
    /// May be empty when a payload omitted it; never null.
    /// </summary>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public required string Cwd
    {
        get => _cwd;
        init => _cwd = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Claude Code's <c>prompt_id</c>, correlating a prompt with its outcome (TS §II.3), or
    /// null when the payload carried none.
    /// </summary>
    public string? PromptId { get; init; }

    /// <summary>
    /// Claude Code's <c>transcript_path</c>. <strong>Fallback only</strong>: it is written
    /// asynchronously and can lag the live turn, which is why the prompt and answer text are
    /// read inline from the events instead (TS §II.3; Impl §9.1).
    /// </summary>
    public string? TranscriptPath { get; init; }
}
