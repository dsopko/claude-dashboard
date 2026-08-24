using System.Collections.ObjectModel;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// Mirrors the Registry into an <see cref="ObservableCollection{T}"/> on the UI thread
/// (Impl §4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The notification carries everything this needs.</strong> The handler runs on the
/// consumer thread and must post and return: it reads the changed <see cref="Session"/> out of
/// the event arguments — an immutable record, already a snapshot — and marshals that. It never
/// touches <see cref="SessionRegistry.Sessions"/>, which is a live view rather than a snapshot,
/// and enumerating it from the notification while the consumer is mid-apply throws. The T1.2
/// review hit exactly that.
/// </para>
/// <para>
/// <strong>Patched, not rebuilt.</strong> One event changes one session, so replacing the whole
/// collection would raise a reset that discards selection and scroll position on every hook —
/// several times a minute across fifteen sessions. Finding the row by id is a linear scan of a
/// collection that TS Part 2 sizes at about fifteen.
/// </para>
/// <para>
/// This holds sessions, not view models: the banding, ordering and grouping that turn these
/// into rows are T1.3's and T1.4's, applied by T1.10.
/// </para>
/// </remarks>
public sealed class SessionProjection : IDisposable
{
    private readonly SessionRegistry _registry;
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;

    /// <summary>Starts mirroring <paramref name="registry"/> onto the UI thread.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public SessionProjection(SessionRegistry registry, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _registry = registry;
        _dispatcher = dispatcher;
        _registry.SessionChanged += OnSessionChanged;
    }

    /// <summary>
    /// The sessions, as the UI sees them. Only ever touched on the UI thread.
    /// </summary>
    public ObservableCollection<Session> Sessions { get; } = [];

    /// <summary>Stops mirroring.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _registry.SessionChanged -= OnSessionChanged;
        _disposed = true;
    }

    /// <summary>
    /// Runs on the consumer thread. Captures the snapshot it was handed and posts; does no work
    /// of its own and touches nothing shared.
    /// </summary>
    private void OnSessionChanged(object? sender, SessionChangedEventArgs e)
    {
        var session = e.Session;
        var kind = e.Kind;

        _dispatcher.Post(() => ApplyOnUiThread(kind, session));
    }

    /// <summary>Runs on the UI thread.</summary>
    private void ApplyOnUiThread(SessionChangeKind kind, Session session)
    {
        var existing = IndexOf(session.Id);

        if (existing >= 0)
        {
            // An Added for a session already present means notifications were replayed or
            // reordered; treating it as an update keeps the collection consistent either way.
            Sessions[existing] = session;
            return;
        }

        if (kind is SessionChangeKind.Added or SessionChangeKind.Updated)
        {
            Sessions.Add(session);
        }
    }

    private int IndexOf(SessionId id)
    {
        for (var i = 0; i < Sessions.Count; i++)
        {
            if (Sessions[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}
