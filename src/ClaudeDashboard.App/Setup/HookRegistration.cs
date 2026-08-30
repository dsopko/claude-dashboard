using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Ingress;

namespace ClaudeDashboard.App.Setup;

/// <summary>What one removal took out of the settings file, by name.</summary>
/// <remarks>
/// <strong>By name rather than by count, and that is a requirement rather than a nicety.</strong>
/// Both removal rules match on a <em>shape</em> — a script path, or a loopback URL ending
/// <c>/hook</c> — so an entry the operator wrote themselves can match. Printing exactly what left
/// their file is the whole safeguard against that, and a number cannot do it.
/// </remarks>
public sealed record HookRemoval(
    IReadOnlyList<string> ScriptPaths,
    IReadOnlyList<string> Urls,
    IReadOnlyList<string> AllowListUrls)
{
    /// <summary>Nothing of ours was there.</summary>
    public static HookRemoval None { get; } = new([], [], []);

    /// <summary>How many entries were taken out altogether.</summary>
    public int Total => ScriptPaths.Count + Urls.Count + AllowListUrls.Count;
}

/// <summary>
/// Merges the dashboard's hook handler into Claude Code's settings, and takes it out again
/// (Impl §9.2, §9.3; issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Parse, merge, write back — never replace the file.</strong> Everything not ours is
/// carried through untouched: keys this build has never heard of, the operator's own hooks, the
/// <c>matcher</c> on their groups, and the order of everything. The file belongs to the operator
/// and holds settings they spent time on.
/// </para>
/// <para>
/// <strong>ONE COMMAND HANDLER, NOT EIGHT HTTP ONES (issue #29).</strong> An <c>http</c> handler
/// names a port, so it is only correct while something is answering that port — which is why the
/// handlers used to be added at start and removed at quit, and why a dashboard that was killed
/// left Claude Code printing an error on every turn in every session. A command handler names a
/// script instead. The script is what discovers whether a dashboard is listening, so the handler
/// is right whether one is or not, and it can be installed once and left alone.
/// </para>
/// <para>
/// <strong>The exec form, and it is not a preference.</strong> <c>command</c> plus <c>args</c>
/// spawns the executable directly with no shell. The alternative — a single <c>command</c> string
/// — runs under the hook's <c>shell</c> field, which on Windows defaults to <c>bash</c>, or to
/// <c>powershell</c> when Git Bash is not installed. The shell therefore varies by machine and
/// cannot be chosen by us, and bash and PowerShell disagree about backslash paths and quoting. The
/// same settings block would behave differently on two operators' machines.
/// </para>
/// <para>
/// <strong>Both paths are absolute and are resolved by the caller.</strong> With no shell, nothing
/// expands <c>%SystemRoot%</c> or <c>%LOCALAPPDATA%</c> in <c>command</c> or <c>args</c>. Inside
/// the <c>.cmd</c> file expansion works normally, because that <em>is</em> a shell.
/// </para>
/// <para>
/// <strong>Ours is identified by the script path and by nothing else.</strong> Not by a marker
/// key: the settings schema is not this project's to extend, and a key a future version rejects
/// would leave handlers that can never be removed. The identity used to be the URL, which moved
/// with the port; the script path does not move, which is the second thing issue #29 buys.
/// </para>
/// <para>
/// <strong>Registration is add-after-remove, which is what makes it idempotent.</strong>
/// Installing twice cannot produce two handlers, and a handler written by an older build — a
/// different shape, a missing field — is replaced rather than sat beside, because it matches by
/// path.
/// </para>
/// <para>
/// <strong>The events registered are exactly the events ingress accepts.</strong> They come from
/// <see cref="HookEventNames.Accepted"/> rather than a list written out here, so registration and
/// ingestion cannot drift apart: a hook we register but refuse would fire, post, and be answered
/// <c>200</c> with nothing done, on every occurrence, for ever. The event <em>spellings</em> are
/// literals over in <see cref="HookEventNames"/>, which is right — Claude Code owns those, not
/// this code.
/// </para>
/// <para>
/// <strong>Nothing here writes an allowlist any more.</strong> A command hook inherits the whole
/// environment, so the token needs neither <c>allowedEnvVars</c> nor <c>httpHookAllowedEnvVars</c>,
/// and <c>allowedHttpHookUrls</c> does not apply to a command hook at all. The keys survive as
/// constants because <see cref="RemoveLegacyHttp"/> still has to find them.
/// </para>
/// </remarks>
public static class HookRegistration
{
    /// <summary>The settings key holding the per-event hook configuration.</summary>
    public const string HooksKey = "hooks";

