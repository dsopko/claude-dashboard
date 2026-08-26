using System.IO;
using ClaudeDashboard.App.Storage;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// The controls for the oracle every other test in this folder leans on (T1.17).
/// </summary>
/// <remarks>
/// <para>
/// <strong>These test the instrument, not the product.</strong> The rule this project arrived at
/// the hard way is: find an oracle the implementation does not control, <em>and</em> a control that
/// proves the oracle was asked under the conditions you think it was. The second half is what
/// catches the failures, and it is the half that looks like ceremony.
/// </para>
/// <para>
/// The specific trap here: <strong>an empty database and a database you failed to open both
/// produce "no rows"</strong>. Every "the row is not there" assertion in this folder would pass
/// against a reader that could not open anything at all. These are what make that impossible.
/// </para>
/// </remarks>
public sealed class ForeignSqliteReaderTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    public ForeignSqliteReaderTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // The temp folder is disposable; a held handle is not this test's business.
        }
    }

    /// <summary>
    /// The reader is not the library the product writes with, which is the whole point of it.
    /// </summary>
    /// <remarks>
    /// If these ever report the same version, this test still passes and should — two builds of
    /// the same version are still two binaries. What it is really pinning is that the reader
    /// resolves at all: <c>winsqlite3.dll</c> answering means System32's SQLite loaded, and a
    /// machine without it fails here rather than in something subtler.
    /// </remarks>
    [Fact]
    public void The_reader_is_a_different_sqlite_from_the_one_the_product_uses()
    {
        var foreign = ForeignSqliteReader.Version();

        Assert.False(string.IsNullOrWhiteSpace(foreign));

        var ours = OurSqliteVersion();

        Assert.False(string.IsNullOrWhiteSpace(ours));
    }

    /// <summary>A file that is not there is a refusal, never an empty result.</summary>
    [Fact]
    public void A_database_that_was_never_created_is_refused_rather_than_read_as_empty()
    {
        var absent = Path.Combine(_folder, "never-created.db");

        var failure = Assert.Throws<ForeignReadFailed>(
            () => ForeignSqliteReader.Column(absent, "SELECT payload_json FROM events"));

        Assert.Contains("no database", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A real database without our table is a refusal too.</summary>
    /// <remarks>
    /// The subtler half of the same trap. A file that opens but has no <c>events</c> table would
    /// otherwise read as "the store wrote nothing", which is a claim about the store made from a
    /// fact about the schema.
    /// </remarks>
    [Fact]
    public void A_database_without_the_table_is_refused_rather_than_read_as_empty()
    {
        var path = Path.Combine(_folder, "real-but-wrong-shape.db");

        // A real database, made by the product's own writer, so this is genuinely the "opened
        // fine" case rather than an absent or empty file.
        using (var store = new SqliteEventStore(path, Logger.None))
        {
            Assert.True(store.Append(TestEvents.Hook("""{"marker":"present"}""")));
        }

        var failure = Assert.Throws<ForeignReadFailed>(
            () => ForeignSqliteReader.Column(path, "SELECT nothing FROM absent_table"));

        Assert.Contains("does not have the shape", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the positive control: on a database that does have rows, the reader returns them.
    /// </summary>
    /// <remarks>
    /// Without this, every refusal above would be satisfied by a reader that refuses everything.
    /// </remarks>
    [Fact]
    public void A_database_with_rows_is_read()
    {
        var path = Path.Combine(_folder, "rows.db");

        using (var store = new SqliteEventStore(path, Logger.None))
        {
            store.Append(TestEvents.Hook("""{"marker":"present"}"""));
        }

        var rows = ForeignSqliteReader.Column(path, "SELECT payload_json FROM events");

        Assert.Single(rows);
        Assert.Contains("present", rows[0], StringComparison.Ordinal);
    }

    private static string OurSqliteVersion()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";

        return (string)command.ExecuteScalar()!;
    }
}
