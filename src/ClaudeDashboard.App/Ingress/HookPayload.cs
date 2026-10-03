using System.Text.Json;
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
    /// <c>Stop</c>: the background tasks still running as the turn ended, raw (T1.41, issue #52).
    /// </summary>
    /// <remarks>
    /// A <see cref="JsonElement"/>, not a typed list, so that no shape it arrives in can fail the
    /// whole payload: a malformed list must leave the Stop meaning what it did before T1.41, not
    /// lose it. <see cref="BackgroundTaskReader"/> reads it, leniently, and never reads an
    /// entry's <c>command</c>.
    /// </remarks>
    [JsonPropertyName("background_tasks")]
    public JsonElement? BackgroundTasks { get; init; }

    /// <summary>
    /// <c>Stop</c>: the session's scheduled jobs, raw (T1.44, issue #56). Read for each entry's
    /// <c>prompt</c> only, by <see cref="SessionCronReader"/>, leniently: a malformed list reads as none.
    /// </summary>
    [JsonPropertyName("session_crons")]
    public JsonElement? SessionCrons { get; init; }

    /// <summary>
    /// <c>Notification</c>: which notification this is — <c>permission_prompt</c>,
    /// <c>idle_prompt</c>, <c>agent_needs_input</c>, <c>agent_completed</c>.
    /// </summary>
    [JsonPropertyName("notification_type")]
    public string? NotificationType { get; init; }

    /// <summary>
    /// <c>StopFailure</c>: the failure kind — <c>rate_limit</c>, <c>server_error</c>, … — as the
    /// wire sends it (T1.53, issue #67).
    /// </summary>
    /// <remarks>
    /// The documentation names this field <c>error_type</c>; Claude Code sends <c>error</c>. All 18
    /// archived <c>StopFailure</c> events carry <c>error</c>, and none carries <c>error_type</c>
    /// (hooks reference, discrepancy 4). Where the two disagree, the wire is the authority.
    /// <para>
    /// A <see cref="JsonElement"/>, not a string, so that no shape it arrives in can fail the
    /// whole payload: an <c>error</c> that is not a string is passed over for the next name, and
    /// the <c>StopFailure</c> still arrives. <see cref="HookEventMapper"/> reads a string only.
    /// </para>
    /// </remarks>
    [JsonPropertyName("error")]
    public JsonElement? Error { get; init; }

    /// <summary>
    /// <c>StopFailure</c>: the failure kind under the name the documentation gives it. Read only
    /// when <see cref="Error"/> is absent, in case a later Claude Code follows its documentation.
    /// </summary>
    /// <remarks>
    /// <c>error_message</c>, documented beside it, is prose about the operator's turn. It is not
    /// bound here, so nothing can read, store, show or log it.
    /// </remarks>
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
