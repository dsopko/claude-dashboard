namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// The <c>hook_event_name</c> values ingress will accept — an allow-list, not a lookup.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because <c>InboundEvent</c> has an eighth variant that no hook sends.</strong>
/// T1.2 added <c>Ack</c>, whose discriminator is the local string <c>"Ack"</c>, because Impl §4
/// requires synthetic acknowledgments to travel the same channel as hook events. If ingress
/// dispatched by name — <c>Enum.Parse</c>, reflection, a type lookup — then anything able to
/// reach the endpoint could post <c>{"hook_event_name":"Ack"}</c> and <strong>forge an
/// acknowledgment</strong>, marking a session as seen when the operator has not seen it. The
/// session goes quiet and stays quiet.
/// </para>
/// <para>
/// The calibration is worth stating so the guard is not mistaken for more than it is: the
/// endpoint is loopback-bound and token-guarded, so anyone able to post a forged <c>Ack</c>
/// could equally post a forged <c>Stop</c>. <c>Ack</c> is the one worth naming because forging
/// it fails <em>silently</em> — a session that needs the operator simply falls quiet — whereas
/// a forged <c>Stop</c> announces itself with a chime and a row that moves. The guard's real
/// job is closing the gap between "what the domain can represent" and "what the wire may say".
/// </para>
/// <para>
/// Putting <c>Ack</c> on its own channel was considered and rejected: Impl §4 and TS §I.3 both
/// require every ack source to travel one path, and a second channel would reintroduce the
/// multiple-writer problem the single-writer invariant exists to prevent.
/// </para>
/// </remarks>
public static class HookEventNames
{
    /// <summary>A session began or resumed.</summary>
    public const string SessionStart = "SessionStart";

    /// <summary>The operator submitted a prompt.</summary>
    public const string UserPromptSubmit = "UserPromptSubmit";

    /// <summary>Claude wants the operator.</summary>
    public const string Notification = "Notification";

    /// <summary>Claude finished responding.</summary>
    public const string Stop = "Stop";

    /// <summary>The turn died on an error.</summary>
    public const string StopFailure = "StopFailure";

    /// <summary>The session terminated.</summary>
    public const string SessionEnd = "SessionEnd";

    /// <summary>The working directory moved.</summary>
    public const string CwdChanged = "CwdChanged";

    /// <summary>A batch of tool calls resolved — the turn is running (TS §IV.1; issue #2).</summary>
    public const string PostToolBatch = "PostToolBatch";

    /// <summary>
    /// Every accepted name. Anything else — including <c>Ack</c> — is refused before it can
    /// reach the pipeline.
    /// </summary>
    public static IReadOnlySet<string> Accepted { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        SessionStart,
        UserPromptSubmit,
        Notification,
        Stop,
        StopFailure,
        SessionEnd,
        CwdChanged,
        PostToolBatch,
    };

    /// <summary>Whether ingress will map <paramref name="hookEventName"/> at all.</summary>
    public static bool IsAccepted(string? hookEventName) =>
        hookEventName is not null && Accepted.Contains(hookEventName);
}
