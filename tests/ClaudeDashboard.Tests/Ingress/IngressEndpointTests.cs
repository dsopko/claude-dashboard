using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Ingress;

/// <summary>
/// The endpoints against a real Kestrel on a real loopback socket (Impl §3.2, §3.3, §3.4).
/// </summary>
/// <remarks>
/// <para>
/// A real server and a real <see cref="HttpClient"/>, not an in-memory test handler. The three
/// things most worth knowing about this boundary — that it answers <c>200</c> with an empty
/// body, that it rejects a bad token, and that it is not reachable from off-machine — are all
/// properties of the socket and the pipeline, and an in-memory harness would assert none of
/// them.
/// </para>
/// <para>
/// The negative cases come first on purpose. A suite that only ever posts well-formed events
/// from a well-behaved client says nothing about a front door.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class IngressEndpointTests : IAsyncLifetime
{
    private const string Token = "test-token-value";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private RecordingEventSink _sink = null!;
    private int _shown;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = FreePort();
        var paths = new DashboardPaths(_root);
        new SettingsStore(paths).Save(new DashboardSettings { Port = _port });

        _sink = new RecordingEventSink();
        _app = BuildIngress(paths, _sink, new IngressToken(Token), () => Interlocked.Increment(ref _shown));

        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        // Deliberately not Log.CloseAndFlush(): these tests use Logger.None, and closing the
        // static logger would tear down the one AppHostTests is writing to in parallel.
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A sink may still hold a log file; the temp folder is disposable.
            }
        }
    }

    /// <summary>
    /// Builds just the ingress surface, so these tests exercise the endpoints without the
    /// settings-and-logging composition <see cref="AppHost"/> performs.
    /// </summary>
    private static WebApplication BuildIngress(DashboardPaths paths, IEventSink sink, IngressToken token, Action onShow)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ListenLocalhost(new SettingsStore(paths).Load().Settings.Port));

        builder.Services.AddSingleton<Serilog.ILogger>(Logger.None);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<IClock>(new FakeClock());
        builder.Services.AddSingleton(token);
        builder.Services.AddSingleton(sp => new HookEventMapper(sp.GetRequiredService<IClock>()));
        builder.Services.AddSingleton(sink);

        var app = builder.Build();
        app.MapIngress(onShow);
        return app;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static HttpRequestMessage Hook(string json, string? token = Token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/hook")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.Add(IngressToken.HeaderName, token);
        }

        return request;
    }

    /// <summary>Asserts Impl §3.3's whole contract: 200, empty body, no decision field.</summary>
    private static async Task AssertPureObserverResponse(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            string.IsNullOrEmpty(body),
            $"Impl §3.3 requires an empty body — a decision field would let the dashboard alter a turn. Got: '{body}'");
    }

    // ---- What an attacker sends ---------------------------------------------------------------

    [Fact]
    public async Task A_post_with_no_token_is_rejected()
    {
        var response = await _client.SendAsync(
            Hook("""{"hook_event_name":"Stop","session_id":"s-1"}""", token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_sink.Published);
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("")]
    [InlineData("test-token-valu")]
    [InlineData("TEST-TOKEN-VALUE")]
    // A trailing space is deliberately absent: HTTP strips optional whitespace around a header
    // value, so " token" and "token " never reach the check as distinct strings.
    public async Task A_post_with_the_wrong_token_is_rejected(string token)
    {
        var response = await _client.SendAsync(
            Hook("""{"hook_event_name":"Stop","session_id":"s-1"}""", token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(_sink.Published);
    }

    /// <summary>
    /// The forged acknowledgment, over the wire. It must be refused <em>before</em> reaching
    /// the pipeline — a rejection that still published would silence a session that needs the
    /// operator.
    /// </summary>
    [Fact]
    public async Task A_forged_Ack_is_refused_without_reaching_the_sink()
    {
        var response = await _client.SendAsync(
            Hook("""{"hook_event_name":"Ack","session_id":"s-1","source":"Manual"}"""));

        await AssertPureObserverResponse(response);
        Assert.Empty(_sink.Published);
    }

    [Theory]
    [InlineData("PermissionRequest")]
    [InlineData("SubagentStop")]
    [InlineData("Invented")]
    public async Task An_unconsumed_event_is_refused_without_reaching_the_sink(string name)
    {
        var response = await _client.SendAsync(
            Hook($$"""{"hook_event_name":"{{name}}","session_id":"s-1"}"""));

        await AssertPureObserverResponse(response);
        Assert.Empty(_sink.Published);
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"hook_event_name\": }")]
    public async Task A_malformed_body_still_answers_200_empty(string body)
    {
        var response = await _client.SendAsync(Hook(body));

        await AssertPureObserverResponse(response);
        Assert.Empty(_sink.Published);
    }

    [Fact]
    public async Task An_event_with_no_session_id_still_answers_200_empty()
    {
        var response = await _client.SendAsync(Hook("""{"hook_event_name":"Stop"}"""));

        await AssertPureObserverResponse(response);
        Assert.Empty(_sink.Published);
    }

    /// <summary>
    /// A full pipeline is a real state (Impl §4). The tempting wrong answer is a 503 — which
    /// would break pure-observer in the one situation where it matters most, because a
    /// dashboard under load must never push back on Claude Code.
    /// </summary>
    [Fact]
    public async Task A_refused_publish_still_answers_200_empty()
    {
        _sink.Capacity = 0;

        var response = await _client.SendAsync(
            Hook("""{"hook_event_name":"Stop","session_id":"s-1"}"""));

        await AssertPureObserverResponse(response);
        Assert.Empty(_sink.Published);
        Assert.Equal(1, _sink.RefusedCount);
    }

    /// <summary>
    /// Impl §3.1: loopback only. Verified by trying to reach the same port on this machine's
    /// routable address, which is what an off-machine client would use.
    /// </summary>
    [Fact]
    public async Task The_endpoint_is_not_reachable_off_loopback()
    {
        var routable = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(address =>
                address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address));

        if (routable is null)
        {
            // Nothing off-machine can reach it because there is no off-machine address at all.
            return;
        }

        using var probe = new TcpClient();
        var connect = probe.ConnectAsync(routable, _port);
        var finished = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(3)));

        if (finished == connect && connect.IsCompletedSuccessfully)
        {
            Assert.Fail(
                $"Ingress accepted a connection on {routable}:{_port}. Impl §3.1 requires loopback only — " +
                "nothing off-machine may post events.");
        }
    }

    // ---- What Claude Code sends ------------------------------------------------------------------

    [Fact]
    public async Task A_well_formed_event_answers_200_empty_and_reaches_the_sink()
    {
        var response = await _client.SendAsync(Hook("""
            {
              "hook_event_name": "UserPromptSubmit",
              "session_id": "s-1",
              "prompt_id": "p-1",
              "prompt": "run the tests",
              "cwd": "C:\\projects\\dashboard"
            }
            """));

        await AssertPureObserverResponse(response);

        var published = Assert.Single(_sink.Published);
        var prompt = Assert.IsType<UserPromptSubmit>(published);
        Assert.Equal("run the tests", prompt.Prompt);
        Assert.Equal("p-1", prompt.PromptId);
    }

    [Theory]
    [InlineData("SessionStart")]
    [InlineData("UserPromptSubmit")]
    [InlineData("Notification")]
    [InlineData("Stop")]
    [InlineData("StopFailure")]
    [InlineData("SessionEnd")]
    [InlineData("CwdChanged")]
    public async Task Every_consumed_event_reaches_the_sink(string name)
    {
        var response = await _client.SendAsync(
            Hook($$"""{"hook_event_name":"{{name}}","session_id":"s-1","cwd":"C:\\w"}"""));

        await AssertPureObserverResponse(response);
        Assert.Single(_sink.Published);
    }

    // ---- The other two endpoints ------------------------------------------------------------------

    /// <summary>
    /// <c>/health</c> takes no token, and that is deliberate rather than an oversight.
    /// </summary>
    /// <remarks>
    /// <strong>Do not "fix" this to expect 401.</strong> A starting process probes this endpoint
    /// to learn whether the dashboard already holding the port is a copy of itself (Impl §5.3).
    /// In the fast-user-switching case it is not: it belongs to another signed-in user, with
    /// another data folder and therefore another token, which the prober could never present. A
    /// token check here would look like a consistency improvement and would break single-instance
    /// detection in the quiet direction — every other dashboard would read as a stranger.
    /// </remarks>
    [Fact]
    public async Task Health_answers_without_a_token()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The health body carries this instance's gate name (Impl §3.2, §5.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The body is load-bearing from T1.15 onwards: a starting dashboard decides whether to run
    /// at all by reading it. Nothing observed it before — the test above reads the status code
    /// and never the body — so an endpoint that answered nothing, or answered without an
    /// identity, would have passed everything.
    /// </para>
    /// <para>
    /// The expected name is computed from <see cref="SingleInstanceGate.NameFor"/> rather than
    /// written out. A literal would be a second copy of the naming rule, and the copy is what
    /// drifts: the test would then go on passing while the two sides disagreed, which is the
    /// failure it exists to catch.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Health_reports_this_instances_gate_name()
    {
        var body = await _client.GetStringAsync("/health");

        using var document = JsonDocument.Parse(body);

        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            SingleInstanceGate.NameFor(_root),
            document.RootElement.GetProperty("instance").GetString());
    }

    /// <summary>
    /// An authorized <c>/show</c> actually raises the window, not merely answers 200.
    /// </summary>
    /// <remarks>
    /// The status code alone would also be produced by the handler this endpoint had before
    /// T1.15, whose <c>onShow</c> was never supplied by anything and did nothing at all.
    /// </remarks>
    [Fact]
    public async Task Show_raises_the_window_when_authorized()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/show");
        request.Headers.Add(IngressToken.HeaderName, Token);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref _shown));
    }

    /// <summary>
    /// An unauthorized <c>/show</c> is refused <em>before</em> it reaches the window.
    /// </summary>
    /// <remarks>
    /// The 401 on its own says nothing about ordering: a handler that surfaced the window and
    /// then returned 401 would satisfy it. Raising another user's window is precisely the harm
    /// here, so the count is what matters.
    /// </remarks>
    [Fact]
    public async Task Show_is_rejected_without_a_token()
    {
        var response = await _client.PostAsync("/show", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, Volatile.Read(ref _shown));
    }
}

