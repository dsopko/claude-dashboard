using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// The Registry's session-title latch (T1.24, issue #18).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not one arrangement in this file uses <see cref="SessionStart"/>, and that is the
/// point of the file.</strong> <c>SessionStart</c> has never fired on this machine — 0
/// occurrences against 799 <c>PostToolBatch</c>, 124 <c>UserPromptSubmit</c>, 119 <c>Stop</c> and
/// 10 <c>SessionEnd</c> that prove sessions have started and ended (issue #20). A suite that
/// handed the title to a <c>SessionStart</c> would go green over a feature that never appears on
/// a single row, because the test would be supplying the one event the product never receives.
/// </para>
/// <para>
/// So every arrangement here drives an event that actually occurs, and
/// <see cref="No_arriving_event_needs_SessionStart_for_its_title_to_land"/> asserts the absence
/// directly rather than leaving it for a reader to notice.
/// </para>
/// </remarks>
public sealed class SessionTitleLatchTests
{
    private const string Id = "s-1";
    private const string Workspace = @"C:\dev\PennCustQuote";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    /// <summary>
    /// <strong>A title latches from an event whose transition DECLINES.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test the feature turns on. A <see cref="PostToolBatch"/> on a session that is
    /// already <see cref="SessionState.Working"/> is declined as
    /// <see cref="ApplyOutcome.Ignored"/> — and that is 799 of the archive's 1,210 payloads, the
    /// single likeliest event to be carrying a title. Latch inside the transition table and every
    /// one of those is dropped on the floor while the rest of the suite stays green.
    /// </para>
    /// <para>
    /// The outcome is asserted as well as the value. A latch that changed the session without
    /// reporting it would leave the consumer counting the event as a decline and nothing would
    /// repaint, which looks identical to the title never having arrived.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_title_lands_on_an_event_whose_transition_is_declined()
    {
        var registry = New();
        registry.Apply(Prompt(At));

        // The control: without a title on it, this very event changes nothing at all.
        Assert.Equal(ApplyOutcome.Ignored, registry.Apply(Batch(At.AddSeconds(1))));

        var outcome = registry.Apply(Batch(At.AddSeconds(2), title: "Director"));

        Assert.Equal(ApplyOutcome.Applied, outcome);
        Assert.Equal("Director", Session(registry).Title);
    }

    /// <summary>Every event that actually occurs can bring a title.</summary>
    /// <remarks>
    /// A theory over the event kinds the archive records, rather than over the ones that seem
    /// likely: the field is common to every variant precisely because which events may carry it
    /// is documented nowhere.
    /// </remarks>
    [Theory]
    [InlineData("UserPromptSubmit")]
    [InlineData("PostToolBatch")]
    [InlineData("Notification")]
    [InlineData("Stop")]
    [InlineData("SessionEnd")]
    [InlineData("CwdChanged")]
    public void Any_event_that_occurs_can_carry_the_title(string kind)
    {
        var registry = New();
        registry.Apply(Prompt(At));
        registry.Apply(Carrying(kind, At.AddSeconds(1), "Coder"));

        Assert.Equal("Coder", Session(registry).Title);
    }

    /// <summary>
    /// <strong>No code path needs <see cref="SessionStart"/> for a title to reach a row.</strong>
    /// </summary>
    /// <remarks>
    /// A session's whole life, driven only by events the archive has actually seen, ending with
    /// the title present. The transition log is asserted too, so this cannot pass by a
    /// <c>SessionStart</c> having quietly got into the arrangement.
    /// </remarks>
    [Fact]
    public void No_arriving_event_needs_SessionStart_for_its_title_to_land()
    {
        var registry = New();

        registry.Apply(Prompt(At, title: "Director"));
        registry.Apply(Batch(At.AddSeconds(1)));
        registry.Apply(Finish(At.AddSeconds(2)));

        var session = Session(registry);

        Assert.Equal("Director", session.Title);
        Assert.DoesNotContain(
            session.Transitions,
            entry => (entry.Cause ?? string.Empty).Contains("SessionStart", StringComparison.Ordinal));
    }

    /// <summary>The very first event seen for a session brings its title with it.</summary>
    [Fact]
    public void The_first_event_of_all_latches_the_title()
    {
        var registry = New();

        Assert.Equal(ApplyOutcome.Applied, registry.Apply(Prompt(At, title: "Reviewer")));
        Assert.Equal("Reviewer", Session(registry).Title);
    }

