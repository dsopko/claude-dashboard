using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// The handler's shape, the parse of Claude Code's settings, and the count of old handlers in them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The parse and the count run at every start, on a file the operator may have edited by
/// hand.</strong> So most of what is here is about input that is wrong: a duplicate key, a value
/// of the wrong type, a path that is not a path. Each must come back as "cannot be read" or "not
/// ours" and never as an exception that stops the dashboard starting.
/// </para>
/// <para>
/// Nothing here writes a settings file, because nothing in the product does.
/// </para>
/// </remarks>
public sealed class HookHandlersTests
{
    private const string Interpreter = @"C:\Windows\System32\cmd.exe";
    private const string Script = @"C:\Data\ClaudeDashboard\post-status.cmd";

    private static JsonObject SettingsWithOldHooks(string script = Script) => new()
    {
        ["model"] = "opus",
        [HookHandlers.HooksKey] = HookHandlers.ForEveryEvent(Interpreter, script),
    };

    // ---- The handler ----------------------------------------------------------------------------

    [Fact]
    public void Every_accepted_event_gets_one_group_holding_one_handler()
    {
        var hooks = HookHandlers.ForEveryEvent(Interpreter, Script);

        Assert.Equal(
            HookEventNames.Accepted.Order(StringComparer.Ordinal),
            hooks.Select(pair => pair.Key));

        foreach (var pair in hooks)
        {
            var group = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(pair.Value)));
            Assert.Single(Assert.IsType<JsonArray>(group["hooks"]));
        }
    }

    [Theory]
    [InlineData(null, Script)]
    [InlineData("  ", Script)]
    [InlineData(Interpreter, null)]
    [InlineData(Interpreter, "")]
    public void The_handler_needs_both_paths(string? interpreter, string? script) =>
        Assert.ThrowsAny<ArgumentException>(() => HookHandlers.ForEveryEvent(interpreter!, script!));

    [Fact]
    public void The_interpreter_is_cmd_by_absolute_path()
    {
        Assert.True(Path.IsPathFullyQualified(HookHandlers.Interpreter));
        Assert.Equal("cmd.exe", Path.GetFileName(HookHandlers.Interpreter));
        Assert.True(File.Exists(HookHandlers.Interpreter));
    }

    // ---- The parse ------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    public void An_empty_file_is_settings_with_nothing_in_them(string text) =>
        Assert.Empty(HookHandlers.Parse(text));

    [Fact]
    public void Comments_and_trailing_commas_are_read_through()
    {
        var settings = HookHandlers.Parse(
            """
            {
              // the model I use
              "model": "opus", /* and a block comment */
              "hooks": {},
            }
            """);

        Assert.Equal("opus", (string?)settings["model"]);
    }

    [Theory]
    [InlineData("{ \"hooks\": ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("3")]
    [InlineData("\"text\"")]
    public void Text_that_is_not_a_settings_object_cannot_be_read(string text) =>
        Assert.ThrowsAny<JsonException>(() => HookHandlers.Parse(text));

    /// <summary>
    /// <strong>A duplicate key fails at the parse, as a <see cref="JsonException"/>.</strong>
    /// <see cref="JsonNode"/> finds it lazily and throws an <see cref="ArgumentException"/> from
    /// whichever indexer first touches the object — which no caller catches, on a path that runs
    /// at every start. Top level and nested, because the nested one is inside an array inside an
    /// object, which is where a hand-merged hooks block puts it.
    /// </summary>
    [Theory]
    [InlineData("""{ "model": "a", "model": "b" }""")]
    [InlineData("""{ "hooks": { "Stop": [], "Stop": [] } }""")]
    [InlineData("""{ "hooks": { "Stop": [ { "matcher": "a", "matcher": "b" } ] } }""")]
    public void A_duplicate_key_cannot_be_read_and_says_so_at_the_parse(string text) =>
        Assert.ThrowsAny<JsonException>(() => HookHandlers.Parse(text));

    // ---- The count of old handlers --------------------------------------------------------------

    [Fact]
    public void Settings_with_no_hooks_hold_no_old_handler()
    {
        Assert.Equal(0, HookHandlers.CountInSettings([], Script));
        Assert.Equal(0, HookHandlers.CountInSettings(HookHandlers.Parse("""{ "model": "opus" }"""), Script));
    }

    [Fact]
    public void A_handler_on_every_event_is_counted_on_every_event() =>
        Assert.Equal(HookEventNames.Accepted.Count, HookHandlers.CountInSettings(SettingsWithOldHooks(), Script));

    [Fact]
    public void A_handler_on_some_events_is_counted_on_those()
    {
        var settings = SettingsWithOldHooks();
        var hooks = (JsonObject)settings[HookHandlers.HooksKey]!;
        hooks.Remove(HookEventNames.Stop);
        hooks.Remove(HookEventNames.SessionEnd);

        Assert.Equal(HookEventNames.Accepted.Count - 2, HookHandlers.CountInSettings(settings, Script));
    }

    /// <summary>
    /// <strong>The same file, spelled differently, is still ours.</strong> Forward slashes, another
    /// case, and a redundant segment all name one script on Windows, and a hand edit produces each.
    /// </summary>
    [Theory]
    [InlineData("C:/Data/ClaudeDashboard/post-status.cmd")]
    [InlineData(@"c:\data\claudedashboard\POST-STATUS.CMD")]
    [InlineData(@"C:\Data\ClaudeDashboard\.\post-status.cmd")]
    [InlineData(@"C:\Data\Other\..\ClaudeDashboard\post-status.cmd")]
    public void A_handler_is_ours_as_Windows_compares_paths(string written) =>
        Assert.Equal(HookEventNames.Accepted.Count, HookHandlers.CountInSettings(SettingsWithOldHooks(written), Script));

    [Fact]
    public void A_handler_that_runs_another_data_folders_script_is_not_ours() =>
        Assert.Equal(
            0,
            HookHandlers.CountInSettings(SettingsWithOldHooks(@"C:\Elsewhere\post-status.cmd"), Script));

    /// <summary>
    /// <strong>The operator's own hooks are never counted.</strong> Each of these is a hook a person
    /// could have, including one that mentions the script somewhere other than as the thing run.
    /// </summary>
    [Theory]
    [InlineData("""{ "type": "command", "command": "node.exe", "args": ["lint.js"] }""")]
    [InlineData("""{ "type": "command", "command": "C:\\Data\\ClaudeDashboard\\post-status.cmd" }""")]
    [InlineData("""{ "type": "command", "command": "cmd.exe", "args": ["C:\\Data\\ClaudeDashboard\\post-status.cmd", "/c"] }""")]
    [InlineData("""{ "type": "http", "url": "http://127.0.0.1:52888/hook" }""")]
    [InlineData("""{ "type": "command", "command": "cmd.exe", "args": [] }""")]
    public void Somebody_elses_handler_is_not_counted(string handler)
    {
        var settings = HookHandlers.Parse($$"""{ "hooks": { "Stop": [ { "hooks": [ {{handler}} ] } ] } }""");

        Assert.Equal(0, HookHandlers.CountInSettings(settings, Script));
    }

    /// <summary>
    /// <strong>A value of the wrong type is "not ours", never an exception.</strong> Each of these
    /// throws from a careless read — <c>GetValue&lt;string&gt;()</c> on a number, an indexer on a
    /// string — and the read is on the startup path.
    /// </summary>
    [Theory]
    [InlineData("""{ "hooks": 7 }""")]
    [InlineData("""{ "hooks": "none" }""")]
    [InlineData("""{ "hooks": [] }""")]
    [InlineData("""{ "hooks": { "Stop": "x" } }""")]
    [InlineData("""{ "hooks": { "Stop": [ 3, "x", null ] } }""")]
    [InlineData("""{ "hooks": { "Stop": [ { "hooks": "x" } ] } }""")]
    [InlineData("""{ "hooks": { "Stop": [ { "hooks": [ 3, null, "x" ] } ] } }""")]
    [InlineData("""{ "hooks": { "Stop": [ { "hooks": [ { "type": 3, "args": ["a"] } ] } ] } }""")]
    [InlineData("""{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "args": 3 } ] } ] } }""")]
    [InlineData("""{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "args": [ 3 ] } ] } ] } }""")]
    [InlineData("""{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "args": [ "C:\\bad\u0000path" ] } ] } ] } }""")]
    public void A_misshapen_hooks_block_counts_nothing_and_does_not_throw(string text) =>
        Assert.Equal(0, HookHandlers.CountInSettings(HookHandlers.Parse(text), Script));

    [Fact]
    public void Ours_among_the_operators_own_hooks_is_counted_once_for_the_event()
    {
        var settings = HookHandlers.Parse(
            """
            {
              "hooks": {
                "Stop": [
                  { "matcher": "x", "hooks": [ { "type": "command", "command": "node.exe", "args": ["lint.js"] } ] },
                  { "hooks": [
                      { "type": "command", "command": "cmd.exe", "args": ["/c", "C:\\Data\\ClaudeDashboard\\post-status.cmd"] },
                      { "type": "command", "command": "cmd.exe", "args": ["/c", "C:\\Data\\ClaudeDashboard\\post-status.cmd"] }
                  ] }
                ]
              }
            }
            """);

        Assert.Equal(1, HookHandlers.CountInSettings(settings, Script));
    }

    [Fact]
    public void The_count_needs_a_script_path()
    {
        Assert.ThrowsAny<ArgumentException>(() => HookHandlers.CountInSettings([], " "));
        Assert.Throws<ArgumentNullException>(() => HookHandlers.CountInSettings(null!, Script));
    }
}
