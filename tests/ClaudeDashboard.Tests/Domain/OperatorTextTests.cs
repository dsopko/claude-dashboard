using System.Text.Json;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// <see cref="OperatorText"/>: a title or a task description in the <c>/state</c> report (T1.46). The endpoint writes
/// the text, and since T1.76 (issue #118) a log line that names it shows the text too.
/// </summary>
public sealed class OperatorTextTests
{
    private const string TitleMarker = "TITLE-MARKER-c41e";
    private const string DescriptionMarker = "DESCRIPTION-MARKER-0d7b";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    /// <summary>
    /// <strong>A log line shows the title and the description</strong> (T1.76, issue #118): plainly, destructured,
    /// and for one entry or one task.
    /// </summary>
    [Fact]
    public void A_log_line_shows_the_title_and_the_description()
    {
        var sink = new RecordingLogSink();
        using var logger = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

        var report = Report();

        logger.Information("The report, destructured: {@Report}", report);
        logger.Information("One entry, destructured: {@Entry}", report.Sessions[0]);
        logger.Information("The title alone: {Title} {@Title}", report.Sessions[0].Title, report.Sessions[0].Title);
        logger.Information("One task, plainly: {Description}", report.Sessions[0].WaitingOn[0].Description);

        Assert.Equal(4, sink.Messages.Count);
        Assert.Equal(3, sink.Containing(TitleMarker));
        Assert.Equal(3, sink.Containing(DescriptionMarker));
    }

    [Fact]
    public void The_endpoint_serializer_writes_the_text_itself()
    {
        var body = JsonSerializer.Serialize(Report(), IngressEndpoints.StateOptions);

        Assert.Contains($"\"title\": \"{TitleMarker}\"", body, StringComparison.Ordinal);
        Assert.Contains($"\"description\": \"{DescriptionMarker}\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_gives_the_text()
    {
        Assert.Equal(TitleMarker, new OperatorText(TitleMarker).ToString());
        Assert.Equal(TitleMarker, new OperatorText(TitleMarker).Text);
        Assert.Equal(string.Empty, default(OperatorText).ToString());
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
