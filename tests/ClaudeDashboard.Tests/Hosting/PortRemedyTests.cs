using System.Globalization;
using System.IO;
using ClaudeDashboard.App;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Setup;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// A port that is taken says what to do, in the log, the tray and the window (T1.57, issue #14).
/// </summary>
/// <remarks>
/// <para>
/// <strong>No test here opens a port.</strong> <see cref="PortSelection.Choose"/> takes its probe as
/// a function, so each test answers "taken" for every candidate itself.
/// </para>
/// <para>
/// <strong>Every place that makes an <see cref="IngressStatus"/> fault has a test here.</strong>
/// <c>IngressStatus.NotBound</c> for a walk that ran out and for a refused pin, which both
/// <c>Program.IngressFor</c> and <c>AppHost.Build</c> use; and <c>AppHost.Build</c> when it is told
/// the port is unavailable. Since the T1.57 review a start never stays deaf on a port its choice
/// found: a stranger on the recorded port is skipped, which the two live cases below hold. A bind that fails after the choice is not a status at all: <c>Program.Main</c>
/// logs a Fatal line, which already says what to do, and exits.
/// </para>
/// </remarks>
public sealed class PortRemedyTests
{
    private const int Base = DashboardSettings.IngressPortBase;
    private const string SettingsFile = @"C:\Users\someone\AppData\Local\ClaudeDashboard\settings.json";

    private static PortChoice EveryPortTaken(int? pinned = null) =>
        PortSelection.Choose(Base, "S-1-5-21-1-2-3-1001", recorded: null, _ => PortOccupant.Unrecognised, pinned: pinned);

    private static string Number(int port) => port.ToString(CultureInfo.CurrentCulture);

    [Fact]
    public void With_no_pin_and_every_port_taken_each_place_says_free_a_port_and_restart()
    {
        var choice = EveryPortTaken();
        Assert.False(choice.Found);
        Assert.False(choice.PinRefused);

        var status = IngressStatus.NotBound(choice, SettingsFile);
        var first = choice.Attempts[0].Port;
        var last = choice.Attempts[^1].Port;

        Assert.Equal($"port {Number(choice.Port)} taken · free a port and restart", status.Fault);
        Assert.Equal(status.Fault, status.TrayText);
        Assert.True(status.IsShown);

        Assert.Contains("restart", status.Text, StringComparison.Ordinal);
        Assert.Contains($"({Number(first)} to {Number(last)})", status.Text, StringComparison.Ordinal);
        Assert.Contains(SettingsFile, status.Text, StringComparison.Ordinal);

        Assert.Contains("restart the dashboard", LogLineFor(choice), StringComparison.Ordinal);
        Assert.Contains("the hook finds the new port by itself", LogLineFor(choice), StringComparison.Ordinal);
    }

