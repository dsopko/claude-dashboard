using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.App.Ingress;

/// <summary>Why a payload produced no event.</summary>
public enum HookRejection
{
    /// <summary>It mapped. Not a rejection.</summary>
    None = 0,

    /// <summary>No <c>hook_event_name</c>, or one ingress does not accept.</summary>
    UnknownEvent = 1,

    /// <summary>No <c>session_id</c>; the event cannot be filed against a session.</summary>
    NoSessionId = 2,
}

/// <summary>What a payload mapped to, or why it did not.</summary>
/// <param name="Event">The normalized event, or null if the payload was rejected.</param>
/// <param name="Rejection">Why it was rejected, when it was.</param>
public readonly record struct HookMapping(InboundEvent? Event, HookRejection Rejection)
{
    /// <summary>True when the payload produced an event.</summary>
    public bool Mapped => Event is not null;
}

/// <summary>
/// Turns a hook body into an <see cref="InboundEvent"/> (Impl §3.2, §9.1).
/// </summary>
/// <remarks>
/// <para>
/// The dispatch is a <strong>total, explicit switch over the seven literal wire names</strong>
/// with a rejecting default — never a parse, a reflection lookup, or a name-to-type match. See
/// <see cref="HookEventNames"/> for why that distinction is a security property rather than a
/// style preference.
/// </para>
/// <para>
/// This is the only place a wire value becomes a domain value, and it decides nothing else. No
/// Registry, no state machine, no ordering: mapping only (Impl §3.2).
/// </para>
/// </remarks>
public sealed class HookEventMapper(IClock clock)
{
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Maps <paramref name="payload"/>, stamping the event with the instant it was received.
    /// </summary>
    /// <remarks>
    /// The timestamp is ingress's, from <see cref="IClock"/>, as T1.1 assumed: hook payloads
    /// carry no timestamp of their own, so this is the only point at which one can be taken.
    /// It gives arrival order rather than occurrence order, which is why the Registry's
    /// stale-drop guard cannot fire on hook events alone (T1.2).
    /// </remarks>
    /// <param name="payload">The deserialized hook body.</param>
    /// <param name="raw">
    /// The body exactly as it arrived, for the event archive (Impl Part 8). Default for callers
    /// that have no wire text — the mapping is unaffected, and only the archived row is poorer.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    public HookMapping Map(HookPayload payload, PayloadJson raw = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!HookEventNames.IsAccepted(payload.HookEventName))
        {
            return new HookMapping(null, HookRejection.UnknownEvent);
        }

        if (string.IsNullOrWhiteSpace(payload.SessionId))
        {
            return new HookMapping(null, HookRejection.NoSessionId);
        }

        var sessionId = new SessionId(payload.SessionId);
        var timestamp = _clock.Now;
        var cwd = payload.Cwd ?? string.Empty;

        // The common fields are repeated in each arm rather than applied afterwards with `with`,
        // because they are `required` and must be satisfied at construction. The repetition is
        // worth it: each arm now shows the whole event it produces.
        var promptId = payload.PromptId;
        var transcript = payload.TranscriptPath;

        InboundEvent mapped = payload.HookEventName switch
        {
            HookEventNames.SessionStart => new SessionStart
            {
                SessionId = sessionId, Timestamp = timestamp, Cwd = cwd,
                PromptId = promptId, TranscriptPath = transcript,
                Source = payload.Source ?? payload.Matcher,
                SessionTitle = payload.SessionTitle,
            },

            HookEventNames.UserPromptSubmit => new UserPromptSubmit
            {
                SessionId = sessionId, Timestamp = timestamp, Cwd = cwd,
                PromptId = promptId, TranscriptPath = transcript,
                Prompt = payload.Prompt ?? string.Empty,
            },

            HookEventNames.Notification => new Notification
            {
                SessionId = sessionId, Timestamp = timestamp, Cwd = cwd,
                PromptId = promptId, TranscriptPath = transcript,
                NotificationType = payload.NotificationType ?? payload.Matcher ?? string.Empty,
            },

            HookEventNames.Stop => new Stop
            {
                SessionId = sessionId, Timestamp = timestamp, Cwd = cwd,
                PromptId = promptId, TranscriptPath = transcript,
                LastAssistantMessage = payload.LastAssistantMessage,
            },

            HookEventNames.StopFailure => new StopFailure
            {
                SessionId = sessionId, Timestamp = timestamp, Cwd = cwd,
                PromptId = promptId, TranscriptPath = transcript,
                ErrorKind = payload.ErrorType ?? payload.Matcher ?? string.Empty,
            },

            HookEventNames.SessionEnd => new SessionEnd
            {
                SessionId = sessionId, Timestamp = timestamp, Cwd = cwd,
                PromptId = promptId, TranscriptPath = transcript,
                Reason = payload.Reason ?? payload.Matcher ?? string.Empty,
            },

            HookEventNames.CwdChanged => new CwdChanged
            {
                SessionId = sessionId, Timestamp = timestamp, Cwd = cwd,
                PromptId = promptId, TranscriptPath = transcript,
            },

            // Nothing batch-specific is read. tool_calls and batch_id are deliberately left on
            // the wire — the event's whole meaning is that it happened, and tool_input is user
            // content that has no business in the domain (issue #2).
            HookEventNames.PostToolBatch => new PostToolBatch
            {
                SessionId = sessionId, Timestamp = timestamp, Cwd = cwd,
                PromptId = promptId, TranscriptPath = transcript,
            },

            // Unreachable: the allow-list above is the same eight names. Present so that adding
            // a name to one and not the other is a loud failure rather than a silently
            // unmapped event.
            _ => throw new InvalidOperationException(
                $"'{payload.HookEventName}' is accepted but unmapped; HookEventNames and HookEventMapper disagree."),
        };

        // Attached once, here, rather than repeated in nine arms. The archive wants the body as it
        // arrived — not a re-serialization of the fields above, which would silently be missing
        // every field Phase 1 does not map and would not be found wanting until Phase 5.
        return new HookMapping(mapped with { Payload = raw }, HookRejection.None);
    }
}
