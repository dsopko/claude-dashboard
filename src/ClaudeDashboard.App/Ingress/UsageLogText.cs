using System.Globalization;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// The words of the log file's lines about a usage post (MOD.8, issue #143, ruling R15), built from what the board
/// answered, never from the raw post.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A kind is named only when the readings could hold it</strong>: at most
/// <see cref="UsageReadings.MaxKindLength"/> characters, and shown escaped (<see cref="EventValues.Shown"/>), because
/// 64 characters can still hold a line break. A reading with no kind, or with a longer one, is "a reading". So a
/// post of a 10,024-character kind with a forged second line makes no long or broken line (the MOD.4 review).
/// </para>
/// <para>
/// <strong>Bounded for any post.</strong> The reader takes at most <see cref="UsageReader.MaxEntries"/> readings, and
/// a post before it carried at most as many kinds. Each name is at most 201 characters once shown, and each reading
/// adds at most about 100 more. So the Debug line is under 6,000 characters and the Information line under 16,000;
/// a real post makes lines of about 200.
/// </para>
/// <para>
/// <strong>No token.</strong> Nothing here reads a header; the post's text holds no token.
/// </para>
/// </remarks>
public static class UsageLogText
{
    /// <summary>The session that sent <paramref name="readings"/>, shown escaped, or words that say it sent none.</summary>
    public static string SessionOf(IReadOnlyList<UsageWindow> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        return readings.Select(reading => reading.SessionId).FirstOrDefault(id => id is not null) is { } id
            ? EventValues.Shown(id)
            : "a session that sent no id";
    }

    /// <summary>The kinds <paramref name="post"/> carried, "five_hour, seven_day", or "no reading".</summary>
    public static string KindsOf(UsagePost post)
    {
        ArgumentNullException.ThrowIfNull(post);

        return post.Kinds.Count == 0 ? "no reading" : Names(post.Kinds);
    }

    /// <summary>
    /// What <paramref name="post"/> changed in the picture, as sentences, or null when it only moved a percentage: the
    /// first post since the start, a kind that appeared or went missing against the post before it, and each reading
    /// not kept with its reason.
    /// </summary>
    public static string? ChangesOf(UsagePost post)
    {
        ArgumentNullException.ThrowIfNull(post);

        var sentences = new List<string>();

        if (post.PreviousKinds is not { } previous)
        {
            sentences.Add("It is the first since the dashboard started.");
        }
        else
        {
            var missing = previous.Except(post.Kinds, StringComparer.Ordinal).ToList();
            var appeared = post.Kinds.Except(previous, StringComparer.Ordinal).ToList();

            if (missing.Count > 0)
            {
                sentences.Add($"The previous post also carried {Names(missing)}.");
            }

            if (appeared.Count > 0)
            {
                sentences.Add($"The previous post did not carry {Names(appeared)}.");
            }
        }

        foreach (var outcome in post.Outcomes)
        {
            if (outcome.Refusal is { } refusal)
            {
                sentences.Add($"Not kept: {ReadingOf(outcome)}, {UsageRefusals.Clause(refusal)}.");
            }
        }

        return sentences.Count == 0 ? null : " " + string.Join(" ", sentences);
    }

    /// <summary>
    /// Each reading of <paramref name="post"/> in full, for the Debug line: "five_hour 24% with reset
    /// 2026-10-08T23:10:00Z, kept; …", or "(none)".
    /// </summary>
    public static string ReadingsOf(UsagePost post)
    {
        ArgumentNullException.ThrowIfNull(post);

        return post.Outcomes.Count == 0
            ? EventValues.None
            : string.Join(
                "; ",
                post.Outcomes.Select(outcome => outcome.Refusal is { } refusal
                    ? $"{ReadingOf(outcome)}, not kept, {UsageRefusals.Clause(refusal)}"
                    : $"{ReadingOf(outcome)}, kept"));
    }

    /// <summary>"five_hour 2% with reset 2026-10-09T10:20:00Z": the kind only when it may be named.</summary>
    private static string ReadingOf(UsageOutcome outcome)
    {
        var reading = outcome.Reading;
        var name = UsageRefusals.NamesItsKind(outcome.Refusal) ? EventValues.Shown(reading.Kind) : "a reading";
        var percent = reading.PercentUsed.ToString(CultureInfo.InvariantCulture);
        var reset = reading.ResetsAt is { } at
            ? "with reset " + at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : "with no reset time";

        return $"{name} {percent}% {reset}";
    }

    private static string Names(IEnumerable<string> kinds) => string.Join(", ", kinds.Select(EventValues.Shown));
}
