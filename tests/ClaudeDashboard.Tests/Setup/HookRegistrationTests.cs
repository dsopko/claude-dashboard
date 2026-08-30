using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// Merging the dashboard's command handler into Claude Code's settings, and taking it out
/// (Impl §9.3; issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The fixture is the operator's real file shape</strong>, down to their four command
/// hooks and the empty <c>matcher</c> their groups carry — and, since issue #29, down to the
/// legacy HTTP handlers a pre-T1.28 build left in it. A merge tested only against the file it
/// produces itself would prove nothing about the file it will actually meet.
/// </para>
/// <para>
/// <strong>Every survival assertion is by command string, never by count.</strong> A count is
/// satisfied by the wrong four hooks, and "the file still parses" is satisfied by <c>{}</c>.
/// </para>
/// <para>
/// <strong>The operator's own hooks are <c>command</c> hooks now, which is the sharper test.</strong>
/// Before issue #29 ours were <c>http</c> and theirs were <c>command</c>, so a matcher that
/// discriminated on nothing but <c>type</c> would have passed every survival test here. Ours are
/// now the same type as theirs, and only the script path tells them apart.
/// </para>
/// </remarks>
public sealed class HookRegistrationTests
{
    private const string Cmd = @"C:\Windows\System32\cmd.exe";
    private const string Script = @"C:\Users\daves\AppData\Local\ClaudeDashboard\post-status.cmd";
    private const string OtherScript = @"C:\scratch\dashboard-home\post-status.cmd";

    private const string Url = "http://127.0.0.1:52789/hook";
    private const string ScratchUrl = "http://127.0.0.1:61000/hook";

    private const string Notify = "powershell -ExecutionPolicy Bypass -File C:/Users/daves/.claude/hooks/notify.ps1";
    private const string Start = "powershell -ExecutionPolicy Bypass -File C:/Users/daves/.claude/hooks/start.ps1";
    private const string Stop = "powershell -ExecutionPolicy Bypass -File C:/Users/daves/.claude/hooks/stop.ps1";

    /// <summary>The shape observed in the operator's own settings on 2026-08-26.</summary>
    /// <remarks>
    /// Their four command hooks sit in their own groups, each with an empty <c>matcher</c>, and a
    /// previous build's HTTP handlers sit in separate groups beside them — on two ports, because
    /// the derivation of Impl §3.1 moves and the old design never removed an allowlist entry. Both
    /// ports are in <c>allowedHttpHookUrls</c>, which is the state §9.3's remarks record as
    /// already being in this operator's file. Unrelated top-level keys are included because
    /// carrying them through untouched is half of what "merge, don't clobber" means.
    /// </remarks>
    private const string OperatorsFile = """
        {
          "cleanupPeriodDays": 30,
          "env": { "VISUAL": "code" },
          "permissions": { "allow": ["Bash(git status)"] },
          "model": "opus",
          "allowedHttpHookUrls": ["http://127.0.0.1:52789/hook", "http://127.0.0.1:61000/hook"],
          "httpHookAllowedEnvVars": ["CLAUDE_DASHBOARD_TOKEN"],
          "hooks": {
            "Notification": [
              { "matcher": "", "hooks": [ { "type": "command", "command": "NOTIFY" } ] },
              { "hooks": [ { "type": "http", "url": "http://127.0.0.1:52789/hook" } ] }
            ],
            "PermissionRequest": [
              { "matcher": "", "hooks": [ { "type": "command", "command": "NOTIFY" } ] },
              { "hooks": [ { "type": "http", "url": "http://127.0.0.1:52789/hook" } ] }
            ],
            "UserPromptSubmit": [
              { "matcher": "", "hooks": [ { "type": "command", "command": "START" } ] },
              { "hooks": [ { "type": "http", "url": "http://127.0.0.1:61000/hook" } ] }
            ],
            "Stop": [
              { "matcher": "", "hooks": [ { "type": "command", "command": "STOP" } ] },
              { "hooks": [ { "type": "http", "url": "http://127.0.0.1:52789/hook" } ] }
            ],
            "SessionStart": [
              { "hooks": [ { "type": "http", "url": "http://127.0.0.1:52789/hook" } ] }
            ]
          },
          "autoUpdatesChannel": "stable"
        }
        """;

    private static JsonObject Operators() =>
        HookRegistration.Parse(OperatorsFile
            .Replace("NOTIFY", Notify, StringComparison.Ordinal)
            .Replace("START", Start, StringComparison.Ordinal)
            .Replace("STOP", Stop, StringComparison.Ordinal));

    private static int Register(JsonObject settings, string script = Script) =>
        HookRegistration.Register(settings, Cmd, script);

