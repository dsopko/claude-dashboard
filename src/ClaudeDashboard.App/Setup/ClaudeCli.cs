using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ClaudeDashboard.App.Configuration;

namespace ClaudeDashboard.App.Setup;

/// <summary>What one run of the <c>claude</c> program came to.</summary>
/// <param name="Found">Whether the program was found and started at all.</param>
/// <param name="ExitCode">Its exit code; meaningless when <paramref name="Found"/> is false.</param>
/// <param name="Output">
/// What it printed, both streams. It is Claude Code's own report about a plugin — a status line
/// and paths in the dashboard's data folder — and never hook or session text.
/// </param>
public readonly record struct ClaudeCliResult(bool Found, int ExitCode, string Output)
{
    /// <summary>The program was not found, or could not be started.</summary>
    public static ClaudeCliResult NotFound { get; } = new(false, -1, string.Empty);

    /// <summary>Whether the program ran and reported success.</summary>
    public bool Succeeded => Found && ExitCode == 0;
}

/// <summary>Runs the <c>claude</c> program with arguments and waits for it.</summary>
/// <remarks>
/// An interface because the real thing starts a process and changes Claude Code's configuration.
/// Every decision about <em>when</em> to run it belongs to callers that a test must be able to
/// drive without doing either.
/// </remarks>
public interface IClaudeCli
{
    /// <summary>Runs <c>claude</c> with <paramref name="arguments"/>, with no person present.</summary>
    ClaudeCliResult Run(IReadOnlyList<string> arguments);
}

/// <summary>
/// The real <c>claude</c> program, found on <c>PATH</c> or in its usual install folder
/// (issue #30).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only <c>claude.exe</c> is looked for.</strong> An install that provides a
/// <c>claude.cmd</c> shim instead is reported as not found, and the caller falls back to the
/// settings file. Running a <c>.cmd</c> needs <c>cmd.exe</c> and its quoting rules around a path
/// that may hold spaces, and nothing here could measure that branch, so it is left out rather
/// than shipped untested.
/// </para>
/// <para>
/// <strong>Input is closed and nothing is shown.</strong> The dashboard is a windowed program
/// with no console, and a child that waited for a person would wait for ever. A run that outlasts
/// <see cref="Timeout"/> is stopped and reported as a failure.
/// </para>
/// <para>
/// <strong><c>CLAUDE_CONFIG_DIR</c> is passed only when it differs from the default.</strong>
/// Claude Code treats an explicit value differently from an absent one even when both name
/// <c>~/.claude</c> — it keeps <c>.claude.json</c> inside an explicit directory — so naming the
/// default explicitly would change where Claude Code looks. The child inherits this process's
/// environment either way; the explicit value covers a <see cref="ClaudeCodePaths"/> that was
/// built for another directory than the environment names.
/// </para>
/// </remarks>
public sealed class ClaudeCli : IClaudeCli
{
    /// <summary>How long one run may take.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private const string ProgramName = "claude.exe";

    private readonly ClaudeCodePaths _claude;

    /// <summary>Creates the runner for the Claude Code configuration at <paramref name="claude"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="claude"/> is null.</exception>
    public ClaudeCli(ClaudeCodePaths claude)
    {
        ArgumentNullException.ThrowIfNull(claude);

        _claude = claude;
    }

    /// <summary>How long one run may take before it is stopped.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>
    /// Finds <c>claude.exe</c>: each folder of <paramref name="pathVariable"/> in order, then the
    /// native installer's folder under <paramref name="userProfile"/>.
    /// </summary>
    /// <returns>The full path, or null when there is none.</returns>
    public static string? Locate(string? pathVariable, string? userProfile)
    {
        var folders = (pathVariable ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => folder.Trim('"'));

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            folders = folders.Append(Path.Combine(userProfile, ".local", "bin"));
        }

        foreach (var folder in folders)
        {
            try
            {
                var candidate = Path.Combine(folder, ProgramName);

                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A PATH entry that is not a path. Skipped, as the shell skips it.
            }
        }

        return null;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is null.</exception>
    public ClaudeCliResult Run(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var program = Locate(
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        if (program is null)
        {
            return ClaudeCliResult.NotFound;
        }

        var start = new ProcessStartInfo
        {
            FileName = program,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (!string.Equals(
                _claude.ConfigDirectory,
                ClaudeCodePaths.DefaultConfigDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            start.Environment[ClaudeCodePaths.ConfigDirectoryVariable] = _claude.ConfigDirectory;
        }

        try
        {
            using var process = Process.Start(start);

            if (process is null)
            {
                return ClaudeCliResult.NotFound;
            }

            process.StandardInput.Close();

            // Both streams are read while the process runs. A child that fills one pipe while the
            // parent waits on the other never exits.
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(Timeout))
            {
                TryStop(process);

                return new ClaudeCliResult(
                    true,
                    -1,
                    $"claude did not finish within {Timeout.TotalSeconds:0} seconds and was stopped.");
            }

            process.WaitForExit();

            return new ClaudeCliResult(true, process.ExitCode, $"{output.Result}{error.Result}".Trim());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new ClaudeCliResult(false, -1, ex.Message);
        }
    }

    private static void TryStop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // It exited between the wait and the kill, or it is not ours to stop. Either way there
            // is nothing more to do about it here.
        }
    }
}
