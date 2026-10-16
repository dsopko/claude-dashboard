using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Domain;

/// <summary>
/// The latch with two sources (issue #161, the operator's ruling of 2026-10-16): a given name, from the hook's
/// <c>session_title</c> or the transcript's <c>customTitle</c>, before the generated title, the transcript's
/// <c>aiTitle</c>. The names arrive on <see cref="InboundEvent.Transcript"/>; these tests set it as the consumer
/// does.
/// </summary>
public sealed class TranscriptTitleLatchTests
{
    private const string Id = "s-1";
    private const string Workspace = @"C:\dev\PennCustQuote";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    /// <summary>
    /// <strong>The defect:</strong> a session nobody named shows the title Claude Code generated for it, which only
    /// its transcript holds.
    /// </summary>
    [Fact]
    public void A_session_with_no_given_name_shows_its_generated_title()
    {
        var registry = New();
        registry.Apply(Prompt(At));

        // The control: the hooks alone leave it blank.
        Assert.Null(Session(registry).Title);

        var outcome = registry.Apply(Batch(At.AddSeconds(1), generated: "PennCustQuote dashboard theme integration"));

        Assert.Equal(ApplyOutcome.Applied, outcome);
        Assert.Equal("PennCustQuote dashboard theme integration", Session(registry).Title);
        Assert.True(Session(registry).TitleIsGenerated);
        Assert.Null(Session(registry).GivenName);
    }

    /// <summary>
    /// <strong>The given name first:</strong> a generated title never replaces a given name from the hook.
    /// </summary>
    [Fact]
    public void A_generated_title_never_replaces_a_name_from_the_hook()
    {
        var registry = New();
        registry.Apply(Prompt(At, title: "Director"));

        var outcome = registry.Apply(Batch(At.AddSeconds(1), generated: "Build network monitoring application"));

        Assert.Equal(ApplyOutcome.Ignored, outcome);
        Assert.Equal("Director", Session(registry).Title);
        Assert.False(Session(registry).TitleIsGenerated);
    }

    /// <summary>
    /// <strong>Custom, then ai, then ai</strong> (measured: in 30 of 36 transcripts with both kinds the newest
    /// <c>aiTitle</c> is after the newest <c>customTitle</c>). The given name stays through both later reads.
    /// </summary>
    [Fact]
    public void A_name_from_the_transcript_stays_through_later_generated_titles()
    {
        var registry = New();
        registry.Apply(Prompt(At));

        registry.Apply(Batch(At.AddSeconds(1), given: "Director", generated: "First generated"));
        registry.Apply(Batch(At.AddSeconds(2), generated: "Second generated"));
        registry.Apply(Finish(At.AddSeconds(3), generated: "Third generated"));

        Assert.Equal("Director", Session(registry).Title);
        Assert.Equal("Director", Session(registry).GivenName);
    }

    /// <summary>
    /// <strong>Follow changes:</strong> a new generated title replaces the old one (measured: 7a239067 went from
    /// "Build network monitoring application" to "home-internet-monitor-ping").
    /// </summary>
    [Fact]
    public void A_changed_generated_title_replaces_the_old_one()
    {
        var registry = New();
        registry.Apply(Prompt(At));
        registry.Apply(Batch(At.AddSeconds(1), generated: "Build network monitoring application"));

        var outcome = registry.Apply(Finish(At.AddSeconds(2), generated: "home-internet-monitor-ping"));

        Assert.Equal(ApplyOutcome.Applied, outcome);
        Assert.Equal("home-internet-monitor-ping", Session(registry).Title);
        Assert.True(Session(registry).TitleIsGenerated);
    }

    /// <summary>
    /// <strong>A rename after a generated title</strong> replaces it, even when the operator gives the same words:
    /// the name is now a given one, and a roster can keep it.
    /// </summary>
    [Theory]
    [InlineData("Director")]
    [InlineData("Generated words")]
    public void A_given_name_replaces_a_generated_title(string given)
    {
        var registry = New();
        registry.Apply(Prompt(At));
        registry.Apply(Batch(At.AddSeconds(1), generated: "Generated words"));

        var outcome = registry.Apply(Batch(At.AddSeconds(2), title: given));

        Assert.Equal(ApplyOutcome.Applied, outcome);
        Assert.Equal(given, Session(registry).Title);
        Assert.Equal(given, Session(registry).GivenName);
        Assert.False(Session(registry).TitleIsGenerated);
    }

    /// <summary>A rename read from the transcript replaces the old given name: the newest <c>customTitle</c>.</summary>
    [Fact]
    public void A_rename_read_from_the_transcript_replaces_the_old_name()
    {
        var registry = New();
        registry.Apply(Prompt(At, title: "Old name"));

        registry.Apply(Finish(At.AddSeconds(1), given: "New name"));

        Assert.Equal("New name", Session(registry).GivenName);
    }

