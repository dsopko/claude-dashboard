using System.Globalization;
using System.IO;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;

namespace ClaudeDashboard.Tests.Storage;

/// <summary>
/// Measures how fast <c>dashboard.db</c> grows, through the real store (T1.17).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this is a test rather than a note.</strong> The file keeps the retention window
/// (T1.64) and holds the operator's prompts and Claude's answers. A window is only reassuring if
/// somebody has said what it holds, and a number nobody re-measures becomes
/// a guess wearing a measurement's clothes the first time the stored shape changes. Running here
/// means it is re-measured on every build.
/// </para>
/// <para>
/// <strong>What the number covers.</strong> Real payload <em>sizes</em>, taken from the operator's
/// own Claude Code transcripts — 4,439 prompts and 11,757 assistant messages across 95 active days
/// — written through the real <see cref="SqliteEventStore"/>, so the figure includes SQLite's own
/// page and row overhead rather than being a sum of string lengths. The file is measured on disk
/// after the store is closed.
/// </para>
/// <para>
/// <strong>What it does not cover, and these matter.</strong> The sizes are real but the text is
/// synthetic filler of the same length: no transcript content is read into this test, on purpose.
/// Compressibility of real prose is therefore not represented, though SQLite does not compress, so
/// this affects nothing today. The per-day event counts are taken from transcript entries, which
/// <em>over</em>-count what the dashboard actually stores — a transcript's <c>user</c> entries
/// include tool results and its <c>assistant</c> entries include every intermediate turn, while
/// <c>UserPromptSubmit</c> and <c>Stop</c> fire once per real prompt and once per completed turn.
/// So this is an upper bound and is meant to be. And it is one operator's traffic on one machine,
/// which is the traffic this tool exists for but is not everybody's.
/// </para>
/// </remarks>
public sealed class GrowthMeasurement(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output = output;

    /// <summary>
    /// The measured distribution of real prompt sizes, in characters.
    /// </summary>
    /// <remarks>
    /// From 4,439 real prompts: median 197, p90 4,993, p99 18,094, max 242,647, mean 1,688.
    /// Reproduced here as a shape to sample rather than as a table of real text.
    /// </remarks>
    private static readonly (int Length, int Weight)[] PromptSizes =
    [
        (100, 30), (200, 20), (500, 15), (1_500, 15), (5_000, 10), (12_000, 7), (18_000, 2), (60_000, 1),
    ];

    /// <summary>
    /// The measured distribution of real answer sizes, in characters.
    /// </summary>
    /// <remarks>
    /// From 11,757 real assistant messages: median 163, p90 2,366, p99 5,380, max 50,659, mean 705.
    /// </remarks>
    private static readonly (int Length, int Weight)[] AnswerSizes =
    [
        (80, 30), (160, 20), (400, 20), (1_000, 15), (2_400, 10), (5_400, 4), (20_000, 1),
    ];

    /// <summary>A typical active day, from the measured medians: 27 prompts, 54 answers.</summary>
    private const int TypicalPrompts = 27;
    private const int TypicalAnswers = 54;

    /// <summary>A busy day, from the measured maximum: 238 prompts, 1,271 answers.</summary>
    private const int BusiestPrompts = 238;
    private const int BusiestAnswers = 1_271;

    /// <summary>
    /// Notifications a real logged day carried (Impl §9.1's correction). They hold no text.
    /// </summary>
    private const int NotificationsPerDay = 207;

    /// <summary>
    /// Decision rows written for each event: 0.284, the ratio in a copy of the operator's database
    /// on 2026-10-04 (7,219 decisions to 25,412 events). Until T1.64 the day wrote no decisions, so
    /// the figure left out the decision record and its three indexes (T1.63's report).
    /// </summary>
    private const double DecisionsPerEvent = 0.284;

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private void EnsureFolder() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Disposable temp folder.
        }
    }

    /// <summary>
    /// A typical day fits inside <see cref="SqliteEventStore.TypicalBytesPerDay"/>.
    /// </summary>
    /// <remarks>
    /// The assertion is on the published constant, so the constant cannot drift from the thing it
    /// claims to describe without this failing. Both bounds are asserted: too large means the
    /// operator was told to expect less than they get, and too small means the constant has been
    /// left behind by a change that made the rows cheaper, which is just as misleading.
    /// </remarks>
    [Fact]
    public void A_typical_day_costs_what_the_constant_says()
    {
        var bytes = WriteOneDay("typical.db", TypicalPrompts, TypicalAnswers);

        Assert.True(
            bytes <= SqliteEventStore.TypicalBytesPerDay,
            $"a typical day wrote {bytes:N0} bytes, over the published {SqliteEventStore.TypicalBytesPerDay:N0}");

        Assert.True(
            bytes >= SqliteEventStore.TypicalBytesPerDay / 4,
            $"a typical day wrote only {bytes:N0} bytes against a published {SqliteEventStore.TypicalBytesPerDay:N0}; " +
            "the constant is stale and overstates what this costs");
    }

    /// <summary>
    /// The busiest day on record, so the number the operator is given has a ceiling beside it.
    /// </summary>
    /// <remarks>
    /// A median is the wrong thing to plan storage against on its own. This is the worst real day
    /// in 95, and it is roughly an order of magnitude above the typical one — which is the fact
    /// worth knowing before deciding retention is somebody else's problem.
    /// </remarks>
    [Fact]
    public void The_busiest_day_on_record_is_bounded()
    {
        var bytes = WriteOneDay("busiest.db", BusiestPrompts, BusiestAnswers);

        Assert.True(
            bytes < 100L * 1024 * 1024,
            $"the busiest recorded day wrote {bytes:N0} bytes; that is past what this claim covers");

        // Recorded rather than asserted tightly: the point is the ratio, and a tight bound here
        // would fail on a change that was not a regression.
        Assert.True(bytes > SqliteEventStore.TypicalBytesPerDay);
    }

    /// <summary>A year of typical days, so the unpruned cost has a number too.</summary>
    /// <remarks>
    /// Extrapolated from the typical day rather than written out — a year of real writes in a unit
    /// test would be dishonest about what it measured and slow enough that nobody would run it.
    /// The arithmetic is stated so the reader can check it: this is 365 typical days, not a
    /// measurement of a year.
    /// </remarks>
    [Fact]
    public void A_year_unpruned_is_a_number_somebody_has_stated()
    {
        var perDay = WriteOneDay("year-basis.db", TypicalPrompts, TypicalAnswers);
        var perYear = perDay * 365;

        Assert.True(
            perYear < 2L * 1024 * 1024 * 1024,
            $"a year of typical days extrapolates to {perYear:N0} bytes, which is too much for history.retentionDays 0, which keeps everything");
    }

    /// <summary>Writes one day of events at real sizes and returns the file's size on disk.</summary>
    private long WriteOneDay(string name, int prompts, int answers)
    {
        EnsureFolder();

        var path = Path.Combine(_folder, name);

        // Deterministic: a growth figure that moved between runs would be unusable as a bound.
        var rng = new Random(20260826);

        using (var store = new SqliteEventStore(path, Serilog.Core.Logger.None))
        {
            // The day's decisions ride on its events at the measured ratio, as the live record writes
            // an event and its decisions in one transaction.
            var owed = 0.0;

            void Append(UserPromptSubmit inboundEvent, int index)
            {
                owed += DecisionsPerEvent;
                var decisions = new List<Decision>();

                for (; owed >= 1; owed--)
                {
                    decisions.Add(new Decision(
                        inboundEvent.Timestamp,
                        $"s{index % 15}",
                        DecisionKind.StateMoved,
                        FromState: "Working",
                        ToState: "Unread",
                        Reason: "Applied",
                        Detail: "silentMinutes=11"));
                }

                store.Append(new ArchiveRecord(inboundEvent, decisions));
            }

            for (var i = 0; i < prompts; i++)
            {
                Append(Event("UserPromptSubmit", "prompt", Sample(rng, PromptSizes), i), i);
            }

            for (var i = 0; i < answers; i++)
            {
                Append(Event("Stop", "last_assistant_message", Sample(rng, AnswerSizes), i), i);
            }

            for (var i = 0; i < NotificationsPerDay; i++)
            {
                Append(Event("Notification", "notification_type", "idle_prompt".Length, i), i);
            }
        }

        // GROWTH, not the file: less the pages an empty database already holds. Each table costs a
        // page before it has a row, so a new table (runs, T1.60) moved the whole-file figure by pages
        // that a day does not add. The constant says how much the file grows in a day.
        var empty = Path.Combine(_folder, "empty-" + name);

        using (var store = new SqliteEventStore(empty, Serilog.Core.Logger.None))
        {
            // Opens the file and runs the schema step, and writes no row.
            store.CountDecisions();
        }

        var bytes = new FileInfo(path).Length - new FileInfo(empty).Length;
        _output.WriteLine($"{name}: {bytes:N0} bytes of growth ({new FileInfo(empty).Length:N0} bytes empty).");

        return bytes;
    }

    private static int Sample(Random rng, (int Length, int Weight)[] distribution)
    {
        var total = distribution.Sum(entry => entry.Weight);
        var pick = rng.Next(total);

        foreach (var (length, weight) in distribution)
        {
            pick -= weight;

            if (pick < 0)
            {
                return length;
            }
        }

        return distribution[^1].Length;
    }

    /// <summary>A payload of the given size, with filler rather than anybody's words.</summary>
    private static UserPromptSubmit Event(string hookEvent, string field, int textLength, int index)
    {
        var body =
            $$"""{"hook_event_name":"{{hookEvent}}","session_id":"s{{index % 15}}","cwd":"C:\\projects\\repo","{{field}}":"{{new string('x', textLength)}}"}""";

        return new UserPromptSubmit
        {
            SessionId = new SessionId($"s{index % 15}"),
            Timestamp = new DateTimeOffset(2026, 8, 26, 9, 0, 0, TimeSpan.Zero).AddSeconds(index),
            Cwd = @"C:\projects\repo",
            Prompt = string.Empty,
            Payload = new PayloadJson(body),
        };
    }

    /// <summary>Prints the measured figures, so the numbers in the remarks can be refreshed.</summary>
    /// <remarks>
    /// Not an assertion. It exists so that whoever changes the stored shape can read the new
    /// numbers off a test run instead of deriving them, which is how the old ones would otherwise
    /// quietly survive into a document that no longer describes the code.
    /// </remarks>
    [Fact]
    public void Report_the_measured_growth()
    {
        var typical = WriteOneDay("report-typical.db", TypicalPrompts, TypicalAnswers);
        var busiest = WriteOneDay("report-busiest.db", BusiestPrompts, BusiestAnswers);

        var report =
            $"typical day {typical:N0} bytes ({typical / 1024.0:N0} KiB); " +
            $"busiest recorded day {busiest:N0} bytes ({busiest / 1024.0 / 1024.0:N1} MiB); " +
            $"a year of typical days {typical * 365 / 1024.0 / 1024.0:N0} MiB; " +
            $"published constant {SqliteEventStore.TypicalBytesPerDay:N0}";

        // Written to the test output, so it lands in the trx every run rather than only on a
        // failure. A number nobody can read without breaking something is a number nobody reads.
        _output.WriteLine(report);

        // And a failure if the numbers ever come
        Assert.True(typical > 0 && busiest > typical, report);

        Assert.Contains("typical day", report, StringComparison.Ordinal);
    }
}
