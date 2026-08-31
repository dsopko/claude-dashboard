using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// A working session that stops saying anything stops reading as busy (issue #28).
/// </summary>
/// <remarks>
/// <para>
/// <strong>WHAT IS DETECTED IS SILENCE, AND EVERY TEST HERE SAYS WHICH SILENCE IT ARRANGES.</strong>
/// Claude Code posts nothing when a turn is interrupted, so elapsed quiet is the only signal there
/// is. A working session that has gone quiet can be any of these, and the sweep cannot tell them
/// apart:
/// </para>
/// <list type="bullet">
/// <item>interrupted with Escape — the case the operator filed, and the common one;</item>
/// <item>inside a single tool call longer than the threshold, which emits nothing until it
/// resolves — the false positive the ten minutes buys against, and the one
/// <see cref="A_batch_after_the_sweep_puts_the_session_back_to_working"/> recovers from;</item>
/// <item>working normally while emitting only events the Registry ignores — <strong>eliminated by
/// <see cref="Session.LastHeardAt"/></strong>, and the subject of
/// <see cref="A_session_emitting_only_ignored_events_is_not_silent"/>;</item>
/// <item>the machine asleep, the dashboard restarted, or the hook removed — all false positives
/// that correct themselves on the session's next event.</item>
/// </list>
/// <para>
/// So these tests claim the first and the third precisely, and name the rest as indistinguishable
/// rather than leaving a reader to assume the sweep is cleverer than it is.
/// </para>
/// <para>
/// Nothing sleeps. Every instant comes from a <see cref="FakeClock"/> the test advances, and the
/// Registry reads no clock of its own — the sweep takes <c>now</c> as a parameter for exactly that
/// reason.
/// </para>
/// </remarks>
public sealed class SilenceSweepTests
{
    private const string Cwd = @"C:\projects\dashboard";
    private static readonly SessionId Id = new("s-1");
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(10);

