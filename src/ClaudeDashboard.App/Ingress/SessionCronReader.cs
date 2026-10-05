using System.Text.Json;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// Reads a <c>Stop</c>'s <c>session_crons</c> into the prompts a tick is recognised by (T1.44,
/// issue #56).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What an entry looks like</strong>, measured on the operator's archive: <c>{ id,
/// schedule, prompt, recurring }</c>. Only <c>prompt</c> is taken, because a tick is recognised by
/// its prompt exactly equalling one of these. It is kept to be compared, inside
/// <see cref="ScheduledPrompts"/>.
/// </para>
/// <para>
/// <strong>Degrade, never crash.</strong> Anything that is not an array of objects reads as no
/// scheduled jobs, which makes no prompt a tick — today's behaviour. An entry with no string prompt
/// is skipped. Nothing here throws on a shape.
/// </para>
/// </remarks>
public static class SessionCronReader
{
    /// <summary>The scheduled jobs' prompts, or none.</summary>
    public static ScheduledPrompts Read(JsonElement? list)
    {
        if (list is not { ValueKind: JsonValueKind.Array } crons)
        {
            return ScheduledPrompts.None;
        }

        var prompts = new List<string>();

        foreach (var entry in crons.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("prompt", out var prompt)
                && prompt.ValueKind == JsonValueKind.String
                && prompt.GetString() is { Length: > 0 } text)
            {
                prompts.Add(text);
            }
        }

        return ScheduledPrompts.Of(prompts);
    }
}
