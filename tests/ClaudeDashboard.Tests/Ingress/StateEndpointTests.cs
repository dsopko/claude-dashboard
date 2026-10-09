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
/// <c>GET /state</c> over a real socket (T1.46): the current token is required.
/// </summary>
/// <remarks>
/// Since T1.48 every dashboard makes a token at its start, so there is no longer a run without one,
/// and the <c>404</c> this endpoint gave in that case went with it. <c>/health</c> still answers
/// without a token.
/// </remarks>
public sealed class StateEndpointTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Token = "state-test-token";

    /// <summary>
    /// <c>/health</c> answers without a token, and <c>/hook</c> refuses a post without one (T1.48).
    /// </summary>
    [Fact]
    public async Task Health_answers_without_a_token_and_hook_refuses_a_post_without_one()
    {
        await using var host = await StateHost.Start(Token);

        using var health = await host.Client.GetAsync("/health");
        using var content = new StringContent("""{"hook_event_name":"Stop","session_id":"s-1"}""", Encoding.UTF8, "application/json");
        using var hook = await host.Client.PostAsync("/hook", content);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, hook.StatusCode);
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
    /// <strong>A session in Error reports its kind</strong> (T1.53, issue #67): a
    /// <c>StopFailure</c> posted to <c>/hook</c> as the wire sends it, with <c>error</c>, answers
    /// <c>"errorKind": "rate_limit"</c> on <c>/state</c>.
    /// </summary>
    [Fact]
    public async Task A_session_in_Error_reports_the_kind_the_wire_sent()
    {
        await using var host = await StateHost.Start(Token);

        host.Registry.Apply(new UserPromptSubmit
        {
            SessionId = new SessionId("s-1"),
            Timestamp = FakeClock.DefaultStart,
            Cwd = @"C:\work",
            PromptId = "p-1",
            Prompt = "run the tests",
        });

        using var post = new HttpRequestMessage(HttpMethod.Post, "/hook")
        {
            Content = new StringContent(
                """{"hook_event_name":"StopFailure","session_id":"s-1","cwd":"C:\\work","prompt_id":"p-1","error":"rate_limit"}""",
                Encoding.UTF8,
                "application/json"),
        };
        post.Headers.Add(IngressToken.HeaderName, Token);
        using var posted = await host.Client.SendAsync(post);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        host.Registry.Apply(Assert.IsType<StopFailure>(Assert.Single(host.Sink.Published)));

        using var response = await host.Client.SendAsync(Get("/state", Token));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var session = Assert.Single(document.RootElement.GetProperty("sessions").EnumerateArray().ToList());

        Assert.Equal("Error", session.GetProperty("state").GetString());
        Assert.Equal("rate_limit", session.GetProperty("errorKind").GetString());
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

    // ---- The plan's usage (MOD.5, issue #133) ---------------------------------------------------

    /// <summary>The first body the usage mod sent in the guide's lab ("What you get").</summary>
    private const string RealUsageBody =
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

    /// <summary>
    /// <strong>The report's members, by name and in order, as they are with <c>usage</c></strong> (MOD.5). The six
    /// that were there before keep their names and places; <c>usage</c> comes last, after <c>health</c>.
    /// </summary>
    [Fact]
    public async Task The_report_names_its_members_as_before_and_usage_last()
    {
        await using var host = await StateHost.Start(Token);

        using var response = await host.Client.SendAsync(Get("/state", Token));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(
            ["publishedAt", "sessionCount", "bands", "tray", "sessions", "health", "usage"],
            document.RootElement.EnumerateObject().Select(member => member.Name));
    }

    /// <summary><strong><c>usage</c> is null before the first post</strong>, and present as a member.</summary>
    [Fact]
    public async Task State_before_any_post_says_usage_is_null()
    {
        await using var host = await StateHost.Start(Token);

        using var response = await host.Client.SendAsync(Get("/state", Token));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("usage").ValueKind);
    }

    /// <summary>
    /// <strong>The guide's real body, posted to <c>/usage</c> after the consumer's last publication, is in the next
    /// <c>/state</c></strong>: <c>lastHeardAt</c> and the two windows, in the order of their kinds, with camelCase
    /// names and each instant in UTC, ending in <c>Z</c>. Reading <c>/state</c> changes no reading.
    /// </summary>
    [Fact]
    public async Task State_carries_the_readings_in_camel_case_with_instants_in_UTC()
    {
        await using var host = await StateHost.Start(Token);

        // A publication, then the post: the post is later than anything the consumer published.
        host.Registry.Apply(new UserPromptSubmit
        {
            SessionId = new SessionId("s-1"),
            Timestamp = FakeClock.DefaultStart,
            Cwd = @"C:\work",
            PromptId = "p-1",
            Prompt = "go",
        });
        var published = host.Board.Current;

        using (var post = await host.Client.SendAsync(Post("/usage", RealUsageBody, Token)))
        {
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        }

        var held = host.Usage.Current;

        using var response = await host.Client.SendAsync(Get("/state", Token));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var usage = document.RootElement.GetProperty("usage");

        output.WriteLine(usage.GetRawText());

        Assert.Same(published, host.Board.Current);
        Assert.Same(held, host.Usage.Current);
        Assert.Equal(["lastHeardAt", "windows"], usage.EnumerateObject().Select(member => member.Name));
        Assert.Equal("2026-08-24T09:00:00Z", usage.GetProperty("lastHeardAt").GetString());

        var windows = usage.GetProperty("windows").EnumerateArray().ToList();

        Assert.Collection(
            windows,
            fiveHour =>
            {
                Assert.Equal(
                    ["kind", "percentUsed", "resetsAt", "heardAt", "sessionId"],
                    fiveHour.EnumerateObject().Select(member => member.Name));
                Assert.Equal("five_hour", fiveHour.GetProperty("kind").GetString());
                Assert.Equal(24, fiveHour.GetProperty("percentUsed").GetDouble());
                Assert.Equal("2026-10-08T23:10:00Z", fiveHour.GetProperty("resetsAt").GetString());
                Assert.Equal("2026-08-24T09:00:00Z", fiveHour.GetProperty("heardAt").GetString());
                Assert.Equal("ab86443d-84b5-4342-85b4-a13111a93020", fiveHour.GetProperty("sessionId").GetString());
            },
            sevenDay =>
            {
                Assert.Equal("seven_day", sevenDay.GetProperty("kind").GetString());
                Assert.Equal(13, sevenDay.GetProperty("percentUsed").GetDouble());
                Assert.Equal("2026-10-14T13:00:00Z", sevenDay.GetProperty("resetsAt").GetString());
            });
    }

    /// <summary>
    /// <strong>A limit whose reset time has passed is not in <c>usage</c></strong>, read at the clock's instant: at
    /// the five-hour limit's reset time it is out, and the weekly one and <c>lastHeardAt</c> stay. The board still
    /// holds it; only the answer leaves it out.
    /// </summary>
    [Fact]
    public async Task State_leaves_out_a_limit_whose_reset_time_has_passed()
    {
        await using var host = await StateHost.Start(Token);

        using (var post = await host.Client.SendAsync(Post("/usage", RealUsageBody, Token)))
        {
            Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        }

        host.Clock.Now = new DateTimeOffset(2026, 10, 8, 23, 10, 0, TimeSpan.Zero);

        using var response = await host.Client.SendAsync(Get("/state", Token));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var usage = document.RootElement.GetProperty("usage");

        var window = Assert.Single(usage.GetProperty("windows").EnumerateArray().ToList());
        Assert.Equal("seven_day", window.GetProperty("kind").GetString());
        Assert.Equal("2026-08-24T09:00:00Z", usage.GetProperty("lastHeardAt").GetString());
        Assert.Equal(2, host.Usage.Current.Windows.Count);
    }

    private static HttpRequestMessage Post(string path, string json, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(IngressToken.HeaderName, token);
        return request;
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
            Sink = (RecordingEventSink)app.Services.GetRequiredService<IEventSink>();
            Usage = app.Services.GetRequiredService<UsageBoard>();
            Clock = (FakeClock)app.Services.GetRequiredService<IClock>();
        }

        public HttpClient Client { get; }

        public SessionRegistry Registry { get; }

        public StateBoard Board { get; }

        /// <summary>What /hook published. Nothing drains it: a test applies what it needs.</summary>
        public RecordingEventSink Sink { get; }

        /// <summary>The plan's limits, as /usage keeps them (MOD.5).</summary>
        public UsageBoard Usage { get; }

        /// <summary>The clock /state and /usage read; a test moves it.</summary>
        public FakeClock Clock { get; }

        public static async Task<StateHost> Start(string token)
        {
            var root = Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));
            var paths = new DashboardPaths(root);

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(System.Net.IPAddress.Loopback, 0));

            var clock = new FakeClock();
            var guard = new SingleWriterGuard();

            builder.Services.AddSingleton<Serilog.ILogger>(Logger.None);
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton<IClock>(clock);
            builder.Services.AddSingleton(new IngressToken(token));
            builder.Services.AddSingleton(sp => new HookEventMapper(sp.GetRequiredService<IClock>()));
            builder.Services.AddSingleton<IEventSink>(new RecordingEventSink());
            builder.Services.AddSingleton(new UsageBoard());
            builder.Services.AddSingleton(new SessionRegistry(guard));
            builder.Services.AddSingleton(new SoundPolicyEngine(new RecordingSoundPlayer(), clock, guard, new SoundPolicyOptions()));
            builder.Services.AddSingleton(sp => new StateBoard(
                sp.GetRequiredService<SessionRegistry>(),
                sp.GetRequiredService<SoundPolicyEngine>(),
                clock,
                Logger.None,
                new ClaudeDashboard.App.Configuration.RosterStore(new RecordingEventSink())));

            var app = builder.Build();

            // As AppHost wires it: the engine hears the Registry first, then the board subscribes.
            var registry = app.Services.GetRequiredService<SessionRegistry>();
            var sound = app.Services.GetRequiredService<SoundPolicyEngine>();
            registry.SessionChanged += (_, e) => sound.OnSessionChanged(e.Session, e.Session.WorkspaceGroup);
            _ = app.Services.GetRequiredService<StateBoard>();

            app.MapIngress();
            await app.StartAsync();

            // The port Windows chose as Kestrel listened, so nothing could take it first (T1.77, issue #120).
            var port = new Uri(app.Urls.Single()).Port;

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
