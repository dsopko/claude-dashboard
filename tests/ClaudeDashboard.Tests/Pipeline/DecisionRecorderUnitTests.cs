using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Pipeline;

/// <summary>
/// The recorder's rows that the pipeline harness cannot reach: an <c>Apply</c> that throws, and
/// the roster group's own notice (T1.37).
/// </summary>
/// <remarks>
/// <c>DecisionRecordTests</c> drives every other kind through the real consumer. Nothing in that
/// harness can make the real Registry throw, and settling a roster group takes the settle
/// window's worth of machinery — so these two are asserted against the recorder directly, which
/// is the same object the consumer calls.
/// </remarks>
public sealed class DecisionRecorderUnitTests
{
    private const string Marker = "zqx-exception-message-marker-3t8";
    private const string Cwd = @"C:\projects\dashboard";

    private readonly SessionRegistry _registry = new(new SingleWriterGuard());
    private readonly RosterStore _rosters = new(new RecordingEventSink());
    private readonly EventArchive _archive = new(Logger.None);
    private readonly DecisionRecorder _recorder;

    public DecisionRecorderUnitTests() =>
        _recorder = new DecisionRecorder(_registry, _rosters, _archive, Logger.None);

    /// <summary>An Apply that throws is recorded by exception TYPE — never the message.</summary>
    /// <remarks>
    /// The consumer's own wiring: <c>BeginEvent</c>, the catch around <c>Apply</c> calling
    /// <see cref="DecisionRecorder.ApplyFailed"/>, and <c>Complete</c> in the finally. The event
    /// itself still rides the record — a row that failed to apply is exactly one the operator
    /// will want to read back. The message is refused because exception text can quote whatever
    /// the code interpolated into it, which is how operator text leaks sideways (T1.24).
    /// </remarks>
    [Fact]
    public void An_apply_that_throws_is_recorded_by_type_and_never_message()
    {
        var prompt = new UserPromptSubmit
        {
            SessionId = new SessionId("s-1"),
            Timestamp = FakeClock.DefaultStart,
            Cwd = Cwd,
            Prompt = "go",
            Payload = new PayloadJson("""{"raw":true}"""),
        };

        _recorder.BeginEvent(prompt);
        _recorder.ApplyFailed(prompt, new InvalidOperationException(Marker));
        _recorder.Complete();

        Assert.True(_archive.Reader.TryRead(out var record));
        Assert.Same(prompt, record.Event);

        var failed = Assert.Single(record.Decisions);
        Assert.Equal(DecisionKind.ApplyFailed, failed.Kind);
        Assert.Equal("s-1", failed.SessionId);
        Assert.Equal(nameof(InvalidOperationException), failed.Reason);
        Assert.DoesNotContain(Marker, failed.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(failed.Detail);
    }

    /// <summary>The group's one notice carries the group key and its member ids — never titles.</summary>
    /// <remarks>
    /// The engine knows a roster group only by key; the member ids come from the Registry, read
    /// on the consumer thread at the moment of the notice, sorted so the row is stable.
    /// </remarks>
    [Fact]
    public void A_group_notice_names_the_group_and_its_members_by_id()
    {
        Apply("s-b");
        Apply("s-a");

        var group = _registry.Sessions[new SessionId("s-a")].WorkspaceGroup;

        _recorder.BeginTick(FakeClock.DefaultStart);
        ((IDecisionSink)_recorder).SoundPlayed(
            SoundDecisionKind.GroupNotice, default, group, SoundId.Finished, rung: 0, waited: TimeSpan.Zero);
        _recorder.Complete();

        Assert.True(_archive.Reader.TryRead(out var record));
        Assert.Null(record.Event);

        var notice = Assert.Single(record.Decisions);
        Assert.Equal(DecisionKind.GroupNoticePlayed, notice.Kind);
        Assert.Null(notice.SessionId);
        Assert.Equal("notice", notice.Reason);
        Assert.Contains($"group={group.Value}", notice.Detail, StringComparison.Ordinal);
        Assert.Contains("members=s-a,s-b", notice.Detail, StringComparison.Ordinal);
    }

    /// <summary>And the group nudge is the same row, saying it was the nudge.</summary>
    [Fact]
    public void A_group_nudge_is_the_group_row_marked_nudge()
    {
        Apply("s-1");

        var group = _registry.Sessions[new SessionId("s-1")].WorkspaceGroup;

        _recorder.BeginTick(FakeClock.DefaultStart);
        ((IDecisionSink)_recorder).SoundPlayed(
            SoundDecisionKind.GroupNudge, default, group, SoundId.Finished, rung: 0, waited: TimeSpan.Zero);
        _recorder.Complete();

        Assert.True(_archive.Reader.TryRead(out var record));

        var nudge = Assert.Single(record.Decisions);
        Assert.Equal(DecisionKind.GroupNoticePlayed, nudge.Kind);
        Assert.Equal("nudge", nudge.Reason);
    }

    /// <summary>
    /// A dropped nudge is a <see cref="DecisionKind.SoundDropped"/> row with the reason, and the
    /// identifiers the played row would have carried: the kind, the sound and the rung (T1.55).
    /// </summary>
    [Fact]
    public void A_dropped_nudge_is_a_dropped_row_with_its_rung()
    {
        Apply("s-1");

        _recorder.BeginTick(FakeClock.DefaultStart);
        ((IDecisionSink)_recorder).SoundDropped(
            SoundDecisionKind.Nudge, new SessionId("s-1"), default, SoundId.Permission,
            rung: 2, waited: TimeSpan.FromMinutes(17), SoundOutcome.NoOutput);
        _recorder.Complete();

        Assert.True(_archive.Reader.TryRead(out var record));

        var dropped = Assert.Single(record.Decisions);
        Assert.Equal(DecisionKind.SoundDropped, dropped.Kind);
        Assert.Equal("s-1", dropped.SessionId);
        Assert.Equal(nameof(SoundOutcome.NoOutput), dropped.Reason);
        Assert.Equal("kind=Nudge sound=permission rung=2 waitedMinutes=17", dropped.Detail);
    }

    /// <summary>
    /// <strong>A held-back group sound names the group and its members by id</strong>, as a played and a dropped one
    /// do (T1.73, issue #108), for each reason a group's sound is held back. A session's held-back sound keeps the
    /// detail it had.
    /// </summary>
    [Theory]
    [InlineData(SuppressionReason.GroupMuted, SoundDecisionKind.GroupNotice)]
    [InlineData(SuppressionReason.AllMuted, SoundDecisionKind.GroupNudge)]
    [InlineData(SuppressionReason.MonitoringPaused, SoundDecisionKind.GroupNotice)]
    [InlineData(SuppressionReason.AlreadyAnnounced, SoundDecisionKind.GroupNotice)]
    public void A_held_back_group_sound_names_the_group_and_its_members(SuppressionReason reason, SoundDecisionKind kind)
    {
        Apply("s-b");
        Apply("s-a");

        var group = _registry.Sessions[new SessionId("s-a")].WorkspaceGroup;

        _recorder.BeginTick(FakeClock.DefaultStart);
        ((IDecisionSink)_recorder).SoundSuppressed(kind, default, group, SoundId.Finished, reason);
        ((IDecisionSink)_recorder).SoundSuppressed(SoundDecisionKind.Notice, new SessionId("s-a"), group, SoundId.Finished, reason);
        _recorder.Complete();

        Assert.True(_archive.Reader.TryRead(out var record));
        Assert.Equal(2, record.Decisions.Count);

        var held = record.Decisions[0];
        Assert.Equal(DecisionKind.NoticeSuppressed, held.Kind);
        Assert.Null(held.SessionId);
        Assert.Equal(reason.ToString(), held.Reason);
        Assert.Equal($"kind={kind} sound=finished group={group.Value} members=s-a,s-b", held.Detail);

        var own = record.Decisions[1];
        Assert.Equal("s-a", own.SessionId);
        Assert.Equal("kind=Notice sound=finished", own.Detail);
    }

    /// <summary>A dropped group sound names the group and its members by id, as the played row does.</summary>
    [Fact]
    public void A_dropped_group_sound_names_the_group_and_its_members()
    {
        Apply("s-b");
        Apply("s-a");

        var group = _registry.Sessions[new SessionId("s-a")].WorkspaceGroup;

        _recorder.BeginTick(FakeClock.DefaultStart);
        ((IDecisionSink)_recorder).SoundDropped(
            SoundDecisionKind.GroupNotice, default, group, SoundId.Finished,
            rung: 0, waited: TimeSpan.Zero, SoundOutcome.Failed);
        _recorder.Complete();

        Assert.True(_archive.Reader.TryRead(out var record));

        var dropped = Assert.Single(record.Decisions);
        Assert.Equal(DecisionKind.SoundDropped, dropped.Kind);
        Assert.Null(dropped.SessionId);
        Assert.Equal(nameof(SoundOutcome.Failed), dropped.Reason);
        Assert.Equal($"kind=GroupNotice sound=finished group={group.Value} members=s-a,s-b", dropped.Detail);
    }

    /// <summary>
    /// <strong>A decision with no session stores no name and no path</strong> (T1.69): a group's sound and
    /// an hourly summary. A session's own sound in the same tick holds both, from the Registry.
    /// </summary>
    [Fact]
    public void A_group_sound_and_an_hourly_summary_store_no_name_or_path()
    {
        _registry.Apply(new UserPromptSubmit
        {
            SessionId = new SessionId("s-1"),
            Timestamp = FakeClock.DefaultStart,
            Cwd = Cwd,
            Prompt = "go",
            SessionTitle = "Payments API",
        });

        var group = _registry.Sessions[new SessionId("s-1")].WorkspaceGroup;

        _recorder.BeginTick(FakeClock.DefaultStart);
        ((IDecisionSink)_recorder).SoundPlayed(
            SoundDecisionKind.GroupNotice, default, group, SoundId.Finished, rung: 0, waited: TimeSpan.Zero);
        _recorder.HourlySummary("applied=1", partial: false);
        ((IDecisionSink)_recorder).SoundPlayed(
            SoundDecisionKind.Nudge, new SessionId("s-1"), group, SoundId.Permission, rung: 0, waited: TimeSpan.FromMinutes(2));
        _recorder.Complete();

        Assert.True(_archive.Reader.TryRead(out var record));

        var groupRow = Assert.Single(record.Decisions, decision => decision.Kind == DecisionKind.GroupNoticePlayed);
        var summary = Assert.Single(record.Decisions, decision => decision.Kind == DecisionKind.HourlySummary);
        var nudge = Assert.Single(record.Decisions, decision => decision.Kind == DecisionKind.NudgePlayed);

        Assert.Equal<(string?, string?)>((null, null), (groupRow.SessionTitle, groupRow.Cwd));
        Assert.Equal<(string?, string?)>((null, null), (summary.SessionTitle, summary.Cwd));
        Assert.Equal<(string?, string?)>(("Payments API", Cwd), (nudge.SessionTitle, nudge.Cwd));
    }

    private void Apply(string id) =>
        _registry.Apply(new UserPromptSubmit
        {
            SessionId = new SessionId(id),
            Timestamp = FakeClock.DefaultStart,
            Cwd = Cwd,
            Prompt = "go",
        });
}