    /// <summary>Every handler in the file, whatever event it hangs off.</summary>
    private static IEnumerable<JsonObject> Handlers(JsonObject settings) =>
        settings["hooks"] is JsonObject hooks
            ? hooks.SelectMany(pair => (pair.Value as JsonArray ?? []).OfType<JsonObject>())
                .SelectMany(group => (group["hooks"] as JsonArray ?? []).OfType<JsonObject>())
            : [];

    /// <summary>Every <c>command:script</c> pair in the file, so survival can be asserted by name.</summary>
    private static List<string> CommandsPerEvent(JsonObject settings)
    {
        var found = new List<string>();

        if (settings["hooks"] is not JsonObject hooks)
        {
            return found;
        }

        foreach (var (eventName, groups) in hooks)
        {
            foreach (var handler in (groups as JsonArray ?? []).OfType<JsonObject>()
                         .SelectMany(group => (group["hooks"] as JsonArray ?? []).OfType<JsonObject>()))
            {
                if (handler["type"]?.GetValue<string>() == "command"
                    && handler["command"]?.GetValue<string>() is { } command)
                {
                    found.Add($"{eventName}:{command}");
                }
            }
        }

        found.Sort(StringComparer.Ordinal);

        return found;
    }

    /// <summary>The script path each of our handlers names.</summary>
    private static List<string> OurScripts(JsonObject settings) =>
        [.. Handlers(settings)
            .Where(handler => handler["args"] is JsonArray)
            .Select(handler => ((JsonArray)handler["args"]!)[^1]!.GetValue<string>())
            .Order(StringComparer.Ordinal)];

    private static List<string> Urls(JsonObject settings) =>
        [.. Handlers(settings)
            .Select(handler => handler["url"]?.GetValue<string>())
            .Where(url => url is not null)
            .Select(url => url!)
            .Order(StringComparer.Ordinal)];

    private static List<string> AllowList(JsonObject settings) =>
        settings[HookRegistration.UrlAllowListKey] is JsonArray list
            ? [.. list.Select(entry => entry!.GetValue<string>())]
            : [];

    // ---- What must never happen ------------------------------------------------------------------

    /// <summary>
    /// The operator's four command hooks survive a registration, by their command strings.
    /// </summary>
    /// <remarks>
    /// This is the assertion the task is judged on, and issue #29 sharpened it: ours are command
    /// hooks now too, so nothing but the script path separates theirs from ours. Asserted by string
    /// and by the event each hangs off, because <c>notify.ps1</c> appears twice and a set-based
    /// check would let one of the two vanish unnoticed.
    /// </remarks>
    [Fact]
    public void Registering_leaves_every_command_hook_of_theirs_alone()
    {
        var settings = Operators();

        Register(settings);

        Assert.Equal(
            [
                $"Notification:{Notify}",
                $"PermissionRequest:{Notify}",
                $"Stop:{Stop}",
                $"UserPromptSubmit:{Start}",
            ],
            CommandsPerEvent(settings).Where(entry => !entry.Contains(Cmd, StringComparison.Ordinal)));
    }

    /// <summary>…and survive a removal, which is the path that runs on the way out.</summary>
    [Fact]
    public void Unregistering_leaves_every_command_hook_of_theirs_alone()
    {
        var settings = Operators();
        Register(settings);

        HookRegistration.Unregister(settings, Script);

        Assert.Equal(
            [
                $"Notification:{Notify}",
                $"PermissionRequest:{Notify}",
                $"Stop:{Stop}",
                $"UserPromptSubmit:{Start}",
            ],
            CommandsPerEvent(settings));
    }

    /// <summary>
    /// Removing the legacy HTTP handlers leaves the operator's command hooks alone as well.
    /// </summary>
    /// <remarks>
    /// The migration path runs against a file that is mostly theirs. A removal that took out
    /// handlers by group rather than by handler would empty the groups their hooks share an event
    /// with.
    /// </remarks>
    [Fact]
    public void Removing_the_legacy_handlers_leaves_every_command_hook_of_theirs_alone()
    {
        var settings = Operators();

        HookRegistration.RemoveLegacyHttp(settings);

        Assert.Equal(
            [
                $"Notification:{Notify}",
                $"PermissionRequest:{Notify}",
                $"Stop:{Stop}",
                $"UserPromptSubmit:{Start}",
            ],
            CommandsPerEvent(settings));
    }

