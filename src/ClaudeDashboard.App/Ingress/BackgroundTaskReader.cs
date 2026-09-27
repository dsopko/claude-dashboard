using System.Text.Json;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// Reads a <c>Stop</c>'s <c>background_tasks</c> into what the Registry needs, and nothing more
/// (T1.41, issue #52).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What an entry looks like</strong>, measured on the operator's archive (2026-09-27):
/// <c>{ id, type, status, description }</c>, plus <c>command</c> on a <c>shell</c> and
/// <c>agent_type</c> on a <c>subagent</c>. Every listed entry had status <c>running</c>; finished
/// tasks drop off the list. See the hook reference.
/// </para>
/// <para>
/// <strong>The allow-list decides, and the type field alone.</strong> A running <c>shell</c> or
/// <c>subagent</c> counts. A <c>monitor</c> is known and does not count. Any other type, or an
/// entry with no type, is unrecognised: it does not count, and it is counted so the decisions
/// record can say one was seen. The description is carried as data and never decides anything.
/// </para>
/// <para>
/// <strong>The command is never read.</strong> It can carry prompts or secrets (T1.24), so this
/// reader does not touch the property at all, and nothing downstream can store, show or log what
/// was never taken.
/// </para>
/// <para>
/// <strong>Degrade, never crash.</strong> Anything that is not an array of objects reads as no
/// tasks, which is the Stop's meaning before T1.41. An entry missing its id or its status is
/// skipped. Nothing here throws on a shape.
/// </para>
/// </remarks>
public static class BackgroundTaskReader
{
    private const string Running = "running";

    /// <summary>The running tasks of an allowed kind, and how many running tasks had an unrecognised type.</summary>
    public static (IReadOnlyList<BackgroundTask> Allowed, int Unrecognised) Read(JsonElement? list)
    {
        if (list is not { ValueKind: JsonValueKind.Array } tasks)
        {
            return ([], 0);
        }

        var allowed = new List<BackgroundTask>();
        var unrecognised = 0;

        foreach (var entry in tasks.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || Text(entry, "id") is not { Length: > 0 } id
                || !string.Equals(Text(entry, "status"), Running, StringComparison.Ordinal))
            {
                continue;
            }

            switch (Text(entry, "type"))
            {
                case "shell":
                    allowed.Add(new BackgroundTask(id, BackgroundTaskKind.Shell, Text(entry, "description") ?? string.Empty));
                    break;

                case "subagent":
                    allowed.Add(new BackgroundTask(id, BackgroundTaskKind.Subagent, Text(entry, "description") ?? string.Empty));
                    break;

                case "monitor":
                    // Known, and deliberately not waited on: a long-lived watcher that may never
                    // fire (the operator's ruling on issue #52).
                    break;

                default:
                    unrecognised++;
                    break;
            }
        }

        return (allowed, unrecognised);
    }

    /// <summary>A string property's value, or null when it is absent or not a string.</summary>
    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
