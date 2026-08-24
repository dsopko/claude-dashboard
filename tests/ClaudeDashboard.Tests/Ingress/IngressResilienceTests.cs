using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Ingress;

/// <summary>An <see cref="IEventSink"/> that fails, to prove ingress survives one that does.</summary>
/// <remarks>
/// The port's contract says <c>TryPublish</c> never throws — but a contract is a promise, not a
/// guard, and this is the boundary where the difference matters. <c>RecordingEventSink</c> in
/// this very test project throws on a null event, so a sink that throws is not hypothetical.
/// </remarks>
file sealed class ThrowingSink : IEventSink
{
    public bool TryPublish(InboundEvent inboundEvent) =>
        throw new InvalidOperationException("deliberate sink failure");
}

/// <summary>
/// Impl §3.3's pure-observer guarantee under failures nobody anticipated.
/// </summary>
/// <remarks>
/// <para>
/// §3.3 is unconditional: <c>/hook</c> answers <c>200</c> with an empty body and no decision
/// field. Catching the exceptions one happens to think of satisfies that only for as long as
/// the list stays complete — and it will not, because the code below the token check calls a
/// sink, a serializer, a mapper and the container, any of which can throw something new.
/// </para>
/// <para>
/// These tests exist to make the guarantee structural. A dashboard that answers <c>500</c> has
/// stopped being an observer and started being something Claude Code must react to, which is
/// exactly what §3.3 forbids.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class IngressResilienceTests : IAsyncLifetime
{
    private const string Token = "resilience-token";

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var port = FreePort();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port));

        builder.Services.AddSingleton<Serilog.ILogger>(Logger.None);
        builder.Services.AddSingleton<IClock>(new FakeClock());
        builder.Services.AddSingleton(new IngressToken(Token));
        builder.Services.AddSingleton(sp => new HookEventMapper(sp.GetRequiredService<IClock>()));
        builder.Services.AddSingleton<IEventSink>(new ThrowingSink());

        _app = builder.Build();
        _app.MapIngress(onShow: () => throw new InvalidOperationException("deliberate show failure"));

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
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static HttpRequestMessage Hook(string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/hook")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(IngressToken.HeaderName, Token);
        return request;
    }

    /// <summary>
    /// The case the review reproduced: a sink that throws produced a <c>500</c>, because the
    /// handler caught only the exceptions it had thought of.
    /// </summary>
    [Fact]
    public async Task A_sink_that_throws_still_answers_200_empty()
    {
        var response = await _client.SendAsync(Hook("""
            {"hook_event_name":"Stop","session_id":"s-1","last_assistant_message":"done"}
            """));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Every consumed event reaches the sink, so every one of them can be thrown from.</summary>
    [Theory]
    [InlineData("SessionStart")]
    [InlineData("UserPromptSubmit")]
    [InlineData("Notification")]
    [InlineData("Stop")]
    [InlineData("StopFailure")]
    [InlineData("SessionEnd")]
    [InlineData("CwdChanged")]
    public async Task A_sink_that_throws_answers_200_for_every_consumed_event(string name)
    {
        var response = await _client.SendAsync(
            Hook($$"""{"hook_event_name":"{{name}}","session_id":"s-1","cwd":"C:\\w"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A rejected payload never reaches the sink, so this proves the guarantee holds on the
    /// path that does not throw as well as the one that does.
    /// </summary>
    [Fact]
    public async Task A_rejected_event_still_answers_200_even_with_a_failing_sink()
    {
        var response = await _client.SendAsync(Hook("""{"hook_event_name":"Ack","session_id":"s-1"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// T1.15 supplies the <c>/show</c> action and it reaches the UI, so it can throw for
    /// reasons ingress cannot anticipate either.
    /// </summary>
    [Fact]
    public async Task A_show_action_that_throws_still_answers_200()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/show");
        request.Headers.Add(IngressToken.HeaderName, Token);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The token check sits above the catch-all deliberately: a request without the token did
    /// not come from Claude Code, so §3.3's guarantee about Claude Code's turns does not apply
    /// to it. A failing sink must not turn that into a 200.
    /// </summary>
    [Fact]
    public async Task A_failing_sink_does_not_soften_the_token_check()
    {
        var response = await _client.PostAsync(
            "/hook",
            new StringContent("""{"hook_event_name":"Stop","session_id":"s-1"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
