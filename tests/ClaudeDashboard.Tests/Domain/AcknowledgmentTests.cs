using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// What acknowledgment applies to, and that the two shipping tiers agree about it
/// (Design Document §4; TS §I.3, §IV.1).
/// </summary>
public sealed class AcknowledgmentTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    [Theory]
    [InlineData(SessionState.Unread)]
    [InlineData(SessionState.NeedsPermission)]
    [InlineData(SessionState.NeedsQuestion)]
    [InlineData(SessionState.Error)]
    public void An_acknowledgment_applies_where_there_is_something_to_acknowledge(SessionState state)
    {
        Assert.True(Acknowledgment.Applies(state));
    }

    /// <summary>
    /// And nowhere else. Enumerated from the enum rather than listed, so a state added later is
    /// covered the day it appears.
    /// </summary>
    [Fact]
    public void And_nowhere_else()
    {
        var applies = Enum.GetValues<SessionState>().Where(Acknowledgment.Applies).ToList();

        Assert.Equal(
            [
                SessionState.Error,
                SessionState.NeedsPermission,
                SessionState.NeedsQuestion,
                SessionState.Unread,
            ],
            applies.OrderBy(state => state.ToString(), StringComparer.Ordinal));
    }

    /// <summary>
    /// <strong>The two tiers cannot drift.</strong> Design Document §4 defines one transition
    /// reached three ways, and the Registry used to hold two private copies of which states it
    /// applied to — identical, and free to diverge. This drives every state through the real
    /// Registry twice: once acknowledged manually, once by the next prompt. Where one
    /// acknowledges, the other must.
    /// </summary>
    /// <remarks>
    /// Asserted through the machine rather than by comparing the predicate with itself, which
    /// would pass however wrong the predicate was.
    /// </remarks>
    [Fact]
    public void The_manual_and_automatic_tiers_acknowledge_the_same_states()
    {
        foreach (var state in Enum.GetValues<SessionState>())
        {
            var manual = AcknowledgedManually(state);
            var automatic = AcknowledgedByTheNextPrompt(state);

            Assert.True(
                manual == automatic,
                $"{state}: the manual tier says {manual} and the next prompt says {automatic}.");
        }
    }

    /// <summary>The event carries the session it is for, and says which tier raised it.</summary>
    [Fact]
    public void The_event_names_its_session_and_its_source()
    {
        var session = Reach(new SessionRegistry(), "s-1", SessionState.Unread);

        var ack = Acknowledgment.For(session, At.AddMinutes(5), AckSource.Manual);

        Assert.Equal(session.Id, ack.SessionId);
        Assert.Equal(At.AddMinutes(5), ack.Timestamp);
        Assert.Equal(AckSource.Manual, ack.Source);
        Assert.Equal("Ack", ack.HookEventName);
    }

    /// <summary>
    /// It carries the session's own workspace, so acknowledging a session cannot move it out of
    /// its group as a side effect.
    /// </summary>
    /// <remarks>
    /// Asserted on the event, because the Registry would currently absorb the mistake: an <c>Ack</c>
    /// does not re-derive the group, so an ack built with an empty <c>cwd</c> is harmless today and
    /// a test that only checked the resulting group would pass. Every event carries a <c>cwd</c>
    /// and TS §IV.3 re-derives from it; leaving this one empty would be a trap set for whoever
    /// makes that uniform.
    /// </remarks>
    [Fact]
    public void The_event_carries_the_sessions_workspace()
    {
        var registry = new SessionRegistry();
        var before = Reach(registry, "s-1", SessionState.Unread);

        var ack = Acknowledgment.For(before, At.AddMinutes(1), AckSource.Manual);
        Assert.Equal(before.Cwd, ack.Cwd);

        // …and the session is where it was afterwards, which is the property that matters.
        registry.Apply(ack);
        var after = registry.Sessions[before.Id];
        Assert.Equal(before.Cwd, after.Cwd);
        Assert.Equal(before.Group, after.Group);
    }

    [Fact]
    public void An_acknowledgment_needs_a_session()
    {
        Assert.Throws<ArgumentNullException>(() => Acknowledgment.For(null!, At, AckSource.Manual));
    }

    /// <summary>
    /// Whether a manual ack <em>moves</em> a session in <paramref name="state"/> to Acked.
    /// </summary>
    /// <remarks>
    /// The outcome, not the resulting state. Asking "is it Acked now" is trivially true for a
    /// session that was already Acked, where the Registry in fact declined the event as
    /// <see cref="ApplyOutcome.Ignored"/> — the question is whether the acknowledgment applied,
    /// and only the outcome answers it.
    /// </remarks>
    private static bool AcknowledgedManually(SessionState state)
    {
        var registry = new SessionRegistry();
        var session = Reach(registry, "s-1", state);

        var outcome = registry.Apply(Acknowledgment.For(session, At.AddMinutes(30), AckSource.Manual));

        return outcome == ApplyOutcome.Applied
            && registry.Sessions[session.Id].State == SessionState.Acked;
    }

    /// <summary>
    /// Whether the next prompt acknowledges a session in <paramref name="state"/> — read from the
    /// transition the Registry recorded, since the session lands in Working either way.
    /// </summary>
    private static bool AcknowledgedByTheNextPrompt(SessionState state)
    {
        var registry = new SessionRegistry();
        var session = Reach(registry, "s-1", state);

        registry.Apply(new UserPromptSubmit
        {
            SessionId = session.Id,
            Timestamp = At.AddMinutes(30),
            Cwd = session.Cwd,
            PromptId = "p-next",
            Prompt = "and now something else",
        });

        return registry.Sessions[session.Id].Transitions
            .Any(entry => entry.Cause?.Contains("auto-ack", StringComparison.Ordinal) == true);
    }

    /// <summary>Drives a fresh session to <paramref name="state"/> through real events.</summary>
    private static Session Reach(SessionRegistry registry, string id, SessionState state)
    {
        var sessionId = new SessionId(id);
        const string Cwd = @"C:\dev\PennCustQuote";

        void Prompt(string promptId) => registry.Apply(new UserPromptSubmit
        {
            SessionId = sessionId,
            Timestamp = At,
            Cwd = Cwd,
            PromptId = promptId,
            Prompt = "run the tests",
        });

        switch (state)
        {
            case SessionState.Working:
                Prompt("p-1");
                break;

            case SessionState.Unread:
                Prompt("p-1");
                registry.Apply(new Stop
                {
                    SessionId = sessionId,
                    Timestamp = At.AddMinutes(1),
                    Cwd = Cwd,
                    PromptId = "p-1",
                    LastAssistantMessage = "29 passed",
                });
                break;

            case SessionState.Error:
                Prompt("p-1");
                registry.Apply(new StopFailure
                {
                    SessionId = sessionId,
                    Timestamp = At.AddMinutes(1),
                    Cwd = Cwd,
                    PromptId = "p-1",
                    ErrorKind = "rate_limit",
                });
                break;

            case SessionState.NeedsPermission:
            case SessionState.NeedsQuestion:
                Prompt("p-1");
                registry.Apply(new Notification
                {
                    SessionId = sessionId,
                    Timestamp = At.AddMinutes(1),
                    Cwd = Cwd,
                    NotificationType = state == SessionState.NeedsPermission
                        ? "permission_prompt"
                        : "idle_prompt",
                });
                break;

            case SessionState.Acked:
                registry.Apply(new SessionStart
                {
                    SessionId = sessionId,
                    Timestamp = At,
                    Cwd = Cwd,
                    Source = "startup",
                });
                break;

            case SessionState.Ended:
                registry.Apply(new SessionStart
                {
                    SessionId = sessionId,
                    Timestamp = At,
                    Cwd = Cwd,
                    Source = "startup",
                });
                registry.Apply(new SessionEnd
                {
                    SessionId = sessionId,
                    Timestamp = At.AddMinutes(1),
                    Cwd = Cwd,
                    Reason = "logout",
                });
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "No pipeline path to this state.");
        }

        var reached = registry.Sessions[sessionId];
        Assert.Equal(state, reached.State);
        return reached;
    }
}
