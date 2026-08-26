using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Threading;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// Phase 1's exit criteria, driven over the wire through the real composition (T1.20).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists when 1013 tests already pass.</strong> Those test the pieces. This
/// posts real hook payloads at a real socket and reads what the operator would see — the bands,
/// the tray colour, the counts — with nothing stubbed between the two. The phase gate is a claim
/// about the assembled thing, and every previous task in this phase found at least one defect
/// that only appeared once the pieces were joined.
/// </para>
/// <para>
/// <strong>What it cannot reach, stated so the document and the code agree.</strong> The
/// dashboard exposes no state surface — <c>/health</c> answers liveness and identity and nothing
/// else — so a replay against the shipped executable can only observe status codes, the log, and
/// the consumer's totals at shutdown. Bands and the tray roll-up are visible only from inside the
/// process, which is why this half runs in-process against the same composition rather than
/// against the staged binary. That split is a finding rather than a convenience: <em>nothing
/// outside the process can tell a correct dashboard from a dashboard showing the wrong states.</em>
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1031:Test methods should not use blocking task operations", Justification = "The UI thread is deliberately not the one blocking; see the remarks on the deadlock this shape avoids.")]
[Collection(WpfApplicationSuite.Name)]
public sealed class PhaseOneAcceptanceTests(StaHarness harness) : IDisposable
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

    /// <summary>
    /// Fifteen sessions' worth of traffic arrives over HTTP; the bands, the counts and the tray
    /// colour are what the operator should see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scenario is the same one <c>tools/replay-hooks.ps1</c> posts at the staged build, so
    /// the two halves of the acceptance describe one run rather than two different ones. It
    /// deliberately includes shapes the dashboard does not classify — an unrecognised
    /// notification type and an event ingress refuses — because a scenario built only from
    /// handled traffic is shaped to the thing it is testing and would pass against a build that
    /// dropped the rest.
    /// </para>
    /// <para>
    /// The tray assertion is the roll-up, and it is the one that could most easily pass for the
    /// wrong reason: red is also what you get from a bug that reports the worst possible state
    /// unconditionally. So the quiet counts are asserted beside it — a session that finished is
    /// unread, one that never blocked is working — which a stuck-on-red implementation cannot
    /// produce.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_phase_of_traffic_produces_the_states_bands_and_tray_light_the_operator_should_see()
    {
        var port = ClaudeDashboard.Tests.Hosting.AppHostTests.FreePort();
        var paths = new DashboardPaths(_root);
        Directory.CreateDirectory(_root);
        new SettingsStore(paths).Save(new DashboardSettings { Port = port });

        Observed observed;

        // The window and the tray are thread-affine, so they are built on the harness's UI thread.
        // Everything else deliberately is not: posting over HTTP and waiting for the consumer must
        // happen on this thread, because a synchronous wait on the UI thread deadlocks — WPF puts
        // a dispatcher SynchronizationContext there, HttpClient's continuations are posted back to
        // it, and the wait that is holding the thread is what they are waiting for. Observed, as a
        // two-minute hang, on the first version of this test.
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
            var consumer = built.Host.Services.GetRequiredService<ClaudeDashboard.App.Pipeline.EventConsumer>();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            var posted = 0;

            void Post(string json)
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = client.PostAsync("/hook", content).GetAwaiter().GetResult();

                // Impl §3.3 on every single path, including the ones below that are refused.
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
                Assert.Empty(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                posted++;
            }

            var at = DateTimeOffset.UtcNow.AddMinutes(-10);
            string When(int seconds) => at.AddSeconds(seconds).ToString("O");

            // Fifteen sessions across five working directories, every one started and prompted.
            for (var i = 0; i < 15; i++)
            {
                var cwd = Cwds[i % Cwds.Length];
                Post($$"""{"hook_event_name":"SessionStart","session_id":"s-{{i}}","cwd":"{{cwd}}","source":"startup","timestamp":"{{When(0)}}"}""");
                Post($$"""{"hook_event_name":"UserPromptSubmit","session_id":"s-{{i}}","cwd":"{{cwd}}","prompt_id":"p-{{i}}","prompt":"run the tests","timestamp":"{{When(10)}}"}""");
            }

            // One of each state the dashboard exists to show.
            Post($$"""{"hook_event_name":"Notification","session_id":"s-0","cwd":"{{Cwds[0]}}","notification_type":"permission_prompt","timestamp":"{{When(60)}}"}""");
            Post($$"""{"hook_event_name":"Notification","session_id":"s-1","cwd":"{{Cwds[1]}}","notification_type":"agent_needs_input","timestamp":"{{When(61)}}"}""");
            Post($$"""{"hook_event_name":"StopFailure","session_id":"s-2","cwd":"{{Cwds[2]}}","prompt_id":"p-2","error_type":"rate_limit","timestamp":"{{When(62)}}"}""");
            Post($$"""{"hook_event_name":"Stop","session_id":"s-3","cwd":"{{Cwds[3]}}","prompt_id":"p-3","timestamp":"{{When(63)}}"}""");

            // issue #1: a finished session going idle must not become a question.
            Post($$"""{"hook_event_name":"Notification","session_id":"s-3","cwd":"{{Cwds[3]}}","notification_type":"idle_prompt","timestamp":"{{When(70)}}"}""");

            // issue #2: a tool batch resumes a blocked turn, and leaves an unread one alone.
            Post($$"""{"hook_event_name":"PostToolBatch","session_id":"s-1","cwd":"{{Cwds[1]}}","prompt_id":"p-1","timestamp":"{{When(80)}}"}""");
            Post($$"""{"hook_event_name":"PostToolBatch","session_id":"s-3","cwd":"{{Cwds[3]}}","prompt_id":"p-3","timestamp":"{{When(81)}}"}""");

            // Shapes we do not classify. Neither may disturb anything above.
            Post($$"""{"hook_event_name":"Notification","session_id":"s-4","cwd":"{{Cwds[4]}}","notification_type":"something_new_next_release","timestamp":"{{When(90)}}"}""");
            Post($$"""{"hook_event_name":"SomeEventFromTheFuture","session_id":"s-4","cwd":"{{Cwds[4]}}","timestamp":"{{When(91)}}"}""");

            // Two of the posts above are refused at ingress and never reach the pipeline: the
            // unknown event name, and nothing else — the unclassified notification is mapped and
            // does reach it. So the consumer sees one fewer than was posted.
            Assert.True(
                SpinWait.SpinUntil(
                    () => consumer.AppliedCount + consumer.DeclinedCount >= posted - 1,
                    TimeSpan.FromSeconds(30)),
                $"the consumer drained {consumer.AppliedCount + consumer.DeclinedCount} of {posted} posts");

            observed = _harness.Invoke(() =>
            {
                _harness.Pump(DispatcherPriority.Background);

                var summary = StatusSummary.Of(built.Host.Services.GetRequiredService<SessionProjection>().Sessions);

                return new Observed(
                    summary.Permissions,
                    summary.Errors,
                    summary.Questions,
                    summary.Unread,
                    summary.Working,
                    built.Tray.ViewModel.Colour,
                    built.Tray.ViewModel.Tooltip,
                    registry.Sessions.Count,
                    built.Window.ViewModel.Rows.OfType<GroupViewModel>().Count());
            });

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

        // One session blocked on permission — the worst state present, so the tray is red.
        Assert.Equal(1, observed.Permissions);
        Assert.Equal(TrayColour.Red, observed.TrayColour);

        // …and the quiet counts, which a stuck-on-red implementation could not produce.
        Assert.Equal(1, observed.Errors);
        Assert.Equal(1, observed.Unread);

        // s-1 was a question and a tool batch resumed it, so it is working again, not waiting.
        Assert.Equal(0, observed.Questions);

        // Every session is accounted for: 15 started, none lost, none invented by the
        // unclassified notification or the refused event.
        Assert.Equal(15, observed.Sessions);
        Assert.Equal(15, observed.Permissions + observed.Errors + observed.Questions + observed.Unread + observed.Working);

        // Five working directories, so five groups.
        Assert.Equal(5, observed.Groups);

        // The tooltip breaks out what the glyph merges (Impl §5.2).
        Assert.Contains("1 permission", observed.Tooltip, StringComparison.Ordinal);
        Assert.Contains("1 error", observed.Tooltip, StringComparison.Ordinal);
    }

    private static readonly string[] Cwds =
    [
        @"C:\\dev\\PennCustQuote",
        @"C:\\projects\\Claude\\claude-dashboard",
        @"C:\\dev\\ledger",
        @"C:\\work\\intake",
        @"C:\\dev\\reports",
    ];

    private sealed record Observed(
        int Permissions,
        int Errors,
        int Questions,
        int Unread,
        int Working,
        TrayColour TrayColour,
        string Tooltip,
        int Sessions,
        int Groups);
}
