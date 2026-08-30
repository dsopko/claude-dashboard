namespace ClaudeDashboard.Core;

/// <summary>What a roster group just did.</summary>
public enum RosterGroupEvent
{
    /// <summary>Every member has been quiet for the settle window: the group is done.</summary>
    Settled = 1,

    /// <summary>A member is working again, so the group is not done after all.</summary>
    Unsettled = 2,

    /// <summary>
    /// The group read done and went back to working too soon afterwards, so that reading was
    /// wrong and the settle window was too short.
    /// </summary>
    MisMarked = 3,
}

/// <summary>One thing a roster group did, for the caller to act on.</summary>
/// <param name="Group">Which group. Never a member's title — see <see cref="RosterGroupWatch"/>.</param>
/// <param name="Event">What it did.</param>
public readonly record struct RosterGroupChange(GroupKey Group, RosterGroupEvent Event);

/// <summary>
/// Watches roster groups across time: reports when one settles, when it stops being settled, and
/// when a settle turns out to have been wrong (issue #16).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the only history in the feature, and it is here because nothing else needs
/// any.</strong> What a group <em>reads</em> is a pure function of its members and the clock
/// (<see cref="RosterSettle"/>). What cannot be computed that way is "it read done a moment ago
/// and now it does not", because that is a comparison between two instants — so this type
/// remembers one previous answer per group and nothing else.
/// </para>
/// <para>
/// <strong>Identified by <see cref="GroupKey"/>, which is why a roster key is built from the
/// roster's name.</strong> Membership churns constantly — that is what a hand-off is — and a key
/// derived from members would give a group a new identity at exactly the moment this type is
/// trying to follow it across one.
/// </para>
/// <para>
/// <strong>It reports rather than logs.</strong> Core does not log, and the mis-mark is the one
/// observation here that has to reach a file. Returning it lets the host write it with the roster's
/// own name and without the members' — which matters, because a member name is a session title and
/// a title can carry the operator's words (T1.24).
/// </para>
/// <para>
/// <strong>Single-threaded, like the Registry and the sound engine.</strong> The event consumer
/// calls this on the thread that applies events; it holds no locks.
/// </para>
/// </remarks>
public sealed class RosterGroupWatch
{
    private readonly Dictionary<GroupKey, Watched> _watched = [];
    private readonly TimeSpan _window;
    private readonly TimeSpan _misMarkWindow;

    /// <summary>Builds a watch over the given windows.</summary>
    /// <param name="window">The settle window; <see cref="RosterSettle.DefaultWindow"/> when null.</param>
    /// <param name="misMarkWindow">
    /// How soon a return to working proves a settle wrong; <see cref="RosterSettle.DefaultMisMarkWindow"/>
    /// when null.
    /// </param>
    public RosterGroupWatch(TimeSpan? window = null, TimeSpan? misMarkWindow = null)
    {
        _window = window ?? RosterSettle.DefaultWindow;
        _misMarkWindow = misMarkWindow ?? RosterSettle.DefaultMisMarkWindow;
    }

    /// <summary>The settle window this watch uses.</summary>
    public TimeSpan Window => _window;

    /// <summary>How many groups this watch is currently following.</summary>
    public int Following => _watched.Count;

    /// <summary>
    /// Takes <paramref name="groups"/> as they are at <paramref name="now"/> and reports what
    /// changed since the last look.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Safe to call at any cadence: called twice with nothing changed it reports nothing, and
    /// called late it reports the change once rather than replaying what was missed.
    /// </para>
    /// <para>
    /// <strong>A group that disappears reports <see cref="RosterGroupEvent.Unsettled"/>.</strong>
    /// Its last member ended, or the roster was deleted; either way the group must stop nudging,
    /// and leaving it settled would nudge for a group that no longer exists.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="groups"/> is null.</exception>
    public IReadOnlyList<RosterGroupChange> Observe(IEnumerable<Group> groups, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var changes = new List<RosterGroupChange>();
        var seen = new HashSet<GroupKey>();

        foreach (var group in groups)
        {
            if (group is null || group.Order != SeverityOrder.RosterGroup)
            {
                continue;
            }

            seen.Add(group.Key);

            var done = RosterSettle.StateOf(group, now, _window) == SessionState.Unread;
            var known = _watched.TryGetValue(group.Key, out var watched);

            if (done)
            {
                if (!known || !watched!.Settled)
                {
                    _watched[group.Key] = new Watched { Settled = true, SettledAt = now };
                    changes.Add(new RosterGroupChange(group.Key, RosterGroupEvent.Settled));
                }

                continue;
            }

            if (!known || !watched!.Settled)
            {
                _watched[group.Key] = new Watched { Settled = false, SettledAt = watched?.SettledAt };
                continue;
            }

            // It was settled and is not any more.
            changes.Add(new RosterGroupChange(group.Key, RosterGroupEvent.Unsettled));

            if (watched.SettledAt is { } settledAt && now - settledAt < _misMarkWindow)
            {
                // The settle window was too short: this group was never really done.
                //
                // Reported on the EDGE, not while the condition holds, so a group that flaps
                // produces one of these per flap rather than a heartbeat — and it cannot produce
                // two inside the settle window, because it has to settle again first. That lower
                // bound is the whole storm guard; a second suppressor would only hide how often it
                // is really happening, which is the one thing this exists to measure.
                changes.Add(new RosterGroupChange(group.Key, RosterGroupEvent.MisMarked));
            }

            _watched[group.Key] = new Watched { Settled = false, SettledAt = watched.SettledAt };
        }

        foreach (var key in _watched.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            if (_watched[key].Settled)
            {
                changes.Add(new RosterGroupChange(key, RosterGroupEvent.Unsettled));
            }

            _watched.Remove(key);
        }

        return changes;
    }

    /// <summary>
    /// The earliest instant any of <paramref name="groups"/> is STILL due to change on its own, or
    /// null when none of them is waiting on the clock.
    /// </summary>
    /// <remarks>
    /// The host waits until this or until its ordinary tick, whichever is sooner. Null means
    /// nothing is pending and the ordinary tick is enough, which is the usual case.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="groups"/> is null.</exception>
    public DateTimeOffset? NextDeadline(IEnumerable<Group> groups, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(groups);

        DateTimeOffset? earliest = null;

        foreach (var group in groups)
        {
            if (group is not null && RosterSettle.PendingDeadlineOf(group, now, _window) is { } deadline &&
                (earliest is null || deadline < earliest))
            {
                earliest = deadline;
            }
        }

        return earliest;
    }

    private sealed class Watched
    {
        public required bool Settled { get; init; }

        /// <summary>When it last became settled, kept after it stops so the mis-mark can be timed.</summary>
        public required DateTimeOffset? SettledAt { get; init; }
    }
}
