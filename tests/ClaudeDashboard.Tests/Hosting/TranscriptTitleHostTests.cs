using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Tests.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// The names from the transcript through the assembled program (issue #161): a real post to <c>/hook</c>, the real
/// consumer and reader, the <c>/state</c> board and the database file. The data folder and Claude Code's folder are
/// scratch folders; the transcripts are written in the scratch projects folder.
/// </summary>
/// <remarks>
/// A fresh host is a restart: its Registry is empty, so each session's first event here is the first event after a
/// restart. The <c>Stop</c> and <c>SessionEnd</c> cases are the issue's four renamed sessions that showed no name.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class TranscriptTitleHostTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private DashboardPaths _paths = null!;
    private ClaudeCodePaths _claude = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private StateBoard _board = null!;
    private EventConsumer _consumer = null!;
    private IEventStore _store = null!;
    private int _posted;

    public async Task InitializeAsync()
    {
        _paths = new DashboardPaths(_root);
        _claude = new ClaudeCodePaths(Path.Combine(_root, "claude-config"));

        (_app, var port, _) = await TestPorts.StartAsync(chosen =>
        {
            new SettingsStore(_paths).Save(new DashboardSettings { Port = chosen });
            return AppHost.Build(_paths, claude: _claude);
        });

        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        _board = _app.Services.GetRequiredService<StateBoard>();
        _consumer = _app.Services.GetRequiredService<EventConsumer>();
        _store = _app.Services.GetRequiredService<IEventStore>();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        (_store as IDisposable)?.Dispose();

        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Disposable temp folder.
            }
        }
    }

    /// <summary>The host gives the consumer the real reader, over the scratch Claude Code folder.</summary>
    [Fact]
    public void The_consumer_reads_transcripts()
    {
        Assert.IsType<TranscriptNameReader>(_consumer.Transcripts);
    }

    /// <summary>
    /// The first event after a restart is a <c>Stop</c> or a <c>SessionEnd</c>, which never carry a name, and the
    /// row has its name at once: the generated title for a session nobody named, the given name for one somebody
    /// did, though a generated title is written after it.
    /// </summary>
    [Fact]
    public async Task The_first_event_after_a_restart_brings_the_name_back()
    {
        var unnamed = Transcript("2fc94061", Generated("PennCustQuote dashboard theme integration"));
        var renamed = Transcript("65685680", Generated("Claude Code settings JSON fix"), Given("Poorly worded and filed issue"), Generated("Claude Code settings JSON fix"));

        await Post(new { hook_event_name = "Stop", session_id = "2fc94061", cwd = @"C:\dev\PennCustQuote", transcript_path = unnamed });
        await Post(new { hook_event_name = "SessionEnd", session_id = "65685680", cwd = @"C:\dev\other", transcript_path = renamed, reason = "prompt_input_exit" });
        Drained();

        Assert.Equal("PennCustQuote dashboard theme integration", Title("2fc94061"));
        Assert.Equal("Poorly worded and filed issue", Title("65685680"));
    }

    /// <summary>
    /// A given name on the hook stays ahead of the transcript's generated title, and a new generated title replaces
    /// the old one on a session nobody named.
    /// </summary>
    [Fact]
    public async Task The_given_name_stays_and_a_generated_title_follows_changes()
    {
        var named = Transcript("named", Generated("Generated words"));
        var unnamed = Transcript("7a239067", Generated("Build network monitoring application"));

        await Post(new { hook_event_name = "UserPromptSubmit", session_id = "named", cwd = @"C:\w", prompt = "go", prompt_id = "p-1", session_title = "Director", transcript_path = named });
        await Post(new { hook_event_name = "UserPromptSubmit", session_id = "7a239067", cwd = @"C:\w", prompt = "go", prompt_id = "p-1", transcript_path = unnamed });
        Drained();

        Assert.Equal("Director", Title("named"));
        Assert.Equal("Build network monitoring application", Title("7a239067"));

        File.AppendAllText(named, Generated("Later generated words") + "\n");
        File.AppendAllText(unnamed, Generated("home-internet-monitor-ping") + "\n");

        await Post(new { hook_event_name = "Stop", session_id = "named", cwd = @"C:\w", prompt_id = "p-1", transcript_path = named });
        await Post(new { hook_event_name = "Stop", session_id = "7a239067", cwd = @"C:\w", prompt_id = "p-1", transcript_path = unnamed });
        Drained();

        Assert.Equal("Director", Title("named"));
        Assert.Equal("home-internet-monitor-ping", Title("7a239067"));
    }

    /// <summary>A transcript path outside the projects folder is not read: the row has no name.</summary>
    [Fact]
    public async Task A_transcript_outside_the_projects_folder_gives_no_name()
    {
        var outside = Path.Combine(_root, "elsewhere", "s-out.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, Generated("Read from outside") + "\n");

        await Post(new { hook_event_name = "Stop", session_id = "s-out", cwd = @"C:\w", transcript_path = outside });
        Drained();

        Assert.Null(Title("s-out"));
    }

    /// <summary>
    /// The database's event row and decision rows hold the name the row shows, from the event that brought it:
    /// here the <c>Stop</c> after a restart.
    /// </summary>
    [Fact]
    public async Task The_database_rows_hold_the_name_the_row_shows()
    {
        var unnamed = Transcript("2fc94061", Generated("PennCustQuote dashboard theme integration"));

        await Post(new { hook_event_name = "Stop", session_id = "2fc94061", cwd = @"C:\dev\PennCustQuote", transcript_path = unnamed });
        Drained();

        await _app.StopAsync();
        (_store as IDisposable)?.Dispose();

        Assert.Equal(
            "PennCustQuote dashboard theme integration",
            Assert.Single(ForeignSqliteReader.Column(_paths.DatabaseFile, "SELECT session_title FROM events")));
        Assert.All(
            ForeignSqliteReader.Column(_paths.DatabaseFile, "SELECT session_title FROM decisions WHERE session_id = '2fc94061'"),
            title => Assert.Equal("PennCustQuote dashboard theme integration", title));
    }

    private static string Generated(string title) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["type"] = "ai-title", ["aiTitle"] = title, ["sessionId"] = "s" });

    private static string Given(string title) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["type"] = "custom-title", ["customTitle"] = title, ["sessionId"] = "s" });

    /// <summary>Writes a transcript in the scratch projects folder and returns its path.</summary>
    private string Transcript(string sessionId, params string[] lines)
    {
        var folder = Path.Combine(_claude.ProjectsDirectory, "C--w");
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, sessionId + ".jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private string? Title(string sessionId) =>
        _board.Current.Sessions.Single(entry => entry.Id == sessionId).Title?.Text;

    private async Task Post(object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hook")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(IngressToken.HeaderName, _app.Services.GetRequiredService<IngressToken>().Reveal());

        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        _posted++;
    }

    private void Drained() =>
        Assert.True(
            SpinWait.SpinUntil(() => _consumer.AppliedCount + _consumer.DeclinedCount >= _posted, TimeSpan.FromSeconds(30)),
            "the consumer did not drain the posted events");
}
