using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Setup;

namespace ClaudeDashboard.Tests.Setup;

/// <summary>
/// Merging the dashboard's handlers into Claude Code's settings, and taking them out (Impl §9.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The fixture is the operator's real file shape</strong>, down to their four command
/// hooks and the empty <c>matcher</c> their groups carry. A merge tested only against the file it
/// produces itself would prove nothing about the file it will actually meet.
/// </para>
/// <para>
/// <strong>Every survival assertion is by command string, never by count.</strong> A count is
/// satisfied by the wrong four hooks, and "the file still parses" is satisfied by <c>{}</c>.
/// </para>
/// </remarks>
public sealed class HookRegistrationTests
{
    private const string Url = "http://127.0.0.1:52789/hook";
    private const string OtherUrl = "http://127.0.0.1:61000/hook";

    private const string Notify = "powershell -ExecutionPolicy Bypass -File C:/Users/daves/.claude/hooks/notify.ps1";
    private const string Start = "powershell -ExecutionPolicy Bypass -File C:/Users/daves/.claude/hooks/start.ps1";
    private const string Stop = "powershell -ExecutionPolicy Bypass -File C:/Users/daves/.claude/hooks/stop.ps1";

    /// <summary>The shape observed in the operator's own settings on 2026-08-26.</summary>
    /// <remarks>
    /// Their four command hooks sit in their own groups, each with an empty <c>matcher</c>, and
    /// the dashboard's handlers sit in separate groups beside them. Unrelated top-level keys are
    /// included because carrying them through untouched is half of what "merge, don't clobber"
    /// means.
    /// </remarks>
    private const string OperatorsFile = """
        {
          "cleanupPeriodDays": 30,
          "env": { "VISUAL": "code" },
          "permissions": { "allow": ["Bash(git status)"] },
          "model": "opus",
          "allowedHttpHookUrls": ["http://127.0.0.1:52789/hook"],
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
              { "hooks": [ { "type": "http", "url": "http://127.0.0.1:52789/hook" } ] }
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

    // ---- What must never happen ------------------------------------------------------------------

    /// <summary>
    /// The operator's four command hooks survive a registration, by their command strings.
    /// </summary>
    /// <remarks>
    /// This is the assertion the task is judged on. Asserted by string and by the event each hangs
    /// off, because <c>notify.ps1</c> appears twice and a set-based check would let one of the two
    /// vanish unnoticed.
    /// </remarks>
    [Fact]
    public void Registering_leaves_every_command_hook_of_theirs_alone()
    {
        var settings = Operators();

        HookRegistration.Register(settings, Url, tokenVariable: null);

        AssertTheirCommandHooksSurvive(settings);
    }

    /// <summary>…and so does removing.</summary>
    [Fact]
    public void Unregistering_leaves_every_command_hook_of_theirs_alone()
    {
        var settings = Operators();

        HookRegistration.Unregister(settings, Url);

        AssertTheirCommandHooksSurvive(settings);
    }

    /// <summary>
    /// …and so does a registration that is never removed, which is what a crash leaves behind.
    /// </summary>
    /// <remarks>
    /// The add-then-crash path. Nothing runs on the way down, so the next start's registration
    /// meets a file that already holds ours — the same shape as starting twice, reached a
    /// different way, and the operator's hooks must come through both.
    /// </remarks>
    [Fact]
    public void Registering_over_a_registration_that_was_never_removed_leaves_theirs_alone()
    {
        var settings = Operators();

        HookRegistration.Register(settings, Url, tokenVariable: null);
        HookRegistration.Register(settings, Url, tokenVariable: null);

        AssertTheirCommandHooksSurvive(settings);
    }

    /// <summary>Keys this build has never heard of come through untouched.</summary>
    /// <remarks>
    /// A merge that rebuilt the file from what it understands would pass every hook assertion and
    /// silently drop the operator's model, permissions and environment.
    /// </remarks>
    [Fact]
    public void Registering_carries_unrelated_settings_through_unchanged()
    {
        var settings = Operators();

        HookRegistration.Register(settings, Url, tokenVariable: null);

        Assert.Equal(30, settings["cleanupPeriodDays"]!.GetValue<int>());
        Assert.Equal("opus", settings["model"]!.GetValue<string>());
        Assert.Equal("stable", settings["autoUpdatesChannel"]!.GetValue<string>());
        Assert.Equal("code", settings["env"]!["VISUAL"]!.GetValue<string>());
        Assert.Equal("Bash(git status)", settings["permissions"]!["allow"]![0]!.GetValue<string>());
    }

    /// <summary>Their group's matcher is theirs, and is not rewritten.</summary>
    [Fact]
    public void Registering_does_not_touch_the_matcher_on_their_groups()
    {
        var settings = Operators();

        HookRegistration.Register(settings, Url, tokenVariable: null);

        var theirGroup = Groups(settings, "Notification")
            .Single(group => Handlers(group).Any(h => h?["command"] is not null));

        Assert.NotNull(theirGroup["matcher"]);
        Assert.Equal(string.Empty, theirGroup["matcher"]!.GetValue<string>());
    }

    // ---- Ours, and only ours -------------------------------------------------------------------

    /// <summary>Every event ingress accepts gets a handler, and no event it refuses does.</summary>
    /// <remarks>
    /// Derived from <see cref="HookEventNames.Accepted"/> on both sides rather than listed here,
    /// which is the point: a hook registered but refused would fire, post, and be answered 200
    /// with nothing done, for ever. Registration and ingestion cannot drift while this holds.
    /// </remarks>
    [Fact]
    public void Registering_covers_exactly_the_events_ingress_accepts()
    {
        var settings = HookRegistration.Parse("{}");

        HookRegistration.Register(settings, Url, tokenVariable: null);

        var registered = ((JsonObject)settings["hooks"]!).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(HookEventNames.Accepted.ToHashSet(StringComparer.Ordinal), registered);
    }

    /// <summary>
    /// A handler of ours on an event we no longer consume is removed and not re-created.
    /// </summary>
    /// <remarks>
    /// The live file carries one on <c>PermissionRequest</c>, which ingress explicitly refuses.
    /// It matches by URL, so the rule already decides it — and their <c>notify.ps1</c> on the same
    /// event must survive, which is what makes this more than a deletion.
    /// </remarks>
    [Fact]
    public void A_handler_of_ours_on_an_event_ingress_refuses_is_removed()
    {
        var settings = Operators();
        Assert.DoesNotContain("PermissionRequest", HookEventNames.Accepted);

        HookRegistration.Register(settings, Url, tokenVariable: null);

        Assert.DoesNotContain(Groups(settings, "PermissionRequest"), group => Handlers(group).Any(IsOurs));
        Assert.Contains(Groups(settings, "PermissionRequest"), group => Handlers(group).Any(h => Command(h) == Notify));
    }

    /// <summary>Registering twice adds no duplicate.</summary>
    /// <remarks>
    /// Counted per event rather than in total, because a total is satisfied by one event gaining a
    /// handler while another loses one.
    /// </remarks>
    [Fact]
    public void Registering_twice_adds_no_duplicate_handler()
    {
        var settings = Operators();

        HookRegistration.Register(settings, Url, tokenVariable: null);
        var afterOnce = OurHandlerCounts(settings);

        HookRegistration.Register(settings, Url, tokenVariable: null);
        var afterTwice = OurHandlerCounts(settings);

        Assert.Equal(afterOnce, afterTwice);
        Assert.All(afterTwice, pair => Assert.Equal(1, pair.Value));
    }

    /// <summary>An older handler at the same URL is replaced rather than left beside the new one.</summary>
    /// <remarks>
    /// The upgrade path. The live file's handlers are <c>{type, url}</c> with no token; registering
    /// with one must leave exactly one handler per event, carrying the header.
    /// </remarks>
    [Fact]
    public void Registering_replaces_a_handler_written_by_an_older_build()
    {
        var settings = Operators();

        HookRegistration.Register(settings, Url, IngressToken.EnvironmentVariable);

        Assert.All(OurHandlerCounts(settings), pair => Assert.Equal(1, pair.Value));

        var ours = Groups(settings, "SessionStart").SelectMany(Handlers).Single(IsOurs)!;
        Assert.Equal("$" + IngressToken.EnvironmentVariable, ours["headers"]![IngressToken.HeaderName]!.GetValue<string>());
        Assert.Equal(IngressToken.EnvironmentVariable, ours["allowedEnvVars"]![0]!.GetValue<string>());
    }

    /// <summary>With no token configured, no header is written at all.</summary>
    /// <remarks>
    /// Rather than a header interpolating a variable nobody set, which Claude Code sends as an
    /// empty string — indistinguishable at ingress from no header, and misleading in the file.
    /// </remarks>
    [Fact]
    public void Registering_without_a_token_writes_no_header()
    {
        var settings = HookRegistration.Parse("{}");

        HookRegistration.Register(settings, Url, tokenVariable: null);

        var ours = Groups(settings, "Stop").SelectMany(Handlers).Single(IsOurs)!;

        Assert.Null(ours["headers"]);
        Assert.Null(ours["allowedEnvVars"]);
        Assert.Null(settings[HookRegistration.EnvVarAllowListKey]);
    }

    /// <summary>The URL allowlist gains our URL, once.</summary>
    /// <remarks>
    /// Mandatory: Claude Code runs an http hook only if its URL matches an entry. The live file
    /// already contains this one, so not duplicating it is the case that actually occurs.
    /// </remarks>
    [Fact]
    public void The_url_allowlist_gains_our_url_without_duplicating_it()
    {
        var settings = Operators();

        HookRegistration.Register(settings, Url, tokenVariable: null);
        HookRegistration.Register(settings, Url, tokenVariable: null);

        var urls = ((JsonArray)settings[HookRegistration.UrlAllowListKey]!)
            .Select(node => node!.GetValue<string>())
            .ToList();

        Assert.Single(urls, entry => entry == Url);
    }

    // ---- Removing -------------------------------------------------------------------------------

    /// <summary>Removing takes out every handler of ours and leaves nothing of ours behind.</summary>
    [Fact]
    public void Unregistering_removes_every_handler_of_ours()
    {
        var settings = Operators();
        HookRegistration.Register(settings, Url, tokenVariable: null);

        var removed = HookRegistration.Unregister(settings, Url);

        Assert.Equal(HookEventNames.Accepted.Count, removed);
        Assert.Empty(AllOurHandlers(settings));
    }

    /// <summary>
    /// The allowlists stay. Deliberate, and the reason is in <see cref="HookRegistration"/>.
    /// </summary>
    [Fact]
    public void Unregistering_leaves_the_allowlists_in_place()
    {
        var settings = Operators();
        HookRegistration.Register(settings, Url, IngressToken.EnvironmentVariable);

        HookRegistration.Unregister(settings, Url);

        Assert.Contains(
            ((JsonArray)settings[HookRegistration.UrlAllowListKey]!).Select(n => n!.GetValue<string>()),
            entry => entry == Url);
        Assert.Contains(
            ((JsonArray)settings[HookRegistration.EnvVarAllowListKey]!).Select(n => n!.GetValue<string>()),
            entry => entry == IngressToken.EnvironmentVariable);
    }

    /// <summary>An array emptied by removal is deleted, and so is <c>hooks</c> when it empties.</summary>
    /// <remarks>
    /// On a file that had no hooks of its own, removing ours must leave no residue — an empty
    /// <c>hooks</c> object is not the same file the operator started with.
    /// </remarks>
    [Fact]
    public void Unregistering_deletes_containers_it_empties()
    {
        var settings = HookRegistration.Parse("""{ "model": "opus" }""");
        HookRegistration.Register(settings, Url, tokenVariable: null);

        HookRegistration.Unregister(settings, Url);

        Assert.Null(settings[HookRegistration.HooksKey]);
        Assert.Equal("opus", settings["model"]!.GetValue<string>());
    }

    /// <summary>A handler at a different URL is not ours and is never touched.</summary>
    /// <remarks>
    /// The port is part of the identity. A second dashboard on another port — another user, a
    /// development build — must not have its handlers removed by this one.
    /// </remarks>
    [Fact]
    public void A_handler_at_another_url_is_left_alone()
    {
        var settings = Operators();
        var other = (JsonObject)settings["hooks"]!;
        ((JsonArray)other["Stop"]!).Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "http", ["url"] = OtherUrl }),
        });

        HookRegistration.Unregister(settings, Url);

        Assert.Contains(
            Groups(settings, "Stop").SelectMany(Handlers),
            handler => handler?["url"]?.GetValue<string>() == OtherUrl);
    }

    /// <summary>Removing from a file that never had ours changes nothing and says so.</summary>
    [Fact]
    public void Unregistering_what_was_never_there_removes_nothing()
    {
        var settings = HookRegistration.Parse("""{ "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "x" } ] } ] } }""");
        var before = HookRegistration.Render(settings);

