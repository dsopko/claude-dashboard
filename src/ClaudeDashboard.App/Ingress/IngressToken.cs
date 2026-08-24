namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// The optional shared secret guarding ingress (Impl §3.4).
/// </summary>
/// <remarks>
/// <para>
/// The token lives in an environment variable and <strong>never in a committed file</strong>
/// (Impl §3.4, §9.2). Claude Code delivers it via a hook <c>headers</c> entry naming
/// <c>CLAUDE_DASHBOARD_TOKEN</c> in <c>allowedEnvVars</c>.
/// </para>
/// <para>
/// Optional, as §3.4 says. With no token set, ingress accepts unauthenticated posts — the
/// endpoint is still loopback-bound, so the exposure is other processes on this machine, which
/// is precisely what the token exists to narrow. Configuring one is the operator's choice and
/// the first-run setup's job (T10.2), not something ingress can invent for itself.
/// </para>
/// </remarks>
public sealed class IngressToken
{
    /// <summary>The environment variable the token is read from.</summary>
    public const string EnvironmentVariable = "CLAUDE_DASHBOARD_TOKEN";

    /// <summary>The header Claude Code sends it in.</summary>
    public const string HeaderName = "X-Dashboard-Token";

    private readonly string? _expected;

    /// <summary>Reads the token from the environment.</summary>
    public IngressToken()
        : this(Environment.GetEnvironmentVariable(EnvironmentVariable))
    {
    }

    /// <summary>Uses <paramref name="expected"/> as the token; null or blank means no check.</summary>
    public IngressToken(string? expected) =>
        _expected = string.IsNullOrWhiteSpace(expected) ? null : expected;

    /// <summary>Whether a token is configured at all.</summary>
    public bool IsConfigured => _expected is not null;

    /// <summary>
    /// Whether <paramref name="presented"/> may pass.
    /// </summary>
    /// <remarks>
    /// Compared with <see cref="StringComparison.Ordinal"/> over the whole string. This is not
    /// a constant-time comparison and does not need to be: the attacker model here is another
    /// process on the same machine, which has faster ways to learn an environment variable than
    /// timing a loopback socket.
    /// </remarks>
    public bool Accepts(string? presented) =>
        _expected is null || string.Equals(presented, _expected, StringComparison.Ordinal);
}
