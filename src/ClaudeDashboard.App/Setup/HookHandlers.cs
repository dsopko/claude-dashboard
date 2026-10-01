using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Ingress;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// The dashboard's hook handler: how it is built for the plugin, and how one that an older build
/// left in Claude Code's settings is recognised (Impl §9.2, §9.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing here writes Claude Code's settings, and nothing in the product does (the
/// operator's ruling of 2026-10-01).</strong> This type merged the handler into
/// <c>~/.claude/settings.json</c> and took it out again until then. That file belongs to Claude
/// Code, which writes it too, and a second writer rewrites the whole file: it can lose a change
/// Claude Code made at the same moment, and it reformats what it did not mean to touch. The hook
/// now reaches Claude Code as a plugin (<see cref="HookPlugin"/>), which Claude Code registers
/// itself. What stays here is the handler's shape and a read.
/// </para>
/// <para>
/// <strong>The read is for one warning.</strong> Builds before the plugin wrote the handler into
/// the settings file, and nobody takes it out for the operator. A start that finds one says so on
/// screen and does not register the plugin beside it, because the two together would post every
/// event twice (<see cref="StartupHookInstall"/>).
/// </para>
/// <para>
/// <strong>ONE COMMAND HANDLER, NOT EIGHT HTTP ONES (issue #29).</strong> An <c>http</c> handler
/// names a port, so it is only correct while something is answering that port. A command handler
/// names a script instead. The script is what discovers whether a dashboard is listening, so the
/// handler is right whether one is or not.
/// </para>
/// <para>
/// <strong>The exec form, and it is not a preference.</strong> <c>command</c> plus <c>args</c>
/// spawns the executable directly with no shell. The alternative — a single <c>command</c> string
/// — runs under the hook's <c>shell</c> field, which on Windows defaults to <c>bash</c>, or to
/// <c>powershell</c> when Git Bash is not installed. The shell therefore varies by machine and
/// cannot be chosen by us, and bash and PowerShell disagree about backslash paths and quoting.
/// </para>
/// <para>
/// <strong>Both paths are absolute and are resolved by the caller.</strong> With no shell, nothing
/// expands <c>%SystemRoot%</c> or <c>%LOCALAPPDATA%</c> in <c>command</c> or <c>args</c>. Inside
/// the <c>.cmd</c> file expansion works normally, because that <em>is</em> a shell.
/// </para>
/// <para>
/// <strong>The events are exactly the events ingress accepts.</strong> They come from
/// <see cref="HookEventNames.Accepted"/> rather than a list written out here, so what is
/// registered and what is ingested cannot drift apart.
/// </para>
/// </remarks>
public static class HookHandlers
{
    /// <summary>The key holding the per-event hook configuration, in a plugin and in the settings.</summary>
    public const string HooksKey = "hooks";

    /// <summary>The name of the script every handler runs.</summary>
    public const string ScriptFileName = "post-status.cmd";

    private const string HandlerListKey = "hooks";
    private const string TypeKey = "type";
    private const string CommandKey = "command";
    private const string ArgsKey = "args";
    private const string AsyncKey = "async";
    private const string CommandType = "command";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// The interpreter the handler names — <c>cmd.exe</c>, by absolute path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Absolute, because nothing expands a variable in the exec form</strong>, and
    /// <strong>resolved rather than written out</strong>, because a machine whose Windows
    /// directory is not <c>C:\Windows</c> is unusual and not impossible.
    /// </para>
    /// <para>
    /// A <c>.cmd</c> file is not an executable image, so <c>CreateProcess</c> cannot run one. The
    /// exec form spawns what it is given, which means the interpreter has to be named explicitly —
    /// that is the one thing <c>cmd.exe</c> is doing here, and it is not a shell in the sense
    /// §9.2 warns about: it is the program that runs <c>.cmd</c> files, chosen by us and identical
    /// on every machine.
    /// </para>
    /// </remarks>
    public static string Interpreter => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>Parses Claude Code's settings text into a tree, to be read and never written back.</summary>
    /// <remarks>
    /// <para>
    /// Comments and trailing commas are tolerated because this is a hand-editable file and people
    /// write both.
    /// </para>
    /// <para>
    /// <strong>A DUPLICATE KEY FAILS HERE, AS A <see cref="JsonException"/>, RATHER THAN LATE AND
    /// AS SOMETHING ELSE.</strong> A duplicate key — <c>"Stop"</c> twice in one object, which is
    /// legal JSON and happens when somebody merges two blocks by hand — does not fail at the parse
    /// on its own. <see cref="JsonNode"/> builds its dictionary lazily, so the throw is an
    /// <see cref="ArgumentException"/> from the first indexer that touches the offending object,
    /// arbitrarily far from any parse. Measured, not assumed. Every caller catches
    /// <see cref="JsonException"/> and none catches <see cref="ArgumentException"/>, and this read
    /// is on the startup path: one duplicate key in the operator's file would have stopped the
    /// dashboard starting. So the tree is materialised here, where the outcome is still "this file
    /// cannot be read".
    /// </para>
    /// <para>
    /// A value of the wrong <em>type</em> is a separate hazard and is handled separately: every
    /// read of a string out of this tree goes through <see cref="Text"/>, because
    /// <c>GetValue&lt;string&gt;()</c> on a node holding a number throws
    /// <see cref="InvalidOperationException"/>, which no caller catches.
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

