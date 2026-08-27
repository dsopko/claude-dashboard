using System.Globalization;
using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// Registers the dashboard's hooks while it is listening, and removes them when it stops
/// (Impl §9.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A hook exists only while something is answering the port it names.</strong> When
/// nothing is listening, Claude Code shows the user an error on every turn — there is no per-hook
/// suppression, only the global <c>disableAllHooks</c>, and the message names no cause beyond a
/// hook having thrown. That was confirmed against the published documentation on 2026-08-26: the
/// documented http-hook fields are <c>type</c>, <c>url</c>, <c>headers</c>, <c>allowedEnvVars</c>,
/// <c>timeout</c>, <c>statusMessage</c> and <c>if</c>, and there is no <c>optional</c>,
/// <c>quiet</c> or <c>continueOnError</c>. So a crashed dashboard would degrade Claude Code
/// itself, and the lifecycle is not one option among several — it is the only mechanism there is.
/// </para>
/// <para>
/// <strong>Nothing is registered unless ingress is actually bound.</strong> A registration names a
/// URL, and hook payloads carry the operator's prompts. If the configured port is held by
/// something that is not us, registering it would post their prompts to a stranger — so a
/// dashboard that cannot hear registers nothing, says so, and leaves whatever is on the port
/// alone.
/// </para>
/// </remarks>
public sealed class HookLifecycle
{
    private readonly ClaudeCodePaths _claude;
    private readonly DashboardPaths _paths;
    private readonly IngressStatus _ingress;
    private readonly IngressToken _token;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    /// <summary>Creates the lifecycle over Claude Code's settings file.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public HookLifecycle(
        ClaudeCodePaths claude,
        DashboardPaths paths,
        IngressStatus ingress,
        IngressToken token,
        IClock clock,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(claude);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(ingress);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _claude = claude;
        _paths = paths;
        _ingress = ingress;
        _token = token;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The hook URL for <paramref name="port"/>, exactly as Impl §9.2 spells it.</summary>
    public static string HookUrlFor(int port) =>
        string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/hook");

    /// <summary>Adds the handlers, and writes <c>port.txt</c>.</summary>
    /// <remarks>
    /// Called after the socket is bound, never before: a handler naming a port nothing answers is
    /// precisely the state this exists to avoid, and between registering and binding there would
    /// be a window of exactly that.
    /// </remarks>
    public SettingsWriteResult Register()
    {
        if (!_ingress.CanReceiveHooks)
        {
            _logger.Warning(
                "Not registering hooks with Claude Code: port {Port} is held by another process, so a " +
                "registration would send hook payloads — including prompts — to whatever holds it.",
                _ingress.Port);

            return new SettingsWriteResult(SettingsWriteOutcome.NothingToDo);
        }

        WritePortFile(_ingress.Port);

        var url = HookUrlFor(_ingress.Port);
        var writer = NewWriter();
        writer.SweepAbandonedTemporaries();

        var result = writer.Modify(
            settings => HookRegistration.Register(settings, url, TokenVariable()),
            _clock.Now);

        Report(result, "registered with", url);

        return result;
    }

    /// <summary>Removes the handlers. The allowlists stay (Impl §9.3).</summary>
    public SettingsWriteResult Unregister()
    {
        var url = HookUrlFor(_ingress.Port);

        var result = NewWriter().Modify(
            settings => HookRegistration.Unregister(settings, url),
            _clock.Now);

        Report(result, "removed from", url);

        return result;
    }

    /// <summary>
    /// The variable to interpolate a token from, or null when there is none to interpolate.
    /// </summary>
    /// <remarks>
    /// Writing the header when no token is configured would put a reference to an unset variable
    /// in the operator's file. Claude Code replaces that with an empty string, which ingress
    /// cannot tell from no header at all — so the file would claim a protection that is not there.
    /// </remarks>
    private string? TokenVariable() =>
        _token.IsConfigured ? IngressToken.EnvironmentVariable : null;

    private SettingsFileWriter NewWriter() => new(_claude.UserSettingsFile);

    /// <summary>Writes the bound port where a command-style hook could find it (Impl Part 8).</summary>
    /// <remarks>
    /// Never fatal. Nothing in Phase 1 reads this file; it exists so that a hook which cannot be
    /// given a URL at registration time can still find one, and failing to write it is not a
    /// reason to refuse to start.
    /// </remarks>
    private void WritePortFile(int port)
    {
        if (!PortFile.Write(_paths, port))
        {
            _logger.Warning("Could not write {PortFile}.", _paths.PortFile);
        }
    }

    private void Report(SettingsWriteResult result, string what, string url)
    {
        switch (result.Outcome)
        {
            case SettingsWriteOutcome.Written:
                _logger.Information(
                    "Hooks {What} Claude Code at {File} for {Url} (attempt {Attempts}). Backup: {Backup}.",
                    what,
                    _claude.UserSettingsFile,
                    url,
                    result.Attempts,
                    result.BackupPath ?? "(none needed)");
                break;

            case SettingsWriteOutcome.NothingToDo:
                _logger.Debug("Claude Code's settings already say what they should; nothing written.");
                break;

            case SettingsWriteOutcome.Abandoned:
                _logger.Warning(
                    "Gave up after {Attempts} attempts to update {File}: {Problem}. The file is unchanged.",
                    result.Attempts,
                    _claude.UserSettingsFile,
                    result.Problem);
                break;

            default:
                _logger.Error(
                    "Could not read {File}: {Problem}. It has been left exactly as it is.",
                    _claude.UserSettingsFile,
                    result.Problem);
                break;
        }
    }
}