    /// <summary>Registering over handlers nobody removed still leaves theirs alone.</summary>
    /// <remarks>
    /// The state after a hard kill: our handlers are already in the file when the next start
    /// merges. Add-after-remove runs the removal against a populated file, which is where a
    /// too-wide match does its damage.
    /// </remarks>
    [Fact]
    public void Registering_over_a_registration_that_was_never_removed_leaves_theirs_alone()
    {
        var settings = Operators();
        Register(settings);
        Register(settings);

        Assert.Equal(
            [
                $"Notification:{Notify}",
                $"PermissionRequest:{Notify}",
                $"Stop:{Stop}",
                $"UserPromptSubmit:{Start}",
            ],
            CommandsPerEvent(settings).Where(entry => !entry.Contains(Cmd, StringComparison.Ordinal)));
    }

    /// <summary>Unrelated top-level settings are carried through byte for byte.</summary>
    [Fact]
    public void Registering_carries_unrelated_settings_through_unchanged()
    {
        var settings = Operators();

        Register(settings);

        Assert.Equal(30, settings["cleanupPeriodDays"]!.GetValue<int>());
        Assert.Equal("code", settings["env"]!["VISUAL"]!.GetValue<string>());
        Assert.Equal("opus", settings["model"]!.GetValue<string>());
        Assert.Equal("stable", settings["autoUpdatesChannel"]!.GetValue<string>());
        Assert.Equal("Bash(git status)", settings["permissions"]!["allow"]![0]!.GetValue<string>());
    }

    /// <summary>Their groups keep their <c>matcher</c>, and ours never acquires one.</summary>
    /// <remarks>
    /// A merge that joined our handler onto their group would make it inherit a filter we did not
    /// choose, and the symptom would be events that simply never arrive.
    /// </remarks>
    [Fact]
    public void Registering_does_not_touch_the_matcher_on_their_groups()
    {
        var settings = Operators();

        Register(settings);

        var groups = (JsonArray)settings["hooks"]!["Notification"]!;
        var theirs = groups.OfType<JsonObject>().Single(group =>
            (group["hooks"] as JsonArray)!.OfType<JsonObject>()
                .Any(handler => handler["command"]?.GetValue<string>() == Notify));

        Assert.Equal(string.Empty, theirs["matcher"]!.GetValue<string>());

        var ours = groups.OfType<JsonObject>().Single(group =>
            (group["hooks"] as JsonArray)!.OfType<JsonObject>()
                .Any(handler => handler["args"] is JsonArray));

        Assert.Null(ours["matcher"]);
    }

    // ---- The shape that is written ---------------------------------------------------------------

    /// <summary>
    /// <strong>The handler is written in the exec form, with no shell involved.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every field asserted, because each is load-bearing and none announces its own absence.
    /// <c>command</c> plus <c>args</c> rather than one string: a single <c>command</c> runs under
    /// the hook's <c>shell</c>, which on Windows defaults to <c>bash</c> or to <c>powershell</c>
    /// depending on whether Git Bash is installed — so the same file would behave differently on
    /// two machines, and the two shells disagree about backslash paths.
    /// </para>
    /// <para>
    /// <c>async</c> true so the script never delays a turn. <c>asyncRewake</c> absent, and asserted
    /// absent: it exists to act on a hook's exit code, and this hook's exit code is always zero by
    /// design, so setting it would arm a mechanism against a value that never varies.
    /// </para>
    /// <para>
    /// Both paths absolute, because nothing expands an environment variable in this form.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_handler_is_the_exec_form_with_both_paths_absolute()
    {
        var settings = new JsonObject();

        Register(settings);

        var handler = Handlers(settings).First(h => h["args"] is JsonArray);

        Assert.Equal("command", handler["type"]!.GetValue<string>());
        Assert.Equal(Cmd, handler["command"]!.GetValue<string>());
        Assert.Equal(["/c", Script], ((JsonArray)handler["args"]!).Select(node => node!.GetValue<string>()));
        Assert.True(handler["async"]!.GetValue<bool>());
        Assert.Null(handler["asyncRewake"]);

        Assert.True(Path.IsPathFullyQualified(handler["command"]!.GetValue<string>()));
        Assert.True(Path.IsPathFullyQualified(((JsonArray)handler["args"]!)[^1]!.GetValue<string>()));
    }

    /// <summary>
    /// <strong>No token header, and no allowlist entry of any kind, is written.</strong>
    /// </summary>
    /// <remarks>
    /// A command hook inherits the whole environment, so the script reads
    /// <c>CLAUDE_DASHBOARD_TOKEN</c> for itself and neither <c>allowedEnvVars</c> nor
    /// <c>httpHookAllowedEnvVars</c> is needed for it to arrive. <c>allowedHttpHookUrls</c> does not
    /// apply to a command hook at all. Writing any of them would leave the operator's file
    /// describing a mechanism that is not in use — the same objection that kept the token header
    /// out when no token was configured.
    /// </remarks>
    [Fact]
    public void Registering_writes_no_allowlist_and_no_header()
    {
        var settings = new JsonObject();

        Register(settings);

        Assert.Null(settings[HookRegistration.UrlAllowListKey]);
        Assert.Null(settings[HookRegistration.EnvVarAllowListKey]);

        var handler = Handlers(settings).First(h => h["args"] is JsonArray);

        Assert.Null(handler["headers"]);
        Assert.Null(handler["allowedEnvVars"]);
        Assert.Null(handler["url"]);
    }

