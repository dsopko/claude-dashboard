using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Domain;

public sealed class SessionTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static Session NewSession() => new()
    {
        Id = new SessionId("s-1"),
        State = SessionState.Working,
        Latest = new Exchange { Prompt = "run the tests", StartedAt = At },
        Cwd = @"C:\projects\dashboard",
        WorkspaceGroup = new GroupKey(@"C:\projects\dashboard"),
        EnteredAt = At,
        LastActivity = At,
        LastHeardAt = At,
    };

    [Fact]
    public void Constructs_with_the_members_TS_IV_1_requires()
    {
        var session = NewSession();

        Assert.Equal(new SessionId("s-1"), session.Id);
        Assert.Equal(SessionState.Working, session.State);
        Assert.Equal("run the tests", session.Latest.Prompt);
        Assert.Equal(@"C:\projects\dashboard", session.Cwd);
        Assert.Equal(new GroupKey(@"C:\projects\dashboard"), session.WorkspaceGroup);
        Assert.Equal(At, session.EnteredAt);
        Assert.Equal(At, session.LastActivity);
    }

    [Fact]
    public void Defaults_to_no_error_kind_and_an_empty_transition_log()
    {
        var session = NewSession();

        Assert.Null(session.ErrorKind);
        Assert.Empty(session.Transitions);
        Assert.Same(TransitionLog.Empty, session.Transitions);
    }

    [Fact]
    public void Rejects_a_null_latest_exchange()
    {
        Assert.Throws<ArgumentNullException>(() => NewSession() with { Latest = null! });
    }

    [Fact]
    public void Rejects_a_null_cwd()
    {
        Assert.Throws<ArgumentNullException>(() => NewSession() with { Cwd = null! });
    }

    /// <summary>
    /// `SessionId` is a struct, so `default` bypasses its non-empty constructor check
    /// (see ValueTypeConventions). Session is the boundary that requires a real one.
    /// </summary>
    [Fact]
    public void Rejects_an_id_that_names_no_session()
    {
        var thrown = Assert.Throws<ArgumentException>(() => NewSession() with { Id = default });

        Assert.Equal("value", thrown.ParamName);
    }

    [Fact]
    public void Rejects_a_group_key_that_names_no_group()
    {
        var thrown = Assert.Throws<ArgumentException>(() => NewSession() with { WorkspaceGroup = default });

        Assert.Equal("value", thrown.ParamName);
    }

    [Fact]
    public void Rejects_a_null_transition_log()
    {
        Assert.Throws<ArgumentNullException>(() => NewSession() with { Transitions = null! });
    }

    /// <summary>
    /// A payload can omit <c>cwd</c>; the session must still exist rather than the event being
    /// dropped, since ingress is a pure observer (Impl §3.3).
    /// </summary>
    [Fact]
    public void Accepts_an_empty_cwd()
    {
        var session = NewSession() with { Cwd = string.Empty };

        Assert.Equal(string.Empty, session.Cwd);
    }

    /// <summary>
    /// The raw matcher string is kept rather than an enum: Impl §9.1's StopFailure list is
    /// open-ended, so an unrecognized kind must still reach the operator intact.
    /// </summary>
    [Fact]
    public void Carries_an_error_kind_verbatim_including_unrecognized_ones()
    {
        var known = NewSession() with { State = SessionState.Error, ErrorKind = "rate_limit" };
        var novel = NewSession() with { State = SessionState.Error, ErrorKind = "quantum_flux" };

        Assert.Equal("rate_limit", known.ErrorKind);
        Assert.Equal("quantum_flux", novel.ErrorKind);
    }

    [Fact]
    public void Has_value_equality()
    {
        Assert.Equal(NewSession(), NewSession());
        Assert.Equal(NewSession().GetHashCode(), NewSession().GetHashCode());
    }

    [Fact]
    public void Distinguishes_sessions_that_differ_in_any_member()
    {
        var baseline = NewSession();

        Assert.NotEqual(baseline, baseline with { Id = new SessionId("s-2") });
        Assert.NotEqual(baseline, baseline with { State = SessionState.Unread });
        Assert.NotEqual(baseline, baseline with { Cwd = @"C:\elsewhere" });
        Assert.NotEqual(baseline, baseline with { WorkspaceGroup = new GroupKey(@"C:\elsewhere") });
        Assert.NotEqual(baseline, baseline with { EnteredAt = At.AddSeconds(1) });
        Assert.NotEqual(baseline, baseline with { LastActivity = At.AddSeconds(1) });
        Assert.NotEqual(baseline, baseline with { ErrorKind = "rate_limit" });
        Assert.NotEqual(baseline, baseline with { Latest = new Exchange { Prompt = "other", StartedAt = At } });
    }

    /// <summary>
    /// The transition log participates in equality by sequence, not by array reference — which
    /// is the reason <see cref="TransitionLog"/> exists as a type at all.
    /// </summary>
    [Fact]
    public void Compares_the_transition_log_by_sequence()
    {
        var transition = new StateTransition(SessionState.Working, SessionState.Unread, At, "Stop");

        var one = NewSession() with { Transitions = TransitionLog.Empty.Append(transition) };
        var other = NewSession() with { Transitions = TransitionLog.Empty.Append(transition) };

        Assert.NotSame(one.Transitions, other.Transitions);
        Assert.Equal(one, other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());
    }

    [Fact]
    public void Distinguishes_sessions_with_different_histories()
    {
        var withHistory = NewSession() with
        {
            Transitions = TransitionLog.Empty.Append(
                new StateTransition(SessionState.Working, SessionState.Unread, At, "Stop")),
        };

        Assert.NotEqual(NewSession(), withHistory);
    }

    /// <summary>
    /// The Registry applies events by producing a new session, never mutating one — that is
    /// what lets it stay single-writer and lock-free (Impl §2.2, §4).
    /// </summary>
    [Fact]
    public void With_expression_leaves_the_original_unchanged()
    {
        var working = NewSession();

        var unread = working with { State = SessionState.Unread, EnteredAt = At.AddMinutes(3) };

        Assert.Equal(SessionState.Working, working.State);
        Assert.Equal(At, working.EnteredAt);
        Assert.Equal(SessionState.Unread, unread.State);
        Assert.Equal(new SessionId("s-1"), unread.Id);
    }
}
