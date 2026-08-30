using System.Text.Json.Serialization;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.App.Configuration;

/// <summary>
/// The <c>rosters</c> section of <c>settings.json</c>: each roster's name, and the session names
/// in it (issue #16).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A section of the existing file, not a store of its own.</strong> <c>SettingsStore.Save</c>
/// is not atomic — that is issue #7 and it predates this — but a second, better-behaved store for
/// one feature would leave every other setting on the worse one and give the product two write
/// paths to reason about.
/// </para>
/// <para>
/// <strong>An object rather than a list of records</strong>, so that a roster name being unique is
/// a property of the shape rather than something to enforce. A hand-edited file with the same key
/// twice resolves last-wins in <c>System.Text.Json</c>, deterministically.
/// </para>
/// <para>
/// <strong>This type is the file, and the file is not the store.</strong> It carries whatever a
/// human typed, including a name in two rosters and a roster with no members, neither of which
/// <see cref="RosterBook"/> can represent. <see cref="ToBook"/> is where that becomes valid.
/// </para>
/// </remarks>
public sealed record RosterSettings
{
    /// <summary>Roster name to the session names in it. Empty when the operator has none.</summary>
    [JsonPropertyName("rosters")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Rosters { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>
    /// The valid book this section describes, and what had to be corrected to get there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every invariant is <see cref="RosterBook.From"/>'s, so a file and the operator UI reach the
    /// store through one gate rather than two. This method only decides the <em>order</em> the
    /// rosters are offered in, because rule 4 keeps a repeated name in the first roster that claims
    /// it and "first" has to mean something stable.
    /// </para>
    /// <para>
    /// <strong>Nothing is written back.</strong> A read that triggers a write is a new write path,
    /// and the one it would use is the non-atomic one. The corrected shape reaches the file the
    /// next time the operator edits a roster.
    /// </para>
    /// </remarks>
    public (RosterBook Book, IReadOnlyList<string> Corrections) ToBook()
    {
        var ordered = Rosters
            .Select(entry => (entry.Key, Members: (IEnumerable<string>)(entry.Value ?? [])))
            .ToList();

        var book = RosterBook.From(ordered);
        var corrections = new List<string>();

        foreach (var (name, members) in ordered)
        {
            var trimmed = (name ?? string.Empty).Trim();
            var kept = book.Rosters.FirstOrDefault(r => string.Equals(r.Name, trimmed, StringComparison.Ordinal));

            if (kept is null)
            {
                // Dropped entirely: a blank name, or every one of its members already claimed.
                corrections.Add(
                    trimmed.Length == 0
                        ? "A roster with a blank name was ignored."
                        : $"Roster \"{trimmed}\" was ignored because it had no members of its own.");

                continue;
            }

            var offered = members.Count(member => !string.IsNullOrWhiteSpace(member));
            if (kept.Members.Length < offered)
            {
                // The member NAMES are deliberately absent: a name here is a session title, and a
                // title can be a model-written summary of the operator's prompt (T1.24).
                corrections.Add(
                    $"Roster \"{kept.Name}\" listed {offered} names and kept {kept.Members.Length}; " +
                    "the rest were duplicates or already belonged to another roster.");
            }
        }

        return (book, corrections);
    }
}

/// <summary>
/// Holds the rosters the application is running with, and hands them to whoever asks.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One writer, two readers, and no lock — because a book is immutable.</strong> The
/// dispatcher reads it when the window regroups and the event consumer reads it when it decides a
/// session's effective group for sound; replacing the reference is a single write that either
/// reader sees whole. This is the same shape the Registry uses for sessions.
/// </para>
/// <para>
/// <strong>REPLACING THE BOOK ANNOUNCES ITSELF, AND THAT IS THIS TYPE'S JOB RATHER THAN ITS
/// CALLER'S.</strong> The two readers do not read at the same moment: the dispatcher regroups on
/// the next refresh, but the consumer only re-resolves after a drain or a tick, and the tick is
/// fifteen seconds. So an edit that did not wake the consumer would leave a dissolved group able to
/// nudge and a new group unable to settle, for up to fifteen seconds, with the screen already
/// right. Publishing from here rather than from the caller is the same principle that put issue
/// #16's rules 4 and 6 in <see cref="RosterBook"/>: a caller that has to remember to announce its
/// change is a caller that will forget, and T1.26 is not the last one.
/// </para>
/// <para>
/// A separate type rather than a field on the settings, because T1.26 replaces the book at runtime
/// when the operator edits a roster and the settings record is immutable.
/// </para>
/// </remarks>
/// <param name="sink">Where a change is announced.</param>
/// <param name="initial">
/// The rosters to start with, or null for none.
/// <para>
/// Supplied at construction rather than through <see cref="Replace"/> so that <c>Replace</c> stays
/// the only mutator and can announce unconditionally. Loading the file is not a change to a running
/// system — nothing is observing yet — and announcing it would put an event at the head of the
/// pipeline before the consumer has started, which is a thing other tests reason about.
/// </para>
/// </param>
public sealed class RosterStore(IEventSink sink, RosterBook? initial = null)
{
    private readonly IEventSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    private volatile RosterBook _book = initial ?? RosterBook.Empty;

    /// <summary>The rosters in force. Never null.</summary>
    public RosterBook Book => _book;

    /// <summary>
    /// Replaces the rosters in force and tells the pipeline that membership may have moved.
    /// </summary>
    /// <remarks>
    /// The announcement is not optional and not the caller's to make — see this type's remarks. A
    /// refused publish is a dropped wake-up, not a dropped edit: the book has already changed, the
    /// screen is already right, and the consumer catches up on its next tick.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="book"/> is null.</exception>
    public void Replace(RosterBook book)
    {
        ArgumentNullException.ThrowIfNull(book);

        _book = book;
        _sink.TryPublish(new RostersChanged { SessionId = default, Timestamp = default, Cwd = string.Empty });
    }
}
