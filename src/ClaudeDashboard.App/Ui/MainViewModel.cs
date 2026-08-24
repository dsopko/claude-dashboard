using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using ClaudeDashboard.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The dashboard body and header (Design Document §6, §7, §9; Impl §5.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It decides nothing about attention.</strong> Banding and ordering come from
/// <see cref="AttentionEngine"/>, grouping from <see cref="GroupResolver"/>, roll-up from
/// <see cref="Group"/>. This assembles what they return into a sequence of rows and keeps that
/// sequence in step with the projection; there is no comparison, no sort and no state test in
/// this file, and any that appeared would be the domain leaking into the host.
/// </para>
/// <para>
/// <strong>What it does decide is how much room each row gets.</strong> Design Document §6's
/// collapse rules are here rather than in XAML because they change which rows exist, not how a
/// row looks — and because a rule that can hide the thing the tool exists to surface must be
/// assertable without standing up a window.
/// </para>
/// <para>
/// <strong>It reads the projection, never the Registry.</strong> The projection's collection is
/// UI-thread-owned and already marshalled (Impl §4). <c>Registry.Sessions</c> is a live view
/// whose enumeration from another thread is unsafe, and the single-writer guard covers writes
/// rather than reads — so this is the one rule in the design that nothing enforces.
/// </para>
/// <para>
/// <strong>Rows are reused, never rebuilt.</strong> T1.3's ordering and T1.4's groups both
/// compare by value over their member sequences, so an unchanged projection produces a result
/// equal to the previous one. That property only reaches the screen if the binding preserves it:
/// a clear-and-refill on every notification would raise a reset for every row several times a
/// minute, discarding selection and scroll position. So a refresh reconciles in place and
/// touches only what actually moved.
/// </para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// How long a group must be entirely quiet before it collapses to one line
    /// (Design Document §6 rule 1: "N minutes (default 15)").
    /// </summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(15);

    private readonly SessionProjection _projection;
    private readonly MotionPolicy _motion;
    private readonly Dictionary<SessionId, SessionViewModel> _sessionRows = [];
    private readonly Dictionary<GroupKey, GroupViewModel> _groupHeaders = [];
    private readonly Dictionary<AttentionBand, BandHeaderViewModel> _bandHeaders = [];
    private readonly Dictionary<string, QuietFooterViewModel> _footers = [];

    private DateTimeOffset _now = DateTimeOffset.MinValue;
    private TimeSpan _staleAfter = DefaultStaleAfter;
    private bool _disposed;

    /// <summary>Grouped by default (Design Document §7).</summary>
    [ObservableProperty]
    private bool _isGrouped = true;

    [ObservableProperty]
    private int _needsYouCount;

    [ObservableProperty]
    private int _unreadCount;

    [ObservableProperty]
    private int _workingCount;

    [ObservableProperty]
    private int _quietCount;

    [ObservableProperty]
    private int _endedCount;

    /// <summary>Binds to <paramref name="projection"/>.</summary>
    /// <param name="projection">The UI-thread mirror of the Registry.</param>
    /// <param name="motion">
    /// Whether rows may animate; defaults to <see cref="MotionPolicy.System"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="projection"/> is null.</exception>
    public MainViewModel(SessionProjection projection, MotionPolicy? motion = null)
    {
        ArgumentNullException.ThrowIfNull(projection);

        _projection = projection;
        _motion = motion ?? MotionPolicy.System;
        _projection.Sessions.CollectionChanged += OnSessionsChanged;
        _motion.PropertyChanged += OnMotionChanged;

        Refresh();
    }

    /// <summary>
    /// The body: band or group headings interleaved with session rows, in display order.
    /// </summary>
    public ObservableCollection<DashboardRow> Rows { get; } = [];

    /// <summary>
    /// How long a group must be entirely quiet before it collapses (Design Document §6 rule 1).
    /// </summary>
    public TimeSpan StaleAfter
    {
        get => _staleAfter;
        set
        {
            if (_staleAfter == value)
            {
                return;
            }

            _staleAfter = value;
            OnPropertyChanged(nameof(StaleAfter));
            Refresh();
        }
    }

    /// <summary>Stops following the projection.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _projection.Sessions.CollectionChanged -= OnSessionsChanged;
        _motion.PropertyChanged -= OnMotionChanged;

        foreach (var header in _groupHeaders.Values)
        {
            header.PropertyChanged -= OnHeaderChanged;
        }

        foreach (var header in _bandHeaders.Values)
        {
            header.PropertyChanged -= OnHeaderChanged;
        }

        _disposed = true;
    }

    /// <summary>
    /// Advances the display to <paramref name="now"/>: every row's age, and every group's idle
    /// time — which is what decides whether it has gone stale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An age has to move without an event arriving — a session blocked for nine minutes must
    /// read nine, then ten — and so does staleness, which is the same clock asking a different
    /// question. Nothing here starts a timer to make that happen: the event consumer owns the
    /// only periodic loop in the process (T1.9), deliberately, and a second one is exactly what
    /// that arrangement exists to prevent. <see cref="UiTick"/> is what carries that loop's tick
    /// across to this method on the UI thread.
    /// </para>
    /// <para>
    /// Every cached row is aged, not merely the visible ones: a row hidden behind a "+ 3 quiet"
    /// footer keeps its instance, and would otherwise reappear showing the age it had when it
    /// was collapsed.
    /// </para>
    /// </remarks>
    public void Tick(DateTimeOffset now)
    {
        _now = now;

        foreach (var row in _sessionRows.Values)
        {
            row.RefreshAge(now);
        }

        Refresh();
    }

    /// <summary>Rebuilds the row sequence from the projection, reusing every row it can.</summary>
    public void Refresh()
    {
        var sessions = _projection.Sessions.ToList();
        var groups = IsGrouped ? GroupResolver.Resolve(sessions) : null;

        Reconcile(groups is null ? FlatRows(sessions) : GroupedRows(groups));
        Forget(sessions, groups?.Select(group => group.Key).ToHashSet());
        RecountBands(sessions);
    }

    /// <summary>
    /// Whether <paramref name="session"/> has been dealt with, and so may be collapsed
    /// (Design Document §6 rules 2 and 3).
    /// </summary>
    /// <remarks>
    /// <strong>The Unread exemption is this one line.</strong> "Quiet" is the Quiet and Ended
    /// bands and nothing else, so an Unread session can never be summarised away — §6 rule 3
    /// replaced the earlier "show only the first green per group" idea precisely because that
    /// rule would have hidden the thing the tool exists to surface. The bands come from Core;
    /// this asks a question of them rather than restating what they contain.
    /// </remarks>
    private static bool IsQuiet(Session session) =>
        AttentionOrder.BandOf(session.State) is AttentionBand.Quiet or AttentionBand.Ended;

    /// <summary>Grouped view: a heading per group, its members in attention order beneath it.</summary>
    private List<DashboardRow> GroupedRows(IReadOnlyList<Group> resolved)
    {
        var rows = new List<DashboardRow>();

        foreach (var group in AttentionEngine.OrderGroups(resolved))
        {
            var header = HeaderFor(group);

            header.IdleAge = _now - group.LastActivity;
            header.IsStale = group.Members.All(IsQuiet) && header.IdleAge >= StaleAfter;

            rows.Add(header);

            if (header.IsExpanded)
            {
                // Everything, quiet included: the operator asked.
                rows.AddRange(group.Members.Select(RowFor));
                continue;
            }

            if (header.IsStale)
            {
                // Rule 1: the whole group is one line. It cannot push active work down, because
                // Core sorts a group by its worst member and every member here is quiet.
                continue;
            }

            // Rule 2: the live rows in full, the dealt-with ones behind a footer.
            var quiet = 0;
            foreach (var member in group.Members)
            {
                if (IsQuiet(member))
                {
                    quiet++;
                }
                else
                {
                    rows.Add(RowFor(member));
                }
            }

            if (quiet > 0)
            {
                rows.Add(FooterFor(header, group.Key.Value, quiet, isBandSummary: false));
            }
        }

        return rows;
    }

    /// <summary>Flat view: global bands, visibly labelled (Design Document §7).</summary>
    private List<DashboardRow> FlatRows(List<Session> sessions)
    {
        var rows = new List<DashboardRow>();

        foreach (var band in AttentionEngine.Order(sessions))
        {
            var header = HeaderFor(band.Band, band.Sessions.Count);
            rows.Add(header);

            if (header.IsCollapsible && !header.IsExpanded)
            {
                // The mockups' flat view: the Quiet band is one line, never a list of grey rows.
                rows.Add(FooterFor(header, band.Band.ToString(), band.Sessions.Count, isBandSummary: true));
                continue;
            }

            rows.AddRange(band.Sessions.Select(RowFor));
        }

        return rows;
    }

    /// <summary>
    /// Brings <see cref="Rows"/> into line with <paramref name="desired"/>, changing only what
    /// differs.
    /// </summary>
    /// <remarks>
    /// Rows are reused by identity, so an unchanged projection yields the same instances in the
    /// same order and this raises nothing at all — which is the churn-free property reaching the
    /// screen rather than stopping at Core's doorstep.
    /// </remarks>
    private void Reconcile(List<DashboardRow> desired)
    {
        for (var i = 0; i < desired.Count; i++)
        {
            if (i >= Rows.Count)
            {
                Rows.Add(desired[i]);
            }
            else if (!ReferenceEquals(Rows[i], desired[i]))
            {
                Rows[i] = desired[i];
            }
        }

        while (Rows.Count > desired.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }

    /// <summary>
    /// Drops cached rows for sessions and groups that no longer exist.
    /// </summary>
    /// <remarks>
    /// Keyed on what the projection holds rather than on what is currently on screen: a row
    /// hidden behind a quiet footer is still that session's row, and forgetting it would rebuild
    /// it — losing its expansion — the moment the footer was opened.
    /// </remarks>
    private void Forget(List<Session> sessions, HashSet<GroupKey>? liveGroups)
    {
        var liveSessions = sessions.Select(session => session.Id).ToHashSet();
        foreach (var gone in _sessionRows.Keys.Where(id => !liveSessions.Contains(id)).ToList())
        {
            _sessionRows.Remove(gone);
        }

        if (liveGroups is null)
        {
            // Flat view resolves no groups. Leaving the headers cached is what makes toggling
            // back to grouped view keep whatever the operator had expanded.
            return;
        }

        foreach (var gone in _groupHeaders.Keys.Where(key => !liveGroups.Contains(key)).ToList())
        {
            _groupHeaders[gone].PropertyChanged -= OnHeaderChanged;
            _groupHeaders.Remove(gone);
            _footers.Remove(gone.Value);
        }
    }

    /// <summary>The counts strip (Design Document §9).</summary>
    /// <remarks>
    /// Derived on every refresh rather than maintained incrementally. Incremental counts are a
    /// cache of something already in hand, and a cache that can disagree with the collection
    /// beside it is worse than a loop over fifteen items. They count sessions, not rows, so a
    /// collapsed group still reports what is inside it.
    /// </remarks>
    private void RecountBands(List<Session> sessions)
    {
        var byBand = sessions.CountBy(session => AttentionOrder.BandOf(session.State))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        NeedsYouCount = byBand.GetValueOrDefault(AttentionBand.NeedsYou);
        UnreadCount = byBand.GetValueOrDefault(AttentionBand.Unread);
        WorkingCount = byBand.GetValueOrDefault(AttentionBand.Working);
        QuietCount = byBand.GetValueOrDefault(AttentionBand.Quiet);
        EndedCount = byBand.GetValueOrDefault(AttentionBand.Ended);
    }

    private SessionViewModel RowFor(Session session)
    {
        if (_sessionRows.TryGetValue(session.Id, out var existing))
        {
            existing.Session = session;
            return existing;
        }

        var created = new SessionViewModel(session, _motion);
        created.RefreshAge(_now > session.LastActivity ? _now : session.LastActivity);
        _sessionRows[session.Id] = created;
        return created;
    }

    private GroupViewModel HeaderFor(Group group)
    {
        if (_groupHeaders.TryGetValue(group.Key, out var existing))
        {
            existing.Group = group;
            return existing;
        }

        var created = new GroupViewModel(group);
        created.PropertyChanged += OnHeaderChanged;
        _groupHeaders[group.Key] = created;
        return created;
    }

    private BandHeaderViewModel HeaderFor(AttentionBand band, int count)
    {
        if (!_bandHeaders.TryGetValue(band, out var existing))
        {
            existing = new BandHeaderViewModel(band);
            existing.PropertyChanged += OnHeaderChanged;
            _bandHeaders[band] = existing;
        }

        existing.Count = count;
        return existing;
    }

    private QuietFooterViewModel FooterFor(DashboardRow owner, string key, int count, bool isBandSummary)
    {
        if (!_footers.TryGetValue(key, out var existing) || !ReferenceEquals(existing.Owner, owner))
        {
            existing = new QuietFooterViewModel(owner, key, isBandSummary);
            _footers[key] = existing;
        }

        existing.Count = count;
        return existing;
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    /// <summary>
    /// A heading was expanded or collapsed, which changes which rows exist.
    /// </summary>
    /// <remarks>
    /// The toggle is bound straight to the heading, so the row sequence has to follow it. Only
    /// <c>IsExpanded</c> re-runs the assembly: the other properties on a heading are things this
    /// method just finished setting, and reacting to those would recurse.
    /// </remarks>
    private void OnHeaderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GroupViewModel.IsExpanded))
        {
            Refresh();
        }
    }

    /// <summary>The reduced-motion setting changed; every row has to re-read it.</summary>
    private void OnMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (null or nameof(MotionPolicy.IsMotionAllowed)))
        {
            return;
        }

        foreach (var row in _sessionRows.Values)
        {
            row.RefreshMotion();
        }
    }

    /// <summary>
    /// Re-projects the same data (Impl §5.5). There is no second collection: the toggle changes
    /// which headings are built, and the rows themselves are the same instances.
    /// </summary>
    partial void OnIsGroupedChanged(bool value) => Refresh();
}