    /// <summary>
    /// <strong>A <see cref="Stop"/> does not blank the row.</strong>
    /// </summary>
    /// <remarks>
    /// <c>Stop</c> carries a title in none of 1,210 archived payloads, and it is the event that
    /// ends the turn — so a row that has just finished, which is exactly the row the operator is
    /// about to look at, is the one a per-event read would strip the name from.
    /// </remarks>
    [Fact]
    public void An_event_carrying_no_title_leaves_the_latched_one_alone()
    {
        var registry = New();
        registry.Apply(Prompt(At, title: "Director"));
        registry.Apply(Finish(At.AddSeconds(1)));

        Assert.Equal("Director", Session(registry).Title);
    }

    /// <summary>Whitespace is not a title, and does not erase one.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void A_blank_title_is_treated_as_none(string blank)
    {
        var registry = New();
        registry.Apply(Prompt(At, title: "Director"));
        registry.Apply(Batch(At.AddSeconds(1), title: blank));

        Assert.Equal("Director", Session(registry).Title);
    }

    /// <summary>A rename lands, whichever of the three ways produced it.</summary>
    /// <remarks>
    /// The collision variant is the one worth naming: launch a second session named <c>Coder</c>
    /// while one is live and Claude Code renames the new one itself, with no operator action at
    /// all. A first-write-wins latch would show the wrong name for the rest of that session.
    /// </remarks>
    [Fact]
    public void A_different_title_replaces_the_latched_one()
    {
        var registry = New();
        registry.Apply(Prompt(At, title: "Coder"));
        registry.Apply(Batch(At.AddSeconds(1), title: "coder-graceful-unicorn"));

        Assert.Equal("coder-graceful-unicorn", Session(registry).Title);
    }

    /// <summary>The same title again changes nothing and raises nothing.</summary>
    [Fact]
    public void The_same_title_again_is_not_a_change()
    {
        var registry = New();
        registry.Apply(Prompt(At, title: "Director"));

        var raised = 0;
        registry.SessionChanged += (_, _) => raised++;

        Assert.Equal(ApplyOutcome.Ignored, registry.Apply(Batch(At.AddSeconds(1), title: "Director")));
        Assert.Equal(0, raised);
    }

    /// <summary>
    /// <strong>A title change never reorders the list or resets an age.</strong>
    /// </summary>
    /// <remarks>
    /// <see cref="Session.LastActivity"/> is the sort key for the Working and Quiet bands and
    /// <see cref="Session.EnteredAt"/> is the age clock. A rename is a cosmetic fact, and letting
    /// one move a session up the list or restart its "4m ago" would be a cosmetic fact rewriting
    /// an attention fact. Asserted because nothing else in the suite would notice: the row would
    /// simply be in a slightly different place.
    /// </remarks>
    [Fact]
    public void A_title_change_moves_neither_the_age_clock_nor_the_recency_key()
    {
        var registry = New();
        registry.Apply(Prompt(At));

        var before = Session(registry);

        registry.Apply(Batch(At.AddMinutes(5), title: "Director"));

        var after = Session(registry);

        Assert.Equal("Director", after.Title);
        Assert.Equal(before.EnteredAt, after.EnteredAt);
        Assert.Equal(before.LastActivity, after.LastActivity);
        Assert.Equal(before.Transitions.Count, after.Transitions.Count);
    }

    /// <summary>A title on an event the stale guard drops never reaches the session.</summary>
    [Fact]
    public void A_stale_event_does_not_latch_its_title()
    {
        var registry = New();
        registry.Apply(Prompt(At.AddMinutes(10), title: "Director"));

        Assert.Equal(ApplyOutcome.Stale, registry.Apply(Batch(At, title: "Stale")));
        Assert.Equal("Director", Session(registry).Title);
    }

