using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// An <see cref="IRosterPersistence"/> that records what was remembered instead of writing a file.
/// </summary>
/// <remarks>
/// The real one writes <c>settings.json</c>. Tests must not, so this stands in — and because it
/// records rather than swallowing, "the operator accepted" and "the operator declined" are told
/// apart by what reached it, which is the only difference between those two paths.
/// </remarks>
internal sealed class RecordingRosterPersistence : IRosterPersistence
{
    private readonly List<RosterBook> _remembered = [];

    /// <summary>Every book that was written, in order.</summary>
    public IReadOnlyList<RosterBook> Remembered => _remembered;

    /// <summary>The rosters in the most recent write, or empty if there was none.</summary>
    public IReadOnlyList<Roster> Last =>
        _remembered.Count == 0 ? [] : _remembered[^1].Rosters;

    /// <inheritdoc/>
    public void Remember(RosterBook book) => _remembered.Add(book);
}
