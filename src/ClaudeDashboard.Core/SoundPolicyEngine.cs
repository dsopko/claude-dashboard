using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.Core;

/// <summary>
/// Decides when notices and nudges fire (TS §IV.5; Impl §2.4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Vocabulary, because the two words are not interchangeable.</strong> A
/// <em>notice</em> is the first sound for an event and fires on state entry. A <em>nudge</em>
/// is a reminder that the session is still waiting. They are the same sound — the same
/// <see cref="SoundId"/> — differing only in gain and fade, which is why
/// <see cref="SoundId"/> carries no volume.
/// </para>
/// <para>
/// <strong>Intents only.</strong> The engine calls <see cref="ISoundPlayer"/> and nothing
/// else: no audio API, no file, no device. That is what lets it run in tests against a
/// recording player on a machine with no sound hardware.
/// </para>
/// <para>
/// <strong>Nothing here is ranked or banded.</strong> A notice fires because a session entered
/// a state, and a nudge because time passed — neither depends on where the session would sort
/// on screen. TS §IV.2's ordering is a display concern and deliberately plays no part.
/// </para>
/// <para>
/// <strong>Asked, never woken.</strong> The engine schedules nothing and owns no timer: it
/// records when each session's next nudge is <em>due</em>, and <see cref="Evaluate(DateTimeOffset)"/>
/// fires whatever has come due. Deciding when to ask is the host's (T1.9). This is what makes
/// cancellation trivially correct — an acknowledgment clears the due time and the next
/// evaluation finds nothing, so there is no timer to cancel and no race between a firing nudge
/// and an in-flight ack.
/// </para>
/// <para>
/// <strong>Single-threaded, like the Registry.</strong> The same consumer thread that applies
/// events calls <see cref="OnSessionChanged"/> and <see cref="Evaluate(DateTimeOffset)"/>
/// (Impl §4), so this type holds no locks. Do not call it from two threads.
/// </para>
/// <para>
/// <strong>On <see cref="SessionState.Error"/>.</strong> TS §IV.5 says nudges fire for a
/// "<c>NeedsYou.*</c>" session, and TS §IV.1 lists <c>Error</c> as a state <em>beside</em> the
/// two <c>NeedsYou</c> ones — so read literally, an errored session would notice once and then
/// go silent forever. TS §IV.2 as ratified puts <c>Error</c> inside the Needs-You band and
/// describes it as "stopped until looked at", which is precisely the condition nudges exist
/// for. Errors therefore nudge, via <see cref="SoundPolicyOptions.NudgeOnError"/> so the call
/// is visible and reversible rather than buried. Flagged to the director as a gap in §IV.5.
/// </para>
/// </remarks>
public sealed class SoundPolicyEngine
{
    private readonly ISoundPlayer _player;
    private readonly IClock _clock;
    private readonly SoundPolicyOptions _options;
    private readonly Dictionary<SessionId, Tracked> _tracked = [];
    private readonly HashSet<SessionId> _mutedSessions = [];
    private readonly HashSet<GroupKey> _mutedGroups = [];

    /// <summary>Builds an engine that plays through <paramref name="player"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> or <paramref name="clock"/> is null.</exception>
    /// <exception cref="ArgumentException">The options describe a policy TS §IV.5 forbids.</exception>
    public SoundPolicyEngine(ISoundPlayer player, IClock clock, SoundPolicyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(clock);

        _player = player;
        _clock = clock;
        _options = options ?? new SoundPolicyOptions();
        _options.Validate();
    }

    /// <summary>
    /// Tells the engine a session changed: plays a notice if it just entered a sounding state,
    /// and starts, restarts or cancels its nudge schedule.
    /// </summary>
    /// <remarks>
    /// Wire this to the Registry's change notification (T1.9). Safe to call repeatedly for an
    /// unchanged session: a change that did not move the session to a new state — a directory
    /// move, a new error kind on an already-errored session — plays nothing and leaves the
    /// ladder where it is. Entry is detected from <see cref="Session.EnteredAt"/>, which T1.2
    /// advances only on a real state change.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    public void OnSessionChanged(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.State == SessionState.Ended)
        {
            _tracked.Remove(session.Id);
            return;
        }

        var known = _tracked.TryGetValue(session.Id, out var existing);
        var entered = !known || existing!.State != session.State || existing.EnteredAt != session.EnteredAt;

        if (!entered)
        {
            // Same state, same entry: nothing sounded, and the ladder must not restart. Only
            // the group can have moved, and that changes which mute applies.
            existing!.Group = session.Group;
            return;
        }

        var tracked = new Tracked
        {
            State = session.State,
            EnteredAt = session.EnteredAt,
            Group = session.Group,
            Step = 0,
            NextNudgeAt = FirstNudgeAt(session),
        };

        _tracked[session.Id] = tracked;

