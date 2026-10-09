using System.Globalization;
using System.Text.Json;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// Reads the plan's limits out of a usage post from the dashboard's mod, leniently.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing in the body can fail the post.</strong> It is what another program sends, and
/// ingress is a pure observer (Impl §3.3). A body that is not JSON, a list that is not a list and
/// an entry of the wrong shape all read as nothing.
/// </para>
/// <para>
/// <strong>Only <c>sessionId</c> and <c>rateLimits</c> are read.</strong> The mod sends the whole
/// <c>session.measure</c> event; <c>context</c>, <c>cost</c> and <c>changed</c> are not bound, so
/// nothing can store, show or log them.
/// </para>
/// <para>
/// <strong>All of it is data.</strong> A kind is kept as text and compared with nothing.
/// </para>
/// </remarks>
public static class UsageReader
{
    /// <summary>How many entries of <c>rateLimits</c> are read: the first sixteen.</summary>
    public const int MaxEntries = 16;

    /// <summary>The longest session id that is kept: 128 characters. A longer one reads as none.</summary>
    public const int MaxSessionIdLength = 128;

    /// <summary>The readings in <paramref name="body"/>, or none.</summary>
    /// <param name="body">The request body, as text.</param>
    /// <param name="heardAt">When the post arrived.</param>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is null.</exception>
    public static IReadOnlyList<UsageWindow> Read(string body, DateTimeOffset heardAt)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("rateLimits", out var limits)
                || limits.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var sessionId = root.TryGetProperty("sessionId", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 and <= MaxSessionIdLength } text
                    ? text
                    : null;

            var windows = new List<UsageWindow>();

            foreach (var entry in limits.EnumerateArray().Take(MaxEntries))
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("kind", out var kind)
                    && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() is { } name
                    && entry.TryGetProperty("percentUsed", out var percent)
                    && percent.ValueKind == JsonValueKind.Number
                    && percent.TryGetDouble(out var used))
                {
                    windows.Add(new UsageWindow(name, used, ResetOf(entry), heardAt, sessionId));
                }
            }

            return windows;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The entry's <c>resetsAt</c> as an instant, or null when it is absent or not a time.</summary>
    private static DateTimeOffset? ResetOf(JsonElement entry) =>
        entry.TryGetProperty("resetsAt", out var value)
        && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var at)
            ? at.ToUniversalTime()
            : null;
}
