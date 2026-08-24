using System.Text.Json.Serialization;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// A Claude Code hook body, exactly as Impl §9.1 names its fields.
/// </summary>
/// <remarks>
/// <para>
/// Every field is optional, deliberately. This is the shape of something another program sends
/// us, and ingress is a pure observer (Impl §3.3): a payload missing a field it "should" have
/// must produce a logged drop, never an exception on the request thread and never a non-200.
/// Deserialization here decides nothing — <see cref="HookEventMapper"/> decides.
/// </para>
/// <para>
/// <strong>Every string on this type is data.</strong> The prompt and the assistant's answer
/// are carried verbatim and are never parsed, interpreted, or executed (Impl §3.4; TS §II.5).
/// Nothing downstream does either — they are stored and rendered as text.
/// </para>
/// </remarks>
public sealed record HookPayload
{
    /// <summary>The wire discriminator. Checked against <see cref="HookEventNames.Accepted"/>.</summary>
    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; init; }

    /// <summary>The Registry key (TS §II.3). Without it the event cannot be filed against a session.</summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    /// <summary>Correlates a prompt with its outcome (TS §II.3).</summary>
    [JsonPropertyName("prompt_id")]
    public string? PromptId { get; init; }

    /// <summary>Fallback only — written asynchronously and can lag the live turn (Impl §9.1).</summary>
    [JsonPropertyName("transcript_path")]
    public string? TranscriptPath { get; init; }

    /// <summary>The session's working directory; the Phase 1 grouping key (TS §IV.3).</summary>
    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    /// <summary><c>SessionStart</c>: <c>startup</c>, <c>resume</c>, <c>fork</c>, … (Impl §9.1).</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary><c>SessionStart</c>: the session's title, if it has one.</summary>
    [JsonPropertyName("session_title")]
    public string? SessionTitle { get; init; }

    /// <summary><c>UserPromptSubmit</c>: the submitted text, verbatim.</summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    /// <summary><c>Stop</c>: the final assistant message, inline (Impl §9.1).</summary>
    [JsonPropertyName("last_assistant_message")]
    public string? LastAssistantMessage { get; init; }

    /// <summary>
    /// <c>Notification</c>: which notification this is — <c>permission_prompt</c>,
    /// <c>idle_prompt</c>, <c>agent_needs_input</c>, <c>agent_completed</c>.
    /// </summary>
    [JsonPropertyName("notification_type")]
    public string? NotificationType { get; init; }

    /// <summary><c>StopFailure</c>: the failure kind — <c>rate_limit</c>, <c>overloaded</c>, … .</summary>
    [JsonPropertyName("error_type")]
    public string? ErrorType { get; init; }

    /// <summary><c>SessionEnd</c>: why it ended — <c>clear</c>, <c>logout</c>, … .</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// A matcher value some payloads carry generically rather than under a per-event name.
    /// </summary>
    /// <remarks>
    /// Impl §9.1 describes the <c>Notification</c>, <c>StopFailure</c> and <c>SessionEnd</c>
    /// discriminators as "from the matcher" without naming a JSON field for each, so ingress
    /// accepts a generic <c>matcher</c> alongside the specific names and prefers the specific
    /// one. That is deliberate tolerance at a boundary whose real shape is not yet confirmed
    /// against live payloads — see the T1.8 status report.
    /// </remarks>
    [JsonPropertyName("matcher")]
    public string? Matcher { get; init; }
}
