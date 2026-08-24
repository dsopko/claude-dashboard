using System.Collections.Concurrent;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>An <see cref="IUiDispatcher"/> that queues work instead of owning a UI thread.</summary>
/// <remarks>
/// Queuing rather than running inline is the point: it makes "the handler posted and returned"
/// observable, and running inline would hide a handler that did its work on the caller's
/// thread — the very mistake the projection exists to avoid.
/// </remarks>
internal sealed class QueueingDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _pending = new();

    public int PostedCount { get; private set; }

    public void Post(Action work)
    {
        PostedCount++;
        _pending.Enqueue(work);
    }

    /// <summary>Runs what has been posted, standing in for the UI thread draining its queue.</summary>
    public int Pump()
    {
        var ran = 0;
        while (_pending.TryDequeue(out var work))
        {
            work();
            ran++;
        }

        return ran;
    }
}

/// <summary>
/// Marshalling the Registry onto the UI thread (Impl §4).
/// </summary>
public sealed class SessionProjectionTests : IDisposable
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    private readonly SessionRegistry _registry = new();
    private readonly QueueingDispatcher _dispatcher = new();
    private readonly SessionProjection _projection;

    public SessionProjectionTests() => _projection = new SessionProjection(_registry, _dispatcher);

    public void Dispose() => _projection.Dispose();

    private static UserPromptSubmit Prompt(string sessionId, DateTimeOffset stamp, string promptId) => new()
    {
        SessionId = new SessionId(sessionId),
        Timestamp = stamp,
        Cwd = @"C:\w",
        PromptId = promptId,
        Prompt = "p",
    };

    [Fact]
    public void A_new_session_appears_once_the_ui_thread_runs()
    {
        _registry.Apply(Prompt("s-1", At, "p-1"));

        // Nothing yet: the handler posted and returned.
        Assert.Empty(_projection.Sessions);
        Assert.Equal(1, _dispatcher.PostedCount);

        _dispatcher.Pump();

        var session = Assert.Single(_projection.Sessions);
        Assert.Equal(new SessionId("s-1"), session.Id);
    }

    /// <summary>
    /// The constraint that matters: the handler must not do its work on the notifying thread.
    /// <see cref="SessionRegistry.Sessions"/> is a live view, not a snapshot, and enumerating it
    /// mid-apply throws — the T1.2 review hit exactly that.
    /// </summary>
    [Fact]
    public void The_handler_posts_and_returns_without_touching_the_collection()
    {
        _registry.Apply(Prompt("s-1", At, "p-1"));

        Assert.Empty(_projection.Sessions);
        Assert.Equal(1, _dispatcher.PostedCount);
    }

    [Fact]
    public void An_updated_session_is_replaced_in_place_rather_than_duplicated()
    {
        _registry.Apply(Prompt("s-1", At, "p-1"));
        _registry.Apply(new Stop
        {
            SessionId = new SessionId("s-1"),
            Timestamp = At.AddMinutes(1),
            Cwd = @"C:\w",
            PromptId = "p-1",
            LastAssistantMessage = "done",
        });

        _dispatcher.Pump();

        var session = Assert.Single(_projection.Sessions);
        Assert.Equal(SessionState.Unread, session.State);
    }

    [Fact]
    public void Several_sessions_are_each_represented_once()
    {
        foreach (var id in new[] { "s-1", "s-2", "s-3" })
        {
            _registry.Apply(Prompt(id, At, "p-1"));
        }

        _dispatcher.Pump();

        Assert.Equal(3, _projection.Sessions.Count);
        Assert.Equal(3, _projection.Sessions.Select(s => s.Id).Distinct().Count());
    }

    /// <summary>
    /// The collection is patched rather than rebuilt: a reset on every hook would discard
    /// selection and scroll position several times a minute.
    /// </summary>
    [Fact]
    public void An_update_does_not_disturb_the_other_rows()
    {
        _registry.Apply(Prompt("s-1", At, "p-1"));
        _registry.Apply(Prompt("s-2", At, "p-1"));
        _dispatcher.Pump();

        var untouched = _projection.Sessions.Single(s => s.Id == new SessionId("s-2"));

        _registry.Apply(new Stop
        {
            SessionId = new SessionId("s-1"), Timestamp = At.AddMinutes(1), Cwd = @"C:\w", PromptId = "p-1",
        });
        _dispatcher.Pump();

        Assert.Same(untouched, _projection.Sessions.Single(s => s.Id == new SessionId("s-2")));
    }

    [Fact]
    public void A_declined_event_posts_nothing()
    {
        _registry.Apply(Prompt("s-1", At, "p-1"));
        _dispatcher.Pump();
        var posted = _dispatcher.PostedCount;

        // A duplicate: the Registry declines it, so there is nothing to marshal.
        _registry.Apply(Prompt("s-1", At.AddMinutes(1), "p-1"));

        Assert.Equal(posted, _dispatcher.PostedCount);
    }

    [Fact]
    public void Disposing_stops_the_mirror()
    {
        _projection.Dispose();

        _registry.Apply(Prompt("s-1", At, "p-1"));

        Assert.Equal(0, _dispatcher.PostedCount);
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        _projection.Dispose();
        _projection.Dispose();
    }

    [Fact]
    public void The_projection_needs_a_registry_and_a_dispatcher()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionProjection(null!, _dispatcher));
        Assert.Throws<ArgumentNullException>(() => new SessionProjection(_registry, null!));
    }
}
