using System.Text.Json;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Ingress;

/// <summary>
/// A <c>Stop</c>'s <c>background_tasks</c>, read off the wire (T1.41, issue #52).
/// </summary>
/// <remarks>
/// Each body goes through the same deserialization ingress and replay use, then the real mapper,
/// so these assert what a real Stop becomes. The entry shape is the one measured on the
/// operator's archive: <c>id</c>, <c>type</c>, <c>status</c>, <c>description</c>, and
/// <c>command</c> on a shell.
/// </remarks>
public sealed class BackgroundTaskReaderTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>A command that must never be read, stored, shown or logged.</summary>
    internal const string PlantedCommand = "zqx-secret-command-marker-4w1 --token=hunter2";

    /// <summary>The allow-list: a running shell and a running subagent count, in order.</summary>
    [Fact]
    public void Running_shells_and_subagents_are_read_in_order()
    {
        var stop = StopWith("""
            [
              {"id":"b1","type":"shell","status":"running","description":"Run the test suite","command":"COMMAND"},
              {"id":"a1","type":"subagent","status":"running","description":"Review the diff","agent_type":"general-purpose"}
            ]
            """);

        Assert.Equal(
            [("b1", BackgroundTaskKind.Shell, "Run the test suite"), ("a1", BackgroundTaskKind.Subagent, "Review the diff")],
            stop.BackgroundTasks.Select(task => (task.Id, task.Kind, task.Description)).ToArray());
        Assert.Equal(0, stop.UnrecognisedBackgroundTasks);
    }

    /// <summary>A monitor is known and does not count; it is not unrecognised either.</summary>
    [Fact]
    public void A_monitor_counts_for_nothing()
    {
        var stop = StopWith("""[{"id":"m1","type":"monitor","status":"running","description":"Watch the artifact"}]""");

        Assert.Empty(stop.BackgroundTasks);
        Assert.Equal(0, stop.UnrecognisedBackgroundTasks);
    }

    /// <summary>
    /// An unseen type, or an entry with no type, does not count and is counted as unrecognised —
    /// the decisions record reports it.
    /// </summary>
    [Fact]
    public void An_unseen_type_counts_for_nothing_and_is_counted_as_unrecognised()
    {
        var stop = StopWith("""
            [
              {"id":"x1","type":"cron","status":"running","description":"Tick"},
              {"id":"x2","status":"running","description":"No type at all"},
              {"id":"b1","type":"shell","status":"running","description":"Run the build"}
            ]
            """);

        Assert.Equal(["b1"], stop.BackgroundTasks.Select(task => task.Id).ToArray());
        Assert.Equal(2, stop.UnrecognisedBackgroundTasks);
    }

    /// <summary>Only running entries count; an entry without an id is skipped.</summary>
    [Fact]
    public void Only_running_entries_with_an_id_count()
    {
        var stop = StopWith("""
            [
              {"id":"b1","type":"shell","status":"completed","description":"Done"},
              {"type":"shell","status":"running","description":"No id"},
              {"id":"b2","type":"shell","status":"running","description":"Still going"}
            ]
            """);

        Assert.Equal(["b2"], stop.BackgroundTasks.Select(task => task.Id).ToArray());
    }

    /// <summary>
    /// <strong>Degrade, never crash:</strong> any malformed list reads as no tasks, and the Stop is
    /// still mapped — its meaning before T1.41.
    /// </summary>
    [Theory]
    [InlineData("\"not a list\"")]
    [InlineData("{\"id\":\"b1\"}")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("[1, \"two\", null, []]")]
    [InlineData("[{\"id\":7,\"type\":\"shell\",\"status\":\"running\"}]")]
    [InlineData("[{\"id\":\"b1\",\"type\":[\"shell\"],\"status\":\"running\"}]")]
    public void A_malformed_list_reads_as_no_tasks(string list)
    {
        var stop = StopWith(list);

        Assert.Equal("done", stop.LastAssistantMessage);
        Assert.Empty(stop.BackgroundTasks);
    }

    /// <summary>A Stop with no list at all is today's Stop.</summary>
    [Fact]
    public void A_stop_without_the_field_is_todays_stop()
    {
        var stop = Map("""{"hook_event_name":"Stop","session_id":"s-1","cwd":"C:\\w","last_assistant_message":"done"}""");

        Assert.Empty(stop.BackgroundTasks);
        Assert.Equal(0, stop.UnrecognisedBackgroundTasks);
    }

    /// <summary>
    /// <strong>The command is never taken.</strong> Nothing the mapper produces carries it: not
    /// the tasks, not the Stop, not anything a record prints.
    /// </summary>
    [Fact]
    public void The_command_is_never_read_into_the_domain()
    {
        var stop = StopWith("""
            [{"id":"b1","type":"shell","status":"running","description":"Run the test suite","command":"COMMAND"}]
            """);

        Assert.Single(stop.BackgroundTasks);
        Assert.DoesNotContain(PlantedCommand, stop.BackgroundTasks[0].ToString(), StringComparison.Ordinal);

        // Everything the record prints except the raw body, which the archive keeps verbatim by
        // design (T1.17) and which PayloadJson refuses to print.
        Assert.DoesNotContain(PlantedCommand, (stop with { Payload = default }).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(PlantedCommand, stop.ToString(), StringComparison.Ordinal);
    }

    private static Stop StopWith(string list) =>
        Map($$"""
            {"hook_event_name":"Stop","session_id":"s-1","cwd":"C:\\w","last_assistant_message":"done","background_tasks":{{list.Replace("COMMAND", PlantedCommand, StringComparison.Ordinal)}}}
            """);

    private static Stop Map(string body)
    {
        var payload = JsonSerializer.Deserialize<HookPayload>(body, Options)!;
        var mapping = new HookEventMapper(new FakeClock()).Map(payload, new PayloadJson(body));

        return Assert.IsType<Stop>(mapping.Event);
    }
}
