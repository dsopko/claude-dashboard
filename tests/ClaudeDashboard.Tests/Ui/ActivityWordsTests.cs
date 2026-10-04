using System.Globalization;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The Activity window's words (T1.70, issue #97): every shown kind and reason has plain words, no enum
/// name reaches the screen, and the name and the project follow the rules.
/// </summary>
public sealed class ActivityWordsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every enum whose names a shown row can carry, and the decision kinds themselves.</summary>
    private static readonly Type[] Enums =
    [
        typeof(DecisionKind), typeof(SessionState), typeof(SuppressionReason), typeof(SoundOutcome),
        typeof(SoundCommandKind), typeof(AckSource), typeof(PromptMeaning), typeof(TickOutcome), typeof(SoundDecisionKind),
    ];

    /// <summary>The table shows the block's kinds and none of the others.</summary>
    [Fact]
    public void The_window_shows_exactly_the_kinds_of_the_table()
    {
        DecisionKind[] shown =
        [
            DecisionKind.SessionAdded, DecisionKind.StateMoved, DecisionKind.SilenceSwept, DecisionKind.SessionEnded,
            DecisionKind.AckApplied, DecisionKind.NoticePlayed, DecisionKind.NudgePlayed, DecisionKind.GroupNoticePlayed,
            DecisionKind.NoticeSuppressed, DecisionKind.SoundDropped, DecisionKind.MuteApplied, DecisionKind.MuteExpired,
        ];

        foreach (var kind in Enum.GetValues<DecisionKind>())
        {
            var line = ActivityWords.LineOf(1, Row(kind));

            Assert.Equal(shown.Contains(kind), line is not null);
        }

        Assert.Equal(shown.Order(), ActivityWords.Shown.Order());
    }

    /// <summary>
    /// <strong>Every shown kind, with every reason it can carry, has words, and no enum name reaches the
    /// screen</strong>: not in what occurred, the name, the project, the detail or the sentence.
    /// </summary>
    [Fact]
    public void Every_shown_kind_and_reason_has_words_and_no_code_name()
    {
        var lines = new List<ActivityLine>();

        foreach (var state in Enum.GetValues<SessionState>())
        {
            lines.Add(Line(DecisionKind.SessionAdded, toState: state.ToString()));
            lines.Add(Line(DecisionKind.StateMoved, fromState: nameof(SessionState.Unread), toState: state.ToString()));
            Assert.NotEqual("changed", ActivityWords.StateWords(state.ToString()));
        }

        foreach (var reason in new[] { nameof(PromptMeaning.ScheduledPrompt), nameof(PromptMeaning.MachinePrompt), nameof(PromptMeaning.AutoAcknowledgment), nameof(TickOutcome.QuietTick) })
        {
            var line = Line(DecisionKind.StateMoved, fromState: nameof(SessionState.Working), toState: nameof(SessionState.Unread), reason: reason);
            Assert.NotEqual(string.Empty, line.Detail);
            lines.Add(line);
        }

        foreach (var source in Enum.GetValues<AckSource>())
        {
            var line = Line(DecisionKind.AckApplied, reason: source.ToString());
            Assert.NotEqual(string.Empty, line.Detail);
            lines.Add(line);
        }

        foreach (var sound in new[] { SoundId.Finished, SoundId.Permission, SoundId.Question, SoundId.Error })
        {
            Assert.NotEqual("a sound", ActivityWords.SoundWords(sound.Name));
            lines.Add(Line(DecisionKind.NoticePlayed, reason: sound.Name));
            lines.Add(Line(DecisionKind.NudgePlayed, reason: sound.Name, detail: "rung=1 waitedMinutes=7"));

            foreach (var reason in Enum.GetValues<SuppressionReason>())
            {
                var line = Line(DecisionKind.NoticeSuppressed, reason: reason.ToString(), detail: $"kind=Notice sound={sound.Name}");
                Assert.NotEqual(string.Empty, ActivityWords.WhyNoSound(reason.ToString()));
                lines.Add(line);
            }

            foreach (var outcome in new[] { SoundOutcome.NoOutput, SoundOutcome.Failed })
            {
                Assert.NotEqual(string.Empty, ActivityWords.WhyNotHeard(outcome.ToString()));
                lines.Add(Line(DecisionKind.SoundDropped, reason: outcome.ToString(), detail: $"kind=Nudge sound={sound.Name} rung=1 waitedMinutes=3"));
            }
        }

        foreach (var command in Enum.GetValues<SoundCommandKind>())
        {
            Assert.NotEqual("sound changed", ActivityWords.MuteWords(command.ToString()));
            lines.Add(Line(DecisionKind.MuteApplied, reason: command.ToString(), detail: "until=2026-10-04T12:30:00.0000000+00:00", sessionId: null));
        }

        lines.Add(Line(DecisionKind.SilenceSwept, fromState: "Working", toState: "Interrupted", reason: "silence", detail: "silentMinutes=11"));
        lines.Add(Line(DecisionKind.SessionEnded, fromState: "Unread", toState: "Ended"));
        lines.Add(Line(DecisionKind.GroupNoticePlayed, reason: "notice", detail: "group=roster:orchestration members=s-1,s-2", sessionId: null));
        lines.Add(Line(DecisionKind.GroupNoticePlayed, reason: "nudge", detail: "group=roster:orchestration members=s-1,s-2", sessionId: null));
        lines.Add(Line(DecisionKind.MuteExpired, detail: "until=2026-10-04T12:30:00.0000000+00:00", sessionId: null));

        var names = Enums.SelectMany(type => Enum.GetNames(type)).Where(name => name.Any(char.IsUpper)).ToHashSet(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            Assert.NotEqual(string.Empty, line.What);

            foreach (var text in new[] { line.What, line.Detail, line.Sentence(At) })
            {
                Assert.DoesNotContain(names, name => text.Contains(name, StringComparison.Ordinal));
                Assert.DoesNotContain("=", text, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>A value this build does not know gives no words rather than its own name.</summary>
    [Fact]
    public void An_unknown_value_shows_no_raw_text()
    {
        var unknown = "SomethingNew";

        Assert.Equal("changed", ActivityWords.StateWords(unknown));
        Assert.Equal("a sound", ActivityWords.SoundWords("chime"));

        foreach (var line in new[]
        {
            Line(DecisionKind.NoticeSuppressed, reason: unknown, detail: "kind=Notice sound=chime"),
            Line(DecisionKind.StateMoved, fromState: "Working", toState: unknown, reason: unknown),
            Line(DecisionKind.SilenceSwept, detail: "silentMinutes=SomethingNew"),
            Line(DecisionKind.MuteApplied, reason: unknown, sessionId: null),
        })
        {
            Assert.DoesNotContain(unknown, line.Sentence(At), StringComparison.Ordinal);
            Assert.DoesNotContain("chime", line.Sentence(At), StringComparison.Ordinal);
        }
    }

    /// <summary>The words the issue shows: "working again", and "went quiet: no event for 10 minutes".</summary>
    [Fact]
    public void The_issue_examples_read_as_written()
    {
        Assert.Equal("working again", Line(DecisionKind.StateMoved, fromState: "NeedsPermission", toState: "Working").What);

        var quiet = Line(DecisionKind.SilenceSwept, fromState: "Working", toState: "Interrupted", detail: "silentMinutes=10");
        Assert.Equal(("went quiet", "no event for 10 minutes"), (quiet.What, quiet.Detail));

        var nudge = Line(DecisionKind.NudgePlayed, reason: "permission", detail: "rung=2 waitedMinutes=7");
        Assert.True(nudge.Played);
        Assert.Equal(("permission", "reminder, waiting 7 min"), (nudge.What, nudge.Detail));

        var held = Line(DecisionKind.NoticeSuppressed, reason: nameof(SuppressionReason.GroupDone), detail: "kind=Notice sound=finished");
        Assert.False(held.Played);
        Assert.Equal(("no sound", "finished, its group owns the sound"), (held.What, held.Detail));
    }

    /// <summary>
    /// <strong>The name:</strong> the stored name; with none, the first eight characters of the id; a group's
    /// sound, the group's name. <strong>The project:</strong> the main window's label for the path, with the
    /// full path for the hover; nothing when there is no path.
    /// </summary>
    [Fact]
    public void The_name_and_the_project_follow_the_main_window()
    {
        const string Path = @"C:\Projects\Claude\claude-dashboard";

        var named = ActivityWords.LineOf(1, Row(DecisionKind.NoticePlayed, reason: "finished", title: "Director", cwd: Path))!;
        Assert.Equal("Director", named.Name);
        Assert.Equal(RowVisuals.WorkspaceLabel(Path), named.Project);
        Assert.Equal("claude-dashboard", named.Project);
        Assert.Equal(Path, named.ProjectPath);

        var unnamed = ActivityWords.LineOf(1, Row(DecisionKind.NoticePlayed, reason: "finished", sessionId: "a3f9c21e-77b0-4c2e-9d1a-0f0e2b6c1d55"))!;
        Assert.Equal("a3f9c21e", unnamed.Name);
        Assert.Equal(string.Empty, unnamed.Project);
        Assert.Null(unnamed.ProjectPath);

        var group = ActivityWords.LineOf(1, Row(DecisionKind.GroupNoticePlayed, reason: "notice", detail: "group=roster:orchestration members=s-1,s-2", sessionId: null))!;
        Assert.Equal("orchestration", group.Name);
        Assert.True(group.Played);
    }

    /// <summary>The time is "14:32" today, with a day name before it on another day; and the sentence holds everything.</summary>
    [Fact]
    public void The_time_and_the_sentence()
    {
        var line = ActivityWords.LineOf(1, Row(DecisionKind.NudgePlayed, reason: "permission", detail: "rung=1 waitedMinutes=7", title: "Reviewer", cwd: @"C:\dev\penn-quote"))!;
        var local = line.Ts.ToLocalTime();

        Assert.Equal(local.ToString("HH:mm", CultureInfo.CurrentCulture), line.TimeText(line.Ts));
        Assert.Equal(local.ToString("ddd HH:mm", CultureInfo.CurrentCulture), line.TimeText(line.Ts.AddDays(2)));
        Assert.Equal(
            $"{line.TimeText(line.Ts)}, sound played, permission, Reviewer, project penn-quote, reminder, waiting 7 min.",
            line.Sentence(line.Ts));
    }

    private static ActivityLine Line(
        DecisionKind kind,
        string? fromState = null,
        string? toState = null,
        string? reason = null,
        string? detail = null,
        string? sessionId = "s-1") =>
        ActivityWords.LineOf(1, Row(kind, fromState, toState, reason, detail, sessionId)) ?? throw new Xunit.Sdk.XunitException($"{kind} gave no line.");

    private static Decision Row(
        DecisionKind kind,
        string? fromState = null,
        string? toState = null,
        string? reason = null,
        string? detail = null,
        string? sessionId = "s-1",
        string? title = null,
        string? cwd = null) =>
        new(At, sessionId, kind, fromState, toState, reason, detail) { SessionTitle = title, Cwd = cwd };
}
