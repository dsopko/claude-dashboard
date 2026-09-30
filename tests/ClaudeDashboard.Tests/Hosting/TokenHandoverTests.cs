using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// The token travels with the port (T1.48, issue #57): the real script against the real host.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The case the issue is about.</strong> A Claude Code session gets its environment once, at
/// launch. While the token lived there, a session started before the token was set or changed was
/// refused, and the script threw the refusal away. Here the script runs with one environment,
/// captured once — even carrying the retired variable with the first run's token in it, the worst
/// copy a session could hold — through a dashboard restart, and must keep reporting.
/// </para>
/// <para>
/// Each host is started and announced the way <c>Program</c> does it: the script written, then
/// <see cref="IngressAnnouncement.Announce"/>. The script is the generated one, run by
/// <c>cmd.exe</c> as Claude Code's exec form runs it.
/// </para>
/// </remarks>
public sealed class TokenHandoverTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;
    private readonly int _port = AppHostTests.FreePort();
    private readonly List<IDisposable> _loggers = [];

    public TokenHandoverTests()
    {
        _paths = new DashboardPaths(_root);
        new SettingsStore(_paths).Save(new DashboardSettings { Port = _port });
    }

    public void Dispose()
    {
        foreach (var logger in _loggers)
        {
            logger.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A sink may still hold a log file; the temp folder is disposable.
        }
    }

    /// <summary>
    /// Acceptance 2 and 3: a hook with an environment captured before a restart keeps reporting
    /// after it; the two starts made two tokens; the first start's token is refused by the second.
    /// </summary>
    [Fact]
    public async Task A_hook_launched_before_a_restart_keeps_reporting_after_it()
    {
        Dictionary<string, string?> captured;
        string first;

        await using (var before = await Start())
        {
            first = Token(before);

            // The session's environment, captured once, at its launch — with the retired variable
            // carrying this run's token, as a session launched on T1.46's instructions would.
            captured = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase);
            captured[IngressToken.RetiredEnvironmentVariable] = first;

            RunHook(captured, "before-restart");
            Assert.True(Arrived(before, "before-restart"), "the hook did not reach the first dashboard");

            await before.StopAsync();
            Assert.True(ListeningFile.Delete(_paths));
        }

        await using var after = await Start();
        var second = Token(after);

        Assert.NotEqual(first, second);

        RunHook(captured, "after-restart");

        Assert.True(Arrived(after, "after-restart"), "a hook with the old environment was not heard after the restart");

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        using var stale = new HttpRequestMessage(HttpMethod.Post, "/hook")
        {
            Content = new StringContent("""{"hook_event_name":"SessionStart","session_id":"stale","cwd":"C:\\w","source":"startup"}""", Encoding.UTF8, "application/json"),
        };
        stale.Headers.Add(IngressToken.HeaderName, first);

        using var refused = await client.SendAsync(stale);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    /// <summary>
    /// Acceptance 6 and 9: with the retired variable set, a start says so once, naming the variable
    /// and never its value; and the token appears in no log line, whatever the dashboard is asked.
    /// </summary>
    /// <remarks>
    /// The variable is process-wide, so it is set for the build only and restored at once. Other
    /// hosts built in parallel meanwhile would log the same Information line, which asserts nothing
    /// of theirs.
    /// </remarks>
    [Fact]
    public async Task The_token_and_the_retired_value_appear_in_no_log_line()
    {
        const string RetiredValue = "RETIRED-VALUE-MARKER-6f2c";
        var previous = Environment.GetEnvironmentVariable(IngressToken.RetiredEnvironmentVariable);
        WebApplication host;

        Environment.SetEnvironmentVariable(IngressToken.RetiredEnvironmentVariable, RetiredValue);

        try
        {
            host = await Start();
        }
        finally
        {
            Environment.SetEnvironmentVariable(IngressToken.RetiredEnvironmentVariable, previous);
        }

        var token = Token(host);

        await using (host)
        {
            RunHook(null, "logged-session");
            Assert.True(Arrived(host, "logged-session"));

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };

            foreach (var (method, path) in new[] { (HttpMethod.Post, "/hook"), (HttpMethod.Post, "/show"), (HttpMethod.Get, "/state") })
            {
                using var wrong = new HttpRequestMessage(method, path) { Content = new StringContent("{}") };
                wrong.Headers.Add(IngressToken.HeaderName, token[..^1] + (token[^1] == 'A' ? 'B' : 'A'));
                using var answer = await client.SendAsync(wrong);
                Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
            }

            using var state = new HttpRequestMessage(HttpMethod.Get, "/state");
            state.Headers.Add(IngressToken.HeaderName, token);
            using var served = await client.SendAsync(state);
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);

            await host.StopAsync();
        }

        var log = ReadLogs();

        Assert.Contains("Rejected a /hook post with a missing or incorrect token.", log, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(log, $"{IngressToken.RetiredEnvironmentVariable} is set and is ignored"));
        Assert.DoesNotContain(RetiredValue, log, StringComparison.Ordinal);
        Assert.DoesNotContain(token, log, StringComparison.Ordinal);
    }

    // ---- The host, started the way Program starts it -----------------------------------------------

    private async Task<WebApplication> Start()
    {
        var host = AppHost.Build(_paths);

        if (host.Services.GetService<Serilog.ILogger>() is IDisposable logger)
        {
            _loggers.Add(logger);
        }

        await host.StartAsync();

        // Program's order since T1.48: the script first, then the announcement.
        Assert.True(HookScript.EnsureWritten(_paths, Serilog.Core.Logger.None));
        Assert.True(host.Services.GetRequiredService<IngressAnnouncement>().Announce());

        return host;
    }

    private static string Token(WebApplication host) => host.Services.GetRequiredService<IngressToken>().Reveal();

    private static bool Arrived(WebApplication host, string sessionId)
    {
        var registry = host.Services.GetRequiredService<SessionRegistry>();

        return SpinWait.SpinUntil(
            () =>
            {
                try
                {
                    return registry.Sessions.ContainsKey(new SessionId(sessionId));
                }
                catch (InvalidOperationException)
                {
                    // Read from the test thread while the consumer may be writing; asked again.
                    return false;
                }
            },
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Runs the generated script with <paramref name="environment"/> as its whole environment, or
    /// this process's when null, and the event on stdin. It must print nothing and exit 0.
    /// </summary>
    private void RunHook(Dictionary<string, string?>? environment, string sessionId)
    {
        var start = new ProcessStartInfo(HookInstaller.Interpreter)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root,
        };

        start.ArgumentList.Add("/c");
        start.ArgumentList.Add(_paths.HookScriptFile);

        if (environment is not null)
        {
            start.Environment.Clear();

            foreach (var (key, value) in environment)
            {
                start.Environment[key] = value;
            }
        }

        using var process = Process.Start(start)!;

        process.StandardInput.Write(
            $$"""{"hook_event_name":"SessionStart","session_id":"{{sessionId}}","cwd":"C:\\work","source":"startup"}""");
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(30_000), "post-status.cmd did not exit within 30 seconds.");
        Assert.Equal(string.Empty, output);
        Assert.Equal(string.Empty, error);
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>
    /// Closes the loggers these hosts made, which flushes them, then reads every log file shared —
    /// the way <c>AppHostTests</c> reads its own, and for the same reasons.
    /// </summary>
    private string ReadLogs()
    {
        foreach (var logger in _loggers)
        {
            logger.Dispose();
        }

        _loggers.Clear();

        var everything = new StringBuilder();

        foreach (var file in Directory.EnumerateFiles(_paths.LogFolder, "*.log"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            everything.Append(reader.ReadToEnd());
        }

        return everything.ToString();
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;

        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