    /// <summary>The events registered are exactly the events ingress accepts — no more, no fewer.</summary>
    /// <remarks>
    /// Read from <see cref="HookEventNames.Accepted"/> rather than listed here, so registration and
    /// ingestion cannot drift apart. A hook registered but refused would fire, post, and be
    /// answered <c>200</c> with nothing done, on every occurrence, for ever.
    /// </remarks>
    [Fact]
    public void Registering_covers_exactly_the_events_ingress_accepts()
    {
        var settings = new JsonObject();

        var count = Register(settings);

        var ours = ((JsonObject)settings["hooks"]!)
            .Where(pair => (pair.Value as JsonArray ?? []).OfType<JsonObject>()
                .SelectMany(group => (group["hooks"] as JsonArray ?? []).OfType<JsonObject>())
                .Any(handler => handler["args"] is JsonArray))
            .Select(pair => pair.Key);

        Assert.Equal(HookEventNames.Accepted.Order(StringComparer.Ordinal), ours.Order(StringComparer.Ordinal));
        Assert.Equal(HookEventNames.Accepted.Count, count);
    }

    /// <summary>A handler of ours on an event ingress refuses is taken out, not left behind.</summary>
    /// <remarks>
    /// The drift this guards runs the other way: an event dropped from <c>Accepted</c> leaves a
    /// handler in the operator's file that fires for ever and is answered with nothing.
    /// </remarks>
    [Fact]
    public void A_handler_of_ours_on_an_event_ingress_refuses_is_removed()
    {
        var settings = HookRegistration.Parse($$"""
            {
              "hooks": {
                "PreCompact": [
                  { "hooks": [ { "type": "command", "command": "{{Cmd.Replace(@"\", @"\\", StringComparison.Ordinal)}}",
                    "args": ["/c", "{{Script.Replace(@"\", @"\\", StringComparison.Ordinal)}}"], "async": true } ] }
                ]
              }
            }
            """);

        Assert.False(HookEventNames.IsAccepted("PreCompact"));

        Register(settings);

        Assert.Null(settings["hooks"]!["PreCompact"]);
    }

    /// <summary>Registering twice produces one handler, not two.</summary>
    [Fact]
    public void Registering_twice_adds_no_duplicate_handler()
    {
        var settings = new JsonObject();

        Register(settings);
        Register(settings);

        Assert.Equal(HookEventNames.Accepted.Count, OurScripts(settings).Count);
    }

    /// <summary>
    /// A handler written by an older build is replaced rather than sat beside.
    /// </summary>
    /// <remarks>
    /// It matches by script path, so a handler that named the same script with a different shape —
    /// no <c>async</c>, an interpreter that has moved — is upgraded. Without add-after-remove the
    /// operator would accumulate one handler per build they had ever run.
    /// </remarks>
    [Fact]
    public void Registering_replaces_a_handler_written_by_an_older_build()
    {
        var settings = HookRegistration.Parse($$"""
            {
              "hooks": {
                "Stop": [
                  { "hooks": [ { "type": "command", "command": "cmd.exe",
                    "args": ["/c", "{{Script.Replace(@"\", @"\\", StringComparison.Ordinal)}}"] } ] }
                ]
              }
            }
            """);

        Register(settings);

        var stop = (JsonArray)settings["hooks"]!["Stop"]!;

        Assert.Single(stop);
        Assert.Equal(Cmd, Handlers(settings).First(h => h["args"] is JsonArray)["command"]!.GetValue<string>());
    }

    // ---- Identity ---------------------------------------------------------------------------------