        if (NoticeFor(session.State) is { } sound)
        {
            Play(session.Id, tracked.Group, sound, _options.NoticeGain, TimeSpan.Zero);
        }
    }

    /// <summary>Fires whatever nudges have come due, using the engine's clock.</summary>
    public void Evaluate() => Evaluate(_clock.Now);

    /// <summary>
    /// Fires whatever nudges have come due as of <paramref name="now"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host calls this on a tick (T1.9); the engine decides what happens. Safe to call at
    /// any cadence and at any time: calling it more often than nudges are due does nothing,
    /// and calling it late does <strong>not</strong> replay the nudges that were missed.
    /// </para>
    /// <para>
    /// At most one nudge per session per call, and the next one is scheduled from
    /// <paramref name="now"/> rather than from when this one was due. If the host is blocked
    /// for twenty minutes, a session does not then receive three nudges in a burst — that
    /// would be "faster", which TS §IV.5 forbids outright.
    /// </para>
    /// </remarks>
    public void Evaluate(DateTimeOffset now)
    {
        foreach (var (id, tracked) in _tracked)
        {
            if (tracked.NextNudgeAt is not { } due || due > now)
            {
                continue;
            }

            if (NoticeFor(tracked.State) is { } sound)
            {
                Play(id, tracked.Group, sound, _options.NudgeGain, _options.NudgeFadeIn);
            }

            if (tracked.State == SessionState.Unread)
            {
                // TS §IV.5: an unread result gets at most one soft nudge.
                tracked.NextNudgeAt = null;
                continue;
            }

            tracked.Step++;
            tracked.NextNudgeAt = now + IntervalAt(tracked.Step);
        }
    }

    /// <summary>Mutes or unmutes one session (TS §IV.5).</summary>
    public void SetSessionMuted(SessionId session, bool muted) =>
        Set(_mutedSessions, session, muted);

    /// <summary>Mutes or unmutes a whole group (TS §IV.5).</summary>
    public void SetGroupMuted(GroupKey group, bool muted) =>
        Set(_mutedGroups, group, muted);

    /// <summary>Whether anything would be heard for this session right now.</summary>
    public bool IsMuted(SessionId session, GroupKey group) =>
        _mutedSessions.Contains(session) || _mutedGroups.Contains(group);

    /// <summary>
    /// When this session's next nudge is due, or null if none is scheduled. Exposed so a host
    /// or a test can see the schedule without waiting for it to fire.
    /// </summary>
    public DateTimeOffset? NextNudgeAt(SessionId session) =>
        _tracked.TryGetValue(session, out var tracked) ? tracked.NextNudgeAt : null;

    /// <summary>
    /// Emits an intent unless the session is muted.
    /// </summary>
    /// <remarks>
    /// Mute is a filter on the <em>output</em>, deliberately, not a freeze on the schedule: a
    /// muted session goes on advancing its ladder silently, so unmuting resumes at the natural
    /// cadence instead of releasing a backlog of reminders the operator asked not to hear.
    /// Being a single predicate at the point of emission is also what lets T1.13's global,
    /// time-boxed "mute all for 30 minutes" drop in as one more clause here, with no change to
    /// scheduling.
    /// </remarks>
    private void Play(SessionId session, GroupKey group, SoundId sound, double gain, TimeSpan fade)
    {
        if (IsMuted(session, group))
        {
            return;
        }

        _player.Play(sound, gain, fade);
    }

    /// <summary>The sound a state announces itself with, or null if it announces nothing.</summary>
    private static SoundId? NoticeFor(SessionState state) => state switch
    {
        SessionState.Unread => SoundId.Finished,
        SessionState.NeedsPermission => SoundId.Permission,
        SessionState.NeedsQuestion => SoundId.Question,
        SessionState.Error => SoundId.Error,
        _ => null,
    };

    /// <summary>When this session's first nudge falls due, or null if it is not nudge-eligible.</summary>
    private DateTimeOffset? FirstNudgeAt(Session session) => session.State switch
    {
        SessionState.NeedsPermission or SessionState.NeedsQuestion =>
            session.EnteredAt + IntervalAt(0),

        SessionState.Error when _options.NudgeOnError =>
            session.EnteredAt + IntervalAt(0),

        SessionState.Unread when _options.UnreadNudgeAfter is { } after =>
            session.EnteredAt + after,

        _ => null,
    };

    /// <summary>The gap before the nudge at <paramref name="step"/>; the last interval repeats.</summary>
    private TimeSpan IntervalAt(int step) =>
        _options.NudgeLadder[Math.Min(step, _options.NudgeLadder.Count - 1)];

    private static void Set<T>(HashSet<T> set, T value, bool present)
    {
        if (present)
        {
            set.Add(value);
        }
        else
        {
            set.Remove(value);
        }
    }

    /// <summary>What the engine remembers about one session.</summary>
    private sealed class Tracked
    {
        public required SessionState State { get; init; }

        public required DateTimeOffset EnteredAt { get; init; }

        public required GroupKey Group { get; set; }

        public required int Step { get; set; }

        public required DateTimeOffset? NextNudgeAt { get; set; }
    }
}