        var removed = HookRegistration.Unregister(settings, Url);

        Assert.Equal(0, removed);
        Assert.Equal(before, HookRegistration.Render(settings));
    }

    // ---- Parsing --------------------------------------------------------------------------------

    [Fact]
    public void An_empty_file_parses_as_empty_settings() =>
        Assert.Empty(HookRegistration.Parse("   "));

    /// <summary>Comments and trailing commas are tolerated, because a person edits this file.</summary>
    [Fact]
    public void A_hand_edited_file_with_comments_still_parses()
    {
        var settings = HookRegistration.Parse("""
            {
              // the model I like
              "model": "opus",
            }
            """);

        Assert.Equal("opus", settings["model"]!.GetValue<string>());
    }

    /// <summary>
    /// Malformed JSON throws rather than falling back to defaults.
    /// </summary>
    /// <remarks>
    /// <strong>The opposite of <c>SettingsStore</c>, deliberately.</strong> For the dashboard's own
    /// file, defaults on a parse failure are right: the alternative is a dashboard that will not
    /// start. Here the file is the operator's and holds their hooks, and "could not parse, use
    /// defaults" would mean writing back a file with everything of theirs gone. The caller must
    /// abandon the write instead.
    /// </remarks>
    [Fact]
    public void A_malformed_file_throws_rather_than_defaulting() =>
        Assert.ThrowsAny<JsonException>(() => HookRegistration.Parse("{ \"model\": }"));

    [Fact]
    public void A_file_that_is_not_an_object_throws() =>
        Assert.Throws<JsonException>(() => HookRegistration.Parse("[1, 2, 3]"));

    // ---- Helpers --------------------------------------------------------------------------------

    private static void AssertTheirCommandHooksSurvive(JsonObject settings)
    {
        Assert.Contains(Groups(settings, "Notification").SelectMany(Handlers), h => Command(h) == Notify);
        Assert.Contains(Groups(settings, "PermissionRequest").SelectMany(Handlers), h => Command(h) == Notify);
        Assert.Contains(Groups(settings, "UserPromptSubmit").SelectMany(Handlers), h => Command(h) == Start);
        Assert.Contains(Groups(settings, "Stop").SelectMany(Handlers), h => Command(h) == Stop);
    }

    private static IEnumerable<JsonObject> Groups(JsonObject settings, string eventName) =>
        settings[HookRegistration.HooksKey] is JsonObject hooks && hooks[eventName] is JsonArray groups
            ? groups.OfType<JsonObject>()
            : [];

    private static IEnumerable<JsonNode?> Handlers(JsonObject group) =>
        group["hooks"] is JsonArray handlers ? handlers : [];

    private static string? Command(JsonNode? handler) => handler?["command"]?.GetValue<string>();

    private static bool IsOurs(JsonNode? handler) =>
        handler?["type"]?.GetValue<string>() == "http"
        && handler["url"]?.GetValue<string>() == Url;

    private static IEnumerable<JsonNode?> AllOurHandlers(JsonObject settings) =>
        settings[HookRegistration.HooksKey] is JsonObject hooks
            ? hooks.SelectMany(pair => (pair.Value as JsonArray ?? []).OfType<JsonObject>())
                .SelectMany(Handlers)
                .Where(IsOurs)
            : [];

    private static Dictionary<string, int> OurHandlerCounts(JsonObject settings) =>
        ((JsonObject)settings[HookRegistration.HooksKey]!)
            .ToDictionary(
                pair => pair.Key,
                pair => (pair.Value as JsonArray ?? []).OfType<JsonObject>().SelectMany(Handlers).Count(IsOurs),
                StringComparer.Ordinal)
            .Where(pair => pair.Value > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}