    /// <summary>
    /// <strong>A path written differently but naming the same file is ours.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Forward slashes, a redundant <c>.\</c>, a different case: Windows names one file several
    /// ways, and a hand-edited entry may use any of them. Comparing the text would make the start
    /// check warn that the hook is missing while looking straight at it, and an operator who then
    /// ran <c>--install-hooks</c> would get a second handler beside the first.
    /// </para>
    /// <para>
    /// The theory is the identity rule and the count assertion is what makes it bite: each of these
    /// must be <em>removed</em>, not merely tolerated.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(@"C:\Users\daves\AppData\Local\ClaudeDashboard\post-status.cmd")]
    [InlineData("C:/Users/daves/AppData/Local/ClaudeDashboard/post-status.cmd")]
    [InlineData(@"C:\Users\daves\AppData\Local\ClaudeDashboard\.\post-status.cmd")]
    [InlineData(@"C:\Users\daves\AppData\Local\ClaudeDashboard\sounds\..\post-status.cmd")]
    [InlineData(@"c:\users\daves\appdata\local\claudedashboard\POST-STATUS.CMD")]
    public void A_path_that_names_the_same_file_is_ours(string written)
    {
        var settings = HookRegistration.Parse($$"""
            {
              "hooks": {
                "Stop": [
                  { "hooks": [ { "type": "command", "command": "cmd.exe",
                    "args": ["/c", "{{written.Replace(@"\", @"\\", StringComparison.Ordinal)}}"] } ] }
                ]
              }
            }
            """);

        Assert.Equal(1, HookRegistration.CountInstalled(settings, Script));
        Assert.Single(HookRegistration.Unregister(settings, Script));
        Assert.Null(settings["hooks"]);
    }

    /// <summary>A handler running a different script is not ours and is left alone.</summary>
    /// <remarks>
    /// The control for the theory above. A matcher that accepted any path, or that matched on the
    /// file name, would pass every assertion there and fail here — and matching on the file name is
    /// the tempting simplification, because both files are called <c>post-status.cmd</c>.
    /// </remarks>
    [Fact]
    public void A_handler_running_another_script_is_left_alone()
    {
        var settings = new JsonObject();
        HookRegistration.Register(settings, Cmd, OtherScript);

        Assert.Empty(HookRegistration.Unregister(settings, Script));
        Assert.Equal(HookEventNames.Accepted.Count, OurScripts(settings).Count);
    }

    /// <summary>
    /// A handler under another data folder is reported as foreign rather than as ours.
    /// </summary>
    /// <remarks>
    /// <c>CLAUDE_DASHBOARD_HOME</c> makes this a real configuration, not a corruption, and the
    /// start warning has to be able to say so. Ours is excluded from the list, which is what stops
    /// a healthy install reporting itself as a mismatch.
    /// </remarks>
    [Fact]
    public void A_handler_under_another_data_folder_is_reported_as_foreign()
    {
        var settings = new JsonObject();
        HookRegistration.Register(settings, Cmd, OtherScript);
        HookRegistration.Register(settings, Cmd, Script);

        Assert.Equal([OtherScript], HookRegistration.ForeignScriptPaths(settings, Script));
        Assert.Empty(HookRegistration.ForeignScriptPaths(settings, OtherScript).Except([Script]));
    }

    /// <summary>
    /// <strong>AN OPERATOR'S OWN EXEC-FORM HOOK IS NOT A FOREIGN DASHBOARD SCRIPT.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The shape no fixture had, and it is the shape this commit argues every Windows user
    /// should adopt.</strong> The two foreign tests above build their fixtures by registering two
    /// of <em>our</em> paths, so every handler is already ours-shaped; the <c>OperatorsFile</c>
    /// fixture's command hooks are the single-string form with no <c>args</c>, so
    /// <c>ScriptPathOf</c> returns null and they never reach the rule at all. Between them they
    /// left the one case that matters untested.
    /// </para>
    /// <para>
    /// <strong>What it cost.</strong> The filter returned every command handler that was not ours,
    /// so an operator running <c>node.exe … lint.js</c> in the exec form was reported as a
    /// dashboard installed under another data folder — a warning at start, telling them to check
    /// <c>CLAUDE_DASHBOARD_HOME</c> over a file that has nothing to do with this application, and
    /// putting a path out of their settings into our log while doing it.
    /// </para>
    /// <para>
    /// Both halves asserted, because they fail apart: theirs is not foreign, and ours under another
    /// root still is. A filter that returned nothing at all would satisfy the first alone.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_operators_own_exec_form_hook_is_not_a_foreign_dashboard_script()
    {
        var settings = HookRegistration.Parse($$"""
            {
              "hooks": {
                "PostToolUse": [
                  { "matcher": "Edit", "hooks": [ { "type": "command", "command": "C:\\Program Files\\nodejs\\node.exe",
                    "args": ["C:\\Users\\daves\\.claude\\hooks\\lint.js"] } ] }
                ],
                "Stop": [
                  { "hooks": [ { "type": "command", "command": "cmd.exe",
                    "args": ["/c", "{{OtherScript.Replace(@"\", @"\\", StringComparison.Ordinal)}}"] } ] }
                ]
              }
            }
            """);

        // Theirs is invisible to the rule; only a post-status.cmd under another root is foreign.
        Assert.Equal([OtherScript], HookRegistration.ForeignScriptPaths(settings, Script));

        // And it is not ours either, so nothing removes it.
        Assert.Empty(HookRegistration.Unregister(settings, Script));
        Assert.Equal(0, HookRegistration.CountInstalled(settings, Script));
    }