    /// <summary>The allowlist of URLs Claude Code will post to. Read only to clean it out.</summary>
    public const string UrlAllowListKey = "allowedHttpHookUrls";

    /// <summary>The global allowlist of environment variables that may be interpolated.</summary>
    /// <remarks>
    /// <strong>Deliberately never written and never removed.</strong> A command hook does not need
    /// it, so nothing puts it there; and an entry naming <c>CLAUDE_DASHBOARD_TOKEN</c> may be
    /// serving something of the operator's, so nothing takes it away either.
    /// </remarks>
    public const string EnvVarAllowListKey = "httpHookAllowedEnvVars";

    /// <summary>The name of the script, used to recognise a handler that is nearly ours.</summary>
    /// <remarks>
    /// Diagnosis only — see <see cref="ForeignScriptPaths"/>. Nothing is ever removed on the
    /// strength of a file name.
    /// </remarks>
    public const string ScriptFileName = "post-status.cmd";

    private const string HandlerListKey = "hooks";
    private const string TypeKey = "type";
    private const string UrlKey = "url";
    private const string CommandKey = "command";
    private const string ArgsKey = "args";
    private const string AsyncKey = "async";
    private const string HttpType = "http";
    private const string CommandType = "command";

    /// <summary>What a loopback hook URL starts with, whichever port it names.</summary>
    private const string LoopbackHookPrefix = "http://127.0.0.1:";

    /// <summary>…and what it ends with.</summary>
    private const string LoopbackHookSuffix = "/hook";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Parses settings text into a mutable tree.</summary>
    /// <remarks>
    /// <para>
    /// Comments and trailing commas are tolerated because this is a hand-editable file and people
    /// write both. Note that they are <em>not</em> preserved on the way out: a merge rewrites the
    /// file, and <see cref="JsonNode"/> carries no comment. That is a real cost of touching the
    /// file at all, and it is why the backup exists.
    /// </para>
    /// <para>
    /// <strong>A DUPLICATE KEY FAILS HERE, AS A <see cref="JsonException"/>, RATHER THAN LATE AND
    /// AS SOMETHING ELSE.</strong> A duplicate key — <c>"Stop"</c> twice in one object, which is
    /// legal JSON and happens when somebody merges two blocks by hand — does not fail here on its
    /// own.
    /// <see cref="JsonNode"/> builds its dictionary lazily, so the throw is an
    /// <see cref="ArgumentException"/> from the first indexer that touches the offending object,
    /// arbitrarily far from any parse. Measured, not assumed.
    /// </para>
    /// <para>
    /// That mattered enough to fix here rather than at the call sites. Every caller already catches
    /// <see cref="JsonException"/> and none of them catches <see cref="ArgumentException"/>, so the
    /// throw escaped — and since issue #29 put a settings read on the startup path, one duplicate
    /// key in the operator's file would have stopped the dashboard starting. It would have
    /// presented as the application being gone.
    /// </para>
    /// <para>
    /// So the tree is materialised here, where the outcome is still "this file cannot be read" and
    /// the file is still untouched. <strong>Refusing is right and repairing would not be:</strong>
    /// keeping the last of two duplicates would write the operator's file back with one of their
    /// keys silently dropped, which is the same objection that makes a malformed file throw rather
    /// than default.
    /// </para>
    /// <para>
    /// <strong>What this does NOT do is close the whole class, and an earlier version of this
    /// remark claimed that it did.</strong> It handles duplicate keys. A value of the wrong
    /// <em>type</em> is a separate hazard and is handled separately: every read of a string out of
    /// this tree goes through <see cref="Text"/>, because <c>GetValue&lt;string&gt;()</c> on a node
    /// holding a number throws <see cref="InvalidOperationException"/> — which no caller catches,
    /// and which the start check would turn into a dashboard that will not start. Two reads of
    /// <c>"type"</c> did not go through <see cref="Text"/> when that claim was first written.
    /// </para>
    /// </remarks>
    /// <exception cref="JsonException">The text is not valid JSON, or cannot be made into settings.</exception>
    public static JsonObject Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (string.IsNullOrWhiteSpace(json))
        {
            // An empty file is a settings file with nothing in it, not a broken one.
            return [];
        }

