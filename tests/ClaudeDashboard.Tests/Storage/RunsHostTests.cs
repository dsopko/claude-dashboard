using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// The composed host writes one <c>runs</c> row per start, against a scratch data folder (T1.60,
/// issue #78).
/// </summary>
/// <remarks>
/// Each host pins a free port in its own scratch settings file, as <see cref="HookToDatabaseTests"/>
/// does. The operator's data folder and database are never touched.
/// </remarks>
public sealed class RunsHostTests : IDisposable
{
    private const string Rows =
        "SELECT id, started_at, IFNULL(stopped_at, 'NULL'), version, IFNULL(port, 'NULL'), data_root FROM runs ORDER BY id";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;

    public RunsHostTests()
    {
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
            // Disposable temp folder.
        }
    }

    private WebApplication Host(out int port, bool ingressAvailable = true, int retentionDays = HistorySettings.DefaultRetentionDays)
    {
        port = ClaudeDashboard.Tests.Hosting.AppHostTests.FreePort();
        new SettingsStore(_paths).Save(new DashboardSettings { Port = port, History = new HistorySettings { RetentionDays = retentionDays } });

        return AppHost.Build(_paths, ingressAvailable: ingressAvailable);
    }

    /// <summary>
    /// Two starts and two clean stops leave two rows, each with both times in UTC, the version,
    /// the port and the data folder.
    /// </summary>
    [Fact]
    public async Task Two_starts_and_stops_leave_two_complete_rows()
    {
        var ports = new List<int>();

        for (var i = 0; i < 2; i++)
        {
            await using var app = Host(out var port);
            ports.Add(port);

            await app.StartAsync();
            await app.StopAsync();
        }

        var rows = ForeignSqliteReader.Query(_paths.DatabaseFile, Rows);

        Assert.Equal(2, rows.Count);

        for (var i = 0; i < 2; i++)
        {
            Assert.EndsWith("Z", rows[i][1], StringComparison.Ordinal);
            Assert.EndsWith("Z", rows[i][2], StringComparison.Ordinal);
            Assert.True(
                DateTimeOffset.Parse(rows[i][1], null, System.Globalization.DateTimeStyles.RoundtripKind)
                    <= DateTimeOffset.Parse(rows[i][2], null, System.Globalization.DateTimeStyles.RoundtripKind));
            Assert.Equal(StartupVersion.Value, rows[i][3]);
            Assert.Equal(ports[i].ToString(System.Globalization.CultureInfo.InvariantCulture), rows[i][4]);
            Assert.Equal(_paths.Root, rows[i][5]);
        }
    }

    /// <summary>A host disposed without a stop leaves its row with no stop time: the record of a kill.</summary>
    [Fact]
    public async Task A_host_disposed_without_a_stop_leaves_the_stop_empty()
    {
        await using (var app = Host(out _))
        {
            await app.StartAsync();

            // The row is written on the writer's loop. Wait for it, so the dispose meets a written
            // row and the test is about the stop, not about whether the start won a race.
            var store = app.Services.GetRequiredService<SqliteEventStore>();
            Assert.True(SpinWait.SpinUntil(() => store.WrittenCount >= 1, TimeSpan.FromSeconds(30)));
        }

        var row = Assert.Single(ForeignSqliteReader.Query(_paths.DatabaseFile, Rows));

        Assert.EndsWith("Z", row[1], StringComparison.Ordinal);
        Assert.Equal("NULL", row[2]);
    }

    /// <summary>A start that could not bind writes its row with no port.</summary>
    [Fact]
    public async Task A_start_that_could_not_bind_has_no_port()
    {
        await using (var app = Host(out _, ingressAvailable: false))
        {
            await app.StartAsync();
            await app.StopAsync();
        }

        var row = Assert.Single(ForeignSqliteReader.Query(_paths.DatabaseFile, Rows));

        Assert.Equal("NULL", row[4]);
        Assert.NotEqual("NULL", row[2]);
    }

    /// <summary>
    /// A database from before T1.60 gains the table at the next start, and its events and decisions
    /// rows are unchanged.
    /// </summary>
    [Fact]
    public async Task An_old_database_gains_the_table_at_the_next_start()
    {
        Directory.CreateDirectory(_root);
        OldDatabase.Create(_paths.DatabaseFile);

        var events = ForeignSqliteReader.Query(_paths.DatabaseFile, OldDatabase.EventRows);
        var decisions = ForeignSqliteReader.Query(_paths.DatabaseFile, OldDatabase.DecisionRows);

        // Keeps everything: the old rows are from 2026-09-01, older than the default 30 days (T1.64).
        await using (var app = Host(out _, retentionDays: 0))
        {
            await app.StartAsync();
            await app.StopAsync();
        }

        Assert.Single(ForeignSqliteReader.Query(_paths.DatabaseFile, Rows));
        Assert.Equal(events, ForeignSqliteReader.Query(_paths.DatabaseFile, OldDatabase.EventRows));

        // The run may add decision rows of its own (a tick); the old ones are unchanged.
        var after = ForeignSqliteReader.Query(_paths.DatabaseFile, OldDatabase.DecisionRows);
        Assert.Equal(decisions, after.Take(decisions.Count));
    }
}
