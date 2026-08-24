using System.Collections.ObjectModel;
using System.Collections.Specialized;
using ClaudeDashboard.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The dashboard body and header (Design Document §7, §9; Impl §5.5).
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
    private readonly SessionProjection _projection;
    private readonly Dictionary<SessionId, SessionViewModel> _sessionRows = [];
    private readonly Dictionary<GroupKey, GroupViewModel> _groupHeaders = [];
    private readonly Dictionary<AttentionBand, BandHeaderViewModel> _bandHeaders = [];

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
    /// <exception cref="ArgumentNullException"><paramref name="projection"/> is null.</exception>
    public MainViewModel(SessionProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        _projection = projection;
        _projection.Sessions.CollectionChanged += OnSessionsChanged;

        Refresh();
    }

    /// <summary>
    /// The body: band or group headings interleaved with session rows, in display order.
    /// </summary>
    public ObservableCollection<DashboardRow> Rows { get; } = [];

    /// <summary>Stops following the projection.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _projection.Sessions.CollectionChanged -= OnSessionsChanged;
        _disposed = true;
    }

    /// <summary>
    /// Advances every row's age display to <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An age has to move without an event arriving — a session blocked for nine minutes must
    /// read nine, then ten — but nothing here starts a timer to make that happen. The event
    /// consumer owns the only periodic loop in the process (T1.9), deliberately, and a second one
    /// is exactly what that arrangement exists to prevent.
    /// </para>
    /// <para>
    /// So this is the entry point that loop should call, marshalled onto the UI thread. Until
    /// something calls it, ages are correct when a row changes and static in between. See the
    /// status report.
    /// </para>
    /// </remarks>
    public void Tick(DateTimeOffset now)
    {
        foreach (var row in Rows.OfType<SessionViewModel>())
        {
            row.RefreshAge(now);
        }
    }

    /// <summary>Rebuilds the row sequence from the projection, reusing every row it can.</summary>
    public void Refresh()
    {
        var sessions = _projection.Sessions.ToList();

        Reconcile(IsGrouped ? GroupedRows(sessions) : FlatRows(sessions));
        RecountBands(sessions);
    }

    /// <summary>Grouped view: a heading per group, its members in attention order beneath it.</summary>
    private List<DashboardRow> GroupedRows(List<Session> sessions)
    {
        var rows = new List<DashboardRow>();

        foreach (var group in AttentionEngine.OrderGroups(GroupResolver.Resolve(sessions)))
        {
            rows.Add(HeaderFor(group));
            rows.AddRange(group.Members.Select(RowFor));
        }

        return rows;
    }

    /// <summary>Flat view: global bands, visibly labelled (Design Document §7).</summary>
    private List<DashboardRow> FlatRows(List<Session> sessions)
    {
        var rows = new List<DashboardRow>();

        foreach (var band in AttentionEngine.Order(sessions))
        {
            rows.Add(HeaderFor(band.Band, band.Sessions.Count));
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

        Forget(desired);
    }

    /// <summary>Drops cached rows for sessions and groups that are no longer shown.</summary>
    private void Forget(List<DashboardRow> desired)
    {
        var liveSessions = desired.OfType<SessionViewModel>().Select(row => row.Id).ToHashSet();
        foreach (var gone in _sessionRows.Keys.Where(id => !liveSessions.Contains(id)).ToList())
        {
            _sessionRows.Remove(gone);
        }

        var liveGroups = desired.OfType<GroupViewModel>().Select(header => header.Key).ToHashSet();
        foreach (var gone in _groupHeaders.Keys.Where(key => !liveGroups.Contains(key)).ToList())
        {
            _groupHeaders.Remove(gone);
        }
    }

    /// <summary>The counts strip (Design Document §9).</summary>
    /// <remarks>
    /// Derived on every refresh rather than maintained incrementally. Incremental counts are a
    /// cache of something already in hand, and a cache that can disagree with the collection
    /// beside it is worse than a loop over fifteen items.
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

        var created = new SessionViewModel(session);
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
        _groupHeaders[group.Key] = created;
        return created;
    }

    private BandHeaderViewModel HeaderFor(AttentionBand band, int count)
    {
        if (!_bandHeaders.TryGetValue(band, out var existing))
        {
            existing = new BandHeaderViewModel(band);
            _bandHeaders[band] = existing;
        }

        existing.Count = count;
        return existing;
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    /// <summary>
    /// Re-projects the same data (Impl §5.5). There is no second collection: the toggle changes
    /// which headings are built, and the rows themselves are the same instances.
    /// </summary>
    partial void OnIsGroupedChanged(bool value) => Refresh();
}
