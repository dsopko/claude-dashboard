using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// The class that decides <em>whether</em> to register, on <em>which</em> URL (Impl §9.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The parts were tested and the composition was not.</strong> The merge, the writer and
/// the task definition each had a class of their own; this — the type <c>Program</c> actually
/// calls — had none. Neutering its stranger-port guard left 994 tests green, and pinning its URL
/// to the compiled-in default port left 994 tests green. Both measured before this file existed.
/// </para>
/// <para>
/// <strong>Every port here is deliberately not <see cref="DashboardSettings.DefaultPort"/>.</strong>
/// A test on the default port cannot tell "the bound port" from "the compiled-in constant", which
/// is precisely the defect that would ship a handler naming a port nothing answers.
/// </para>
/// </remarks>
public sealed class HookLifecycleTests : IDisposable
{
    private const int BoundPort = 61345;
    private const int OtherPort = 61999;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly string _claudeRoot;
    private readonly DashboardPaths _paths;
    private readonly ClaudeCodePaths _claude;

    public HookLifecycleTests()
    {
        _claudeRoot = Path.Combine(_root, "dot-claude");
        Directory.CreateDirectory(_claudeRoot);

        _paths = new DashboardPaths(Path.Combine(_root, "data"));
        Directory.CreateDirectory(_paths.Root);
        _claude = new ClaudeCodePaths(_claudeRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private HookLifecycle Lifecycle(IngressStatus ingress, string? token = null) =>
        new(_claude, _paths, ingress, new IngressToken(token), new FakeClock(), Logger.None);

    private string SettingsText() =>
        File.Exists(_claude.UserSettingsFile) ? File.ReadAllText(_claude.UserSettingsFile) : string.Empty;

    private static IEnumerable<JsonNode?> HandlersIn(JsonObject settings) =>
        settings["hooks"] is JsonObject hooks
            ? hooks.SelectMany(pair => (pair.Value as JsonArray ?? []).OfType<JsonObject>())
                .SelectMany(group => group["hooks"] as JsonArray ?? [])
            : [];

    private IEnumerable<string> RegisteredUrls() =>
        SettingsText() is { Length: > 0 } text
            ? HandlersIn(HookRegistration.Parse(text))
                .Select(handler => handler?["url"]?.GetValue<string>())
                .Where(url => url is not null)
                .Select(url => url!)
            : [];

    // ---- Whether to register at all ---------------------------------------------------------------

    /// <summary>
    /// A dashboard whose port is held by something else registers nothing at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is a security property, not tidiness.</strong> A registration writes a URL into
    /// the operator's settings, and Claude Code then posts hook payloads to it —
    /// <c>UserPromptSubmit</c> carries their prompt text, on every turn, in every session. If the
    /// port is held by a process T1.15 has already classified as <em>not ours</em>, registering it
    /// hands that process their prompts until they notice and quit.
    /// </para>
    /// <para>
    /// The path is live: <c>Program</c> calls <c>Register()</c> unconditionally after the host
    /// starts, and T1.15 deliberately keeps starting when a stranger holds the port. One
    /// <c>if</c> stands between the two, and nothing observed it before this test.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_dashboard_that_cannot_hear_writes_nothing_to_the_settings_file()
    {
        var result = Lifecycle(IngressStatus.Unavailable(BoundPort)).Register();

        Assert.Equal(SettingsWriteOutcome.NothingToDo, result.Outcome);

        // Not "no handler of ours" — no file at all. Nothing was written anywhere.
        Assert.False(File.Exists(_claude.UserSettingsFile));
        Assert.False(File.Exists(_paths.PortFile));
    }

    /// <summary>
    /// …and a dashboard that can hear does register. The control, without which an
    /// implementation that never registers satisfies the test above.
    /// </summary>
    [Fact]
    public void A_dashboard_that_can_hear_registers()
    {
        var result = Lifecycle(IngressStatus.Healthy(BoundPort)).Register();

        Assert.Equal(SettingsWriteOutcome.Written, result.Outcome);
        Assert.NotEmpty(RegisteredUrls());
    }

    /// <summary>An unavailable ingress leaves an existing file untouched, byte for byte.</summary>
    /// <remarks>
    /// The test above proves nothing is created; this proves nothing is edited. A guard that
    /// skipped the write but still rewrote the file — reformatting it, dropping a comment — would
    /// pass the first and fail this.
    /// </remarks>
    [Fact]
    public void An_unavailable_ingress_does_not_touch_an_existing_file()
    {
        const string Theirs = """{ "model": "opus", "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "theirs" } ] } ] } }""";
        File.WriteAllText(_claude.UserSettingsFile, Theirs);

        Lifecycle(IngressStatus.Unavailable(BoundPort)).Register();

        Assert.Equal(Theirs, SettingsText());
    }

    // ---- Which URL ---------------------------------------------------------------------------------

    /// <summary>
    /// The registered URL carries the <strong>bound</strong> port, not the compiled-in default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The acceptance criterion, and the reason it names a non-default port. An operator who
    /// overrides the port would otherwise get a handler pointing at 52789 — a port nothing answers
    /// — which is issue #4's symptom produced by the feature that closes issue #4.
    /// </para>
    /// <para>
    /// <strong>It carries a second meaning since T1.21, and the port it uses must satisfy
    /// both.</strong> With the port derived per user (Impl §3.1), a URL built from anything but
    /// the bound port is wrong for every user rather than only for one who overrode a setting. So
    /// the port here is asserted to be outside the derivation range as well as different from the
    /// default: were it inside, this test could pass against an implementation that rebuilt the URL
    /// by deriving it again instead of reading what was bound.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_registered_url_carries_the_bound_port()
    {
        // Neither the compiled-in default nor anything the derivation could produce.
        Assert.NotEqual(DashboardSettings.DefaultPort, BoundPort);
        Assert.False(
            BoundPort >= DashboardSettings.DefaultPort &&
            BoundPort < DashboardSettings.DefaultPort + PortSelection.DefaultRange,
            $"port {BoundPort} is inside the derivation range, so this test could not tell a bound " +
            "port from a re-derived one");

        Lifecycle(IngressStatus.Healthy(BoundPort)).Register();

        var urls = RegisteredUrls().Distinct(StringComparer.Ordinal).ToList();

        Assert.Equal([$"http://127.0.0.1:{BoundPort}/hook"], urls);
        Assert.DoesNotContain(
            DashboardSettings.DefaultPort.ToString(CultureInfo.InvariantCulture),
            SettingsText(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>port.txt</c> records the bound port, which is how the next launch finds this one.
    /// </summary>
    /// <remarks>
    /// Write-only until T1.21 and now load-bearing twice over: it is §3.1's first attempt, and it
    /// is the only thing that tells a second launch where a running instance actually is now that
    /// the port is not a constant (§5.3).
    /// </remarks>
    [Fact]
    public void The_port_file_records_the_bound_port()
    {
        Lifecycle(IngressStatus.Healthy(BoundPort)).Register();

        Assert.Equal(BoundPort, PortFile.Read(_paths));
    }

    /// <summary>
    /// Removal also uses the bound port, which is a separate claim from registration using it.
    /// </summary>
    /// <remarks>
    /// Both call sites had to be mutated to prove this was unobserved, and both mattered: a
    /// <c>Unregister</c> pinned to the default port removes nothing, so the handler survives the
    /// quit and fires at a dead port for as long as the operator's sessions live — the exact state
    /// the lifecycle exists to prevent.
    /// </remarks>
    [Fact]
    public void Removal_takes_out_the_handler_at_the_bound_port()
    {
        var lifecycle = Lifecycle(IngressStatus.Healthy(BoundPort));
        lifecycle.Register();
        Assert.NotEmpty(RegisteredUrls());

        lifecycle.Unregister();

        Assert.Empty(RegisteredUrls());
    }

    /// <summary>A dashboard on another port leaves this one's handlers alone.</summary>
    /// <remarks>
    /// The control for the two above. A lifecycle that ignored its port entirely — removing
    /// everything, or registering one fixed URL — would satisfy both and fail here.
    /// </remarks>
    [Fact]
    public void A_lifecycle_on_another_port_does_not_disturb_this_ones_handlers()
    {
        Lifecycle(IngressStatus.Healthy(BoundPort)).Register();

        Lifecycle(IngressStatus.Healthy(OtherPort)).Unregister();

        Assert.Equal([$"http://127.0.0.1:{BoundPort}/hook"], RegisteredUrls().Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void The_hook_url_is_loopback_and_names_the_port_given() =>
        Assert.Equal($"http://127.0.0.1:{OtherPort}/hook", HookLifecycle.HookUrlFor(OtherPort));

    // ---- port.txt ----------------------------------------------------------------------------------

    /// <summary>
    /// <c>port.txt</c> is written, in the dashboard's own folder, holding the bound port.
    /// </summary>
    /// <remarks>
    /// A named deliverable of Impl Part 8 that nothing observed. All three claims matter
    /// separately: that it exists, that it is under <see cref="DashboardPaths"/> rather than
    /// beside Claude Code's settings, and that it holds the port actually bound — a file naming a
    /// port nothing answers would send its reader somewhere useless, which is the whole failure
    /// this task is about.
    /// </remarks>
    [Fact]
    public void The_port_file_records_the_bound_port_in_the_dashboards_own_folder()
    {
        Lifecycle(IngressStatus.Healthy(BoundPort)).Register();

        Assert.True(File.Exists(_paths.PortFile));
        Assert.Equal(BoundPort.ToString(CultureInfo.InvariantCulture), File.ReadAllText(_paths.PortFile).Trim());
        Assert.Equal(_paths.Root, Path.GetDirectoryName(_paths.PortFile));
        Assert.NotEqual(_claude.ConfigDirectory, Path.GetDirectoryName(_paths.PortFile));
    }

    // ---- The token -------------------------------------------------------------------------------

    /// <summary>With a token configured, the handler interpolates it and both allowlists say so.</summary>
    [Fact]
    public void A_configured_token_reaches_the_registered_handler()
    {
        Lifecycle(IngressStatus.Healthy(BoundPort), token: "a-real-token").Register();

        var settings = HookRegistration.Parse(SettingsText());
        var handler = HandlersIn(settings).First(h => h?["url"] is not null)!;

        Assert.Equal("$" + IngressToken.EnvironmentVariable, handler["headers"]![IngressToken.HeaderName]!.GetValue<string>());
        Assert.Contains(
            ((JsonArray)settings[HookRegistration.EnvVarAllowListKey]!).Select(n => n!.GetValue<string>()),
            entry => entry == IngressToken.EnvironmentVariable);
    }

    /// <summary>
    /// With no token configured, no header is written — the file does not claim a protection that
    /// is not there.
    /// </summary>
    /// <remarks>
    /// A header referencing an unset variable is sent by Claude Code as an empty string, which
    /// ingress cannot tell from no header at all. Writing it would leave the operator's settings
    /// describing a token that never travels.
    /// </remarks>
    [Fact]
    public void No_configured_token_means_no_header_in_the_operators_file()
    {
        Lifecycle(IngressStatus.Healthy(BoundPort)).Register();

        var settings = HookRegistration.Parse(SettingsText());
        var handler = HandlersIn(settings).First(h => h?["url"] is not null)!;

        Assert.Null(handler["headers"]);
        Assert.Null(settings[HookRegistration.EnvVarAllowListKey]);
    }

    // ---- Composition -------------------------------------------------------------------------------

    [Fact]
    public void It_needs_all_of_its_collaborators()
    {
        var ingress = IngressStatus.Healthy(BoundPort);
        var token = new IngressToken(null);
        var clock = new FakeClock();

        Assert.Throws<ArgumentNullException>(() => new HookLifecycle(null!, _paths, ingress, token, clock, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new HookLifecycle(_claude, null!, ingress, token, clock, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new HookLifecycle(_claude, _paths, null!, token, clock, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new HookLifecycle(_claude, _paths, ingress, null!, clock, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new HookLifecycle(_claude, _paths, ingress, token, null!, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new HookLifecycle(_claude, _paths, ingress, token, clock, null!));
    }
}