    /// <summary>
    /// <strong>A <c>"type"</c> that is not a string is ignored, not thrown over.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetValue&lt;string&gt;()</c> on a node holding a number throws
    /// <see cref="InvalidOperationException"/>, which no caller in this application catches. Two
    /// reads of <c>"type"</c> used it, and the start check reads Claude Code's settings on every
    /// launch — so <strong>one hand-edited <c>"type": 1</c> stopped the dashboard starting</strong>,
    /// which is the same failure the duplicate-key fix was written about.
    /// </para>
    /// <para>
    /// Every read of a string out of this tree goes through the same helper now. The theory covers
    /// each JSON kind that is not a string, because the throw is about the kind and not about the
    /// value.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[\"command\"]")]
    [InlineData("{ \"kind\": \"command\" }")]
    public void A_type_that_is_not_a_string_is_ignored_rather_than_thrown_over(string type)
    {
        var settings = HookRegistration.Parse($$"""
            {
              "allowedHttpHookUrls": ["http://127.0.0.1:52789/hook"],
              "hooks": {
                "Stop": [
                  { "hooks": [ { "type": {{type}}, "command": "beep", "url": "http://127.0.0.1:52789/hook" } ] }
                ]
              }
            }
            """);

        // Not ours by either rule, and no rule throws while deciding that.
        Assert.Equal(0, HookRegistration.CountInstalled(settings, Script));
        Assert.Empty(HookRegistration.Unregister(settings, Script));
        Assert.Empty(HookRegistration.ForeignScriptPaths(settings, Script));
        Assert.Empty(HookRegistration.RemoveLegacyHttp(settings).Urls);

        // Registering over it still works, which is what the operator's repair depends on.
        Assert.Equal(HookEventNames.Accepted.Count, Register(settings));
    }

    /// <summary>An unusable path fails to match instead of throwing.</summary>
    /// <remarks>
    /// The value comes out of the operator's file and may be anything at all. A throw here would
    /// take down the start check, which exists to make a missing hook visible — so a broken entry
    /// would hide the very condition the check reports.
    /// </remarks>
    [Fact]
    public void An_unusable_path_in_the_file_is_not_ours_and_does_not_throw()
    {
        var settings = HookRegistration.Parse("""
            {
              "hooks": {
                "Stop": [
                  { "hooks": [ { "type": "command", "command": "cmd.exe", "args": ["/c", "   "] } ] }
                ]
              }
            }
            """);

        Assert.Equal(0, HookRegistration.CountInstalled(settings, Script));
        Assert.Empty(HookRegistration.Unregister(settings, Script));
    }

    /// <summary>
    /// The script is identified by the <em>last</em> argument, not by any argument.
    /// </summary>
    /// <remarks>
    /// <c>/c</c> is an argument too. A rule of "any argument matches" would let a handler be
    /// claimed by a switch, and a settings file naming our script as a working directory or a
    /// parameter to something else would be removed on the strength of it.
    /// </remarks>
    [Fact]
    public void The_script_is_the_last_argument_and_not_merely_one_of_them()
    {
        var settings = HookRegistration.Parse($$"""
            {
              "hooks": {
                "Stop": [
                  { "hooks": [ { "type": "command", "command": "cmd.exe",
                    "args": ["/c", "{{Script.Replace(@"\", @"\\", StringComparison.Ordinal)}}", "theirs.exe"] } ] }
                ]
              }
            }
            """);

        Assert.Equal(0, HookRegistration.CountInstalled(settings, Script));
        Assert.Empty(HookRegistration.Unregister(settings, Script));
    }

    // ---- Removal ----------------------------------------------------------------------------------

    /// <summary>Removal takes out every handler of ours and names each one.</summary>
    [Fact]
    public void Unregistering_removes_every_handler_of_ours_and_names_them()
    {
        var settings = Operators();
        Register(settings);

        var removed = HookRegistration.Unregister(settings, Script);

        Assert.Equal(HookEventNames.Accepted.Count, removed.Count);
        Assert.All(removed, path => Assert.Equal(Script, path));
        Assert.Empty(OurScripts(settings));
    }

    /// <summary>An array emptied by removal is deleted, and so is <c>hooks</c> if it empties.</summary>
    [Fact]
    public void Unregistering_deletes_containers_it_empties()
    {
        var settings = new JsonObject();
        Register(settings);

        HookRegistration.Unregister(settings, Script);

        Assert.Null(settings["hooks"]);
    }

