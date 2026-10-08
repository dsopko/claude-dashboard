using System.Globalization;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The one cut for text a row shows: by grapheme clusters, with a ceiling in characters (T1.81, issue #21).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Clusters, not characters.</strong> A position in a .NET string is a UTF-16 code unit. Cutting at a
/// position can split a surrogate pair (an emoji), a ZWJ family sequence, a flag's two regional indicators, or a
/// letter from its combining accent; a split pair leaves a lone surrogate, which does not round-trip through UTF-8
/// and draws as the replacement glyph. A cut between clusters keeps each of those whole.
/// </para>
/// <para>
/// <strong>Two bounds, because a cluster count does not bound length.</strong> One cluster can hold any number of
/// combining marks, so a cluster budget alone can hand the row a very long string to lay out. The character ceiling
/// bounds that, and it also lands on a cluster boundary, so it cannot bring back the split glyph.
/// </para>
/// <para>
/// The title (<see cref="SessionViewModel.TitleClusters"/>, <see cref="SessionViewModel.TitleCharacterCeiling"/>)
/// and the prompt (<see cref="SessionViewModel.SnippetLength"/>, <see cref="SessionViewModel.SnippetCharacterCeiling"/>)
/// share this method, so the two cannot drift apart again: until T1.81 the prompt cut at a code unit.
/// </para>
/// </remarks>
internal static class ClusterText
{
    /// <summary>
    /// Cuts <paramref name="text"/> to <paramref name="maxClusters"/> clusters and <paramref name="maxCharacters"/>
    /// characters, whichever bites first, and adds an ellipsis when anything was cut.
    /// </summary>
    /// <param name="text">The text, whole.</param>
    /// <param name="maxClusters">The most grapheme clusters shown.</param>
    /// <param name="maxCharacters">The most UTF-16 characters shown, before the ellipsis.</param>
    /// <returns>The text as shown, and whether anything was cut.</returns>
    public static (string Shown, bool Truncated) Shorten(string text, int maxClusters, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);

        var elements = StringInfo.GetTextElementEnumerator(text);
        var clusters = 0;
        var taken = 0;

        while (elements.MoveNext())
        {
            var element = (string)elements.Current;

            if (clusters == maxClusters || taken + element.Length > maxCharacters)
            {
                return (text[..taken] + "…", true);
            }

            clusters++;
            taken += element.Length;
        }

        return (text, false);
    }
}
