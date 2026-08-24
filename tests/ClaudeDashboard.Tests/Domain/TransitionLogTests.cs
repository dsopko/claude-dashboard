using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

public sealed class TransitionLogTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static StateTransition Transition(int ordinal) =>
        new(SessionState.Working, SessionState.Unread, At.AddSeconds(ordinal), $"Stop#{ordinal}");

    [Fact]
    public void Empty_log_has_no_entries()
    {
        Assert.Empty(TransitionLog.Empty);
    }

    [Fact]
    public void Append_returns_a_new_log_and_leaves_the_original_empty()
    {
        var appended = TransitionLog.Empty.Append(Transition(1));

        Assert.Single(appended);
        Assert.Equal(Transition(1), appended[0]);
        Assert.Empty(TransitionLog.Empty);
    }

    [Fact]
    public void Keeps_entries_oldest_first()
    {
        var log = TransitionLog.Empty
            .Append(Transition(1))
            .Append(Transition(2))
            .Append(Transition(3));

        Assert.Equal([Transition(1), Transition(2), Transition(3)], log);
    }

    /// <summary>
    /// "A small transition log" (Impl §2.1) — it explains how a row got the way it is; it is
    /// not an audit trail. Durable history is the persistence layer's (Impl §8).
    /// </summary>
    [Fact]
    public void Drops_the_oldest_entry_once_full()
    {
        var log = TransitionLog.Empty;
        for (var i = 0; i <= TransitionLog.Capacity; i++)
        {
            log = log.Append(Transition(i));
        }

        Assert.Equal(TransitionLog.Capacity, log.Count);
        Assert.Equal(Transition(1), log[0]);
        Assert.Equal(Transition(TransitionLog.Capacity), log[^1]);
    }

    [Fact]
    public void From_keeps_only_the_most_recent_entries()
    {
        var overfull = Enumerable.Range(0, TransitionLog.Capacity + 5).Select(Transition);

        var log = TransitionLog.From(overfull);

        Assert.Equal(TransitionLog.Capacity, log.Count);
        Assert.Equal(Transition(5), log[0]);
        Assert.Equal(Transition(TransitionLog.Capacity + 4), log[^1]);
    }

    [Fact]
    public void From_rejects_a_null_sequence()
    {
        Assert.Throws<ArgumentNullException>(() => TransitionLog.From(null!));
    }

    [Fact]
    public void From_an_empty_sequence_is_the_empty_log()
    {
        Assert.Same(TransitionLog.Empty, TransitionLog.From([]));
    }

    [Fact]
    public void Has_value_equality_by_sequence()
    {
        var one = TransitionLog.Empty.Append(Transition(1)).Append(Transition(2));
        var other = TransitionLog.Empty.Append(Transition(1)).Append(Transition(2));

        Assert.NotSame(one, other);
        Assert.Equal(one, other);
        Assert.True(one == other);
        Assert.False(one != other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());
    }

    [Fact]
    public void Distinguishes_logs_that_differ()
    {
        var one = TransitionLog.Empty.Append(Transition(1));

        Assert.NotEqual(one, TransitionLog.Empty.Append(Transition(2)));
        Assert.NotEqual(one, TransitionLog.Empty);
        Assert.NotEqual(one, one.Append(Transition(2)));
    }

    /// <summary>Order matters: the same transitions in a different sequence are a different history.</summary>
    [Fact]
    public void Is_order_sensitive()
    {
        var forward = TransitionLog.Empty.Append(Transition(1)).Append(Transition(2));
        var reversed = TransitionLog.Empty.Append(Transition(2)).Append(Transition(1));

        Assert.NotEqual(forward, reversed);
    }

    [Fact]
    public void Equality_operators_handle_null()
    {
        TransitionLog? nothing = null;

        Assert.True(nothing == null);
        Assert.False(TransitionLog.Empty == null);
        Assert.False(null == TransitionLog.Empty);
        Assert.True(TransitionLog.Empty != null);
    }
}

public sealed class StateTransitionTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Records_where_it_came_from_where_it_went_and_when()
    {
        var transition = new StateTransition(SessionState.Working, SessionState.Unread, At, "Stop");

        Assert.Equal(SessionState.Working, transition.From);
        Assert.Equal(SessionState.Unread, transition.To);
        Assert.Equal(At, transition.At);
        Assert.Equal("Stop", transition.Cause);
    }

    [Fact]
    public void Cause_is_optional()
    {
        var transition = new StateTransition(SessionState.Working, SessionState.Unread, At);

        Assert.Null(transition.Cause);
    }

    [Fact]
    public void Has_value_equality()
    {
        var a = new StateTransition(SessionState.Working, SessionState.Unread, At, "Stop");
        var b = new StateTransition(SessionState.Working, SessionState.Unread, At, "Stop");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Distinguishes_transitions_that_differ_in_any_member()
    {
        var baseline = new StateTransition(SessionState.Working, SessionState.Unread, At, "Stop");

        Assert.NotEqual(baseline, baseline with { From = SessionState.Acked });
        Assert.NotEqual(baseline, baseline with { To = SessionState.Error });
        Assert.NotEqual(baseline, baseline with { At = At.AddTicks(1) });
        Assert.NotEqual(baseline, baseline with { Cause = "StopFailure" });
    }
}
