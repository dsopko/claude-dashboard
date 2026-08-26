using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>Events shaped the way ingress shapes them, for the archive's tests.</summary>
internal static class TestEvents
{
    public static readonly DateTimeOffset At = new(2026, 8, 26, 14, 30, 0, TimeSpan.Zero);

    /// <summary>An event carrying a raw body, as one off the wire does.</summary>
    public static UserPromptSubmit Hook(
        string rawBody,
        string sessionId = "session-1",
        string cwd = @"C:\projects\thing") =>
        new()
        {
            SessionId = new SessionId(sessionId),
            Timestamp = At,
            Cwd = cwd,
            Prompt = "the prompt as the domain sees it",
            Payload = new PayloadJson(rawBody),
        };

    /// <summary>An event with no raw body — one the archive must decline.</summary>
    public static Ack Synthetic(string sessionId = "session-1") =>
        new()
        {
            SessionId = new SessionId(sessionId),
            Timestamp = At,
            Cwd = @"C:\projects\thing",
            Source = AckSource.Manual,
        };
}
