namespace ClaudeDashboard.Core;

/// <summary>
/// The names a read of a session's transcript found (issue #161): the newest name someone gave the session, and
/// the newest title Claude Code generated for it. Either is null when the read found none.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why the transcript.</strong> The hook's <c>session_title</c> carries only a given name: <c>/rename</c>,
/// <c>--name</c>, or the name a fork carries over. The generated title, the one in the terminal tab, is written
/// only to the transcript, as <c>{"type":"ai-title","aiTitle":"…"}</c>. A given name is there too, as
/// <c>{"type":"custom-title","customTitle":"…"}</c>, so a session whose only event since a restart is a
/// <c>Stop</c> or a <c>SessionEnd</c> gets its name back.
/// </para>
/// <para>
/// <strong>What the last read found, not what the file holds.</strong> A read that found nothing new gives
/// <see cref="None"/>, and the session keeps the name it has. The Registry holds the latch
/// (<see cref="SessionRegistry"/>); this value only carries one read's result to it.
/// </para>
/// <para>Text is data: both values are stored and shown, never run.</para>
/// </remarks>
/// <param name="Given">The newest <c>customTitle</c> the read found, or null.</param>
/// <param name="Generated">The newest <c>aiTitle</c> the read found, or null.</param>
public readonly record struct TranscriptNames(string? Given, string? Generated)
{
    /// <summary>No name was read.</summary>
    public static TranscriptNames None => default;
}