    /// <summary>Removing what was never there removes nothing and reports nothing.</summary>
    [Fact]
    public void Unregistering_what_was_never_there_removes_nothing()
    {
        var settings = Operators();
        var before = HookRegistration.Render(settings);

        Assert.Empty(HookRegistration.Unregister(settings, Script));
        Assert.Equal(before, HookRegistration.Render(settings));
    }

    // ---- Legacy removal ---------------------------------------------------------------------------

    /// <summary>
    /// <strong>The legacy HTTP handlers go, on every port, with their allowlist entries — by name.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every port, not only the current one. The derivation of Impl §3.1 moves, and the old design
    /// never removed an allowlist entry, so a machine carries one per port it ever bound. An
    /// operator upgrading has to be left with none of them.
    /// </para>
    /// <para>
    /// <strong>Asserted by name, because that is the safeguard.</strong> Both rules match a shape
    /// rather than a marker, so an entry the operator wrote themselves can match. What left their
    /// file has to be printable, and a count cannot be printed usefully.
    /// </para>
    /// </remarks>
    [Fact]
    public void Removing_the_legacy_handlers_takes_every_port_and_names_what_went()
    {
        var settings = Operators();

        var removed = HookRegistration.RemoveLegacyHttp(settings);

        Assert.Empty(Urls(settings));
        Assert.Equal([Url, Url, Url, Url, ScratchUrl], removed.Urls.Order(StringComparer.Ordinal));
        Assert.Equal([Url, ScratchUrl], removed.AllowListUrls);
        Assert.Empty(removed.ScriptPaths);
        Assert.Empty(AllowList(settings));
    }

    /// <summary>
    /// The variable allowlist is left exactly as it was.
    /// </summary>
    /// <remarks>
    /// Impl §9.3's "leave the allowlists alone" was about a URL still in use, and after issue #29
    /// no loopback hook URL is in use by anybody — so the URL list is cleaned. A variable name is
    /// different: <c>CLAUDE_DASHBOARD_TOKEN</c> costs nothing where it is and may be serving
    /// something the operator set up themselves.
    /// </remarks>
    [Fact]
    public void Removing_the_legacy_handlers_leaves_the_variable_allowlist_alone()
    {
        var settings = Operators();

        HookRegistration.RemoveLegacyHttp(settings);

        Assert.Equal(
            ["CLAUDE_DASHBOARD_TOKEN"],
            ((JsonArray)settings[HookRegistration.EnvVarAllowListKey]!).Select(node => node!.GetValue<string>()));
    }

    /// <summary>An HTTP handler that is not a loopback hook URL is left alone.</summary>
    /// <remarks>
    /// The control. A removal that took every <c>http</c> handler would pass the test above and
    /// delete an integration of the operator's — and the old <c>IsOurs</c> never claimed that
    /// much, so this rule is not being widened while it is being kept.
    /// </remarks>
    [Theory]
    [InlineData("http://127.0.0.1:52789/other")]
    [InlineData("http://127.0.0.1/hook")]
    [InlineData("http://localhost:52789/hook")]
    [InlineData("http://192.168.1.9:52789/hook")]
    [InlineData("http://127.0.0.1:abc/hook")]
    public void An_http_handler_that_is_not_a_loopback_hook_url_is_left_alone(string url)
    {
        var settings = HookRegistration.Parse($$"""
            { "allowedHttpHookUrls": ["{{url}}"],
              "hooks": { "Stop": [ { "hooks": [ { "type": "http", "url": "{{url}}" } ] } ] } }
            """);

        var removed = HookRegistration.RemoveLegacyHttp(settings);

        Assert.Equal(0, removed.Total);
        Assert.Equal([url], Urls(settings));
        Assert.Equal([url], AllowList(settings));
    }

    /// <summary>Removing the legacy handlers where there are none changes nothing.</summary>
    [Fact]
    public void Removing_the_legacy_handlers_where_there_are_none_changes_nothing()
    {
        var settings = new JsonObject();
        Register(settings);
        var before = HookRegistration.Render(settings);

        Assert.Equal(0, HookRegistration.RemoveLegacyHttp(settings).Total);
        Assert.Equal(before, HookRegistration.Render(settings));
    }

    // ---- The start check --------------------------------------------------------------------------