    /// <summary>
    /// <strong>AN OLD TITLE ARRIVING AFTER A RENAME WINS, AND THAT IS NOT GUARDED.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// This asserts the residual rather than a fix, because no fix is available. Ingress stamps
    /// events at <em>arrival</em>, so an event in flight before the rename is stamped after it and
    /// beats any comparison the domain could make; the wire carries no occurrence time, no title
    /// version and no sequence number.
    /// </para>
    /// <para>
    /// And underneath that: <strong>a stale title arriving late and a genuine rename back to a
    /// previous name are the same observation, byte for byte.</strong> Any rule that rejected the
    /// first would reject the second, and Claude Code documents the second as real. So the
    /// behaviour is pinned here, where someone will come looking for the guard, rather than left
    /// to be discovered and taken for a bug.
    /// </para>
    /// <para>
    /// It self-heals, and the second half asserts that, so "wrong for a moment" is not read as
    /// "wrong for good".
    /// </para>
    /// </remarks>
    [Fact]
    public void A_title_that_arrives_late_wins_because_arrival_order_is_the_only_order()
    {
        var registry = New();
        registry.Apply(Prompt(At, title: "Coder"));
        registry.Apply(Batch(At.AddSeconds(1), title: "coder-graceful-unicorn"));

        // In flight before the rename, stamped on arrival, and therefore stamped later.
        registry.Apply(Batch(At.AddSeconds(2), title: "Coder"));

        Assert.Equal("Coder", Session(registry).Title);

        // …and the next event carrying the new name puts it back.
        registry.Apply(Batch(At.AddSeconds(3), title: "coder-graceful-unicorn"));

        Assert.Equal("coder-graceful-unicorn", Session(registry).Title);
    }

    /// <summary>A session nobody named has no title, and the row has nothing to show.</summary>
    [Fact]
    public void A_session_that_never_carried_a_title_has_none()
    {
        var registry = New();
        registry.Apply(Prompt(At));
        registry.Apply(Finish(At.AddSeconds(1)));

        Assert.Null(Session(registry).Title);
    }

    /// <summary>The title is kept exactly as it arrived — the domain reshapes nothing.</summary>
    /// <remarks>
    /// Folding and truncation are the view model's. A domain that trimmed or collapsed the value
    /// would be settling a display question in the one place that cannot see the display, and the
    /// tooltip would then show something the operator's session is not called.
    /// </remarks>
    [Fact]
    public void The_latched_title_is_verbatim()
    {
        const string Awkward = "  Director\tand\nthe rest  ";

        var registry = New();
        registry.Apply(Prompt(At, title: Awkward));

        Assert.Equal(Awkward, Session(registry).Title);
    }

    private static SessionRegistry New() => new(new SingleWriterGuard());

    private static Session Session(SessionRegistry registry) => registry.Sessions[new SessionId(Id)];

    private static UserPromptSubmit Prompt(DateTimeOffset at, string? title = null) => new()
    {
        SessionId = new SessionId(Id),
        Timestamp = at,
        Cwd = Workspace,
        PromptId = "p-1",
        Prompt = "run the tests",
        SessionTitle = title,
    };

    private static PostToolBatch Batch(DateTimeOffset at, string? title = null) => new()
    {
        SessionId = new SessionId(Id),
        Timestamp = at,
        Cwd = Workspace,
        SessionTitle = title,
    };

    private static Stop Finish(DateTimeOffset at, string? title = null) => new()
    {
        SessionId = new SessionId(Id),
        Timestamp = at,
        Cwd = Workspace,
        PromptId = "p-1",
        LastAssistantMessage = "29 passed",
        SessionTitle = title,
    };

    private static InboundEvent Carrying(string kind, DateTimeOffset at, string title) => kind switch
    {
        "UserPromptSubmit" => new UserPromptSubmit
        {
            SessionId = new SessionId(Id), Timestamp = at, Cwd = Workspace,
            PromptId = "p-2", Prompt = "and again", SessionTitle = title,
        },
        "PostToolBatch" => Batch(at, title),
        "Notification" => new Notification
        {
            SessionId = new SessionId(Id), Timestamp = at, Cwd = Workspace,
            NotificationType = "permission_prompt", SessionTitle = title,
        },
        "Stop" => Finish(at, title),
        "SessionEnd" => new SessionEnd
        {
            SessionId = new SessionId(Id), Timestamp = at, Cwd = Workspace,
            Reason = "logout", SessionTitle = title,
        },
        "CwdChanged" => new CwdChanged
        {
            SessionId = new SessionId(Id), Timestamp = at, Cwd = @"C:\dev\elsewhere",
            SessionTitle = title,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown event kind."),
    };
}
