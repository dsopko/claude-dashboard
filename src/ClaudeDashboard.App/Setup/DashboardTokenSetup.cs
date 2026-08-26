using System.Security.Cryptography;
using ClaudeDashboard.App.Ingress;

namespace ClaudeDashboard.App.Setup;

/// <summary>What ensuring the ingress token did.</summary>
public enum TokenSetupOutcome
{
    /// <summary>A token was already set. Nothing was changed.</summary>
    AlreadySet = 1,

    /// <summary>None was set, so one was generated and written.</summary>
    Generated = 2,

    /// <summary>None was set and it could not be written. Ingress stays open.</summary>
    Failed = 3,
}

/// <summary>The outcome, and the reason when there was a problem.</summary>
public readonly record struct TokenSetupResult(TokenSetupOutcome Outcome, string? Problem = null);

/// <summary>
/// Generates the ingress token and puts it where Claude Code can find it (Impl §3.4, §10.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>User scope, not process scope.</strong> A variable set for this process reaches nothing
/// else; Claude Code runs in its own terminals and inherits the user environment. Impl §10.2 is
/// explicit about it, and getting it wrong produces a dashboard that looks configured and a token
/// that never arrives.
/// </para>
/// <para>
/// <strong>A terminal already open when it is set never sees it.</strong> Environment variables
/// are inherited at process creation, so every Claude Code session running at that moment carries
/// the old environment — no token — for the rest of its life. If ingress then has one configured,
/// it answers <c>401</c> to those sessions on every hook, which is issue #4's symptom arriving
/// from the opposite direction. That is a sequencing hazard for whoever applies this, not
/// something this code can fix, and it is why it is written as a step somebody chooses to run.
/// </para>
/// <para>
/// <strong>The reading and writing are parameters.</strong> Not because a test cannot set an
/// environment variable, but because setting one at User scope writes to the registry and outlives
/// the test run. A seam here keeps the decision testable without leaving anything behind on the
/// machine that ran it.
/// </para>
/// </remarks>
public static class DashboardTokenSetup
{
    /// <summary>How many random bytes a generated token carries.</summary>
    /// <remarks>
    /// 32 bytes. The threat model is other local processes (Impl §3.4), against which the token is
    /// a shared secret rather than a password, so what matters is that it cannot be guessed and
    /// that it survives being pasted into a settings file. Base64url keeps it to one word with no
    /// character a shell or a JSON string will argue with.
    /// </remarks>
    public const int TokenBytes = 32;

    /// <summary>Generates a token. Never returns the same value twice.</summary>
    public static string Generate() =>
        System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>
    /// Ensures a token exists, generating one only when there is none.
    /// </summary>
    /// <param name="read">Reads the current value at the scope being ensured.</param>
    /// <param name="write">Writes a new value at that scope.</param>
    /// <remarks>
    /// <strong>An existing token is never replaced.</strong> Rotating it would silently break every
    /// Claude Code session already carrying the old one, and the operator may have set it
    /// deliberately. Absent means absent: a blank value is treated as no token, matching
    /// <see cref="IngressToken"/>, so the two cannot disagree about whether one is configured.
    /// </remarks>
    public static TokenSetupResult Ensure(Func<string?> read, Action<string> write)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        string? existing;

        try
        {
            existing = read();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return new TokenSetupResult(TokenSetupOutcome.Failed, ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(existing))
        {
            return new TokenSetupResult(TokenSetupOutcome.AlreadySet);
        }

        try
        {
            write(Generate());

            return new TokenSetupResult(TokenSetupOutcome.Generated);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Ingress fails open without a token (Impl §3.4), so this is a downgrade rather than a
            // failure to start — but it is a downgrade the operator should be told about, because
            // a dashboard that silently accepts unauthenticated posts is not what they asked for.
            return new TokenSetupResult(TokenSetupOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Reads the token from the user environment.</summary>
    public static string? ReadUserScope() =>
        Environment.GetEnvironmentVariable(IngressToken.EnvironmentVariable, EnvironmentVariableTarget.User);

    /// <summary>Writes the token to the user environment.</summary>
    /// <remarks>
    /// Deliberately not called anywhere yet. Applying it is a step the operator takes when their
    /// sessions have turned over — see this type's remarks for why doing it under them is worse
    /// than not doing it at all.
    /// </remarks>
    public static void WriteUserScope(string token) =>
        Environment.SetEnvironmentVariable(
            IngressToken.EnvironmentVariable,
            token,
            EnvironmentVariableTarget.User);
}
