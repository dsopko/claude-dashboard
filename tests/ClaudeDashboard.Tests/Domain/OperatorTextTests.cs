using System.Text.Json;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// The <c>/state</c> report reveals no title and no task description to a log, and still answers
/// with both (T1.46).
/// </summary>
/// <remarks>
/// Measured through a real Serilog pipeline with a marker in it, the way
/// <c>UnprotectedTextInventory</c>'s entries were — not by comparing <see cref="OperatorText.ToString"/>
/// with our own expectation, which would still pass on the day Serilog rendered it differently.
/// Both routes: a plain <c>{Report}</c>, which calls the record's generated <c>ToString</c>, and
/// <c>{@Report}</c>, which reflects over public properties.
/// </remarks>
public sealed class OperatorTextTests
{
    private const string TitleMarker = "TITLE-MARKER-c41e";
    private const string DescriptionMarker = "DESCRIPTION-MARKER-0d7b";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    [Fact]
    public void Serilog_renders_neither_the_title_nor_the_description_of_a_whole_report()
    {
        var sink = new RecordingLogSink();
        using var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        var report = Report();

        logger.Information("The report, plainly: {Report}", report);
        logger.Information("The report, destructured: {@Report}", report);
        logger.Information("One entry, destructured: {@Entry}", report.Sessions[0]);
        logger.Information("One task, plainly: {Task}", report.Sessions[0].WaitingOn[0]);
        logger.Information("The title alone: {Title} {@Title}", report.Sessions[0].Title, report.Sessions[0].Title);

        foreach (var message in sink.Messages)
        {
            Assert.DoesNotContain(TitleMarker, message, StringComparison.Ordinal);
            Assert.DoesNotContain(DescriptionMarker, message, StringComparison.Ordinal);
        }

        // The control: every line arrived, and the destructured ones really reached inside the
        // report — the session id is there. A plain {Report} prints the list by its type name.
        Assert.Equal(5, sink.Messages.Count);
        Assert.Equal(2, sink.Containing("s-state-1"));
    }

    [Fact]
    public void The_endpoint_serializer_writes_the_text_itself()
    {
        var body = JsonSerializer.Serialize(Report(), IngressEndpoints.StateOptions);

        Assert.Contains($"\"title\": \"{TitleMarker}\"", body, StringComparison.Ordinal);
        Assert.Contains($"\"description\": \"{DescriptionMarker}\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_gives_a_size_and_never_the_text()
    {
        Assert.Equal("<text: 17 chars>", new OperatorText(TitleMarker).ToString());
        Assert.Equal("<text: none>", default(OperatorText).ToString());
    }

    [Fact]
    public void The_text_round_trips_through_the_converter()
    {
        var json = JsonSerializer.Serialize(new OperatorText(TitleMarker));

        Assert.Equal($"\"{TitleMarker}\"", json);
        Assert.Equal(TitleMarker, JsonSerializer.Deserialize<OperatorText>(json).Reveal());
    }

    private static StateReport Report()
    {
        var entry = new SessionStateEntry(
            "s-state-1",
            SessionState.Waiting,
            AttentionOrder.BandOf(SessionState.Waiting),
            @"cwd:C:\dev\PennCustQuote",
            @"C:\dev\PennCustQuote",
            new OperatorText(TitleMarker),
            At,
            At,
            At,
            null,
            null,
            [new WaitingTaskEntry("task-1", BackgroundTaskKind.Subagent, new OperatorText(DescriptionMarker), At)]);

        return new StateReport(
            At,
            1,
            new Dictionary<AttentionBand, int> { [AttentionBand.Working] = 1 },
            new TrayRollUp(SessionState.Waiting, TrayVisuals.ColourOf(SessionState.Waiting)),
            [entry]);
    }
}
