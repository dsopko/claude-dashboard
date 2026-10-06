using System.Globalization;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;
using Serilog;
using Serilog.Events;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>The text written for a value, and the memory that names each unknown value once (T1.79, issue #9).</summary>
public sealed class EventValuesTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    /// <summary>A value as sent, up to the limit; none for a missing one.</summary>
    [Fact]
    public void A_value_is_written_as_sent_up_to_the_limit()
    {
        Assert.Equal("quota_auto_resume_stale", EventValues.Shown("quota_auto_resume_stale"));
        Assert.Equal(EventValues.None, EventValues.Shown(null));
        Assert.Equal(EventValues.None, EventValues.Shown(string.Empty));

        var full = new string('a', EventValues.MaxLength);
        Assert.Equal(full, EventValues.Shown(full));
        Assert.Equal(full + "…", EventValues.Shown(full + "b"));
        Assert.Equal(full + "…", EventValues.Shown(new string('a', 10_000)));
    }

    /// <summary>A control character is escaped, so one value stays on one line; a cut never splits a character.</summary>
    [Fact]
    public void A_control_character_is_escaped_and_a_cut_keeps_whole_characters()
    {
        Assert.Equal("a\\nb\\rc\\td\\u0007e", EventValues.Shown("a\nb\rc\td\ae"));

        // A surrogate pair across the limit: the cut leaves the pair out rather than half of it.
        var raw = new string('a', EventValues.MaxLength - 1) + "\U0001F600" + "tail";
        var shown = EventValues.Shown(raw);

        Assert.Equal(new string('a', EventValues.MaxLength - 1) + "…", shown);
    }

    /// <summary>The type field of each event that has one, as sent; none for the others.</summary>
    [Fact]
    public void The_type_field_is_the_raw_value_of_the_events_that_have_one()
    {
        var id = new SessionId("s-1");

        Assert.Equal("x_type", EventValues.TypeOf(new Notification { SessionId = id, Timestamp = At, Cwd = "c", NotificationType = "x_type" }));
        Assert.Equal("x_source", EventValues.TypeOf(new SessionStart { SessionId = id, Timestamp = At, Cwd = "c", Source = "x_source" }));
        Assert.Equal(string.Empty, EventValues.TypeOf(new SessionStart { SessionId = id, Timestamp = At, Cwd = "c" }));
        Assert.Equal("x_error", EventValues.TypeOf(new StopFailure { SessionId = id, Timestamp = At, Cwd = "c", ErrorKind = "x_error" }));
        Assert.Equal("x_reason", EventValues.TypeOf(new SessionEnd { SessionId = id, Timestamp = At, Cwd = "c", Reason = "x_reason" }));
        Assert.Null(EventValues.TypeOf(new Stop { SessionId = id, Timestamp = At, Cwd = "c" }));
        Assert.Null(EventValues.TypeOf(new UserPromptSubmit { SessionId = id, Timestamp = At, Cwd = "c", Prompt = "p" }));
    }

    /// <summary>
    /// The memory holds at most its limit of values. Past it, one more line says that no more are named in this run,
    /// and a value already named still writes nothing.
    /// </summary>
    [Fact]
    public void The_memory_names_at_most_its_limit_of_values_and_then_says_so_once()
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var log = new UnknownValueLog(logger);

        for (var i = 0; i < UnknownValueLog.MaxValues + 5; i++)
        {
            log.Note(Notified("type_" + i.ToString(CultureInfo.InvariantCulture)));
        }

        log.Note(Notified("type_0"));

        var lines = sink.AtLevel(LogEventLevel.Information).Select(e => e.RenderMessage(CultureInfo.InvariantCulture)).ToList();

        Assert.Equal(UnknownValueLog.MaxValues, log.Count);
        Assert.Equal(UnknownValueLog.MaxValues + 1, lines.Count);
        Assert.StartsWith("Claude Code sent more values that this build does not know.", lines[^1], StringComparison.Ordinal);
    }

    private static Notification Notified(string type) => new()
    {
        SessionId = new SessionId("s-1"), Timestamp = At, Cwd = "c", NotificationType = type,
    };
}
