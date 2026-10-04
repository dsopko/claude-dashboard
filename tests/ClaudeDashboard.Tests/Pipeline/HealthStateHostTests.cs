using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// <c>/state</c>'s counts are the consumer's published snapshot, never a live count (T1.65).
/// </summary>
/// <remarks>
/// The composed host, against a scratch data folder. The consumer ticks every 15 seconds, so for
/// the first seconds after the start no snapshot exists while the consumer has already applied an
/// event: a request that read a live count would show it, and the snapshot shows nothing. If a tick
/// falls between the two reads below, the check is taken again.
/// </remarks>
public sealed class HealthStateHostTests : IAsyncLifetime, IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var port = ClaudeDashboard.Tests.Hosting.AppHostTests.FreePort();
        var paths = new DashboardPaths(_root);
        new SettingsStore(paths).Save(new DashboardSettings { Port = port });

        _app = AppHost.Build(paths);
        await _app.StartAsync();
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

    private string Token => _app.Services.GetRequiredService<IngressToken>().Reveal();

    /// <summary>The counts in <c>/state</c> are the published snapshot's, even while the live count is ahead.</summary>
    [Fact]
    public async Task State_shows_the_snapshot_and_not_the_live_count()
    {
        using (var post = new HttpRequestMessage(HttpMethod.Post, "/hook")
        {
            Content = new StringContent(
                """{"hook_event_name":"UserPromptSubmit","session_id":"s-1","cwd":"C:\\w","prompt_id":"p","prompt":"go"}""",
                Encoding.UTF8,
                "application/json"),
        })
        {
            post.Headers.Add(IngressToken.HeaderName, Token);
            using var answer = await _client.SendAsync(post);
            answer.EnsureSuccessStatusCode();
        }

        var consumer = _app.Services.GetRequiredService<EventConsumer>();
        Assert.True(SpinWait.SpinUntil(() => consumer.AppliedCount >= 1, TimeSpan.FromSeconds(30)));

        var board = _app.Services.GetRequiredService<HealthBoard>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = board.Current;
            var health = await Health();
            var after = board.Current;

            if (!ReferenceEquals(before, after))
            {
                continue;
            }

            if (before is null)
            {
                Assert.False(
                    health.TryGetProperty("counts", out var counts) && counts.ValueKind != JsonValueKind.Null,
                    "/state showed counts before the consumer published any.");
            }
            else
            {
                Assert.Equal(
                    before.SinceStart.Applied,
                    health.GetProperty("counts").GetProperty("sinceStart").GetProperty("applied").GetInt64());
                Assert.Equal(before.CountedAt.UtcDateTime, health.GetProperty("countedAt").GetDateTime());
            }

            return;
        }

        Assert.Fail("A tick fell between the reads three times.");
    }

    private async Task<JsonElement> Health()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/state");
        request.Headers.Add(IngressToken.HeaderName, Token);

        using var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("health").Clone();
    }
}