    /// <summary>Renders a tree to indented text. For the dashboard's own plugin files.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="tree"/> is null.</exception>
    public static string Render(JsonObject tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        return tree.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// The handler on every accepted event, as the value of a <see cref="HooksKey"/> key.
    /// </summary>
    /// <param name="interpreter">The absolute path to <c>cmd.exe</c>.</param>
    /// <param name="scriptPath">The absolute path to <c>post-status.cmd</c>.</param>
    /// <exception cref="ArgumentException">Either path is null, empty, or whitespace.</exception>
    public static JsonObject ForEveryEvent(string interpreter, string scriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interpreter);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);

        var hooks = new JsonObject();

        foreach (var eventName in HookEventNames.Accepted.OrderBy(name => name, StringComparer.Ordinal))
        {
            // One group per event, holding the one handler. A group carries a matcher, and ours
            // has none: the dashboard hears every occurrence of the event.
            hooks[eventName] = new JsonArray(new JsonObject
            {
                [HandlerListKey] = new JsonArray(Handler(interpreter, scriptPath)),
            });
        }

        return hooks;
    }

    /// <summary>
    /// How many events in Claude Code's settings carry a handler that runs
    /// <paramref name="scriptPath"/> — the handler a build from before the plugin left there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Matched by the script path and by nothing else.</strong> The <em>last</em> argument
    /// of a command handler, compared as Windows compares paths. A handler that runs another data
    /// folder's script is another dashboard's, and is not counted.
    /// </para>
    /// <para>
    /// Counted by event, so a caller can say "on 6 of 8 events" where a hand edit left some.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="scriptPath"/> is null, empty, or whitespace.</exception>
    public static int CountInSettings(JsonObject settings, string scriptPath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);

        var ours = Normalize(scriptPath);

        return EventGroups(settings)
            .Count(handlers => handlers
                .Any(handler => ScriptPathOf(handler) is { } path && PathsMatch(path, ours)));
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

    /// <summary>Builds one handler, in the exec form.</summary>
    /// <remarks>
    /// <para>
    /// <c>async</c> is <see langword="true"/> so the script runs in the background and never delays
    /// a turn. <c>asyncRewake</c> is deliberately absent: it exists to act on a hook's exit code,
    /// and this hook's exit code is always zero by design.
    /// </para>
    /// <para>
    /// No <c>headers</c> and no <c>allowedEnvVars</c>. The script reads the token from
    /// <c>listening.txt</c> at every event (T1.48), so nothing about it is in the environment.
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
    /// <strong>Compared after <see cref="Path.GetFullPath(string)"/>.</strong> A hand-edited entry
    /// written with forward slashes, or with a redundant <c>.\</c>, names the same file and must
    /// count as ours — otherwise the warning is missed while looking straight at the handler.
    /// </para>
    /// <para>
    /// <strong>Ordinal-ignore-case, because Windows paths are.</strong>
    /// </para>
    /// <para>
    /// <strong>Accepted limit: an 8.3 short path does not match.</strong>
    /// <c>C:\PROGRA~1\…</c> and its long form name one file and compare unequal here. It cannot
    /// arise from our own writing — the path was built from
    /// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>, which returns the long
    /// form.
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

    /// <summary>One sequence of handlers per event, in file order.</summary>
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

    /// <summary>A node's string value, or null when it does not hold one.</summary>
    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
