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
/// <c>Program.IngressFor</c> and <c>AppHost.Build</c> use; <c>Program.IngressFor</c> for a start
/// that will not bind a port it found; and <c>AppHost.Build</c> when it is told the port is
/// unavailable. A bind that fails after the choice is not a status at all: <c>Program.Main</c>
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
    /// A start that will not bind the port it found (a stranger on the port in <c>port.txt</c>) keeps
    /// its line, now with the remedy.
    /// </summary>
    [Fact]
    public void A_start_without_ingress_on_a_found_port_says_what_to_do()
    {
        var choice = PortSelection.Choose(Base, "S-1-5-21-1-2-3-1001", recorded: null, _ => PortOccupant.Free);
        Assert.True(choice.Found);

        var status = Program.IngressFor(StartupAction.StartWithoutIngress, choice, SettingsFile);

        Assert.Equal($"port {Number(choice.Port)} taken · free a port and restart", status.Fault);
        Assert.Contains("restart", status.Text, StringComparison.Ordinal);

        Assert.Null(Program.IngressFor(StartupAction.StartNormally, choice, SettingsFile).Fault);
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