    private readonly FakeClock _clock = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());

    private Session Current => _registry.Sessions[Id];

    /// <summary>The instant the fixture began, captured rather than re-read.</summary>
    /// <remarks>
    /// It was a property returning <c>_clock.Now</c>, which advances — so an assertion written as
    /// <c>Start.AddMinutes(45)</c> quietly measured from the end of the test rather than the
    /// beginning. Captured once, the name means what it says.
    /// </remarks>
    private DateTimeOffset Start { get; }

    public SilenceSweepTests() => Start = _clock.Now;

    private void Prompt(string promptId = "p-1", SessionId? id = null) => _registry.Apply(new UserPromptSubmit
    {
        SessionId = id ?? Id,
        Timestamp = _clock.Now,
        Cwd = Cwd,
        PromptId = promptId,
        Prompt = "run the tests",
    });

    private void Batch(SessionId? id = null) => _registry.Apply(new PostToolBatch
    {
        SessionId = id ?? Id,
        Timestamp = _clock.Now,
        Cwd = Cwd,
    });

    private IReadOnlyList<SilentSession> Sweep() => _registry.SweepSilent(_clock.Now, Threshold);

    // ---- The threshold, pinned either side ---------------------------------------------------------

    /// <summary>Past the threshold, a working session stops reading as busy.</summary>
    [Fact]
    public void Silence_past_the_threshold_moves_a_working_session()
    {
        Prompt();
        _clock.Advance(Threshold + TimeSpan.FromSeconds(1));

        var moved = Sweep();

        Assert.Equal(SessionState.Interrupted, Current.State);
        Assert.Equal(Id, Assert.Single(moved).Session.Id);
    }

    /// <summary>
    /// <strong>The boundary, asserted on both sides rather than with a tolerance.</strong>
    /// </summary>
    /// <remarks>
    /// One case and a margin proves a timeout fires somewhere near ten minutes. A pair proves
    /// where. The instant itself is still working: silence must exceed the threshold, not merely
    /// reach it, so a session heard from exactly the threshold ago has not yet been quiet longer
    /// than the threshold.
    /// </remarks>
    [Theory]
    [InlineData(0, false)]
    [InlineData(9, false)]
    [InlineData(10, false)]
    [InlineData(11, true)]
    [InlineData(600, true)]
    public void The_threshold_is_exact_at_the_boundary(double minutes, bool moves)
    {
        Prompt();
        _clock.AdvanceMinutes(minutes);

        Sweep();

        Assert.Equal(moves ? SessionState.Interrupted : SessionState.Working, Current.State);
    }

    /// <summary>
    /// <strong>The shipped threshold is ten minutes, and the number itself is pinned.</strong>
    /// </summary>
    /// <remarks>
    /// Every other test here passes its own threshold, so the constant the product actually ships
    /// with was asserted nowhere — setting it to zero left the whole suite green. It is a guess,
    /// and a guess nothing pins can be changed by anyone for any reason without a test disagreeing.
    /// The log line is how it gets revised; this is what makes revising it deliberate.
    /// </remarks>
    [Fact]
    public void The_shipped_threshold_is_ten_minutes() =>
        Assert.Equal(TimeSpan.FromMinutes(10), SilenceWatch.DefaultThreshold);

    /// <summary>The threshold is a parameter, so a shorter one moves the same session sooner.</summary>
    /// <remarks>
    /// Injectable rather than compiled in, which is what lets these tests drive it from a clock
    /// and what would let the number change on the strength of the log without touching them.
    /// </remarks>
    [Fact]
    public void The_threshold_is_the_callers_to_choose()
    {
        Prompt();
        _clock.AdvanceMinutes(2);

        Assert.Empty(_registry.SweepSilent(_clock.Now, TimeSpan.FromMinutes(10)));
        Assert.Single(_registry.SweepSilent(_clock.Now, TimeSpan.FromMinutes(1)));
    }

    // ---- What must never be swept ------------------------------------------------------------------

    /// <summary>
    /// <strong>A SESSION ASKING FOR THE OPERATOR IS NEVER TIMED OUT, WHATEVER THE SILENCE.</strong>
    /// </summary>
    /// <remarks>
    /// Design §4: an absence of activity may quieten a session and must never promote one — and
    /// greying out a permission prompt is worse than a promotion, because it hides a request for
    /// help behind a colour that says nothing is wanted. These are exactly the states that wait
    /// indefinitely and therefore go silent indefinitely, so the guard is load-bearing rather than
    /// theoretical.
    /// </remarks>
    [Theory]
    [InlineData(SessionState.NeedsPermission)]
    [InlineData(SessionState.NeedsQuestion)]
    [InlineData(SessionState.Error)]
    [InlineData(SessionState.Unread)]
    [InlineData(SessionState.Acked)]
    [InlineData(SessionState.Ended)]
    public void No_state_but_working_is_ever_swept(SessionState state)
    {
        Reach(state);
        _clock.AdvanceMinutes(600);

        Assert.Empty(Sweep());
        Assert.Equal(state, Current.State);
    }

    /// <summary>
    /// <strong>A SESSION EMITTING ONLY EVENTS THE REGISTRY IGNORES IS NOT SILENT (issue #28).</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This test is the whole finding, and it fails against <c>LastActivity</c>.</strong>
    /// A <c>PostToolBatch</c> on a session already working is declined — 799 of the 1,210 payloads
    /// in the archive — and a declined event never advances <c>LastActivity</c>, because
    /// <c>Moved</c> returns null when nothing differs. Measured: a prompt, then a tool batch and an
    /// ignored notification over the following hour, left <c>LastActivity</c> at the prompt.
    /// </para>
    /// <para>
    /// So a timeout built on that field would grey out <strong>every long turn</strong>, not merely
    /// the long single tool call the design anticipated. A session emitting a batch every four
    /// seconds for eleven minutes would be marked interrupted while visibly working — the
    /// expensive false positive, arriving on ordinary work rather than a corner case.
    /// </para>
    /// <para>
    /// The batches here are twenty minutes apart on a ten-minute threshold, so each one has to be
    /// what keeps the session working. Swap <see cref="Session.LastHeardAt"/> for
    /// <c>LastActivity</c> in the sweep and this goes red on the first one.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_session_emitting_only_ignored_events_is_not_silent()
    {
        Prompt();

        for (var batch = 0; batch < 5; batch++)
        {
            _clock.AdvanceMinutes(9);
            Batch();

            Assert.Empty(Sweep());
            Assert.Equal(SessionState.Working, Current.State);
        }

        // 45 minutes of work, and LastActivity has not moved off the prompt the whole time —
        // which is precisely why it cannot be the field this reads.
        Assert.Equal(Start.AddMinutes(45), Current.LastHeardAt);
        Assert.Equal(Start, Current.LastActivity);
    }

    /// <summary>An acknowledgment is the dashboard talking to itself and does not count as hearing.</summary>
    /// <remarks>
    /// It cannot change an outcome today, because an acknowledged session is not working and the
    /// sweep reads nothing else. It is asserted so the field keeps meaning what its name says:
    /// the next person to read <see cref="Session.LastHeardAt"/> will rely on the name.
    /// </remarks>
    [Fact]
    public void An_acknowledgment_is_not_the_session_speaking()
    {
        Prompt();
        _clock.AdvanceMinutes(1);
        _registry.Apply(new Stop { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = "p-1" });

        var heard = Current.LastHeardAt;
        _clock.AdvanceMinutes(5);
        _registry.Apply(new Ack { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Source = AckSource.Manual });

        Assert.Equal(heard, Current.LastHeardAt);
    }

    // ---- Coming back -------------------------------------------------------------------------------

    /// <summary>Any event puts the session where that event says it belongs.</summary>
    /// <remarks>
    /// The state is neither sticky nor terminal, which is what bounds every false positive the
    /// heuristic can produce: a session marked wrongly corrects itself the moment it speaks.
    /// </remarks>
    [Fact]
    public void A_prompt_after_the_sweep_puts_the_session_back_to_working()
    {
        Prompt();
        _clock.AdvanceMinutes(11);
        Sweep();
        Assert.Equal(SessionState.Interrupted, Current.State);

        _clock.AdvanceMinutes(1);
        Prompt("p-2");

        Assert.Equal(SessionState.Working, Current.State);
    }

    /// <summary>
    /// <strong>A tool batch recovers the false positive the threshold cannot prevent.</strong>
    /// </summary>
    /// <remarks>
    /// The single long tool call: it emits nothing while it runs, the session goes grey, and then
    /// the batch resolves. Without this the row would stay grey until <c>Stop</c> — telling the
    /// operator a session had stopped while it was working the whole time.
    /// </remarks>
    [Fact]
    public void A_batch_after_the_sweep_puts_the_session_back_to_working()
    {
        Prompt();
        _clock.AdvanceMinutes(11);
        Sweep();

        _clock.AdvanceMinutes(4);
        Batch();

        Assert.Equal(SessionState.Working, Current.State);
    }

    /// <summary>A finished turn reads finished, not interrupted.</summary>
    [Fact]
    public void A_stop_after_the_sweep_reads_unread()
    {
        Prompt();
        _clock.AdvanceMinutes(11);
        Sweep();

        _clock.AdvanceMinutes(1);
        _registry.Apply(new Stop { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = "p-1" });

        Assert.Equal(SessionState.Unread, Current.State);
    }

    // ---- Sweeping twice ----------------------------------------------------------------------------

    /// <summary>Sweeping again moves nothing and reports nothing.</summary>
    /// <remarks>
    /// Idempotent by construction rather than by a flag: the sweep reads only working sessions, so
    /// one it has already moved is no longer a candidate. The loop runs this every fifteen seconds
    /// and a second transition per tick would be a second log line and a second row change for one
    /// event that never happened.
    /// </remarks>
    [Fact]
    public void Sweeping_twice_moves_nothing_the_second_time()
    {
        Prompt();
        _clock.AdvanceMinutes(11);

        Assert.Single(Sweep());
        Assert.Empty(Sweep());

        _clock.AdvanceMinutes(60);
        Assert.Empty(Sweep());
    }

    /// <summary>The entry stamp is the sweep's instant; what we last heard is untouched.</summary>
    /// <remarks>
    /// <para>
    /// The honest pair. The session entered this state now, so <see cref="Session.EnteredAt"/>
    /// says now — but nothing was heard, so <see cref="Session.LastHeardAt"/> must not move, and
    /// neither must <see cref="Session.LastActivity"/>, which is the Quiet band's sort key. A row
    /// that fell silent an hour ago sorts and reads as an hour old rather than as brand new.
    /// </para>
    /// <para>
    /// The transition is logged with a cause that names silence rather than interruption, because
    /// every other entry in that log is something that arrived and this one is the absence of any.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_sweep_stamps_entry_and_leaves_the_hearing_alone()
    {
        Prompt();
        var prompted = Current.LastHeardAt;

        _clock.AdvanceMinutes(11);
        var moved = Assert.Single(Sweep());

        Assert.Equal(_clock.Now, Current.EnteredAt);
        Assert.Equal(prompted, Current.LastHeardAt);
        Assert.Equal(prompted, Current.LastActivity);
        Assert.Equal(TimeSpan.FromMinutes(11), moved.Silence);
        Assert.Equal(SilenceWatch.Cause, Current.Transitions[^1].Cause);
    }

    // ---- More than one session ---------------------------------------------------------------------

    /// <summary>Only the silent sessions move, and the report names them worst first.</summary>
    /// <remarks>
    /// Ordered by silence so the log reads most-stalled first, which is the order somebody
    /// calibrating the threshold wants to read.
    /// </remarks>
    [Fact]
    public void Only_the_silent_move_and_the_report_is_ordered()
    {
        var quiet = new SessionId("s-quiet");
        var busy = new SessionId("s-busy");

        Prompt(id: Id);
        Prompt(id: quiet);

        _clock.AdvanceMinutes(20);
        Prompt("p-busy", busy);

        _clock.AdvanceMinutes(11);
        Batch(quiet);

        _clock.AdvanceMinutes(11);
        Batch(busy);

        var moved = Sweep();

        // Silent 42 minutes and 11 minutes; busy was heard from just now, so it is not swept.
        Assert.Equal([Id, quiet], moved.Select(entry => entry.Session.Id));
        Assert.Equal([TimeSpan.FromMinutes(42), TimeSpan.FromMinutes(11)], moved.Select(entry => entry.Silence));
        Assert.Equal(SessionState.Working, _registry.Sessions[busy].State);
    }

    /// <summary>Reaching a state through ordinary events, so no test writes one directly.</summary>
    private void Reach(SessionState state)
    {
        Prompt();
        _clock.AdvanceMinutes(1);

        switch (state)
        {
            case SessionState.Working:
                break;

            case SessionState.NeedsPermission:
                _registry.Apply(Notified("permission_prompt"));
                break;

            case SessionState.NeedsQuestion:
                _registry.Apply(Notified("agent_needs_input"));
                break;

            case SessionState.Error:
                _registry.Apply(new StopFailure
                {
                    SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, ErrorKind = "rate_limit",
                });
                break;

            case SessionState.Unread:
                _registry.Apply(new Stop { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = "p-1" });
                break;

            case SessionState.Acked:
                _registry.Apply(new Stop { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, PromptId = "p-1" });
                _registry.Apply(new Ack { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Source = AckSource.Manual });
                break;

            case SessionState.Ended:
                _registry.Apply(new SessionEnd { SessionId = Id, Timestamp = _clock.Now, Cwd = Cwd, Reason = "logout" });
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "No path to this state here.");
        }

        Assert.Equal(state, Current.State);
    }

    private Notification Notified(string type) => new()
    {
        SessionId = Id,
        Timestamp = _clock.Now,
        Cwd = Cwd,
        NotificationType = type,
    };
}
