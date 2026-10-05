using System.IO;
using System.Net.Http;
using System.Text;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// A real hook post, through the whole composed app, to a row in a real file (T1.17).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every part of this passing its own tests would not say the parts are connected.</strong>
/// The body is buffered by ingress, carried on the event, handed to the archive by the consumer,
/// written by the writer, and read back by a SQLite that is not ours. Any one of those links being
/// absent shows up here and nowhere else — most sharply the first, because ingress used to
/// deserialize straight from the request stream, and a version of this that only checked the
/// mapped fields would have been perfectly happy with an empty <c>payload_json</c>.
/// </para>
/// <para>
/// This is also the acceptance criterion the task states as "write every <c>InboundEvent</c>",
/// asserted through the composed host rather than by constructing the pieces by hand.
/// </para>
/// </remarks>
public sealed class HookToDatabaseTests : IAsyncLifetime, IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private DashboardPaths _paths = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private int _port;

    /// <summary>Held from start-up: the container is gone by the time DisposeAsync runs.</summary>
    private IEventStore _store = null!;

    public async Task InitializeAsync()
    {
        _port = ClaudeDashboard.Tests.Hosting.AppHostTests.FreePort();
        _paths = new DashboardPaths(_root);
        new SettingsStore(_paths).Save(new DashboardSettings { Port = _port });

        _app = AppHost.Build(_paths, claude: new ClaudeCodePaths(Path.Combine(_paths.Root, "claude-config")));

        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
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

        if (_store is IDisposable store)
        {
            store.Dispose();
        }

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

    /// <summary>Releases the client; the host and the file go in DisposeAsync.</summary>
    public void Dispose() => _client?.Dispose();

    private async Task Post(string body)
    {
        // The token this host made at its start (T1.48), as the hook reads it from listening.txt.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(
            ClaudeDashboard.App.Ingress.IngressToken.HeaderName,
            _app.Services.GetRequiredService<ClaudeDashboard.App.Ingress.IngressToken>().Reveal());

        using var response = await _client.SendAsync(request);

        response.EnsureSuccessStatusCode();
    }

    /// <summary>The body posted to <c>/hook</c> is the body in the file, unchanged.</summary>
    [Fact]
    public async Task A_posted_hook_becomes_a_row_carrying_the_body_it_arrived_with()
    {
        const string Body = """
            {"hook_event_name":"UserPromptSubmit","session_id":"sess-live","cwd":"C:\\work",
             "prompt":"write the acceptance supplement","prompt_id":"p-1",
             "a_field_phase_one_does_not_map":"kept anyway"}
            """;

        await Post(Body);

        // Stop the host so the writer drains and the store releases the file. This is also the
        // "events survive the run" path rather than a peek at a live connection.
        await _app.StopAsync();
        (_store as IDisposable)?.Dispose();

        var rows = ForeignSqliteReader.Query(
            _paths.DatabaseFile,
            "SELECT session_id, event_type, payload_json, cwd FROM events");

        var row = Assert.Single(rows);

        Assert.Equal("sess-live", row[0]);
        Assert.Equal("UserPromptSubmit", row[1]);
        Assert.Equal(Body, row[2]);
        Assert.Contains("a_field_phase_one_does_not_map", row[2], StringComparison.Ordinal);
        Assert.Equal(@"C:\work", row[3]);
    }

    /// <summary>The prompt reaches the database, through a real post and the real store.</summary>
    [Fact]
    public async Task The_prompt_reaches_the_database()
    {
        const string Secret = "a prompt the database keeps";

        await Post($$"""{"hook_event_name":"UserPromptSubmit","session_id":"s1","cwd":"C:\\w","prompt":"{{Secret}}"}""");

        await _app.StopAsync();
        (_store as IDisposable)?.Dispose();

        var stored = Assert.Single(ForeignSqliteReader.Column(_paths.DatabaseFile, "SELECT payload_json FROM events"));

        Assert.Contains(Secret, stored, StringComparison.Ordinal);
    }

    /// <summary>Events with no session id are not archived, because they are not events.</summary>
    /// <remarks>
    /// Ingress rejects them before mapping, so nothing reaches the archive. Asserted through the
    /// foreign reader: the events table is empty. Until T1.60 no database was created at all; since
    /// then every start writes its runs row, and that row is the control that this is the file the
    /// run wrote (issue #78).
    /// </remarks>
    [Fact]
    public async Task A_hook_ingress_rejects_never_reaches_the_file()
    {
        await Post("""{"hook_event_name":"UserPromptSubmit","cwd":"C:\\w","prompt":"no session id"}""");
        await Post("""{"hook_event_name":"SomethingWeDoNotConsume","session_id":"s1"}""");

        await _app.StopAsync();
        (_store as IDisposable)?.Dispose();

        Assert.Empty(ForeignSqliteReader.Column(_paths.DatabaseFile, "SELECT payload_json FROM events"));
        Assert.Single(ForeignSqliteReader.Column(_paths.DatabaseFile, "SELECT id FROM runs"));
    }

    private string ReadLogs()
    {
        if (!Directory.Exists(_paths.LogFolder))
        {
            return string.Empty;
        }

        var everything = new StringBuilder();

        foreach (var file in Directory.EnumerateFiles(_paths.LogFolder, "*.log"))
        {
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            everything.Append(reader.ReadToEnd());
        }

        return everything.ToString();
    }
}
