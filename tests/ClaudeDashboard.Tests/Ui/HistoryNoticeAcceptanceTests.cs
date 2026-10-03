using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Threading;
using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// A database that cannot be written, through the whole composed app (T1.54, issue #71).
/// </summary>
/// <remarks>
/// <para>
/// The scratch data folder holds a folder named <c>dashboard.db</c>, so the store fails at its first
/// write on every machine. One hook post must then show the history notice in the window and lead
/// the tray tooltip, and the dashboard must go on: the session's row appears, and the sound engine
/// still hands its notice to the player.
/// </para>
/// <para>
/// <strong>The sound is asserted through its path.</strong> The host's player counts every sound
/// it is handed, as queued or, with no device, as degraded. One of the two counts rising is the
/// engine calling the player, whatever the machine's audio. The data folder is given to
/// <see cref="AppHost.Build"/> as a <see cref="DashboardPaths"/>, which is what
/// <c>CLAUDE_DASHBOARD_HOME</c> sets; the variable itself is process-wide, and tests run in
/// parallel.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1031:Test methods should not use blocking task operations", Justification = "As PhaseOneAcceptanceTests: the UI thread is not the one blocking.")]
[Collection(WpfApplicationSuite.Name)]
public sealed class HistoryNoticeAcceptanceTests(StaHarness harness) : IDisposable
{
    private readonly StaHarness _harness = harness;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void A_database_that_cannot_be_written_shows_the_notice_and_the_dashboard_goes_on()
    {
        var port = ClaudeDashboard.Tests.Hosting.AppHostTests.FreePort();
        var paths = new DashboardPaths(_root);
        Directory.CreateDirectory(_root);
        new SettingsStore(paths).Save(new DashboardSettings { Port = port });

        // A folder where the file must be: the store's open fails, with no ACL games.
        Directory.CreateDirectory(paths.DatabaseFile);

        var built = _harness.Invoke(() =>
        {
            var host = AppHost.Build(paths);
            host.Start();

            _ = host.Services.GetRequiredService<SessionProjection>();
            var tray = host.Services.GetRequiredService<TrayIcon>();
            var window = host.Services.GetRequiredService<MainWindow>();

            return (Host: host, Tray: tray, Window: window);
        });

        try
        {
            var registry = built.Host.Services.GetRequiredService<SessionRegistry>();
            var store = built.Host.Services.GetRequiredService<SqliteEventStore>();
            var player = (NAudioSoundPlayer)built.Host.Services.GetRequiredService<ISoundPlayer>();
            var handedBefore = player.QueuedCount + player.DegradedCount;

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            client.DefaultRequestHeaders.Add(
                ClaudeDashboard.App.Ingress.IngressToken.HeaderName,
                built.Host.Services.GetRequiredService<ClaudeDashboard.App.Ingress.IngressToken>().Reveal());

            // One post: a permission prompt, which makes the session and plays the notice sound.
            using (var content = new StringContent(
                """{"hook_event_name":"Notification","session_id":"s-1","cwd":"C:\\work","notification_type":"permission_prompt"}""",
                Encoding.UTF8,
                "application/json"))
            {
                var response = client.PostAsync("/hook", content).GetAwaiter().GetResult();
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            }

            Assert.True(
                SpinWait.SpinUntil(() => store.Available == false && registry.Sessions.Count == 1, TimeSpan.FromSeconds(30)),
                $"store available: {store.Available}; sessions: {registry.Sessions.Count}");

            var seen = _harness.Invoke(() =>
            {
                _harness.Pump(DispatcherPriority.Background);

                // The 15-second tick that refreshes the tray, as the consumer would echo it.
                var tray = built.Window.Tray;
                tray.Tick(DateTimeOffset.Now);

                return (
                    Notices: tray.NoticeTexts.ToList(),
                    Tooltip: tray.Tooltip,
                    Rows: built.Window.ViewModel.Rows.OfType<GroupViewModel>().Count(),
                    Sessions: built.Host.Services.GetRequiredService<SessionProjection>().Sessions.Count);
            });

            Assert.Equal([HistoryNotice.WindowText], seen.Notices);
            Assert.StartsWith(HistoryNotice.TrayShort, seen.Tooltip, StringComparison.Ordinal);
            Assert.Equal(1, seen.Sessions);
            Assert.Equal(1, seen.Rows);
            Assert.True(
                player.QueuedCount + player.DegradedCount > handedBefore,
                "the sound engine did not hand the permission notice to the player");

            built.Host.StopAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _harness.Invoke(() =>
            {
                built.Tray.Dispose();
                ((IDisposable)built.Host).Dispose();
                return true;
            });
        }
    }
}
