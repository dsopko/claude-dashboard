using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using ClaudeDashboard.Tests.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Ingress;

/// <summary>
/// <c>GET /state</c> over a real socket (T1.46): a token is required here and only here.
/// </summary>
/// <remarks>
/// <para>
/// <strong>404 with no token configured, not 401.</strong> <see cref="IngressToken.Accepts"/>
/// passes everything when no token is set, which is right for <c>/hook</c> and wrong for the first
/// endpoint that emits. With none configured the endpoint is not usable, and a <c>404</c> does not
/// advertise it as usable the way a <c>401</c> would. It does not hide the route: a
/// <c>POST /state</c> answers <c>405</c>.
/// </para>
/// <para>
/// Each test builds its own host, because the token is a host-wide choice and the two cases need
/// opposite ones.
/// </para>
/// </remarks>
public sealed class StateEndpointTests
{
    private const string Token = "state-test-token";

    [Fact]
    public async Task With_no_token_configured_state_is_not_found()
    {
        await using var host = await StateHost.Start(token: null);

        using var bare = await host.Client.GetAsync("/state");
        using var presented = await host.Client.SendAsync(Get("/state", "anything"));

        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, presented.StatusCode);
    }

    /// <summary>The same host still answers <c>/health</c> and swallows <c>/hook</c> as before.</summary>
    [Fact]
    public async Task With_no_token_configured_health_and_hook_are_unchanged()
    {
        await using var host = await StateHost.Start(token: null);

        using var health = await host.Client.GetAsync("/health");
        using var content = new StringContent("""{"hook_event_name":"Stop","session_id":"s-1"}""", Encoding.UTF8, "application/json");
        using var hook = await host.Client.PostAsync("/hook", content);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, hook.StatusCode);
        Assert.Empty(await hook.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task With_a_token_configured_no_token_presented_is_unauthorized()
    {
        await using var host = await StateHost.Start(Token);

        using var response = await host.Client.GetAsync("/state");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("state-test-toke")]
    [InlineData("STATE-TEST-TOKEN")]
    public async Task With_a_token_configured_a_wrong_token_is_unauthorized(string presented)
    {
        await using var host = await StateHost.Start(Token);

        using var response = await host.Client.SendAsync(Get("/state", presented));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The right token gets the report, as JSON, with the field names a reader would expect.
    /// </summary>
    [Fact]
    public async Task With_the_right_token_state_answers_the_report()
    {
        await using var host = await StateHost.Start(Token);

        host.Registry.Apply(new UserPromptSubmit
        {
            SessionId = new SessionId("s-1"),
            Timestamp = FakeClock.DefaultStart,
            Cwd = @"C:\work",
            PromptId = "p-1",
            Prompt = "PROMPT-MARKER-e01f",
            SessionTitle = "Director",
        });
        host.Registry.Apply(new Notification
        {
            SessionId = new SessionId("s-1"),
            Timestamp = FakeClock.DefaultStart.AddSeconds(2),
            Cwd = @"C:\work",
            NotificationType = "permission_prompt",
        });

        using var response = await host.Client.SendAsync(Get("/state", Token));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("PROMPT-MARKER-e01f", body, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("sessionCount").GetInt32());
        Assert.Equal(1, root.GetProperty("bands").GetProperty("needsYou").GetInt32());
        Assert.Equal("NeedsPermission", root.GetProperty("tray").GetProperty("worst").GetString());

        var session = Assert.Single(root.GetProperty("sessions").EnumerateArray().ToList());

        Assert.Equal("s-1", session.GetProperty("id").GetString());
        Assert.Equal("NeedsPermission", session.GetProperty("state").GetString());
        Assert.Equal("NeedsYou", session.GetProperty("band").GetString());
        Assert.Equal("Director", session.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.String, session.GetProperty("nextNudgeAt").ValueKind);
    }

    /// <summary>
    /// A request changes nothing: the Registry and the report are the same after it as before.
    /// </summary>
    [Fact]
    public async Task A_state_request_changes_nothing()
    {
        await using var host = await StateHost.Start(Token);

        host.Registry.Apply(new UserPromptSubmit
        {
            SessionId = new SessionId("s-1"),
            Timestamp = FakeClock.DefaultStart,
            Cwd = @"C:\work",
            PromptId = "p-1",
            Prompt = "go",
        });

        var before = host.Registry.Sessions[new SessionId("s-1")];
        var report = host.Board.Current;

        using var first = await host.Client.SendAsync(Get("/state", Token));
        using var second = await host.Client.SendAsync(Get("/state", Token));

        Assert.Same(before, host.Registry.Sessions[new SessionId("s-1")]);
        Assert.Same(report, host.Board.Current);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Requests served while the Registry's writer is applying events neither fail nor disturb it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The writer here is one thread applying new sessions, as the consumer does; the readers are
    /// Kestrel's request threads. Every event adds a session, because adding a key is what
    /// invalidates an enumeration of a <see cref="Dictionary{TKey,TValue}"/> — so a request that
    /// walked <see cref="SessionRegistry.Sessions"/> instead of the published report would throw
    /// here and answer <c>500</c>. That is the defect this test was planted against, and it failed.
    /// </para>
    /// <para>
    /// A bulk copy such as <c>[.. Sessions.Values]</c> fails differently, and not reliably. It
    /// sizes the destination from <c>Count</c> and then copies with no version check, so a writer
    /// adding a key between the two makes <c>ValueCollection.CopyTo</c> throw
    /// <see cref="ArgumentException"/> ("Destination array is not long enough") — the review
    /// measured that in <c>StateHostTests</c>. Without that timing it can take an entry whose
    /// count was raised before its value was written. Either way it is wrong, and this test
    /// catches it only by chance. <c>The_endpoint_serves_the_published_report_and_nothing_else</c>
    /// is the test that holds the handler to the published reference.
    /// </para>
    /// <para>
    /// The writer's side is checked as firmly as the reader's: every event applied, none lost, and
    /// the last report holds every session.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Requests_served_while_events_arrive_neither_fail_nor_disturb_the_writer()
    {
        const int Sessions = 600;

        await using var host = await StateHost.Start(Token);

        var outcomes = new List<ApplyOutcome>(Sessions);
        var writer = new Thread(() =>
        {
            for (var i = 0; i < Sessions; i++)
            {
                outcomes.Add(host.Registry.Apply(new UserPromptSubmit
                {
                    SessionId = new SessionId($"s-{i}"),
                    Timestamp = FakeClock.DefaultStart.AddSeconds(i),
                    Cwd = @"C:\work",
                    PromptId = $"p-{i}",
                    Prompt = "go",
                }));
            }
        });

        writer.Start();

        var requests = 0;
        var statuses = new List<HttpStatusCode>();

        while (writer.IsAlive || requests == 0)
        {
            using var response = await host.Client.SendAsync(Get("/state", Token));
            statuses.Add(response.StatusCode);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.True(document.RootElement.GetProperty("sessionCount").GetInt32() <= Sessions);
            }

            requests++;
        }

        writer.Join();

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
        Assert.True(requests > 1, $"only {requests} request overlapped the writer; the test proved nothing");
        Assert.Equal(Sessions, outcomes.Count(outcome => outcome == ApplyOutcome.Applied));
        Assert.Equal(Sessions, host.Board.Current.SessionCount);
    }

    /// <summary>
    /// The handler serves the report the board published, and builds nothing of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The board is disposed, so it stops hearing the Registry, and then the Registry changes. The
    /// body must still describe the world the board last published. A handler that built its
    /// report per request — from <see cref="SessionRegistry.Sessions"/>, the sound engine and the
    /// clock — would describe the changed world instead, and fail here.
    /// </para>
    /// <para>
    /// This is the guard the review asked for (P1b). The concurrency tests catch an off-thread
    /// read only when it happens to collide with a write, and
    /// <c>A_state_request_changes_nothing</c> cannot tell a per-request build from the published
    /// report when the clock is injected, because both give the same body.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_endpoint_serves_the_published_report_and_nothing_else()
    {
        await using var host = await StateHost.Start(Token);

        host.Registry.Apply(new UserPromptSubmit
        {
            SessionId = new SessionId("published"),
            Timestamp = FakeClock.DefaultStart,
            Cwd = @"C:\work",
            PromptId = "p-1",
            Prompt = "go",
        });

        host.Board.Dispose();

        host.Registry.Apply(new UserPromptSubmit
        {
            SessionId = new SessionId("unpublished"),
            Timestamp = FakeClock.DefaultStart.AddSeconds(1),
            Cwd = @"C:\work",
            PromptId = "p-1",
            Prompt = "go",
        });

        using var response = await host.Client.SendAsync(Get("/state", Token));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, host.Registry.Sessions.Count);
        Assert.Equal(1, root.GetProperty("sessionCount").GetInt32());
        Assert.Equal("published", Assert.Single(root.GetProperty("sessions").EnumerateArray().ToList()).GetProperty("id").GetString());
    }

    private static HttpRequestMessage Get(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(IngressToken.HeaderName, token);
        return request;
    }

    /// <summary>A slim ingress host with a Registry, a sound engine and a board behind it.</summary>
    private sealed class StateHost : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WebApplication _app;

        private StateHost(string root, WebApplication app, HttpClient client)
        {
            _root = root;
            _app = app;
            Client = client;
            Registry = app.Services.GetRequiredService<SessionRegistry>();
            Board = app.Services.GetRequiredService<StateBoard>();
        }

        public HttpClient Client { get; }

        public SessionRegistry Registry { get; }

        public StateBoard Board { get; }

        public static async Task<StateHost> Start(string? token)
        {
            var root = Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));
            var port = AppHostTests.FreePort();
            var paths = new DashboardPaths(root);

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port));

            var clock = new FakeClock();
            var guard = new SingleWriterGuard();

            builder.Services.AddSingleton<Serilog.ILogger>(Logger.None);
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton<IClock>(clock);
            builder.Services.AddSingleton(new IngressToken(token));
            builder.Services.AddSingleton(sp => new HookEventMapper(sp.GetRequiredService<IClock>()));
            builder.Services.AddSingleton<IEventSink>(new RecordingEventSink());
            builder.Services.AddSingleton(new SessionRegistry(guard));
            builder.Services.AddSingleton(new SoundPolicyEngine(new RecordingSoundPlayer(), clock, guard, new SoundPolicyOptions()));
            builder.Services.AddSingleton(sp => new StateBoard(
                sp.GetRequiredService<SessionRegistry>(),
                sp.GetRequiredService<SoundPolicyEngine>(),
                clock,
                Logger.None));

            var app = builder.Build();

            // As AppHost wires it: the engine hears the Registry first, then the board subscribes.
            var registry = app.Services.GetRequiredService<SessionRegistry>();
            var sound = app.Services.GetRequiredService<SoundPolicyEngine>();
            registry.SessionChanged += (_, e) => sound.OnSessionChanged(e.Session, e.Session.WorkspaceGroup);
            _ = app.Services.GetRequiredService<StateBoard>();

            app.MapIngress();
            await app.StartAsync();

            return new StateHost(root, app, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();

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
    }
}
