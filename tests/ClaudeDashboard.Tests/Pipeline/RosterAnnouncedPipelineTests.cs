using System.Collections.Concurrent;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// A roster group's "finished" plays only when it has something new to announce (T1.72, issue #107). Each
/// case runs through the real consumer, roster store, roster watch, engine and decisions recorder, wired as
/// <c>AppHost</c> wires them, under a fake clock. No settle is called by hand: an event or a roster edit wakes
/// the consumer, and its settle pass reads the groups as they stand.
/// </summary>
/// <remarks>
/// The operator's ruling of 2026-10-04: a roster made from finished sessions that already played their sound
/// plays no new sound; a member still working when it joins is the one sound the roster owes.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "xUnit disposes the fixture through IAsyncLifetime.DisposeAsync.")]
public sealed class RosterAnnouncedPipelineTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;
    private static readonly RosterBook Orchestration = RosterBook.From([("orchestration", ["Coder", "Reviewer"])]);

    private readonly FakeClock _clock = new();
    private readonly SingleWriterGuard _guard = new();
    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly EventPipeline _pipeline = new(Logger.None);
    private readonly EventArchive _archive = new(Logger.None);
    private readonly RecordingSoundPlayer _player = new();
    private readonly ConcurrentQueue<SoundMarkedEventArgs> _marks = new();
    private readonly List<Decision> _decisions = [];
    private readonly QueueingDispatcher _ui = new();
    private ActivityLog _activity = null!;
    private RosterStore _rosters = null!;
    private EventConsumer _consumer = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _consumer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <strong>A roster made from two finished sessions</strong> that each played "finished": no third sound,
    /// one <c>NoticeSuppressed</c> row with <c>AlreadyAnnounced</c> for the group, and no new speaker sign.
    /// The group plays no reminder, and each session's own reminder plays at its own time.
    /// </summary>
    [Fact]
    public async Task A_roster_made_from_finished_sessions_plays_nothing_and_keeps_their_own_reminders()
    {
        await Start(RosterBook.Empty);

        Publish(Prompt("s-1", "Coder", "p-1", At));
        Publish(Prompt("s-2", "Reviewer", "p-2", At));
        Publish(Finished("s-1", "p-1", At.AddSeconds(60)));
        Publish(Finished("s-2", "p-2", At.AddSeconds(120)));
        Assert.True(await Until(() => Finishes() == 2 && _marks.Count == 2), Seen());

        _clock.Now = At.AddMinutes(3);
        _rosters.Replace(Orchestration);

        Assert.True(await Until(() => _consumer.SettledCount == 1 && GroupSilences() == 1), Seen());
        Assert.Equal(2, Finishes());
        Assert.Equal(2, _marks.Count);
        Assert.Empty(Rows(DecisionKind.GroupNoticePlayed));

        // s-1's own reminder is due at 6:00, s-2's at 7:00; the group would have reminded at 8:00.
        _clock.Now = At.AddMinutes(6).AddSeconds(30);
        Assert.True(await Until(() => Rows(DecisionKind.NudgePlayed).Count == 1), Seen());
        Assert.Equal("s-1", Rows(DecisionKind.NudgePlayed)[0].SessionId);

        _clock.Now = At.AddMinutes(9);
        Assert.True(await Until(() => Rows(DecisionKind.NudgePlayed).Count == 2), Seen());
        Assert.Equal("s-2", Rows(DecisionKind.NudgePlayed)[1].SessionId);

        await Task.Delay(100);
        Assert.Empty(Rows(DecisionKind.GroupNoticePlayed));
    }

    /// <summary>
    /// <strong>The silent settle's line names the group</strong> (T1.73, issue #108): a roster made from two announced
    /// sessions writes a held-back group sound with the group and its members, and the Activity window reads "no
    /// sound", with the roster's name and "announced before".
    /// </summary>
    [Fact]
    public async Task The_silent_settle_line_names_the_group()
    {
        await Start(RosterBook.Empty);

        Publish(Prompt("s-1", "Coder", "p-1", At));
        Publish(Prompt("s-2", "Reviewer", "p-2", At));
        Publish(Finished("s-1", "p-1", At.AddSeconds(60)));
        Publish(Finished("s-2", "p-2", At.AddSeconds(120)));
        Assert.True(await Until(() => Finishes() == 2), Seen());

        _clock.Now = At.AddMinutes(3);
        _rosters.Replace(Orchestration);
        Assert.True(await Until(() => GroupSilences() == 1), Seen());

        var held = Assert.Single(Rows(DecisionKind.NoticeSuppressed), row => row.SessionId is null);
        Assert.Equal("kind=GroupNotice sound=finished group=roster:orchestration members=s-1,s-2", held.Detail);

        Assert.True(await Until(() => { _ui.Pump(); return _activity.Lines.Any(line => line.Line.Group is not null); }), Seen());
        var line = Assert.Single(_activity.Lines, candidate => candidate.Line.Group is not null);
        Assert.Equal("no sound", line.What);
        Assert.Equal("orchestration", line.Name);
        Assert.Equal("finished, announced before", line.Detail);
        Assert.Equal(GroupKeys.ForRoster("orchestration"), line.Line.Group);
    }

    /// <summary>
    /// <strong>A roster of one announced and one working session:</strong> when the working one finishes, the
    /// group plays "finished" once, and the group's reminder runs as before.
    /// </summary>
    [Fact]
    public async Task A_working_member_is_the_one_sound_the_roster_owes()
    {
        await Start(RosterBook.Empty);

        Publish(Prompt("s-1", "Coder", "p-1", At));
        Publish(Prompt("s-2", "Reviewer", "p-2", At));
        Publish(Finished("s-1", "p-1", At.AddSeconds(60)));
        Assert.True(await Until(() => Rows(DecisionKind.NoticePlayed).Count == 1), Seen());

        _clock.Now = At.AddMinutes(2);
        _rosters.Replace(Orchestration);
        Assert.True(await Until(() => _consumer.RosterEditCount == 1), Seen());
        Assert.Equal(0, _consumer.SettledCount);

        _clock.Now = At.AddMinutes(3);
        Publish(Finished("s-2", "p-2", At.AddMinutes(3)));
        _clock.Now = At.AddMinutes(3).AddSeconds(2);

        Assert.True(await Until(() => GroupNotices("notice") == 1), Seen());
        Assert.Equal(0, GroupSilences());
        Assert.Single(Rows(DecisionKind.NoticePlayed));

        // The group's reminder, five minutes after its settle.
        _clock.Now = At.AddMinutes(9);
        Assert.True(await Until(() => GroupNotices("nudge") == 1), Seen());
        Assert.Equal(1, GroupNotices("notice"));
    }

    /// <summary><strong>A roster that exists before its members finish</strong> plays one "finished" for the group.</summary>
    [Fact]
    public async Task A_roster_that_exists_before_its_members_finish_plays_once()
    {
        await Start(Orchestration);

        Publish(Prompt("s-1", "Coder", "p-1", At));
        Publish(Prompt("s-2", "Reviewer", "p-2", At));
        Publish(Finished("s-1", "p-1", At.AddSeconds(60)));
        _clock.Now = At.AddMinutes(2);
        Publish(Finished("s-2", "p-2", At.AddMinutes(2)));
        _clock.Now = At.AddMinutes(2).AddSeconds(2);

        Assert.True(await Until(() => GroupNotices("notice") == 1), Seen());
        Assert.Empty(Rows(DecisionKind.NoticePlayed));
        Assert.Equal(0, GroupSilences());
        Assert.Equal(1, Finishes());
    }

    /// <summary>
    /// <strong>A roster renamed after its group announced</strong> is a new group of members already announced:
    /// no sound.
    /// </summary>
    [Fact]
    public async Task A_roster_renamed_after_its_group_announced_plays_nothing()
    {
        await Start(Orchestration);

        Publish(Prompt("s-1", "Coder", "p-1", At));
        Publish(Prompt("s-2", "Reviewer", "p-2", At));
        Publish(Finished("s-1", "p-1", At.AddSeconds(60)));
        _clock.Now = At.AddMinutes(2);
        Publish(Finished("s-2", "p-2", At.AddMinutes(2)));
        _clock.Now = At.AddMinutes(2).AddSeconds(2);
        Assert.True(await Until(() => GroupNotices("notice") == 1), Seen());

        _clock.Now = At.AddMinutes(3);
        _rosters.Replace(RosterBook.From([("pair", ["Coder", "Reviewer"])]));

        Assert.True(await Until(() => _consumer.SettledCount == 2 && GroupSilences() == 1), Seen());
        Assert.Equal(1, GroupNotices("notice"));
        Assert.Equal(1, Finishes());
    }

    /// <summary>
    /// <strong>A roster made inside the settle window of a member's own finish:</strong> the group waits out the
    /// window, settles, and plays no second sound.
    /// </summary>
    [Fact]
    public async Task A_roster_made_inside_the_settle_window_of_a_members_own_finish_plays_nothing()
    {
        await Start(RosterBook.Empty);

        Publish(Prompt("s-1", "Coder", "p-1", At));
        Publish(Prompt("s-2", "Reviewer", "p-2", At));
        Publish(Finished("s-1", "p-1", At.AddSeconds(60)));
        _clock.Now = At.AddSeconds(120);
        Publish(Finished("s-2", "p-2", At.AddSeconds(120)));
        Assert.True(await Until(() => Finishes() == 2), Seen());

        // Half a second after s-2's own finish: the window (1.5 s) is still open.
        _clock.Now = At.AddSeconds(120.5);
        _rosters.Replace(Orchestration);
        Assert.True(await Until(() => _consumer.RosterEditCount == 1), Seen());
        await Task.Delay(100);
        Assert.Equal(0, _consumer.SettledCount);

        _clock.Now = At.AddSeconds(122);
        Assert.True(await Until(() => _consumer.SettledCount == 1 && GroupSilences() == 1), Seen());
        Assert.Equal(2, Finishes());
    }

    /// <summary>
    /// <strong>A finished session whose name changes to a roster member's name</strong> joins the roster as a
    /// new group, already announced: no sound for the group.
    /// </summary>
    [Fact]
    public async Task A_finished_session_renamed_into_a_roster_plays_nothing()
    {
        await Start(Orchestration);

        Publish(Prompt("s-1", "Solo", "p-1", At));
        Publish(Finished("s-1", "p-1", At.AddSeconds(60)));
        Assert.True(await Until(() => Rows(DecisionKind.NoticePlayed).Count == 1), Seen());

        _clock.Now = At.AddMinutes(2);
        Publish(new Notification
        {
            SessionId = new SessionId("s-1"),
            Timestamp = At.AddMinutes(2),
            Cwd = @"C:\w",
            NotificationType = "idle_prompt",
            SessionTitle = "Coder",
        });

        Assert.True(await Until(() => _consumer.SettledCount == 1 && GroupSilences() == 1), Seen());
        Assert.Equal("Coder", _registry.Sessions[new SessionId("s-1")].Title);
        Assert.Equal(SessionState.Unread, _registry.Sessions[new SessionId("s-1")].State);
        Assert.Equal(1, Finishes());
        Assert.Empty(Rows(DecisionKind.GroupNoticePlayed));
    }

    /// <summary>
    /// <strong>A session whose own "finished" was held back by a mute</strong> has announced: put in a roster
    /// afterwards, it plays no sound.
    /// </summary>
    [Fact]
    public async Task A_finish_held_back_by_a_mute_counts_as_announced()
    {
        await Start(RosterBook.Empty);

        Publish(new SoundCommand { SessionId = default, Timestamp = At, Cwd = string.Empty, Kind = SoundCommandKind.MuteAll });
        Publish(Prompt("s-1", "Coder", "p-1", At));
        Publish(Prompt("s-2", "Reviewer", "p-2", At));
        Publish(Finished("s-1", "p-1", At.AddSeconds(60)));
        Publish(Finished("s-2", "p-2", At.AddSeconds(120)));
        Assert.True(await Until(() => Rows(DecisionKind.NoticeSuppressed).Count(row => row.Reason == nameof(SuppressionReason.AllMuted)) == 2), Seen());

        Publish(new SoundCommand { SessionId = default, Timestamp = At.AddMinutes(2), Cwd = string.Empty, Kind = SoundCommandKind.UnmuteAll });
        _clock.Now = At.AddMinutes(3);
        _rosters.Replace(Orchestration);

        Assert.True(await Until(() => _consumer.SettledCount == 1 && GroupSilences() == 1), Seen());
        Assert.Equal(0, Finishes());
        Assert.Empty(Rows(DecisionKind.GroupNoticePlayed));
    }

    /// <summary>
    /// <strong>Removing the working member from a roster whose other members finished inside it</strong> plays
    /// "finished" once: the roster held their sound, so their finish was never announced (the director's
    /// reading, which stays).
    /// </summary>
    [Fact]
    public async Task Removing_the_working_member_plays_once_for_members_that_finished_inside()
    {
        await Start(RosterBook.From([("trio", ["Coder", "Reviewer", "Director"])]));

        Publish(Prompt("s-1", "Coder", "p-1", At));
        Publish(Prompt("s-2", "Reviewer", "p-2", At));
        Publish(Prompt("s-3", "Director", "p-3", At));
        Publish(Finished("s-1", "p-1", At.AddSeconds(60)));
        Publish(Finished("s-2", "p-2", At.AddSeconds(120)));
        Assert.True(await Until(() => Rows(DecisionKind.NoticeSuppressed).Count(row => row.Reason == nameof(SuppressionReason.GroupDone)) == 2), Seen());

        _clock.Now = At.AddMinutes(3);
        _rosters.Replace(RosterBook.From([("trio", ["Coder", "Reviewer"])]));

        Assert.True(await Until(() => _consumer.SettledCount == 1 && GroupNotices("notice") == 1), Seen());
        Assert.Equal(0, GroupSilences());
        Assert.Equal(1, Finishes());
    }

    // ---- The fixture ---------------------------------------------------------------------------

    private Task Start(RosterBook book)
    {
        // The roster store publishes into the pipeline, so an edit wakes the consumer as in the product.
        _rosters = new RosterStore(_pipeline.Sink, book, _clock);

        var recorder = new DecisionRecorder(_registry, _rosters, _archive, Logger.None);

        // The Activity window's log, told by the recorder as AppHost wires it (T1.70).
        _activity = new ActivityLog(_ui, _clock);
        recorder.Decided = _activity.Decided;
        var engine = new SoundPolicyEngine(_player, _clock, _guard, new SoundPolicyOptions(), recorder);
        engine.SoundMarked += (_, e) => _marks.Enqueue(e);

        // As AppHost wires it: the engine hears each change with the session's effective group.
        _registry.SessionChanged += (_, e) => engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, _rosters.Book));

        _consumer = new EventConsumer(
            _pipeline,
            _registry,
            engine,
            _clock,
            _guard,
            Logger.None,
            new RecordingUiTick(),
            _archive,
            _rosters,
            recorder: recorder,
            tickInterval: TimeSpan.FromMilliseconds(20));

        return _consumer.StartAsync(CancellationToken.None);
    }

    /// <summary>Every decision recorded so far.</summary>
    private List<Decision> Decisions()
    {
        lock (_decisions)
        {
            while (_archive.Reader.TryRead(out var record))
            {
                _decisions.AddRange(record.Decisions);
            }

            return [.. _decisions];
        }
    }

    private List<Decision> Rows(DecisionKind kind) => [.. Decisions().Where(row => row.Kind == kind)];

    /// <summary>The group's notices that played: <c>notice</c> for the settle, <c>nudge</c> for the reminder.</summary>
    private int GroupNotices(string reason) => Rows(DecisionKind.GroupNoticePlayed).Count(row => row.Reason == reason);

    /// <summary>The group's silent settles: a suppressed group notice, already announced.</summary>
    private int GroupSilences() =>
        Rows(DecisionKind.NoticeSuppressed).Count(row =>
            row.SessionId is null
            && row.Reason == nameof(SuppressionReason.AlreadyAnnounced)
            // Since T1.73 the detail goes on with the group and its members.
            && row.Detail is { } detail
            && detail.StartsWith($"kind={SoundDecisionKind.GroupNotice} sound={SoundId.Finished} group=", StringComparison.Ordinal));

    /// <summary>How many "finished" sounds the player queued, notices and reminders alike.</summary>
    private int Finishes() => _player.PlayedOf(SoundId.Finished).Count;

    /// <summary>What happened, for a failure message: identifiers and counts only.</summary>
    private string Seen() =>
        string.Join(" | ", Decisions()
            .Where(row => row.Kind is DecisionKind.NoticePlayed or DecisionKind.NudgePlayed or DecisionKind.GroupNoticePlayed or DecisionKind.NoticeSuppressed)
            .Select(row => $"{row.Kind}:{row.SessionId}:{row.Reason}"))
        + $" | settled={_consumer.SettledCount} finishes={Finishes()} marks={_marks.Count}";

    private void Publish(InboundEvent inboundEvent) => Assert.True(_pipeline.Sink.TryPublish(inboundEvent));

    private static async Task<bool> Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(5);
        }

        return condition();
    }

    private static UserPromptSubmit Prompt(string id, string title, string promptId, DateTimeOffset at) => new()
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = @"C:\w",
        PromptId = promptId,
        Prompt = "run the tests",
        SessionTitle = title,
    };

    private static Stop Finished(string id, string promptId, DateTimeOffset at) => new()
    {
        SessionId = new SessionId(id),
        Timestamp = at,
        Cwd = @"C:\w",
        PromptId = promptId,
        LastAssistantMessage = "29 passed",
    };
}