/// <summary>The token check itself (Impl §3.4).</summary>
public sealed class IngressTokenTests
{
    [Fact]
    public void A_configured_token_must_match_exactly()
    {
        var token = new IngressToken("secret");

        Assert.True(token.IsConfigured);
        Assert.True(token.Accepts("secret"));
        Assert.False(token.Accepts("Secret"));
        Assert.False(token.Accepts("secret "));
        Assert.False(token.Accepts("secre"));
        Assert.False(token.Accepts(null));
        Assert.False(token.Accepts(string.Empty));
    }

    /// <summary>
    /// Impl §3.4 calls the shared secret optional. With none set, ingress accepts — the
    /// endpoint is still loopback-bound, which is the boundary the token narrows rather than
    /// creates.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unconfigured_token_accepts_anything(string? configured)
    {
        var token = new IngressToken(configured);

        Assert.False(token.IsConfigured);
        Assert.True(token.Accepts(null));
        Assert.True(token.Accepts("anything"));
    }

    /// <summary>Impl §3.4, §9.2: the token comes from the environment, never a committed file.</summary>
    [Fact]
    public void The_token_is_named_by_the_environment_variable_the_specs_use()
    {
        Assert.Equal("CLAUDE_DASHBOARD_TOKEN", IngressToken.EnvironmentVariable);
        Assert.Equal("X-Dashboard-Token", IngressToken.HeaderName);
    }
}
