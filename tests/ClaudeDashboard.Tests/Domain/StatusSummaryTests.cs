using System.Linq;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// What the whole dashboard amounts to right now (Impl §5.2).
/// </summary>
public sealed class StatusSummaryTests
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    /// <summary>Each counted state is counted, and the others are not.</summary>
    [Fact]
    public void It_counts_the_states_the_tooltip_names()
    {
        var summary = StatusSummary.Of(Sessions(
            SessionState.NeedsPermission,
            SessionState.NeedsPermission,
            SessionState.Error,
            SessionState.NeedsQuestion,
            SessionState.Unread,
            SessionState.Unread,
            SessionState.Working,
            SessionState.Acked,
            SessionState.Ended));

        Assert.Equal(2, summary.Permissions);
        Assert.Equal(1, summary.Errors);
        Assert.Equal(1, summary.Questions);
        Assert.Equal(2, summary.Unread);
        Assert.Equal(1, summary.Working);
        Assert.False(summary.IsAllQuiet);
    }

    /// <summary>
    /// <strong>The roll-up cannot drift from <see cref="AttentionOrder.WorstOf"/>.</strong>
    /// </summary>
    /// <remarks>
    /// <see cref="StatusSummary.Of"/> inlines the roll-up rather than calling
    /// <see cref="AttentionOrder.WorstOf"/>, so it can walk the sessions once instead of twice.
    /// That is a second copy of a loop, and this is what stops it becoming a second copy of the
    /// <em>ranking</em>: every combination of states is put through both, and they must agree.
    /// Driven from the enum, so a state added later is covered without anyone remembering.
    /// </remarks>
    [Fact]
    public void The_roll_up_agrees_with_the_one_in_AttentionOrder()
    {
        var states = Enum.GetValues<SessionState>();

        foreach (var a in states)
        {
            foreach (var b in states)
            {
                var summary = StatusSummary.Of(Sessions(a, b));

                Assert.Equal(AttentionOrder.WorstOf([a, b]), summary.Worst);
            }
        }
    }

    /// <summary>Nothing at all is quiet, and rolls up to rank zero.</summary>
    [Fact]
    public void An_empty_dashboard_is_all_quiet()
    {
        var summary = StatusSummary.Of([]);

        Assert.True(summary.IsAllQuiet);
        Assert.Equal(SessionState.Ended, summary.Worst);
    }

    /// <summary>
    /// Quiet and Ended sessions are present but count for nothing, so "all quiet" survives them.
    /// </summary>
    /// <remarks>
    /// This is what makes the Ended question decidable rather than open: an Ended session can
    /// only be the worst when nothing outranks quiet, and the tray paints every such state grey —
    /// so whether it "participates" cannot change the glyph or the tooltip.
    /// </remarks>
    [Fact]
    public void Quiet_and_ended_sessions_do_not_disturb_all_quiet()
    {
        var summary = StatusSummary.Of(Sessions(SessionState.Acked, SessionState.Ended, SessionState.Acked));

        Assert.True(summary.IsAllQuiet);

        // The worst of them does not outrank quiet, which is what sends the glyph to grey.
        Assert.True(AttentionOrder.Rank(summary.Worst) <= AttentionOrder.Rank(SessionState.Acked));
    }

    [Fact]
    public void It_needs_sessions()
    {
        Assert.Throws<ArgumentNullException>(() => StatusSummary.Of(null!));
    }

    /// <summary>Builds one session per state, through the real Registry.</summary>
    private static IReadOnlyList<Session> Sessions(params SessionState[] states)
    {
        var registry = new SessionRegistry(new SingleWriterGuard());

        for (var i = 0; i < states.Length; i++)
        {
            Reach(registry, $"s-{i}", states[i]);
        }

        return [.. registry.Sessions.Values];
    }

    private static void Reach(SessionRegistry registry, string id, SessionState state)
    {
        var sessionId = new SessionId(id);
        const string Cwd = @"C:\dev\PennCustQuote";

        void Prompt() => registry.Apply(new Core.Events.UserPromptSubmit
        {
            SessionId = sessionId,
            Timestamp = At,
            Cwd = Cwd,
            PromptId = "p-1",
            Prompt = "run the tests",
        });

        switch (state)
        {
            case SessionState.Working:
                Prompt();
                break;

            // Work, then silence past the threshold. No event reaches it (issue #28).
            //
            // THE SWEEP IS GLOBAL, AND THIS HELPER BUILDS ONE SESSION AT A TIME. Every other
            // working session in the registry has been silent just as long, so sweeping to reach
            // this state greys them out too — which made a Working/Interrupted pair roll up as
            // Interrupted and fail this test. The collateral is put back through the real resume
            // path rather than by writing state directly, so what this fixture leaves behind is
            // reachable in production.
            case SessionState.Interrupted:
                Prompt();

                var working = registry.Sessions.Values
                    .Where(session => session.Id != sessionId && session.State == SessionState.Working)
                    .Select(session => session.Id)
                    .ToList();

                var sweptAt = At + Core.SilenceWatch.DefaultThreshold + TimeSpan.FromMinutes(1);
                registry.SweepSilent(sweptAt, Core.SilenceWatch.DefaultThreshold);

                foreach (var other in working)
                {
                    registry.Apply(new Core.Events.PostToolBatch
                    {
                        SessionId = other,
                        Timestamp = sweptAt,
                        Cwd = Cwd,
                    });
                }

                break;

            case SessionState.Unread:
                Prompt();
                registry.Apply(new Core.Events.Stop
                {
                    SessionId = sessionId,
                    Timestamp = At.AddMinutes(1),
                    Cwd = Cwd,
                    PromptId = "p-1",
                    LastAssistantMessage = "29 passed",
                });
                break;

            case SessionState.Error:
                Prompt();
                registry.Apply(new Core.Events.StopFailure
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
                Prompt();
                registry.Apply(new Core.Events.Notification
                {
                    SessionId = sessionId,
                    Timestamp = At.AddMinutes(1),
                    Cwd = Cwd,
                    NotificationType = state == SessionState.NeedsPermission
                        ? "permission_prompt"
                        : "agent_needs_input",
                });
                break;

            case SessionState.Acked:
                registry.Apply(new Core.Events.SessionStart
                {
                    SessionId = sessionId,
                    Timestamp = At,
                    Cwd = Cwd,
                    Source = "startup",
                });
                break;

            case SessionState.Ended:
                registry.Apply(new Core.Events.SessionStart
                {
                    SessionId = sessionId,
                    Timestamp = At,
                    Cwd = Cwd,
                    Source = "startup",
                });
                registry.Apply(new Core.Events.SessionEnd
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

        Assert.Equal(state, registry.Sessions[sessionId].State);
    }
}
