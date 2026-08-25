namespace ClaudeDashboard.Core.Events;

/// <summary>
/// The <c>SessionStart</c> matcher, i.e. why the session began (Impl §9.1).
/// </summary>
public enum SessionStartSource
{
    /// <summary>The payload carried a source this build does not recognize.</summary>
    Unknown = 0,

    /// <summary>A fresh session.</summary>
    Startup = 1,

    /// <summary>A resumed session — surfaces a pre-existing one (Impl §9.1).</summary>
    Resume = 2,

    /// <summary>A forked session — likewise pre-existing.</summary>
    Fork = 3,

    /// <summary>Restarted by <c>/clear</c>. Listed in TS §II.2 but not among Impl §9.1's matchers.</summary>
    Clear = 4,

    /// <summary>Restarted after a compaction. Listed in TS §II.2 but not among Impl §9.1's matchers.</summary>
    Compact = 5,
}

/// <summary>The <c>Notification</c> matcher (Impl §9.1).</summary>
public enum NotificationKind
{
    /// <summary>The payload carried a notification type this build does not recognize.</summary>
    Unknown = 0,

    /// <summary>A permission dialog is up — the session is blocked on approval.</summary>
    PermissionPrompt = 1,

    /// <summary>
    /// Nothing has happened in this session for a while. <strong>Changes no state</strong>
    /// (TS §II.2, corrected 2026-08-24 — issue #1).
    /// </summary>
    /// <remarks>
    /// This used to be read as a question, alongside <see cref="AgentNeedsInput"/>. It is the
    /// opposite: <c>agent_needs_input</c> is a request and this is the absence of one. Every
    /// session that finishes eventually sits idle, so treating it as a question turned every
    /// unread result red about ninety seconds after it arrived.
    /// </remarks>
    IdlePrompt = 2,

    /// <summary>An agent is blocked on an answer — the one notification that is a question.</summary>
    AgentNeedsInput = 3,

    /// <summary>A corroborating "finished" signal (Impl §9.1 marks it optional).</summary>
    AgentCompleted = 4,
}

/// <summary>
/// The <c>StopFailure</c> matcher — why the turn died (Impl §9.1).
/// </summary>
/// <remarks>
/// Impl §9.1's list ends in "…", so this enum is explicitly <em>not</em> exhaustive. The raw
/// matcher string is always preserved on the event and on <see cref="Session.ErrorKind"/>;
/// this is a convenience over it, never a replacement for it.
/// </remarks>
public enum StopFailureKind
{
    /// <summary>A failure kind this build does not recognize. The raw string still carries it.</summary>
    Unknown = 0,

    /// <summary>Rate limited.</summary>
    RateLimit = 1,

    /// <summary>The service was overloaded.</summary>
    Overloaded = 2,

    /// <summary>Authentication failed.</summary>
    AuthenticationFailed = 3,
}

/// <summary>The <c>SessionEnd</c> matcher — why the session terminated (Impl §9.1).</summary>
public enum SessionEndReason
{
    /// <summary>The payload carried a reason this build does not recognize.</summary>
    Unknown = 0,

    /// <summary>Ended by <c>/clear</c>.</summary>
    Clear = 1,

    /// <summary>Ended because the session was resumed elsewhere.</summary>
    Resume = 2,

    /// <summary>Ended by logout.</summary>
    Logout = 3,

    /// <summary>Ended by exiting the prompt.</summary>
    PromptInputExit = 4,

    /// <summary>Ended for a reason Claude Code reports as "other".</summary>
    Other = 5,
}

/// <summary>Wire values for <see cref="SessionStartSource"/>, exactly as Impl §9.1 and TS §II.2 spell them.</summary>
public static class SessionStartSources
{
    /// <summary>Maps a raw <c>source</c> to its enum, or <see cref="SessionStartSource.Unknown"/>.</summary>
    public static SessionStartSource Parse(string? value) => value switch
    {
        "startup" => SessionStartSource.Startup,
        "resume" => SessionStartSource.Resume,
        "fork" => SessionStartSource.Fork,
        "clear" => SessionStartSource.Clear,
        "compact" => SessionStartSource.Compact,
        _ => SessionStartSource.Unknown,
    };

