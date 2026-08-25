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
public sealed class SoundPolicyEngine : ISoundModeReader
{
    private readonly ISoundPlayer _player;
    private readonly IClock _clock;
    private readonly SoundPolicyOptions _options;
    private readonly Dictionary<SessionId, Tracked> _tracked = [];
    private readonly HashSet<SessionId> _mutedSessions = [];
    private readonly HashSet<GroupKey> _mutedGroups = [];
    private readonly SingleWriterGuard _guard;

    /// <summary>
    /// When the global mute lapses, in ticks: <c>0</c> when nothing is globally muted, and
    /// <see cref="DateTimeOffset.MaxValue"/>'s ticks for a mute with no expiry (Impl §5.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A predicate, not a timer.</strong> "Muted until T" is evaluated where a sound
    /// would be emitted, and nothing is scheduled to re-enable it — the same ruling as the
    /// Ended-removal sweep. An armed timer fires against state that has since changed, and a
    /// timer that unmutes exactly when a nudge falls due is a beep out of nowhere. The cost is
    /// that a lapse produces no event, which is why the host recomputes its tooltip on the tick
    /// rather than only on change.
    /// </para>
    /// <para>
    /// <strong>Why a <see cref="long"/> and why volatile.</strong> These two are the only engine
    /// state read from outside the consumer thread: the tray renders the mute and pause modes
    /// into its tooltip on the UI thread. A <see cref="DateTimeOffset"/> is wider than a word
    /// and could tear; a <see cref="long"/> cannot, and <see cref="Volatile"/> makes the write
    /// visible. Writes still happen only inside the single-writer region, so this adds a safe
    /// reader rather than a second writer.
    /// </para>
    /// </remarks>
    private long _allMutedUntilTicks;

    private volatile bool _monitoringPaused;

    /// <summary>Builds an engine that plays through <paramref name="player"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> or <paramref name="clock"/> is null.</exception>
    /// <exception cref="ArgumentException">The options describe a policy TS §IV.5 forbids.</exception>
    public SoundPolicyEngine(
        ISoundPlayer player,
        IClock clock,
        SingleWriterGuard guard,
        SoundPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(options);

        _player = player;
        _clock = clock;
        _options = options;
        _options.Validate();
        _guard = guard;
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

        using var writing = _guard.Enter("recording a session change in the sound engine");

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
        // Enters the single-writer region because it ENUMERATES _tracked, not merely because it
        // writes to the entries it finds. The distinction decides the method's future: what the
        // T1.5 review actually reproduced was this enumeration being invalidated by
        // OnSessionChanged inserting into the same dictionary — a structural modification during
        // a walk, not two writes colliding. So if this were ever rewritten to stop touching the
        // entries — returning the due nudges for the caller to act on, say — it would still have
        // to enter the region, and a reviewer who classified it as a query on the grounds that
        // it no longer writes would reopen exactly the race this closed.
        using var writing = _guard.Enter("evaluating the nudge schedule");

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
    public void SetSessionMuted(SessionId session, bool muted)
    {
        // T1.13's tray menu is the caller that will reach for this from the Dispatcher.
        using var writing = _guard.Enter("muting or unmuting a session");
        Set(_mutedSessions, session, muted);
    }

    /// <summary>Mutes or unmutes a whole group (TS §IV.5).</summary>
    public void SetGroupMuted(GroupKey group, bool muted)
    {
        using var writing = _guard.Enter("muting or unmuting a group");
        Set(_mutedGroups, group, muted);
    }

    /// <summary>
    /// Silences every session until <paramref name="until"/>, or indefinitely when it is null
    /// (Impl §5.2, "Mute all"). Passing <see langword="false"/> unmutes at once.
    /// </summary>
    /// <remarks>
    /// The volume knob. Sound stops; the glyph goes on telling the truth, so an operator who
    /// silenced the room can still glance at a burning red icon and know. Contrast
    /// <see cref="SetMonitoringPaused"/>.
    /// </remarks>
    /// <param name="muted">Whether everything is silenced.</param>
    /// <param name="until">
    /// When the mute lapses. Null mutes with no expiry. Ignored when <paramref name="muted"/>
    /// is false.
    /// </param>
    public void SetAllMuted(bool muted, DateTimeOffset? until = null)
    {
        using var writing = _guard.Enter("muting or unmuting everything");

        Volatile.Write(
            ref _allMutedUntilTicks,
            muted ? (until ?? DateTimeOffset.MaxValue).UtcTicks : 0L);
    }

    /// <summary>
    /// Goes off duty: silences everything, with no expiry, until the operator resumes
    /// (Impl §5.2, "Pause monitoring").
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="SetAllMuted"/> in what the operator sees, not in what they
    /// hear: the host greys the tray glyph while this is set, which is the one deliberate
    /// exception to "the tray tells the truth" (Design §9) — they turned it off on purpose,
    /// from that menu, this second. The engine's part is only the silence; the glyph is the
    /// host's, which is why this is a separate flag rather than a mute with a longer expiry.
    /// </remarks>
    /// <param name="paused">Whether monitoring is off duty.</param>
    public void SetMonitoringPaused(bool paused)
    {
        using var writing = _guard.Enter("pausing or resuming monitoring");

        _monitoringPaused = paused;
    }

    /// <inheritdoc/>
    public bool IsMonitoringPaused => _monitoringPaused;

    /// <summary>
    /// When the global mute lapses; null when nothing is globally muted, and
    /// <see cref="DateTimeOffset.MaxValue"/> when it has no expiry. Safe to read from any thread.
    /// </summary>
    public DateTimeOffset? AllMutedUntil
    {
        get
        {
            var ticks = Volatile.Read(ref _allMutedUntilTicks);

            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Whether everything is silenced at <paramref name="now"/> — by pause, or by a global mute
    /// that has not yet lapsed. Safe to read from any thread.
    /// </summary>
    /// <remarks>
    /// This is the predicate the lapse is evaluated by. A mute set to expire in thirty minutes
    /// simply stops being true; nothing fires, nothing is scheduled, and nothing needs undoing.
    /// </remarks>
    /// <param name="now">The instant to judge.</param>
    public bool IsSilenced(DateTimeOffset now)
    {
        if (_monitoringPaused)
        {
            return true;
        }

        var ticks = Volatile.Read(ref _allMutedUntilTicks);

        return ticks != 0 && now.UtcTicks < ticks;
    }

    /// <summary>Whether anything would be heard for this session right now.</summary>
    /// <remarks>
    /// Per-session and per-group mute only. The global modes are time-dependent and are asked
    /// separately, through <see cref="IsSilenced"/>, so that a caller holding an instant judges
    /// against that instant rather than against the clock's idea of "now" a moment later.
    /// </remarks>
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
        // Global mute and pause fold in here, exactly as this method's remarks anticipated: one
        // more clause at the point of emission, and no change to scheduling. The ladder goes on
        // advancing silently, so resuming picks up the natural cadence instead of releasing a
        // backlog of reminders the operator asked not to hear — which matters most for pause,
        // which has no expiry and can span hours.
        if (IsSilenced(_clock.Now) || IsMuted(session, group))
        {
            return;
        }

        // Master volume is folded in here and nowhere else (Impl Part 7). The adapter receives a
        // finished number; it does not know there is such a thing as a master volume, which is
        // what keeps "how loud is this" answerable by reading one method.
        _player.Play(sound, gain * _options.MasterVolume, fade);
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
