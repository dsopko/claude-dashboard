using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace ClaudeDashboard.Tests.Ingress;

/// <summary>
/// <c>POST /usage</c> against a real Kestrel on a real loopback socket (Impl §3.2, §9.5; issue #133, MOD.4).
/// </summary>
/// <remarks>
/// <para>
/// The usage mod's post: the token as on <c>/hook</c>, then <c>200</c> with an empty body on every path, and the
/// readings on the <see cref="UsageBoard"/>. A refusal is counted as one on <c>/hook</c> is (ruling R3); an accepted
/// post does not move "last heard" (ruling R5); nothing enters the event channel.
/// </para>
/// <para>
/// <strong>The body limit here is 16 KB, not Kestrel's 30 MB</strong>, so that one test can make the body's read
/// throw after the token check, a fault nobody anticipated, as a body over the limit does in the product. Every other
/// body here is far below it.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class UsageEndpointTests : IAsyncLifetime
{
    private const string Token = "test-token-value";
    private const int BodyLimit = 16 * 1024;

    /// <summary>The first body the mod sent in the guide's lab ("What you get"), as the receiver got it.</summary>
    private const string RealBody =
        """
        {
          "sessionId": "ab86443d-84b5-4342-85b4-a13111a93020",
          "context": { "tokens": 3575, "window": 1000000, "percent": 0 },
          "rateLimits": [
            { "kind": "five_hour", "percentUsed": 24, "resetsAt": "2026-10-08T23:10:00.000Z" },
            { "kind": "seven_day", "percentUsed": 13, "resetsAt": "2026-10-14T13:00:00.000Z" }
          ],
          "cost": { "usd": 0.0006151500000000001 },
          "changed": ["context", "rateLimits", "cost"]
        }
        """;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly FakeClock _clock = new();
    private readonly RecordingEventSink _sink = new();
    private readonly RecordingLogSink _log = new();
    private readonly HookHealth _health = new();
    private readonly UsageBoard _board = new();

    private Serilog.Core.Logger _logger = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(_log).CreateLogger();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0);
            kestrel.Limits.MaxRequestBodySize = BodyLimit;
        });

        builder.Services.AddSingleton<Serilog.ILogger>(_logger);
        builder.Services.AddSingleton(new DashboardPaths(_root));
        builder.Services.AddSingleton<IClock>(_clock);
        builder.Services.AddSingleton(new IngressToken(Token));
        builder.Services.AddSingleton(sp => new HookEventMapper(sp.GetRequiredService<IClock>()));
        builder.Services.AddSingleton<IEventSink>(_sink);
        builder.Services.AddSingleton(_health);
        builder.Services.AddSingleton(_board);

        _app = builder.Build();
        _app.MapIngress();

        await _app.StartAsync();

        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
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

        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---- The token ------------------------------------------------------------------------------

    [Fact]
    public async Task A_post_with_no_token_is_rejected()
    {
        using var response = await _client.SendAsync(Usage(RealBody, token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(_board.Current.LastHeardAt);
        Assert.Empty(_board.Current.Windows);
        Assert.Single(_log.Matching("Rejected a /usage post with a missing or incorrect token."));
    }

    /// <summary>A wrong token is refused, and neither it nor the right one is in any log line.</summary>
    [Fact]
    public async Task A_post_with_the_wrong_token_is_rejected()
    {
        using var response = await _client.SendAsync(Usage(RealBody, token: "a-wrong-token-value"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(_board.Current.LastHeardAt);
        Assert.Equal(0, _log.Containing("a-wrong-token-value"));
        Assert.Equal(0, _log.Containing(Token));
    }

    /// <summary>
    /// <strong>A refusal on <c>/usage</c> counts as one on <c>/hook</c> does</strong> (ruling R3): the same fault, with
    /// the same remedy. Three within ten minutes show the refused notice.
    /// </summary>
    [Fact]
    public async Task A_refused_usage_post_is_counted_as_a_refusal()
    {
        using (var first = await _client.SendAsync(Usage(RealBody, token: null)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        }

        Assert.Equal(1, _health.RefusedCount);
        Assert.False(_health.RefusalsShowAt(_clock.Now));

        for (var i = 0; i < 2; i++)
        {
            using var response = await _client.SendAsync(Usage(RealBody, token: "a-wrong-token-value"));
        }

        Assert.Equal(3, _health.RefusedCount);
        Assert.True(_health.RefusalsShowAt(_clock.Now));
    }

    // ---- What an accepted post does -------------------------------------------------------------

    /// <summary>
    /// The guide's real body: <c>200</c> with an empty body, and its two limits on the board, with the session, the
    /// instant the post arrived, and the reset times in UTC. One Debug line names the kinds and the percentages.
    /// </summary>
    [Fact]
    public async Task A_real_body_answers_200_empty_and_reaches_the_board()
    {
        using var response = await _client.SendAsync(Usage(RealBody));

        await AssertPureObserverResponse(response);

        var current = _board.Current;
        Assert.Equal(_clock.Now, current.LastHeardAt);
        Assert.Collection(
            current.Windows,
            fiveHour =>
            {
                Assert.Equal("five_hour", fiveHour.Kind);
                Assert.Equal(24, fiveHour.PercentUsed);
                Assert.Equal(new DateTimeOffset(2026, 10, 8, 23, 10, 0, TimeSpan.Zero), fiveHour.ResetsAt);
                Assert.Equal(_clock.Now, fiveHour.HeardAt);
                Assert.Equal("ab86443d-84b5-4342-85b4-a13111a93020", fiveHour.SessionId);
            },
            sevenDay =>
            {
                Assert.Equal("seven_day", sevenDay.Kind);
                Assert.Equal(13, sevenDay.PercentUsed);
                Assert.Equal(new DateTimeOffset(2026, 10, 14, 13, 0, 0, TimeSpan.Zero), sevenDay.ResetsAt);
            });

        var line = Assert.Single(_log.Events, entry => entry.Level == Serilog.Events.LogEventLevel.Debug);
        Assert.Contains("five_hour 24%, seven_day 13%", RecordingLogSink.Render(line), StringComparison.Ordinal);
        Assert.Equal(0, _log.Containing(Token));
    }

    /// <summary>
    /// A body that is not JSON, or JSON of the wrong shape, answers the same and moves no reading: the readings held
    /// before are the same instances. It is still a post with the right token, so it is heard.
    /// </summary>
    [Fact]
    public async Task A_malformed_body_still_answers_200_empty_and_moves_no_reading()
    {
        using (var first = await _client.SendAsync(Usage(RealBody)))
        {
            await AssertPureObserverResponse(first);
        }

        var held = _board.Current.Windows;

        foreach (var body in new[] { "{not json", string.Empty, "[1, 2, 3]", """{"rateLimits": "five_hour"}""" })
        {
            _clock.Now += TimeSpan.FromSeconds(1);

            using var response = await _client.SendAsync(Usage(body));

            await AssertPureObserverResponse(response);
            Assert.Same(held, _board.Current.Windows);
            Assert.Equal(_clock.Now, _board.Current.LastHeardAt);
        }
    }

    /// <summary>
    /// <strong>A post with no limits in it is still heard</strong>, as for a session that is not on a subscription or
    /// before the first reading: <c>lastHeardAt</c> moves, and no reading is made.
    /// </summary>
    [Fact]
    public async Task A_post_with_no_limits_in_it_is_still_heard()
    {
        using var response = await _client.SendAsync(Usage(
            """{ "sessionId": "s-1", "context": { "tokens": 10, "window": 200000, "percent": 0 }, "rateLimits": [], "changed": ["context"] }"""));

        await AssertPureObserverResponse(response);
        Assert.Equal(_clock.Now, _board.Current.LastHeardAt);
        Assert.Empty(_board.Current.Windows);
    }

    /// <summary>Two hundred posts at once: each answers <c>200</c> empty, and the board holds one reading of each kind.</summary>
    [Fact]
    public async Task Two_hundred_posts_at_once_all_answer_200_and_leave_one_reading_of_each_kind()
    {
        var posts = Enumerable.Range(0, 200).Select(async _ =>
        {
            using var response = await _client.SendAsync(Usage(RealBody));
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
        });

        var answers = await Task.WhenAll(posts);

        Assert.All(answers, answer =>
        {
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            Assert.Equal(string.Empty, answer.Body);
        });
        Assert.Equal(["five_hour", "seven_day"], _board.Current.Windows.Select(window => window.Kind));
    }

    // ---- What a post never does -----------------------------------------------------------------

    /// <summary>
    /// <strong>Nothing of a usage post enters the event channel</strong>: no event for a good body, a malformed one, a
    /// body with no limits, or a refused one. A limit belongs to the account, not to a session.
    /// </summary>
    [Fact]
    public async Task A_usage_post_reaches_no_sink()
    {
        foreach (var (body, token) in new (string, string?)[]
        {
            (RealBody, Token), ("{not json", Token), ("""{"rateLimits": []}""", Token), (RealBody, null),
        })
        {
            using var response = await _client.SendAsync(Usage(body, token));
        }

        Assert.Empty(_sink.Published);
    }

    /// <summary>
    /// <strong>An accepted post does not move "last heard"</strong> (ruling R5): that instant says the script's path
    /// works, and a usage post comes by another path. The board keeps its own.
    /// </summary>
    [Fact]
    public async Task A_usage_post_does_not_move_last_heard()
    {
        using (var response = await _client.SendAsync(Usage(RealBody)))
        {
            await AssertPureObserverResponse(response);
        }

        Assert.Equal(_clock.Now, _board.Current.LastHeardAt);
        Assert.Null(_health.LastHeardAt);
        Assert.Null(_health.Report().LastHeardAt);
    }

    /// <summary>
    /// <strong>A fault after the token check still answers <c>200</c> empty</strong> (Impl §3.3). A body over the
    /// limit makes the body's read throw, which no step names; the catch-all takes it, and nothing is kept.
    /// </summary>
    [Fact]
    public async Task A_post_whose_read_throws_still_answers_200_empty()
    {
        using var response = await _client.SendAsync(Usage(new string(' ', BodyLimit * 2) + RealBody));

        await AssertPureObserverResponse(response);
        Assert.Null(_board.Current.LastHeardAt);
        Assert.Single(_log.Matching("A /usage post failed after the token check. Answering 200 regardless"));
    }

    private static HttpRequestMessage Usage(string json, string? token = Token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/usage")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.Add(IngressToken.HeaderName, token);
        }

        return request;
    }

    /// <summary>Impl §3.3's whole contract: 200, an empty body, no decision field.</summary>
    private static async Task AssertPureObserverResponse(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(string.IsNullOrEmpty(body), $"Impl §3.3 requires an empty body. Got: '{body}'");
    }
}
