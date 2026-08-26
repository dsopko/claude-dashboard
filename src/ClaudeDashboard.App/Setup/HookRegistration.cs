using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeDashboard.App.Ingress;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// Merges the dashboard's hook handlers into Claude Code's settings, and takes them out again
/// (Impl §9.2, §9.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Parse, merge, write back — never replace the file.</strong> Everything not ours is
/// carried through untouched: keys this build has never heard of, the operator's own hooks, the
/// <c>matcher</c> on their groups, and the order of everything. The file belongs to the operator
/// and holds settings they spent time on.
/// </para>
/// <para>
/// <strong>Ours are identified by URL and by nothing else.</strong> An <c>http</c> handler whose
/// <c>url</c> is the dashboard's loopback hook URL is ours. Not a marker key: the settings schema
/// is not this project's to extend, and a key a future version rejects would leave handlers that
/// can never be removed. Removing one an operator happened to add with the same URL is harmless —
/// the next start puts it back.
/// </para>
/// <para>
/// <strong>Registration is add-after-remove, which is what makes it idempotent.</strong> Starting
/// twice cannot produce two handlers, and a handler left over from an older build — a different
/// shape, a missing header — is replaced rather than duplicated, because it matches by URL.
/// </para>
/// <para>
/// <strong>The events registered are exactly the events ingress accepts.</strong> They come from
/// <see cref="HookEventNames.Accepted"/> rather than a list written out here, so registration and
/// ingestion cannot drift apart: a hook we register but refuse would fire, post, and be answered
/// <c>200</c> with nothing done, on every occurrence, for ever. The event <em>spellings</em> are
/// literals over in <see cref="HookEventNames"/>, which is right — Claude Code owns those, not
/// this code.
/// </para>
/// </remarks>
public static class HookRegistration
{
    /// <summary>The settings key holding the per-event hook configuration.</summary>
    public const string HooksKey = "hooks";

    /// <summary>The allowlist of URLs Claude Code will post to. Without it, no http hook runs.</summary>
    public const string UrlAllowListKey = "allowedHttpHookUrls";

    /// <summary>The global allowlist of environment variables that may be interpolated.</summary>
    public const string EnvVarAllowListKey = "httpHookAllowedEnvVars";

    private const string HandlerListKey = "hooks";
    private const string TypeKey = "type";
    private const string UrlKey = "url";
    private const string HttpType = "http";

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Parses settings text into a mutable tree.</summary>
    /// <remarks>
    /// Comments and trailing commas are tolerated because this is a hand-editable file and people
    /// write both. Note that they are <em>not</em> preserved on the way out: a merge rewrites the
    /// file, and <see cref="JsonNode"/> carries no comment. That is a real cost of touching the
    /// file at all, and it is why the backup exists.
    /// </remarks>
    /// <exception cref="JsonException">The text is not valid JSON.</exception>
    public static JsonObject Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (string.IsNullOrWhiteSpace(json))
        {
            // An empty file is a settings file with nothing in it, not a broken one.
            return [];
        }

        return JsonNode.Parse(json, documentOptions: ReadOptions) as JsonObject
            ?? throw new JsonException("Claude Code's settings file did not contain a JSON object.");
    }

    /// <summary>Renders a settings tree back to text.</summary>
    public static string Render(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// Adds the dashboard's handlers for every accepted event, replacing any already present.
    /// </summary>
    /// <param name="settings">The parsed settings; modified in place.</param>
    /// <param name="hookUrl">The dashboard's hook URL, carrying the <strong>bound</strong> port.</param>
    /// <param name="tokenVariable">
    /// The environment variable holding the ingress token, or null to register handlers that send
    /// no token. Null is the honest choice when no token is configured: a header interpolating an
    /// unset variable is sent empty, which ingress would then have to treat as absent anyway.
    /// </param>
    public static void Register(JsonObject settings, string hookUrl, string? tokenVariable)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookUrl);

        // Remove first. This is the whole of what makes registering twice a no-op, and it also
        // upgrades a handler written by an older build rather than sitting beside it.
        Unregister(settings, hookUrl);

        var hooks = settings[HooksKey] as JsonObject;

        if (hooks is null)
        {
            hooks = [];
            settings[HooksKey] = hooks;
        }

        foreach (var eventName in HookEventNames.Accepted.OrderBy(name => name, StringComparer.Ordinal))
        {
            if (hooks[eventName] is not JsonArray groups)
            {
                groups = [];
                hooks[eventName] = groups;
            }

            // Our own group, never joined onto one of theirs: a group carries a matcher, and
            // sharing one would make our handler inherit a filter we did not choose.
            groups.Add(new JsonObject
            {
                [HandlerListKey] = new JsonArray(Handler(hookUrl, tokenVariable)),
            });
        }

        AddToAllowList(settings, UrlAllowListKey, hookUrl);

        if (tokenVariable is not null)
        {
            AddToAllowList(settings, EnvVarAllowListKey, tokenVariable);
        }
    }

    /// <summary>
    /// Removes every handler pointing at <paramref name="hookUrl"/>, and any container left empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The allowlists deliberately stay. An <c>allowedHttpHookUrls</c> entry pointing at no hook
    /// does nothing and causes no error, so leaving it halves the writes to a file every Claude
    /// Code session on the machine is reading, and removes a class of half-written state.
    /// </para>
    /// <para>
    /// Returns the number of handlers taken out, so a caller can tell "nothing of ours was there"
    /// from "we removed some" without re-reading the file.
    /// </para>
    /// </remarks>
    public static int Unregister(JsonObject settings, string hookUrl)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookUrl);

        if (settings[HooksKey] is not JsonObject hooks)
        {
            return 0;
        }

        var removed = 0;

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
                    if (IsOurs(handlers[h], hookUrl))
                    {
                        handlers.RemoveAt(h);
                        removed++;
                    }
                }

                // A group whose handlers we emptied was ours alone; one that still holds the
                // operator's handlers stays exactly as it is, matcher included.
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

        return removed;
    }

    /// <summary>Whether <paramref name="node"/> is a handler this dashboard installed.</summary>
    private static bool IsOurs(JsonNode? node, string hookUrl) =>
        node is JsonObject handler
        && handler[TypeKey]?.GetValue<string>() == HttpType
        && string.Equals(handler[UrlKey]?.GetValue<string>(), hookUrl, StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds one handler, exactly as Impl §9.2 spells it.</summary>
    private static JsonObject Handler(string hookUrl, string? tokenVariable)
    {
        var handler = new JsonObject
        {
            [TypeKey] = HttpType,
            [UrlKey] = hookUrl,
        };

        if (tokenVariable is null)
        {
            return handler;
        }

        handler["headers"] = new JsonObject
        {
            [IngressToken.HeaderName] = "$" + tokenVariable,
        };

        // Per-handler as well as global: Claude Code replaces a reference to an unlisted variable
        // with an empty string, so both allowlists are required for the token to arrive at all.
        handler["allowedEnvVars"] = new JsonArray(tokenVariable);

        return handler;
    }

    /// <summary>Adds <paramref name="value"/> to an allowlist array, without duplicating it.</summary>
    private static void AddToAllowList(JsonObject settings, string key, string value)
    {
        if (settings[key] is not JsonArray list)
        {
            list = [];
            settings[key] = list;
        }

        var present = list.Any(entry =>
            entry is JsonValue item
            && item.TryGetValue<string>(out var text)
            && string.Equals(text, value, StringComparison.OrdinalIgnoreCase));

        if (!present)
        {
            list.Add(value);
        }
    }
}