    [Fact]
    public void A_taken_pin_says_unpin_it_or_free_it_then_restart()
    {
        var choice = EveryPortTaken(pinned: 52961);
        Assert.True(choice.PinRefused);

        var status = IngressStatus.NotBound(choice, SettingsFile);

        Assert.Equal("pinned port 52961 taken · unpin it or free it, then restart", status.Fault);
        Assert.Contains("port 52961 is pinned in settings.json", status.Text, StringComparison.Ordinal);
        Assert.Contains("restart", status.Text, StringComparison.Ordinal);

        Assert.Contains("restart", LogLineFor(choice), StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The live case from the T1.57 review</strong>: a stranger holds the port in
    /// <c>port.txt</c>, there is no pin, and the dashboard binds the next free port it walks to. No
    /// notice shows.
    /// </summary>
    /// <remarks>
    /// The reviewer measured "52977:Silent → 52888:OtherInstance → 52889:Free" with nothing bound,
    /// and a window that said every port tried was in use. The decision and the choice are both
    /// asked here, as <c>Program.Main</c> asks them: the decision on the recorded port's occupant,
    /// then the choice, then the status.
    /// </remarks>
    [Fact]
    public void A_stranger_on_the_recorded_port_lets_the_dashboard_bind_the_next_free_port()
    {
        const int Recorded = 52977;
        var taken = new HashSet<int> { Recorded };

        // The recorded port is silent; the first port after it is another user's dashboard; the
        // next is free.
        PortOccupant Probe(int port)
        {
            if (port == Recorded)
            {
                return PortOccupant.Silent;
            }

            if (taken.Count == 1)
            {
                taken.Add(port);
                return PortOccupant.OtherInstance;
            }

            return taken.Contains(port) ? PortOccupant.OtherInstance : PortOccupant.Free;
        }

        Assert.Equal(StartupAction.StartNormally, StartupDecision.For(holdsGate: true, PortOccupant.Silent));

        var choice = PortSelection.Choose(Base, "S-1-5-21-1-2-3-1001", Recorded, Probe);

        Assert.True(choice.Found);
        Assert.Equal(PortSource.Walked, choice.Source);
        Assert.NotEqual(Recorded, choice.Port);

        var status = Program.IngressFor(choice, SettingsFile);

        Assert.True(status.CanReceiveHooks);
        Assert.Equal(choice.Port, status.Port);
        Assert.Null(status.Fault);
        Assert.False(status.IsShown);
        Assert.Empty(new NoticeBoard(status).Texts);
    }

    /// <summary>
    /// A pin on a free port, with a stranger on the recorded port, binds the pin (the second case
    /// the reviewer measured: 52979 was free, and the start stayed deaf).
    /// </summary>
    [Fact]
    public void A_free_pin_is_bound_though_a_stranger_holds_the_recorded_port()
    {
        const int Recorded = 52977;
        const int Pin = 52979;

        Assert.Equal(StartupAction.StartNormally, StartupDecision.For(holdsGate: true, PortOccupant.Unrecognised));

        var choice = PortSelection.Choose(
            Base, "S-1-5-21-1-2-3-1001", Recorded, port => port == Recorded ? PortOccupant.Unrecognised : PortOccupant.Free, pinned: Pin);

        Assert.True(choice.Found);
        Assert.Equal(PortSource.Pinned, choice.Source);
        Assert.Equal(Pin, choice.Port);

        var status = Program.IngressFor(choice, SettingsFile);

        Assert.True(status.CanReceiveHooks);
        Assert.Equal(Pin, status.Port);
        Assert.False(status.IsShown);
    }

    /// <summary>The host told that its port is unavailable says the same.</summary>
    [Fact]
    public void A_host_without_its_port_says_what_to_do()
    {
        var root = Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var paths = new DashboardPaths(root);
            using var host = AppHost.Build(paths, ingressAvailable: false, claude: new ClaudeCodePaths(Path.Combine(root, "claude")));

            var status = (IngressStatus)host.Services.GetService(typeof(IngressStatus))!;

            Assert.EndsWith("free a port and restart", status.Fault, StringComparison.Ordinal);
            Assert.Contains(paths.SettingsFile, status.Text, StringComparison.Ordinal);

            (host.Services.GetService(typeof(Serilog.ILogger)) as IDisposable)?.Dispose();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// <strong>The tooltip shows the port fault exactly once, first</strong>, and the counts after it;
    /// with a plugin notice as well, the port fault still leads.
    /// </summary>
    [Fact]
    public void The_tooltip_shows_the_port_fault_once_and_first()
    {
        var status = IngressStatus.NotBound(EveryPortTaken(), SettingsFile);
        var hook = new HookNotice();
        hook.ShowPluginDisabled();

        using var registry = new ClaudeDashboard.Tests.Ui.RegistryHarness();
        using var board = new NoticeBoard(status, hook);
        using var tray = new TrayViewModel(
            registry.Projection, new SettableSoundModes(), new RecordingEventSink(), new FakeClock(), status, Logger.None, notices: board);

        Assert.StartsWith($"{status.Fault} · {HookNotice.PluginDisabledShort}", tray.Tooltip, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(tray.Tooltip, status.Fault!));

        // Without a board, the tray makes one that holds the port: still once, still first.
        using var bare = new TrayViewModel(
            registry.Projection, new SettableSoundModes(), new RecordingEventSink(), new FakeClock(), status, Logger.None);

        Assert.StartsWith(status.Fault!, bare.Tooltip, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(bare.Tooltip, status.Fault!));
        Assert.Equal([status.Text!], bare.NoticeTexts);
    }

    private static int Occurrences(string text, string part)
    {
        var count = 0;

        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>What <c>Program.ReportPortChoice</c> logs for <paramref name="choice"/>, rendered.</summary>
    private static string LogLineFor(PortChoice choice)
    {
        var sink = new RecordingLogSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        Program.ReportPortChoice(logger, choice, isSid: true);

        return string.Join("\n", sink.Events.Select(entry => entry.RenderMessage(CultureInfo.InvariantCulture)));
    }
}
