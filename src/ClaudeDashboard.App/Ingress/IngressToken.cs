using System.Security.Cryptography;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// The shared secret guarding ingress (Impl §3.4), made fresh at every start and handed to the
/// hook through <c>listening.txt</c> (T1.48, issue #57).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Always on, and never from the environment.</strong> A Claude Code session gets its
/// environment once, when it starts. While the token lived in <see cref="RetiredEnvironmentVariable"/>,
/// every session started before the token was set — or before it changed — sent none or the wrong
/// one, <c>/hook</c> refused it, and the script threw the answer away: a session that worked in its
/// terminal and never appeared on the dashboard. <c>listening.txt</c> is read fresh at every event,
/// so the token now travels there, beside the port, and no session ever holds a copy (the operator's
/// ruling of 2026-09-30).
/// </para>
/// <para>
/// <strong>A leaked token lives only until the next start.</strong> Each start makes a new one, and
/// the file that carries it is deleted on the way out. A crash dump, or a file a hard kill left
/// behind, holds a token that no running dashboard accepts.
/// </para>
/// <para>
/// <strong>Never logged, never shown.</strong> The value is reachable only through
/// <see cref="Reveal"/>, which is internal and has one caller in the product:
/// <c>ListeningFile.Write</c>, which puts it in <c>listening.txt</c>. <see cref="ToString"/>
/// redacts, and there is no public property for Serilog's <c>{@}</c> to find.
/// </para>
/// </remarks>
public sealed class IngressToken
{
    /// <summary>
    /// The environment variable the token used to be read from. <strong>Retired and ignored</strong>
    /// (T1.48); named only so a start can say, once, that it is set and ignored.
    /// </summary>
    public const string RetiredEnvironmentVariable = "CLAUDE_DASHBOARD_TOKEN";

    /// <summary>The header the hook sends it in.</summary>
    public const string HeaderName = "X-Dashboard-Token";

    /// <summary>How many random bytes a token is made from.</summary>
    public const int TokenBytes = 32;

    /// <summary>
    /// How many characters a token is: 32 bytes in unpadded base64url. The hook script checks for
    /// exactly this many, so the two must not drift.
    /// </summary>
    public const int Length = 43;

    private readonly string _expected;

    /// <summary>Makes a new token for this run of the dashboard.</summary>
    public IngressToken()
        : this(Generate())
    {
    }

    /// <summary>Uses <paramref name="expected"/> as the token. For tests.</summary>
    /// <exception cref="ArgumentException"><paramref name="expected"/> is null or blank.</exception>
    public IngressToken(string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new ArgumentException("A token must have a value; ingress has no unauthenticated mode.", nameof(expected));
        }

        _expected = expected;
    }

    /// <summary>
    /// A new token: <see cref="TokenBytes"/> random bytes, unpadded base64url, so every character is
    /// one of <c>A–Z a–z 0–9 - _</c> — the set the hook script accepts and nothing else.
    /// </summary>
    public static string Generate() =>
        System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>Whether <paramref name="presented"/> is this token.</summary>
    /// <remarks>
    /// Compared with <see cref="StringComparison.Ordinal"/> over the whole string. This is not
    /// a constant-time comparison and does not need to be: the attacker model here is another
    /// process on the same machine, which can read <c>listening.txt</c> far faster than it could
    /// time a loopback socket.
    /// </remarks>
    public bool Accepts(string? presented) =>
        string.Equals(presented, _expected, StringComparison.Ordinal);

    /// <summary>
    /// The token itself, for <c>listening.txt</c>. <strong>The only way to the value.</strong>
    /// </summary>
    /// <remarks>
    /// Internal and a method, for the reason <c>PayloadJson.Reveal</c> is a method: Serilog's
    /// <c>{@}</c> reflects over properties, and a value that must never reach a log should not be
    /// one.
    /// </remarks>
    internal string Reveal() => _expected;

    /// <summary>Never the token.</summary>
    public override string ToString() => "<ingress token>";
}
