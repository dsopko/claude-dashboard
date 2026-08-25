using System.IO;
using System.Windows.Threading;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The tray is constructed, registered, and <strong>actually driven</strong> in the real host
/// graph (T1.13).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the half T1.12b did not close.</strong> That task made a <em>missing</em>
/// registration loud, so the tray's required collaborators throw at startup if one is lost. It
/// says nothing about "registered, constructed, and nothing ever calls it" — which is precisely
/// what happened to T1.6's tick, T1.11's <c>UiTick</c> and T1.11a's <c>Flush</c>. A tray built
/// correctly and never updated shows its startup colour forever, and a tray stuck on grey is
/// indistinguishable from a quiet afternoon: the failure this glyph exists to prevent, wearing
/// the glyph's own face.
/// </para>
/// <para>
/// <strong>Why this needs the STA harness and <c>AppHostTests</c> does not.</strong> The real
/// graph registers <c>WpfDispatcher</c>, which discards every post when there is no
/// <see cref="System.Windows.Application"/> — so on the pool the projection never reaches the
/// tray and a test there would assert against a tray nothing could have updated. Running where a
/// real Application exists is what makes "driven" mean driven, rather than "driven if something
/// had been listening".
/// </para>
/// <para>
/// Both update paths are exercised, because the tray has two and either can be lost without the
/// other noticing: a session changing, and the clock moving.
/// </para>
/// </remarks>
[Collection(WpfApplicationSuite.Name)]
public sealed class TrayCompositionTests : IDisposable
{
    private readonly StaHarness _harness;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;

    public TrayCompositionTests(StaHarness harness)
    {
        _harness = harness;
        Directory.CreateDirectory(_root);
        _paths = new DashboardPaths(_root);
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

    /// <summary>A session changing reaches the tray through the container's own wiring.</summary>
    [Fact]
    public void A_session_changing_drives_the_tray()
    {
        var (colour, tooltip) = _harness.Invoke(() =>
        {
            using var host = AppHost.Build(_paths);

            // Resolving throws if a collaborator is unregistered — the T1.12b half, free.
            var tray = host.Services.GetRequiredService<TrayViewModel>();
            var registry = host.Services.GetRequiredService<SessionRegistry>();

            // Resolving the projection is what subscribes it to the Registry, exactly as
            // AppHost's own startup does.
            _ = host.Services.GetRequiredService<SessionProjection>();

            Assert.Equal(TrayColour.Grey, tray.Colour);

            registry.Apply(new UserPromptSubmit
            {
                SessionId = new SessionId("s-tray"),
                Timestamp = DateTimeOffset.UtcNow,
                Cwd = _root,
                PromptId = "p-1",
                Prompt = "run the tests",
            });

            _harness.Pump(DispatcherPriority.Background);

            return (tray.Colour, tray.Tooltip);
        });

        Assert.Equal(TrayColour.Blue, colour);
        Assert.Equal("1 working", tooltip);
    }

    /// <summary>
    /// …and the clock moving drives it too, which is the path a lapsing mute depends on.
    /// </summary>
    /// <remarks>
    /// Asserted with <em>no event arriving</em>. A global mute stops being in force by predicate
    /// and raises nothing, so if the tray were only wired to the projection its tooltip would go
    /// on claiming a mute that ended half an hour ago — correct at startup, wrong forever after.
    /// </remarks>
    [Fact]
    public void The_clock_moving_drives_the_tray()
    {
        var (mutedBefore, mutedAfter, tooltip) = _harness.Invoke(() =>
        {
            using var host = AppHost.Build(_paths);

            // Resolving the icon is what puts the tray on the clock — nothing here attaches it,
            // which is the point: a test that wired the tick itself would prove the tray responds
            // to a tick without proving one ever arrives.
            using var icon = host.Services.GetRequiredService<TrayIcon>();
            var tray = icon.ViewModel;
            var tick = host.Services.GetRequiredService<UiTick>();
            var engine = host.Services.GetRequiredService<SoundPolicyEngine>();

            engine.SetAllMuted(muted: true, DateTimeOffset.UtcNow.AddMinutes(30));

            // Nothing has told the tray, and nothing ever will: the mute raised no event.
            var before = tray.IsMuted;

            tick.Tick(DateTimeOffset.UtcNow);
            _harness.Pump(DispatcherPriority.Background);

            return (before, tray.IsMuted, tray.Tooltip);
        });

        Assert.False(mutedBefore);
        Assert.True(mutedAfter);
        Assert.StartsWith("muted ", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// A menu item is enabled and invoking its command, and the command has an effect.
    /// </summary>
    /// <remarks>
    /// A command bound to nothing looks exactly like one bound and broken, so this asserts both
    /// halves: that the item is live, and that invoking it puts a real
    /// <see cref="SoundCommand"/> on the channel the consumer reads. The menu is built in
    /// <see cref="TrayIcon"/> from the container's own view model, so a wrong binding there
    /// fails here rather than in front of the operator.
    /// </remarks>
    [Fact]
    public void The_menu_items_are_live_and_have_an_effect()
    {
        var (labels, queued) = _harness.Invoke(() =>
        {
            using var host = AppHost.Build(_paths);
            using var tray = host.Services.GetRequiredService<TrayIcon>();
            var pipeline = host.Services.GetRequiredService<EventPipeline>();

            var items = tray.Menu.Items.OfType<System.Windows.Controls.MenuItem>().ToList();
            var headers = items.Select(item => item.Header?.ToString() ?? string.Empty).ToList();

            // Every item is bound to something, and only Settings is inert.
            Assert.All(items, item => Assert.NotNull(item.Command));

            var pause = items.Single(item => (item.Header as string) == "Pause monitoring");
            Assert.True(pause.IsEnabled);

            pause.Command.Execute(pause.CommandParameter);
            _harness.Pump(DispatcherPriority.Background);

            var read = new List<InboundEvent>();

            while (pipeline.Reader.TryRead(out var queuedEvent))
            {
                read.Add(queuedEvent);
            }

            return (headers, read);
        });

        Assert.Equal(
            ["Open", "Mute all", "Mute all for 30 min", "Pause monitoring", "Settings…", "Quit"],
            labels);

        var command = Assert.IsType<SoundCommand>(Assert.Single(queued));
        Assert.Equal(SoundCommandKind.PauseMonitoring, command.Kind);
    }

    /// <summary>Settings is present and visibly inert, rather than absent or silently dead.</summary>
    [Fact]
    public void Settings_is_present_and_disabled()
    {
        var enabled = _harness.Invoke(() =>
        {
            using var host = AppHost.Build(_paths);
            using var tray = host.Services.GetRequiredService<TrayIcon>();

            var settings = tray.Menu.Items
                .OfType<System.Windows.Controls.MenuItem>()
                .Single(item => (item.Header as string) == "Settings…");

            return settings.IsEnabled;
        });

        Assert.False(enabled);
    }
}