        var settings = JsonNode.Parse(json, documentOptions: ReadOptions) as JsonObject
            ?? throw new JsonException("Claude Code's settings file did not contain a JSON object.");

        try
        {
            Materialize(settings);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException(
                $"Claude Code's settings file cannot be read as settings: {ex.Message}", ex);
        }

        return settings;
    }

    /// <summary>Touches every object in the tree, so a lazy failure happens now rather than later.</summary>
    /// <remarks>
    /// Enumerating a <see cref="JsonObject"/> is what builds its dictionary, and the dictionary is
    /// what rejects a duplicate key. Arrays are walked because the duplicate may be inside one — a
    /// hook group is an object inside an array inside an object.
    /// </remarks>
    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var pair in o)
                {
                    Materialize(pair.Value);
                }

                break;

            case JsonArray a:
                foreach (var item in a)
                {
                    Materialize(item);
                }

                break;
        }
    }

    /// <summary>Renders a settings tree back to text.</summary>
    public static string Render(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// Adds the dashboard's command handler to every accepted event, replacing any already there.
    /// </summary>
    /// <param name="settings">The parsed settings; modified in place.</param>
    /// <param name="interpreter">The absolute path to <c>cmd.exe</c>.</param>
    /// <param name="scriptPath">The absolute path to <c>post-status.cmd</c>.</param>
    /// <returns>How many events now carry the handler.</returns>
    /// <exception cref="ArgumentException">Either path is null, empty, or whitespace.</exception>
    public static int Register(JsonObject settings, string interpreter, string scriptPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(interpreter);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);

        // Remove first. This is the whole of what makes installing twice a no-op, and it also
        // upgrades a handler written by an older build rather than sitting beside it.
        Unregister(settings, scriptPath);

        var hooks = settings[HooksKey] as JsonObject;

        if (hooks is null)
        {
            hooks = [];
            settings[HooksKey] = hooks;
        }

        var registered = 0;

        foreach (var eventName in HookEventNames.Accepted.OrderBy(name => name, StringComparer.Ordinal))
        {
            // THE ONE PLACE "CARRIED THROUGH UNTOUCHED" DOES NOT HOLD, NAMED RATHER THAN LEFT TO BE
            // FOUND. An event whose value is not an array — a string, an object, a number — is
            // replaced outright rather than merged into. Claude Code's schema has no such shape, so
            // nothing it would run is lost; what is lost is whatever a hand-edit left there, and
            // the backup is the only place it survives. Merging into it is not possible: there is
            // no defined position for a handler inside a value that is not a list of groups.
            if (hooks[eventName] is not JsonArray groups)
            {
                groups = [];
                hooks[eventName] = groups;
            }

            // Our own group, never joined onto one of theirs: a group carries a matcher, and
            // sharing one would make our handler inherit a filter we did not choose.
            groups.Add(new JsonObject
            {
                [HandlerListKey] = new JsonArray(Handler(interpreter, scriptPath)),
            });

            registered++;
        }

        return registered;
    }

    /// <summary>Removes every command handler that runs <paramref name="scriptPath"/>.</summary>
    /// <remarks>
    /// Returns the path each removed handler named, so a caller can report what left the operator's
    /// file rather than how much of it did.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="scriptPath"/> is null, empty, or whitespace.</exception>
    public static IReadOnlyList<string> Unregister(JsonObject settings, string scriptPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);

        var ours = Normalize(scriptPath);
        var removed = new List<string>();

        RemoveHandlers(
            settings,
            handler => ScriptPathOf(handler) is { } path && PathsMatch(path, ours),
            handler => removed.Add(ScriptPathOf(handler)!));

        return removed;
    }

    /// <summary>
    /// Removes the <em>legacy</em> HTTP handlers of the pre-issue-#29 design, and the
    /// <c>allowedHttpHookUrls</c> entries that went with them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>LEGACY. Nothing in the running dashboard reaches this.</strong> It exists so that
    /// <c>--remove-hooks</c> is a complete migration tool: an operator upgrading from a build that
    /// registered HTTP handlers runs <c>--remove-hooks</c> and then <c>--install-hooks</c>, and is
    /// done. Removing an <c>http</c> handler must never be automatic — the old design added and
    /// removed them at every start and quit, and a new build doing either on its own would be
    /// indistinguishable from that.
    /// </para>
    /// <para>
    /// <strong>Matched by URL shape, which is the rule the old <c>IsOurs</c> used.</strong> Any
    /// <c>http</c> handler posting to <c>http://127.0.0.1:&lt;digits&gt;/hook</c>. Every port, not
    /// only the current one: the port derivation of Impl §3.1 moves, so a machine can carry
    /// entries for several. An operator's own handler at that address would match, which is
    /// precisely why every removal is printed by name.
    /// </para>
    /// <para>
    /// <strong>The URL allowlist goes with them and the two variable allowlists do not.</strong>
    /// Impl §9.3 ruled that allowlists stay, and that ruling was about a URL still in use. After
    /// issue #29 no loopback hook URL is ours or anyone's, so an entry for one is dead. A variable
    /// name is different: <c>CLAUDE_DASHBOARD_TOKEN</c> in <c>httpHookAllowedEnvVars</c> costs
    /// nothing and may be serving something the operator set up.
    /// </para>
    /// </remarks>
    public static HookRemoval RemoveLegacyHttp(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var urls = new List<string>();

        RemoveHandlers(
            settings,
            handler => Text(handler[TypeKey]) == HttpType
                && IsLoopbackHookUrl(Text(handler[UrlKey])),
            handler => urls.Add(Text(handler[UrlKey])!));

        var allowList = new List<string>();

        if (settings[UrlAllowListKey] is JsonArray list)
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (Text(list[i]) is { } entry && IsLoopbackHookUrl(entry))
                {
                    allowList.Add(entry);
                    list.RemoveAt(i);
                }
            }

            if (list.Count == 0)
            {
                settings.Remove(UrlAllowListKey);
            }
        }

        allowList.Reverse();

        return new HookRemoval([], urls, allowList);
    }

    /// <summary>How many events carry our handler for <paramref name="scriptPath"/>.</summary>
    /// <remarks>
    /// The start check of issue #29 reads this and warns when it is not
    /// <see cref="HookEventNames.Accepted"/>'s size. Partial is worth naming separately from
    /// absent: it means somebody edited the file, rather than that nothing was ever installed.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="scriptPath"/> is null, empty, or whitespace.</exception>
    public static int CountInstalled(JsonObject settings, string scriptPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);

        var ours = Normalize(scriptPath);

        return EventGroups(settings)
            .Count(handlers => handlers
                .Any(handler => ScriptPathOf(handler) is { } path && PathsMatch(path, ours)));
    }

    /// <summary>
    /// Script paths that look like ours but are not — a hook installed under a different data
    /// folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Diagnosis only, and it is what makes the start warning explain itself.</strong>
    /// <c>CLAUDE_DASHBOARD_HOME</c> moves the data folder, so a hook installed under one root and
    /// a dashboard started under another is a real configuration and not a corruption. A warning
    /// that named only the path this process expected would leave the operator looking for a
    /// missing entry that is in fact right there, spelled differently.
    /// </para>
    /// <para>
    /// <strong>Matched on the file name — <see cref="ScriptFileName"/> — which is a weaker rule
    /// than the identity rule and is deliberately confined to this method.</strong> Nothing is ever
    /// removed on the strength of it.
    /// </para>
    /// <para>
    /// <strong>The confinement is what keeps the warning honest, and the first version of this
    /// method described it without doing it.</strong> It returned every command handler that was
    /// not ours, so an operator's own exec-form hook running <c>node.exe … lint.js</c> came back as
    /// a foreign dashboard script, and the start warning told them to check
    /// <c>CLAUDE_DASHBOARD_HOME</c> over a file that has nothing to do with this application. It
    /// also put a path out of their settings file into our log, which the check must not do.
    /// </para>
    /// <para>
    /// The remark was written before the filter and was believed instead of the code. <strong>A
    /// comment that claims more than the line under it is worse than no comment, because it stops
    /// the next reader looking.</strong>
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="scriptPath"/> is null, empty, or whitespace.</exception>
    public static IReadOnlyList<string> ForeignScriptPaths(JsonObject settings, string scriptPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);

        var ours = Normalize(scriptPath);

        return
        [
            .. EventGroups(settings)
                .SelectMany(handlers => handlers)
                .Select(ScriptPathOf)
                .Where(path => path is not null && IsTheScript(path) && !PathsMatch(path, ours))
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>Whether a path ends in the name this dashboard gives its forwarder.</summary>
    /// <remarks>
    /// <see cref="Path.GetFileName(string)"/> rather than <c>EndsWith</c>, so that
    /// <c>their-post-status.cmd</c> is not ours by accident. Never throws: the value came out of the
    /// operator's file, and this is only ever asked in order to write a log line.
    /// </remarks>
    private static bool IsTheScript(string? path)
    {
        if (path is null)
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFileName(path), ScriptFileName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Builds one handler, in the exec form.</summary>
    /// <remarks>
    /// <para>
    /// <c>async</c> is <see langword="true"/> so the script runs in the background and never delays
    /// a turn. <c>asyncRewake</c> is deliberately absent: it exists to act on a hook's exit code,
    /// and this hook's exit code is always zero by design.
    /// </para>
    /// <para>
    /// No <c>headers</c> and no <c>allowedEnvVars</c>. A command hook inherits the whole
    /// environment, so the script reads <c>CLAUDE_DASHBOARD_TOKEN</c> for itself.
    /// </para>
    /// </remarks>
    private static JsonObject Handler(string interpreter, string scriptPath) => new()
    {
        [TypeKey] = CommandType,
        [CommandKey] = interpreter,
        [ArgsKey] = new JsonArray("/c", scriptPath),
        [AsyncKey] = true,
    };

    /// <summary>The script a command handler runs, or null if it is not that shape.</summary>
    /// <remarks>
    /// The <em>last</em> argument, not any of them. <c>/c</c> is an argument too, and a rule of
    /// "any argument that matches" would let a handler be identified by a switch.
    /// </remarks>
    private static string? ScriptPathOf(JsonNode? node) =>
        node is JsonObject handler
        && Text(handler[TypeKey]) == CommandType
        && handler[ArgsKey] is JsonArray args
        && args.Count > 0
            ? Text(args[^1])
            : null;

    /// <summary>Whether two path strings name the same file.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Compared after <see cref="Path.GetFullPath(string)"/>, which is what stops the start
    /// check warning falsely.</strong> A hand-edited entry written with forward slashes, or with a
    /// redundant <c>.\</c>, names the same file and must count as ours — otherwise the dashboard
    /// warns that its hook is missing while looking straight at it, and an operator who then runs
    /// <c>--install-hooks</c> gets a second handler.
    /// </para>
    /// <para>
    /// <strong>Ordinal-ignore-case, because Windows paths are.</strong>
    /// </para>
    /// <para>
    /// <strong>Accepted limit: an 8.3 short path does not match.</strong>
    /// <c>C:\PROGRA~1\…</c> and its long form name one file and compare unequal here.
    /// <see cref="Path.GetFullPath(string)"/> does not expand short names. It cannot arise from our
    /// own writing — the path is built from
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>, which returns the long
    /// form — so this is a limit of the matcher rather than a defect in it.
    /// </para>
    /// </remarks>
    private static bool PathsMatch(string? candidate, string normalizedOurs) =>
        candidate is not null
        && string.Equals(Normalize(candidate), normalizedOurs, StringComparison.OrdinalIgnoreCase);

    /// <summary>A path in a comparable form, or the original when it cannot be made into one.</summary>
    /// <remarks>
    /// Falling back rather than throwing: the input is a string out of the operator's settings file
    /// and may be anything at all. An unusable value simply fails to match, which is the right
    /// answer, and it must not take the start check down with it.
    /// </remarks>
    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>Whether a URL is a loopback hook URL of the pre-issue-#29 design, on any port.</summary>
    /// <remarks>
    /// Parsed by hand rather than by <see cref="Uri"/>, so that the shape asserted is exactly the
    /// shape the old <c>HookUrlFor</c> produced and nothing wider. <c>localhost</c> is not accepted
    /// and never was written.
    /// </remarks>
    private static bool IsLoopbackHookUrl(string? url)
    {
        if (url is null
            || !url.StartsWith(LoopbackHookPrefix, StringComparison.OrdinalIgnoreCase)
            || !url.EndsWith(LoopbackHookSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var port = url[LoopbackHookPrefix.Length..^LoopbackHookSuffix.Length];

        return port.Length > 0 && port.All(char.IsAsciiDigit);
    }

    /// <summary>One sequence of handlers per event, in file order.</summary>
    /// <remarks>
    /// Grouped by event rather than flattened, because the start check counts <em>events</em> that
    /// carry the handler and not handlers. Flattening here would make "installed on six of eight
    /// events" indistinguishable from "installed six times on one event", which is the difference
    /// between a partial install and a duplicated one.
    /// </remarks>
    private static IEnumerable<IEnumerable<JsonNode?>> EventGroups(JsonObject settings)
    {
        if (settings[HooksKey] is not JsonObject hooks)
        {
            yield break;
        }

        foreach (var pair in hooks)
        {
            yield return HandlersIn(pair.Value);
        }
    }

    /// <summary>Every handler under one event's group array.</summary>
    private static IEnumerable<JsonNode?> HandlersIn(JsonNode? groups) =>
        (groups as JsonArray ?? [])
            .OfType<JsonObject>()
            .SelectMany(group => group[HandlerListKey] as JsonArray ?? []);

    /// <summary>
    /// Takes out every handler <paramref name="isOurs"/> accepts, and any container left empty.
    /// </summary>
    /// <remarks>
    /// One walk shared by both removal rules, so the two can differ in what they match and cannot
    /// differ in how they tidy up. A group whose handlers we emptied was ours alone; one that still
    /// holds the operator's handlers stays exactly as it is, matcher included.
    /// </remarks>
    private static void RemoveHandlers(
        JsonObject settings,
        Func<JsonObject, bool> isOurs,
        Action<JsonObject> onRemoved)
    {
        if (settings[HooksKey] is not JsonObject hooks)
        {
            return;
        }

        foreach (var eventName in hooks.Select(pair => pair.Key).ToList())
        {
            if (hooks[eventName] is not JsonArray groups)
            {
                continue;
            }

            for (var g = groups.Count - 1; g >= 0; g--)
            {
                if (groups[g] is not JsonObject group || group[HandlerListKey] is not JsonArray handlers)
                {
                    continue;
                }

                for (var h = handlers.Count - 1; h >= 0; h--)
                {
                    if (handlers[h] is JsonObject handler && isOurs(handler))
                    {
                        onRemoved(handler);
                        handlers.RemoveAt(h);
                    }
                }

                if (handlers.Count == 0)
                {
                    groups.RemoveAt(g);
                }
            }

            if (groups.Count == 0)
            {
                hooks.Remove(eventName);
            }
        }

        if (hooks.Count == 0)
        {
            settings.Remove(HooksKey);
        }
    }

    /// <summary>A node's string value, or null when it does not hold one.</summary>
    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
