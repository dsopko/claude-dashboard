using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// The message box a second start shows when it exits without bringing a window forward
/// (Impl §5.3, issue #150).
/// </summary>
/// <remarks>
/// <strong>No test here shows a box.</strong> The text is a pure function, and the box is a seam
/// that each test replaces with a recorder or a thrower.
/// </remarks>
public sealed class StandDownNoticeTests
{
    private const string LogFolder = @"C:\scratch\logs";

    [Fact]
    public void Another_session_gets_the_other_session_text() =>
        AssertText(StandDownNotice.OtherSession, StandDownNotice.TextFor(StartupAction.ExitForOtherSession, null, LogFolder));

    [Fact]
    public void A_copy_that_holds_the_gate_and_cannot_be_asked_gets_the_cannot_be_reached_text() =>
        AssertText(StandDownNotice.CannotBeReached, StandDownNotice.TextFor(StartupAction.ReportAndExit, null, LogFolder));

    /// <summary>Every <c>/show</c> that did not bring the window forward, and the one never sent.</summary>
    [Theory]
    [InlineData(ShowSignalOutcome.Rejected)]
    [InlineData(ShowSignalOutcome.Failed)]
    [InlineData(ShowSignalOutcome.Unreachable)]
    [InlineData(null)]
    public void A_show_that_did_not_work_gets_the_cannot_be_reached_text(ShowSignalOutcome? outcome) =>
        AssertText(StandDownNotice.CannotBeReached, StandDownNotice.TextFor(StartupAction.SignalAndExit, outcome, LogFolder));

    /// <summary>The window came forward: nothing to say.</summary>
    [Fact]
    public void A_show_that_worked_shows_nothing() =>
        Assert.Null(StandDownNotice.TextFor(StartupAction.SignalAndExit, ShowSignalOutcome.Shown, LogFolder));

    /// <summary>A start that goes on does not exit, so it has no box.</summary>
    [Fact]
    public void A_start_that_goes_on_shows_nothing() =>
        Assert.Null(StandDownNotice.TextFor(StartupAction.StartNormally, null, LogFolder));

    /// <summary>The two texts are the ruled words, in plain language, and they differ.</summary>
    [Fact]
    public void The_two_texts_are_the_ruled_words()
    {
        Assert.Equal(
            "Claude Dashboard is already running in another Windows session for this account. " +
            "Quit it there, or end its process, and start again.",
            StandDownNotice.OtherSession);
        Assert.Equal(
            "Claude Dashboard is already running, but it could not be asked to show its window. " +
            "Open it from its tray icon, or end its process and start again.",
            StandDownNotice.CannotBeReached);
        Assert.Equal("Claude Dashboard", StandDownNotice.Title);
    }

    /// <summary>The box gets the text and the title, once.</summary>
    [Fact]
    public void The_box_gets_the_text_and_the_title()
    {
        var shown = new List<(string Text, string Title)>();
        var sink = new RecordingLogSink();

        StandDownNotice.Show((text, title) => shown.Add((text, title)), "the text", Logger(sink));

        Assert.Equal([("the text", "Claude Dashboard")], shown);
        Assert.Empty(sink.Events);
    }

    /// <summary>No text, no box.</summary>
    [Fact]
    public void No_text_shows_no_box()
    {
        var calls = 0;

        StandDownNotice.Show((_, _) => calls++, null, Logger(new RecordingLogSink()));

        Assert.Equal(0, calls);
    }

    /// <summary>A box that throws is logged, and the exit goes on.</summary>
    [Fact]
    public void A_box_that_throws_is_logged_and_not_rethrown()
    {
        var sink = new RecordingLogSink();

        StandDownNotice.Show((_, _) => throw new InvalidOperationException("no desktop"), "the text", Logger(sink));

        var line = Assert.Single(sink.Events);
        Assert.Equal(LogEventLevel.Warning, line.Level);
        Assert.IsType<InvalidOperationException>(line.Exception);
    }

    private static void AssertText(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.StartsWith(expected, actual, StringComparison.Ordinal);

        // Each text ends with where the detail is.
        Assert.EndsWith($"The details are in the newest log file in {LogFolder}", actual, StringComparison.Ordinal);
    }

    private static ILogger Logger(RecordingLogSink sink) =>
        new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
}