    /// <summary>The check counts events carrying the handler, not handlers.</summary>
    /// <remarks>
    /// The two differ exactly when something has gone wrong: a file with two handlers on one event
    /// and none on the other seven is a duplicated install, not a complete one, and a count of
    /// handlers would call it healthy.
    /// </remarks>
    [Fact]
    public void The_check_counts_events_and_not_handlers()
    {
        var settings = new JsonObject();
        Register(settings);

        Assert.Equal(HookEventNames.Accepted.Count, HookRegistration.CountInstalled(settings, Script));

        var stop = (JsonArray)settings["hooks"]![HookEventNames.Stop]!;
        stop.Add(stop[0]!.DeepClone());

        Assert.Equal(HookEventNames.Accepted.Count, HookRegistration.CountInstalled(settings, Script));
    }

    /// <summary>A partial install counts as partial.</summary>
    [Fact]
    public void The_check_sees_a_hook_that_was_deleted_from_one_event()
    {
        var settings = new JsonObject();
        Register(settings);

        ((JsonObject)settings["hooks"]!).Remove(HookEventNames.Stop);

        Assert.Equal(HookEventNames.Accepted.Count - 1, HookRegistration.CountInstalled(settings, Script));
    }

    // ---- Parsing ----------------------------------------------------------------------------------

    [Fact]
    public void An_empty_file_parses_as_empty_settings() =>
        Assert.Empty(HookRegistration.Parse("   "));

    /// <summary>Comments and trailing commas are tolerated, because people write both.</summary>
    /// <remarks>
    /// They are not preserved: the merge rewrites the file and <c>JsonNode</c> carries no comment.
    /// That is a real cost of touching the file, and it is why the backup exists.
    /// </remarks>
    [Fact]
    public void A_hand_edited_file_with_comments_still_parses()
    {
        var settings = HookRegistration.Parse("""
            {
              // the model I use
              "model": "opus",
              "cleanupPeriodDays": 30,
            }
            """);

        Assert.Equal("opus", settings["model"]!.GetValue<string>());
    }

    /// <summary>
    /// A malformed file throws rather than parsing as empty — the caller must not write back.
    /// </summary>
    /// <remarks>
    /// "Use defaults" is right for the dashboard's own settings and catastrophic here: writing a
    /// default object back would delete every hook, permission and preference the operator has.
    /// <c>ThrowsAny</c> because the reader raises a <c>JsonReaderException</c> subclass and the
    /// contract the caller catches on is the base type.
    /// </remarks>
    [Fact]
    public void A_malformed_file_throws_rather_than_defaulting() =>
        Assert.ThrowsAny<JsonException>(() => HookRegistration.Parse("{ \"model\": "));

    [Fact]
    public void A_file_that_is_not_an_object_throws() =>
        Assert.Throws<JsonException>(() => HookRegistration.Parse("[1, 2, 3]"));

    /// <summary>
    /// <strong>A duplicate key fails at the parse, not later from somewhere unrelated.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Found by running the switch against a file this test's author had broken by
    /// accident.</strong> Two <c>"Stop"</c> keys in one object is legal JSON and is what a hand
    /// merge of two blocks produces. <see cref="JsonNode"/> builds its dictionary lazily, so it
    /// parses cleanly and then throws <see cref="ArgumentException"/> — not
    /// <see cref="JsonException"/> — from the first indexer that touches it, which may be in an
    /// entirely different method.
    /// </para>
    /// <para>
    /// <strong>What that cost before it was fixed.</strong> Every caller catches
    /// <c>JsonException</c> and none catches <c>ArgumentException</c>, so the throw escaped. Since
    /// issue #29 reads Claude Code's settings at every start, one duplicate key in the operator's
    /// file stopped the dashboard starting — and a tray application that will not start does not
    /// present as a configuration error. It presents as the dashboard being gone.
    /// </para>
    /// <para>
    /// The nested case is the one that matters and the flat one is its control: the duplicate that
    /// actually happens is inside <c>hooks</c>, which is an object inside the object being parsed.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("""{ "model": "opus", "model": "sonnet" }""")]
    [InlineData("""{ "hooks": { "Stop": [], "Stop": [] } }""")]
    [InlineData("""{ "hooks": { "Stop": [ { "matcher": "", "matcher": "x" } ] } }""")]
    public void A_duplicate_key_is_reported_by_the_parse(string json) =>
        Assert.Throws<JsonException>(() => HookRegistration.Parse(json));

    /// <summary>…and the file it came from is never written back.</summary>
    /// <remarks>
    /// The whole reason to refuse rather than repair. Keeping the last of two duplicates would
    /// render the file back with one of the operator's keys silently gone.
    /// </remarks>
    [Fact]
    public void A_duplicate_key_is_refused_rather_than_repaired()
    {
        var thrown = Assert.Throws<JsonException>(
            () => HookRegistration.Parse("""{ "hooks": { "Stop": [], "Stop": [] } }"""));

        Assert.Contains("Stop", thrown.Message, StringComparison.Ordinal);
    }
}
