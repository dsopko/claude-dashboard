using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeDashboard.Tests.Ingress;

/// <summary>
/// The self-test, the test event, refused messages and "last heard", through the composed host
/// (T1.61, issue #74).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Everything is a scratch copy.</strong> Each test has its own data folder, with the script
/// written there by <see cref="HookScript.EnsureWritten"/> and <c>listening.txt</c> written there by
/// the host's own announcement, as <c>Program</c> does. The operator's installed script and their
/// <c>listening.txt</c> are never run or read, and nothing posts to a real dashboard.
/// </para>
/// </remarks>
public sealed class PathCheckHostTests : IAsyncLifetime, IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private DashboardPaths _paths = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private IDisposable? _logger;

    public async Task InitializeAsync()
    {
        _paths = new DashboardPaths(_root);

        // A port the host keeps: a taken one is chosen again (T1.77, issue #120).
        (_app, var port, _) = await ClaudeDashboard.Tests.Hosting.TestPorts.StartAsync(chosen =>
        {
            new SettingsStore(_paths).Save(new DashboardSettings { Port = chosen });
            return AppHost.Build(_paths, claude: new ClaudeCodePaths(Path.Combine(_paths.Root, "claude-config")));
        });
        _logger = _app.Services.GetService<Serilog.ILogger>() as IDisposable;

        // Program's order: the script, then the announcement, then the self-test.
        Assert.True(HookScript.EnsureWritten(_paths, Serilog.Core.Logger.None));
        Assert.True(_app.Services.GetRequiredService<IngressAnnouncement>().Announce());

        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _logger?.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp folder.
        }
    }

    /// <summary>Releases the client; the host and the folder go in DisposeAsync.</summary>
    public void Dispose() => _client?.Dispose();

    private HookHealth Health => _app.Services.GetRequiredService<HookHealth>();

    private HookSelfTest SelfTest(TimeSpan? limit = null, string? curl = null) =>
        new(
            _paths,
            Health,
            _app.Services.GetRequiredService<IClock>(),
            _app.Services.GetRequiredService<Serilog.ILogger>(),
            curl ?? HookSelfTest.DefaultCurl,
            limit ?? HookSelfTest.ArrivalLimit);

    private async Task<HttpResponseMessage> Post(string body, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(IngressToken.HeaderName, token ?? _app.Services.GetRequiredService<IngressToken>().Reveal());

        return await _client.SendAsync(request);
    }

    private async Task<JsonElement> State()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/state");
        request.Headers.Add(IngressToken.HeaderName, _app.Services.GetRequiredService<IngressToken>().Reveal());

        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>Stops the host so the writer drains and the store lets go of the file.</summary>
    private async Task Stop()
    {
        await _app.StopAsync();
        (_app.Services.GetRequiredService<IEventStore>() as IDisposable)?.Dispose();
    }

    private string ReadLogs()
    {
        _logger?.Dispose();
        _logger = null;

        var everything = new StringBuilder();

        foreach (var file in Directory.EnumerateFiles(_paths.LogFolder, "*.log"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            everything.Append(reader.ReadToEnd());
        }

        return everything.ToString();
    }

    private static string Prompt(string marker) =>
        $$"""{"hook_event_name":"UserPromptSubmit","session_id":"s-real","cwd":"C:\\work","prompt_id":"p-1","prompt":"{{marker}}"}""";

    // ---- The self-test ------------------------------------------------------------------------

    /// <summary>
    /// The real script, a scratch copy, posts the test message: it arrives, the round trip is
    /// logged, and no notice shows.
    /// </summary>
    [Fact]
    public async Task A_self_test_against_the_host_arrives_and_shows_no_notice()
    {
        var result = await _app.Services.GetRequiredService<HookSelfTest>().RunAsync();

        Assert.True(result.Passed, $"The test message did not arrive: {result.Cause}.");
        Assert.NotNull(result.RoundTripMs);

        // The round trip is also a timing (T1.66), through AppHost's own wiring.
        var roundTrip = _app.Services.GetRequiredService<ClaudeDashboard.App.Pipeline.Timings>().HookRoundTrip.SinceStart;
        Assert.Equal(1, roundTrip.Count);
        Assert.Equal(result.RoundTripMs!.Value, roundTrip.WorstShown, precision: 0);

        var notice = new SelfTestNotice(Health);
        notice.Tick(DateTimeOffset.Now);
        Assert.False(notice.IsShown);

        var state = await State();
        var test = state.GetProperty("health").GetProperty("selfTest");
        Assert.True(test.GetProperty("passed").GetBoolean());
        Assert.Equal(result.RoundTripMs, test.GetProperty("roundTripMs").GetInt64());
        Assert.EndsWith("Z", test.GetProperty("at").GetString(), StringComparison.Ordinal);

        // The self-test does not move "last heard".
        Assert.Equal(JsonValueKind.Null, state.GetProperty("health").GetProperty("lastHeardAt").ValueKind);

        Assert.Contains("The test message from the hook script arrived in", ReadLogs(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The test event leaves no session, no row and no decision; <c>/hook</c> answers 200 empty, and
    /// ingress takes it out before the mapper, so the mapper never logs it as an unknown event.
    /// </summary>
    [Fact]
    public async Task The_test_event_leaves_no_session_row_or_decision()
    {
        var result = await _app.Services.GetRequiredService<HookSelfTest>().RunAsync();
        Assert.True(result.Passed);

        // A stray one, posted directly, with a value nobody waits for: answered the same way.
        using (var response = await Post(HookSelfTest.Body("not-the-value")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }

        var state = await State();
        Assert.Equal(0, state.GetProperty("sessionCount").GetInt32());
        Assert.Equal(0, state.GetProperty("sessions").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("health").GetProperty("lastHeardAt").ValueKind);

        await Stop();

        Assert.Empty(ForeignSqliteReader.Column(_paths.DatabaseFile, "SELECT id FROM events"));
        Assert.Empty(ForeignSqliteReader.Column(_paths.DatabaseFile, "SELECT id FROM decisions"));
        Assert.DoesNotContain(HookHealth.SelfTestEventName, ReadLogs(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A script that does nothing: the notice shows with its cause, the button gives the same words,
    /// and a real accepted message then clears the notice.
    /// </summary>
    [Fact]
    public async Task A_script_that_does_nothing_shows_the_notice_until_a_real_message()
    {
        File.WriteAllText(_paths.HookScriptFile, "@exit /b 0\r\n");

        var result = await SelfTest(limit: TimeSpan.FromSeconds(1)).RunAsync();

        Assert.False(result.Passed);
        Assert.Equal(SelfTestCause.NothingArrived, result.Cause);

        var notice = new SelfTestNotice(Health);
        notice.Tick(DateTimeOffset.Now);

        Assert.True(notice.IsShown);
        Assert.Equal($"{SelfTestNotice.WindowLead} The script ran and nothing arrived.", notice.Text);
        Assert.Equal(SelfTestNotice.TrayShort, notice.TrayText);

        // The button: the same test, the same words.
        var settings = Settings(SelfTest(limit: TimeSpan.FromSeconds(1)));
        await settings.TestConnectionCommand.ExecuteAsync(null);
        Assert.Equal(notice.Text, settings.TestResult);

        using (var response = await Post(Prompt("a real one")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        notice.Tick(DateTimeOffset.Now);
        Assert.False(notice.IsShown);
    }

    /// <summary>A missing script and a missing <c>curl.exe</c> each say so.</summary>
    [Fact]
    public async Task A_missing_script_or_curl_is_the_cause()
    {
        var missingCurl = await SelfTest(curl: Path.Combine(_root, "no-curl.exe")).RunAsync();
        Assert.Equal(SelfTestCause.CurlMissing, missingCurl.Cause);
        Assert.EndsWith("curl.exe is not in System32, so the script cannot send them.", SelfTestNotice.Describe(missingCurl), StringComparison.Ordinal);

        File.Delete(_paths.HookScriptFile);

        var missingScript = await SelfTest().RunAsync();
        Assert.Equal(SelfTestCause.ScriptMissing, missingScript.Cause);
        Assert.EndsWith("The script that forwards them is missing.", SelfTestNotice.Describe(missingScript), StringComparison.Ordinal);
    }

    // ---- Refused messages and last heard ------------------------------------------------------

    /// <summary>
    /// A refused post writes exactly one HookRefused row with no event and no session, and nothing
    /// from its body. Its detail is the count, one. It does not move "last heard". An accepted post writes no such row and does.
    /// </summary>
    [Fact]
    public async Task A_refused_post_writes_one_row_and_an_accepted_post_none()
    {
        const string Marker = "refused-body-marker";

        using (var refused = await Post(Prompt(Marker), token: new string('x', 43)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        Assert.Null(Health.LastHeardAt);
        Assert.Equal(1, Health.RefusedCount);

        using (var accepted = await Post(Prompt("accepted")))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        Assert.NotNull(Health.LastHeardAt);

        var state = await State();
        Assert.EndsWith("Z", state.GetProperty("health").GetProperty("lastHeardAt").GetString(), StringComparison.Ordinal);

        await Stop();

        var rows = ForeignSqliteReader.Query(
            _paths.DatabaseFile,
            "SELECT IFNULL(event_id, 'NULL'), IFNULL(session_id, 'NULL'), IFNULL(reason, 'NULL'), IFNULL(detail, 'NULL') " +
            "FROM decisions WHERE kind = 'HookRefused'");

        var row = Assert.Single(rows);
        // At most one row a second, with the count (the ruling of 2026-10-04): one refusal, one row.
        Assert.Equal(["NULL", "NULL", "NULL", "refused=1"], row);

        var everything = string.Join("|", ForeignSqliteReader.Query(_paths.DatabaseFile, "SELECT * FROM decisions").SelectMany(r => r));
        Assert.DoesNotContain(Marker, everything, StringComparison.Ordinal);
    }

    // ---- The board ----------------------------------------------------------------------------

    /// <summary>
    /// AppHost's own board puts the self-test notice and the refused notice directly after the
    /// plugin notice, and before the history and settings notices (T1.61 review: they could move
    /// unseen; the history notice since T1.62).
    /// </summary>
    [Fact]
    public void The_real_board_puts_the_two_notices_directly_after_the_plugin_notice()
    {
        var root = Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));
        var paths = new DashboardPaths(root);

        try
        {
            // A directory where the database must be: the history cannot be recorded, so its notice shows.
            Directory.CreateDirectory(paths.DatabaseFile);

            var loaded = new SettingsStore(paths).Load();
            var start = new SettingsAtStart(loaded, BackupFile: Path.Combine(root, "settings.error-20261004-090000.json"));

            using var host = AppHost.Build(paths, settingsAtStart: start, claude: new ClaudeCodePaths(Path.Combine(paths.Root, "claude-config")));
            var services = host.Services;
            var clock = services.GetRequiredService<IClock>();

            services.GetRequiredService<HookNotice>().ShowPluginDisabled();

            var health = services.GetRequiredService<HookHealth>();
            var failed = new SelfTestResult(false, null, clock.Now, SelfTestCause.NothingArrived);
            health.Finished(failed);

            for (var i = 0; i < HookHealth.RefusalsToShow; i++)
            {
                health.Refused(clock.Now);
            }

            var store = services.GetRequiredService<SqliteEventStore>();
            Assert.False(store.Append(new ArchiveRecord(null, [new Decision(clock.Now, null, DecisionKind.SilenceSwept)])));

            var board = services.GetRequiredService<NoticeBoard>();
            board.Tick(clock.Now);

            var texts = board.Texts.ToList();
            var plugin = texts.IndexOf(HookNotice.PluginDisabledText);
            var history = texts.IndexOf(HistoryNotice.WindowText);
            var settings = texts.FindIndex(text => text.Contains("settings.error-20261004-090000.json", StringComparison.Ordinal));

            Assert.True(plugin >= 0, string.Join(Environment.NewLine, texts));
            Assert.Equal(SelfTestNotice.Describe(failed), texts[plugin + 1]);
            Assert.Equal(RefusedNotice.WindowText, texts[plugin + 2]);
            Assert.True(history > plugin + 2, string.Join(Environment.NewLine, texts));
            Assert.True(settings > plugin + 2, string.Join(Environment.NewLine, texts));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Disposable temp folder.
            }
        }
    }

    // ---- The Settings button ------------------------------------------------------------------

    private static SettingsViewModel Settings(HookSelfTest selfTest)
    {
        var root = Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var startup = new StartWithWindows(new FakeStartupRegistry(), null, Serilog.Core.Logger.None);

        return new SettingsViewModel(startup, new SettingsStore(new DashboardPaths(root)), Serilog.Core.Logger.None, selfTest);
    }
}
