using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// The race of issue #120, made certain, and closed (T1.77): another test takes the chosen port between the
/// choice and the start. The old way (choose, let go, start on it) leaves a host that cannot hear on its port;
/// <see cref="TestPorts.StartAsync"/> chooses again and keeps a port it listens on.
/// </summary>
public sealed class TestPortsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly List<TcpListener> _strangers = [];

    public void Dispose()
    {
        foreach (var stranger in _strangers)
        {
            stranger.Stop();
            stranger.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp folder.
        }
    }

    /// <summary>
    /// <strong>The old way loses its port:</strong> the port is taken after the choice, so the host starts and cannot
    /// hear on the port that the test would post to.
    /// </summary>
    [Fact]
    public async Task A_port_taken_after_the_choice_leaves_the_old_way_unable_to_hear()
    {
        var port = TestPorts.Unbound();
        Take(port);

        await using var host = Build(port);
        await host.StartAsync();

        var status = host.Services.GetRequiredService<IngressStatus>();
        Assert.False(status.CanReceiveHooks);
        Assert.Equal(port, status.Port);

        await host.StopAsync();
    }

    /// <summary>
    /// <strong>The new way keeps a port:</strong> the first port is taken after the choice, before the host is built or
    /// after it, so it chooses again, and the host answers on the port it returns. The host that lost its port leaves
    /// no log file open.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_port_taken_after_the_choice_is_chosen_again(bool afterTheBuild)
    {
        var taken = new List<int>();

        void TakeTheFirst(int chosen)
        {
            if (taken.Count == 0)
            {
                taken.Add(chosen);
                Take(chosen);
            }
        }

        // Before the build, the host's own probe finds the port taken; after it, the start's bind fails.
        var (host, port, _) = afterTheBuild
            ? await TestPorts.StartAsync(Build, takeBeforeStart: TakeTheFirst)
            : await TestPorts.StartAsync(Build, takeFirst: TakeTheFirst);

        await using (host)
        {
            Assert.NotEqual(taken[0], port);
            Assert.True(host.Services.GetRequiredService<IngressStatus>().CanReceiveHooks);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var body = await client.GetStringAsync(new Uri($"http://127.0.0.1:{port}/health"));
            Assert.Contains("\"status\":\"ok\"", body, StringComparison.Ordinal);

            await host.StopAsync();

            // The kept host's logger, as every class that reads its log closes it: the container does not.
            (host.Services.GetService<Serilog.ILogger>() as IDisposable)?.Dispose();
        }

        // The host that lost its port wrote to the same log; TestPorts closed its logger, so the folder can go (the
        // T1.77 review).
        Directory.Delete(new DashboardPaths(_root).LogFolder, recursive: true);
    }

    /// <summary>A stranger takes the port, as another test that chose it would.</summary>
    private void Take(int port)
    {
        var stranger = new TcpListener(IPAddress.Loopback, port);
        stranger.Start();
        _strangers.Add(stranger);
    }

    private WebApplication Build(int port)
    {
        var paths = new DashboardPaths(_root);
        new SettingsStore(paths).Save(new DashboardSettings { Port = port });

        return AppHost.Build(paths, claude: new ClaudeCodePaths(Path.Combine(_root, "claude-config")));
    }
}
