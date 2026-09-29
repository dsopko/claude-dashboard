using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// The state board as the product assembles it (T1.46): the real host, the real consumer, the
/// real sound engine.
/// </summary>
/// <remarks>
/// These read <see cref="StateBoard.Current"/> rather than <c>GET /state</c>, because the product
/// reads its token from the environment and a test that set one would set it for every test
/// running beside it. The endpoint's own tests cover the socket and the token; these cover what
/// only the assembled program can get wrong.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class StateHostTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private StateBoard _board = null!;
    private EventConsumer _consumer = null!;
    private int _posted;

    public async Task InitializeAsync()
    {
        var port = AppHostTests.FreePort();
        var paths = new DashboardPaths(_root);
        new SettingsStore(paths).Save(new DashboardSettings { Port = port });

        _app = AppHost.Build(paths);
        await _app.StartAsync();

        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        _board = _app.Services.GetRequiredService<StateBoard>();
        _consumer = _app.Services.GetRequiredService<EventConsumer>();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
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

    /// <summary>
    /// A session needing permission shows the nudge the engine scheduled for it — which it can only
    /// do if the board heard the change after the engine did.
    /// </summary>
    /// <remarks>
    /// AppHost resolves the board after wiring the sound engine to the Registry, because handlers
    /// run in subscription order. The other way round, the board reads the schedule before the
    /// engine has set it, and this session reports no nudge at all.
    /// </remarks>
    [Fact]
    public async Task The_assembled_board_reports_the_nudge_the_engine_just_scheduled()
    {
        var now = DateTimeOffset.UtcNow;

        await Post($$"""{"hook_event_name":"UserPromptSubmit","session_id":"asks","cwd":"C:\\work","prompt_id":"p-1","prompt":"go","timestamp":"{{now:O}}"}""");
        await Post($$"""{"hook_event_name":"Notification","session_id":"asks","cwd":"C:\\work","notification_type":"permission_prompt","timestamp":"{{now.AddSeconds(1):O}}"}""");

        Drained();

        var entry = Assert.Single(_board.Current.Sessions);

        Assert.Equal(SessionState.NeedsPermission, entry.State);
        Assert.NotNull(entry.NextNudgeAt);
        Assert.True(entry.NextNudgeAt > entry.EnteredAt, "the first nudge falls after the state was entered");
    }

    /// <summary>
    /// Reports read and serialized on another thread while the consumer applies a burst neither
    /// throw nor cost the consumer an event.
    /// </summary>
    [Fact]
    public async Task Reading_the_report_while_the_consumer_applies_neither_throws_nor_loses_an_event()
    {
        const int Sessions = 150;

        using var stop = new CancellationTokenSource();
        var reads = 0;

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = JsonSerializer.Serialize(_board.Current, IngressEndpoints.StateOptions);
                reads++;
            }
        });

        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < Sessions; i++)
        {
            await Post($$"""{"hook_event_name":"UserPromptSubmit","session_id":"s-{{i}}","cwd":"C:\\work","prompt_id":"p-{{i}}","prompt":"go","timestamp":"{{now:O}}"}""");
        }

        Drained();
        await stop.CancelAsync();
        await reader;

        Assert.True(reads > 0, "the reader never ran beside the consumer");
        Assert.Equal(Sessions, _consumer.AppliedCount);
        Assert.Equal(0, _consumer.DeclinedCount);
        Assert.Equal(Sessions, _board.Current.SessionCount);
    }

    private async Task Post(string json)
    {
        // The token comes from the environment, as HookToDatabaseTests reads it.
        var token = Environment.GetEnvironmentVariable(IngressToken.EnvironmentVariable);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/hook")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Add(IngressToken.HeaderName, token);
        }

        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        _posted++;
    }

    private void Drained() =>
        Assert.True(
            SpinWait.SpinUntil(() => _consumer.AppliedCount + _consumer.DeclinedCount >= _posted, TimeSpan.FromSeconds(30)),
            "the consumer did not drain the posted events");
}