    /// <summary>On one event, the hook's name comes before the transcript's: the ruling's order.</summary>
    [Fact]
    public void The_hooks_name_comes_before_the_transcripts_on_one_event()
    {
        var registry = New();

        registry.Apply(Prompt(At, title: "From the hook") with { Transcript = new TranscriptNames("From the file", "Generated") });

        Assert.Equal("From the hook", Session(registry).Title);
        Assert.False(Session(registry).TitleIsGenerated);
    }

    /// <summary>
    /// <strong>After a restart</strong> the Registry is empty, and a session whose first event is a <c>Stop</c> or a
    /// <c>SessionEnd</c> (which never carry a name) gets its name back from the transcript at once.
    /// </summary>
    [Theory]
    [InlineData("Stop", "Director", null)]
    [InlineData("SessionEnd", "Director", null)]
    [InlineData("Stop", null, "Generated words")]
    [InlineData("SessionEnd", null, "Generated words")]
    public void The_first_event_after_a_restart_brings_the_name_back(string kind, string? given, string? generated)
    {
        var registry = New();
        var names = new TranscriptNames(given, generated);
        InboundEvent first = kind == "Stop"
            ? Finish(At) with { Transcript = names }
            : new SessionEnd { SessionId = new SessionId(Id), Timestamp = At, Cwd = Workspace, Reason = "prompt_input_exit", Transcript = names };

        Assert.Equal(ApplyOutcome.Applied, registry.Apply(first));

        Assert.Equal(given ?? generated, Session(registry).Title);
        Assert.Equal(given is null, Session(registry).TitleIsGenerated);
    }

    /// <summary>A blank name from the transcript is none, as a blank one from the hook is.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_from_the_transcript_is_none(string blank)
    {
        var registry = New();
        registry.Apply(Prompt(At, title: "Director"));

        Assert.Equal(ApplyOutcome.Ignored, registry.Apply(Batch(At.AddSeconds(1), given: blank, generated: blank)));
        Assert.Equal("Director", Session(registry).Title);
    }

    /// <summary>
    /// A generated title is a cosmetic fact, as a rename is: it moves neither the age clock nor the recency key.
    /// </summary>
    [Fact]
    public void A_generated_title_moves_neither_the_age_clock_nor_the_recency_key()
    {
        var registry = New();
        registry.Apply(Prompt(At));
        var before = Session(registry);

        registry.Apply(Batch(At.AddMinutes(5), generated: "Generated words"));

        Assert.Equal(before.EnteredAt, Session(registry).EnteredAt);
        Assert.Equal(before.LastActivity, Session(registry).LastActivity);
    }

    /// <summary>
    /// <strong>A roster keeps its members by given name only.</strong> A session whose generated title equals a
    /// rostered name stays in its workspace group: the title will change.
    /// </summary>
    [Fact]
    public void A_generated_title_does_not_join_a_roster()
    {
        var rosters = RosterBook.Empty.With("Team", ["Director", "Coder"]);
        var registry = New();
        registry.Apply(Prompt(At));
        registry.Apply(Batch(At.AddSeconds(1), generated: "Director"));

        Assert.Equal(Session(registry).WorkspaceGroup, GroupKeys.Effective(Session(registry), rosters));

        registry.Apply(Batch(At.AddSeconds(2), title: "Director"));

        Assert.Equal(GroupKeys.ForRoster("Team"), GroupKeys.Effective(Session(registry), rosters));
    }

    private static SessionRegistry New() => new(new SingleWriterGuard());

    private static Session Session(SessionRegistry registry) => registry.Sessions[new SessionId(Id)];

    private static UserPromptSubmit Prompt(DateTimeOffset at, string? title = null) => new()
    {
        SessionId = new SessionId(Id),
        Timestamp = at,
        Cwd = Workspace,
        PromptId = "p-1",
        Prompt = "go",
        SessionTitle = title,
    };

    private static PostToolBatch Batch(
        DateTimeOffset at,
        string? title = null,
        string? given = null,
        string? generated = null) => new()
        {
            SessionId = new SessionId(Id),
            Timestamp = at,
            Cwd = Workspace,
            SessionTitle = title,
            Transcript = new TranscriptNames(given, generated),
        };

    private static Stop Finish(DateTimeOffset at, string? given = null, string? generated = null) => new()
    {
        SessionId = new SessionId(Id),
        Timestamp = at,
        Cwd = Workspace,
        PromptId = "p-1",
        Transcript = new TranscriptNames(given, generated),
    };
}