    /// <summary>The wire spelling, or null for <see cref="SessionStartSource.Unknown"/>.</summary>
    public static string? ToWireValue(this SessionStartSource source) => source switch
    {
        SessionStartSource.Startup => "startup",
        SessionStartSource.Resume => "resume",
        SessionStartSource.Fork => "fork",
        SessionStartSource.Clear => "clear",
        SessionStartSource.Compact => "compact",
        _ => null,
    };

    /// <summary>
    /// True when the source means the session already existed, so the dashboard is
    /// surfacing a pre-existing session rather than creating one (Impl §9.1).
    /// </summary>
    public static bool IsPreExisting(this SessionStartSource source) =>
        source is SessionStartSource.Resume or SessionStartSource.Fork;
}

/// <summary>Wire values for <see cref="NotificationKind"/>, exactly as Impl §9.1 spells them.</summary>
public static class NotificationKinds
{
    /// <summary>Maps a raw notification type to its enum, or <see cref="NotificationKind.Unknown"/>.</summary>
    public static NotificationKind Parse(string? value) => value switch
    {
        "permission_prompt" => NotificationKind.PermissionPrompt,
        "idle_prompt" => NotificationKind.IdlePrompt,
        "agent_needs_input" => NotificationKind.AgentNeedsInput,
        "agent_completed" => NotificationKind.AgentCompleted,
        _ => NotificationKind.Unknown,
    };

    /// <summary>The wire spelling, or null for <see cref="NotificationKind.Unknown"/>.</summary>
    public static string? ToWireValue(this NotificationKind kind) => kind switch
    {
        NotificationKind.PermissionPrompt => "permission_prompt",
        NotificationKind.IdlePrompt => "idle_prompt",
        NotificationKind.AgentNeedsInput => "agent_needs_input",
        NotificationKind.AgentCompleted => "agent_completed",
        _ => null,
    };
}

/// <summary>Wire values for <see cref="StopFailureKind"/>, exactly as Impl §9.1 spells them.</summary>
public static class StopFailureKinds
{
    /// <summary>Maps a raw error type to its enum, or <see cref="StopFailureKind.Unknown"/>.</summary>
    public static StopFailureKind Parse(string? value) => value switch
    {
        "rate_limit" => StopFailureKind.RateLimit,
        "overloaded" => StopFailureKind.Overloaded,
        "authentication_failed" => StopFailureKind.AuthenticationFailed,
        _ => StopFailureKind.Unknown,
    };

    /// <summary>The wire spelling, or null for <see cref="StopFailureKind.Unknown"/>.</summary>
    public static string? ToWireValue(this StopFailureKind kind) => kind switch
    {
        StopFailureKind.RateLimit => "rate_limit",
        StopFailureKind.Overloaded => "overloaded",
        StopFailureKind.AuthenticationFailed => "authentication_failed",
        _ => null,
    };
}

/// <summary>Wire values for <see cref="SessionEndReason"/>, exactly as Impl §9.1 spells them.</summary>
public static class SessionEndReasons
{
    /// <summary>Maps a raw end reason to its enum, or <see cref="SessionEndReason.Unknown"/>.</summary>
    public static SessionEndReason Parse(string? value) => value switch
    {
        "clear" => SessionEndReason.Clear,
        "resume" => SessionEndReason.Resume,
        "logout" => SessionEndReason.Logout,
        "prompt_input_exit" => SessionEndReason.PromptInputExit,
        "other" => SessionEndReason.Other,
        _ => SessionEndReason.Unknown,
    };

    /// <summary>The wire spelling, or null for <see cref="SessionEndReason.Unknown"/>.</summary>
    public static string? ToWireValue(this SessionEndReason reason) => reason switch
    {
        SessionEndReason.Clear => "clear",
        SessionEndReason.Resume => "resume",
        SessionEndReason.Logout => "logout",
        SessionEndReason.PromptInputExit => "prompt_input_exit",
        SessionEndReason.Other => "other",
        _ => null,
    };
}
