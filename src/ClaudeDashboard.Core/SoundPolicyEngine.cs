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

    // The decisions record (T1.37, issue #48). An intent port beside the player: the engine
    // says what it decided — played, or suppressed and why — at the moment it decides, because
    // the reasons live in this type's private state and cross no other boundary. Observation
    // only: nothing the sink hears changes a decision, and the default is the null sink, which
    // is this engine exactly as it was.
    private readonly IDecisionSink _sink;
    private readonly IClock _clock;
    private readonly SoundPolicyOptions _options;
    private readonly Dictionary<SessionId, Tracked> _tracked = [];
    private readonly HashSet<SessionId> _mutedSessions = [];
    private readonly HashSet<GroupKey> _mutedGroups = [];
    private readonly Dictionary<GroupKey, TrackedGroup> _groups = [];

    /// <summary>
    /// The entry each session's latest transition replaced, so an entry that comes back unchanged is
    /// recognised as one already announced (T1.44). One per session; dropped when it ends.
    /// </summary>
    private readonly Dictionary<SessionId, Tracked> _replaced = [];

    /// <summary>
    /// The settle each roster group's latest unsettle removed, for the same reason (T1.44).
    /// </summary>
    private readonly Dictionary<GroupKey, TrackedGroup> _unsettled = [];

    /// <summary>
    /// The session a group notice is attributed to: none, because a group notice is the group's.
    /// </summary>
    /// <remarks>
    /// This is what makes the mute rules come out right without a second predicate. Muting the
    /// roster group silences its done chime, because <see cref="IsMuted"/> checks the group key;
    /// muting one member does not, because the notice does not belong to that member. A caller
    /// cannot reach this id — <see cref="SessionId"/> is empty only for <c>default</c>, which no
    /// session can hold.
    /// </remarks>
    private static SessionId GroupNotice => default;
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
        SoundPolicyOptions options,
        IDecisionSink? sink = null)
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

        // Optional so no existing caller changes, and null-object so no call site checks.
        _sink = sink ?? NullDecisionSink.Instance;
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
    /// <param name="session">The session that changed.</param>
    /// <param name="effectiveGroup">
    /// The group the session is ACTUALLY in — <see cref="GroupKeys.Effective"/>, not
    /// <see cref="Session.WorkspaceGroup"/>, because an operator roster overrides it (issue #16).
    /// <para>
    /// <strong>Required rather than defaulted, deliberately.</strong> A parameter that could be
    /// omitted would silently give every caller workspace behaviour: group mute would apply to the
    /// wrong key and a roster member would sound its own finished chime, both of them wrong in a
    /// way no test that forgot to pass it would notice. Making it required means the compiler finds
    /// every call site instead.
    /// </para>
    /// <para>
    /// This is also all the engine ever learns about rosters: it reads
    /// <see cref="GroupKeys.KindOf"/> on the key and needs no roster book of its own.
    /// </para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    public void OnSessionChanged(Session session, GroupKey effectiveGroup)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var writing = _guard.Enter("recording a session change in the sound engine");

        if (session.State == SessionState.Ended)
        {
            _tracked.Remove(session.Id);
            _replaced.Remove(session.Id);
            return;
        }

        var known = _tracked.TryGetValue(session.Id, out var existing);
        var entered = !known || existing!.State != session.State || existing.EnteredAt != session.EnteredAt;

        if (!entered)
        {
            // Same state, same entry: nothing sounded, and the ladder must not restart. Only
            // the group can have moved, and that changes which mute applies.
            existing!.Group = effectiveGroup;
            return;
        }

        // AN ENTRY ALREADY ANNOUNCED, BACK UNCHANGED (T1.44, issue #56). A quiet tick of the
        // session's own scheduled job puts the row back as it was — the same state, and the same
        // instant it entered that state, which no real transition ever reuses. That is the entry
        // this engine already announced, so it is not announced again, and its nudge ladder is
        // restored where it was rather than restarted. Recorded as a suppression, so the decisions
        // record says why a Finished state made no sound.
        if (_replaced.TryGetValue(session.Id, out var prior)
            && prior.State == session.State
            && prior.EnteredAt == session.EnteredAt)
        {
            prior.Group = effectiveGroup;
            _tracked[session.Id] = prior;
            _replaced.Remove(session.Id);

            if (NoticeFor(session.State) is { } again)
            {
                _sink.SoundSuppressed(
                    SoundDecisionKind.Notice, session.Id, effectiveGroup, again, SuppressionReason.AlreadyAnnounced);
            }

            return;
        }

        if (known)
        {
            _replaced[session.Id] = existing!;
        }
        else
        {
            _replaced.Remove(session.Id);
        }

        var ownNotice = NoticeFor(session.State) is not null && !IsGroupDone(session.State, effectiveGroup);

        var tracked = new Tracked
        {
            State = session.State,
            EnteredAt = session.EnteredAt,
            Group = effectiveGroup,
            Step = 0,
            NextNudgeAt = FirstNudgeAt(session, effectiveGroup),

            // T1.72: the session's own notice is made below, whatever becomes of it (played, held back by a
            // mute or a pause, or dropped), so this entry's finish is announced. A notice given to the roster
            // group instead is not, until the group announces.
            Announced = ownNotice,
        };

        _tracked[session.Id] = tracked;

        if (ownNotice && NoticeFor(session.State) is { } sound)
        {
            Play(session.Id, tracked.Group, sound, _options.NoticeGain, TimeSpan.Zero,
                SoundDecisionKind.Notice, rung: 0, waited: TimeSpan.Zero);
        }
        else if (NoticeFor(session.State) is { } suppressed)
        {
            // The member's done notice belongs to its roster group, which will announce once for
            // everyone (issue #16). Suppressed here at the point of emission — and recorded here,
            // because this is the one suppression Play never sees: the call is not made at all.
            _sink.SoundSuppressed(
                SoundDecisionKind.Notice, session.Id, effectiveGroup, suppressed, SuppressionReason.GroupDone);
        }
    }


    /// <summary>
    /// A roster group has settled: every member has been quiet for the settle window, so the
    /// <em>group</em> announces it is done (issue #16).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One notice for the group, not one per member.</strong> The members' own finished
    /// notices were suppressed as they happened — see <see cref="IsGroupDone"/> — so this is the
    /// first and only done sound the group produces, however many members finished and in whatever
    /// order.
    /// </para>
    /// <para>
    /// <strong>Nothing races, because nothing was queued.</strong> A member's finished notice is
    /// suppressed at the point of emission, exactly as mute is, rather than being scheduled and
    /// then cancelled — so there is nothing in flight for this to overtake. Both decisions happen
    /// on the one consumer thread, in order.
    /// </para>
    /// <para>
    /// Calling this again for a group that is already settled changes nothing and re-sounds
    /// nothing: the settle is an edge, and the caller reports it once.
    /// </para>
    /// <para>
    /// <strong>A settle already announced, back unchanged (T1.44).</strong> A quiet tick in a
    /// member unsettles the group and then puts the member back exactly as it was, so the group
    /// settles again with the same <paramref name="quietSince"/> — the latest member's entry
    /// instant, which no real hand-off reuses. That is the settle already announced: restored with
    /// its nudge ladder, and not announced twice. Without the instant the settle is always new.
    /// </para>
    /// </remarks>
    /// <param name="group">The roster group that settled.</param>
    /// <param name="settledAt">When the settle was observed.</param>
    /// <param name="quietSince">When the group went quiet: its latest member's entry instant.</param>
    /// <param name="settledBy">
    /// The member whose change settled the group (<see cref="RosterSettle.SettledBy"/>), whose row the
    /// group's notice and its reminder mark (T1.67). Empty when the caller does not say: no row is marked.
    /// </param>
    /// <param name="unreadMembers">
    /// The group's Unread members as it stands (<see cref="RosterSettle.UnreadMembers"/>), read by the settle
    /// pass (T1.72, issue #107). <strong>The settle is silent when every one of them already announced its
    /// finish:</strong> a roster made from sessions that finished and played their sound has nothing new to say.
    /// Null or empty when the caller does not say, and then the group plays, as before: when in doubt, the sound
    /// plays.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="group"/> names no group.</exception>
    public void OnRosterGroupSettled(
        GroupKey group,
        DateTimeOffset settledAt,
        DateTimeOffset? quietSince = null,
        SessionId settledBy = default,
        IReadOnlyCollection<SessionId>? unreadMembers = null)
    {
        if (group.IsEmpty)
        {
            throw new ArgumentException("A settled group must have a key.", nameof(group));
        }

        using var writing = _guard.Enter("recording a roster group settling in the sound engine");

        if (_groups.ContainsKey(group))
        {
            return;
        }

        if (quietSince is { } since
            && _unsettled.TryGetValue(group, out var prior)
            && prior.QuietSince == since)
        {
            _groups[group] = prior;
            _unsettled.Remove(group);
            _sink.SoundSuppressed(
                SoundDecisionKind.GroupNotice, default, group, SoundId.Finished, SuppressionReason.AlreadyAnnounced);
            return;
        }

        // NOTHING NEW TO ANNOUNCE (T1.72, issue #107). Every Unread member already announced its own finish:
        // a roster made, renamed or joined after the sounds were made. Held as settled, so a later unsettle and
        // settle behave as before; no reminder, because each member keeps its own; no sound, so no speaker sign.
        // Recorded as T1.44 records its own, so the decisions record says why nothing played.
        if (AllAnnounced(unreadMembers))
        {
            _groups[group] = new TrackedGroup
            {
                NextNudgeAt = null,
                QuietSince = quietSince,
                SettledBy = settledBy,
            };

            _sink.SoundSuppressed(
                SoundDecisionKind.GroupNotice, default, group, SoundId.Finished, SuppressionReason.AlreadyAnnounced);
            return;
        }

        _groups[group] = new TrackedGroup
        {
            NextNudgeAt = _options.UnreadNudgeAfter is { } after ? settledAt + after : null,
            QuietSince = quietSince,
            SettledBy = settledBy,
        };

        Play(GroupNotice, group, SoundId.Finished, _options.NoticeGain, TimeSpan.Zero,
            SoundDecisionKind.GroupNotice, rung: 0, waited: TimeSpan.Zero);

        // The group announced, whatever became of the sound: every member Unread now has had its finish told.
        MarkAnnounced(unreadMembers);
    }

    /// <summary>
    /// Whether every one of the group's Unread members already announced its finish (T1.72). False for no
    /// members, and for a member this engine holds no Unread record of: when in doubt, the sound plays.
    /// </summary>
    private bool AllAnnounced(IReadOnlyCollection<SessionId>? unreadMembers)
    {
        if (unreadMembers is not { Count: > 0 })
        {
            return false;
        }

        foreach (var member in unreadMembers)
        {
            if (!_tracked.TryGetValue(member, out var tracked)
                || tracked.State != SessionState.Unread
                || !tracked.Announced)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Marks the finish of each Unread member as announced, by the group's settle (T1.72).</summary>
    private void MarkAnnounced(IReadOnlyCollection<SessionId>? unreadMembers)
    {
        if (unreadMembers is null)
        {
            return;
        }

        foreach (var member in unreadMembers)
        {
            if (_tracked.TryGetValue(member, out var tracked) && tracked.State == SessionState.Unread)
            {
                tracked.Announced = true;
            }
        }
    }

    /// <summary>
    /// A roster group is no longer settled — a member started working again — so it stops nudging.
    /// </summary>
    /// <remarks>
    /// Silent by design. The group going back to work is not an event the operator needs to hear;
    /// it needs only to stop the reminder that the group is waiting, because it is not.
    /// </remarks>
    public void OnRosterGroupUnsettled(GroupKey group)
    {
        using var writing = _guard.Enter("clearing a roster group's settle in the sound engine");

        // Kept aside, not forgotten: if the group settles again at the same quiet instant, it is
        // this settle coming back (T1.44), and it is restored rather than announced again.
        if (_groups.Remove(group, out var gone) && gone.QuietSince is not null)
        {
            _unsettled[group] = gone;
        }
    }

    /// <summary>
    /// Whether this state's notice belongs to the group rather than to the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Done only, and every other sound is untouched.</strong> A member needing permission,
    /// asking a question or erroring still notices and nudges immediately, grouped or not.
    /// Suppressing those because a sibling happens to be working would hide precisely what this
    /// product exists to surface — and unlike "done", they are about that member and nobody else.
    /// </para>
    /// <para>
    /// The engine knows a roster group only by the kind of its key. It holds no roster book, does
    /// not know what a roster is, and cannot be wrong about membership.
    /// </para>
    /// </remarks>
    private static bool IsGroupDone(SessionState state, GroupKey group) =>
        state == SessionState.Unread && GroupKeys.KindOf(group) == GroupKeyKind.Roster;
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

        var advanced = false;

        foreach (var (id, tracked) in _tracked)
        {
            if (tracked.NextNudgeAt is not { } due || due > now)
            {
                continue;
            }

            if (NoticeFor(tracked.State) is { } sound)
            {
                Play(id, tracked.Group, sound, _options.NudgeGain, _options.NudgeFadeIn,
                    SoundDecisionKind.Nudge, tracked.Step, now - tracked.EnteredAt);
            }

            if (tracked.State == SessionState.Unread)
            {
                // TS §IV.5: an unread result gets at most one soft nudge.
                tracked.NextNudgeAt = null;
                advanced = true;
                continue;
            }

            tracked.Step++;
            tracked.NextNudgeAt = now + IntervalAt(tracked.Step);
            advanced = true;
        }

        // Settled roster groups nudge on the same pass and through the same Play, so mute, global
        // silence and the master volume apply to a group notice without being written twice.
        foreach (var (key, group) in _groups)
        {
            if (group.NextNudgeAt is not { } groupDue || groupDue > now)
            {
                continue;
            }

            Play(GroupNotice, key, SoundId.Finished, _options.NudgeGain, _options.NudgeFadeIn,
                SoundDecisionKind.GroupNudge, rung: 0, waited: TimeSpan.Zero);

            // TS §IV.5: an unread result gets at most one soft nudge, and a settled group is one
            // unread result however many members produced it.
            group.NextNudgeAt = null;
        }

        if (advanced)
        {
            NudgeScheduleAdvanced?.Invoke(this, EventArgs.Empty);
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
    /// Raised when <see cref="Evaluate(DateTimeOffset)"/> moves a session's
    /// <see cref="NextNudgeAt"/>: a nudge fired, and the next one is scheduled or none is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The one change to the schedule that no session change announces.</strong> Every
    /// other move of <see cref="NextNudgeAt"/> happens inside <see cref="OnSessionChanged"/>,
    /// which the Registry's <c>SessionChanged</c> drives. A nudge firing is time passing, and
    /// nothing else says so. The state endpoint (T1.46) listens here, so the time it reports
    /// does not go stale in the past after the first nudge.
    /// </para>
    /// <para>
    /// Raised on the thread that called <see cref="Evaluate(DateTimeOffset)"/>, inside the
    /// single-writer region, after the pass. A handler may read <see cref="NextNudgeAt"/> and
    /// must do nothing else here.
    /// </para>
    /// </remarks>
    public event EventHandler? NudgeScheduleAdvanced;

    /// <summary>
    /// Raised when the player queued a sound, with the session whose row shows the speaker sign
    /// (T1.67, issue #99).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Only a queued sound.</strong> A suppressed sound (paused, muted, already announced)
    /// and a dropped one (no output, failed) raise nothing: the operator heard nothing, so no row
    /// says that it made a noise.
    /// </para>
    /// <para>
    /// <strong>The engine decides the row</strong>, so a second interface gets the same answer. A
    /// session's own sound marks that session. A group's own sound marks the member whose state
    /// entry instant is the group's quiet instant: the member whose change settled the group, which the
    /// settle pass reads from the groups as they stand and hands to <see cref="OnRosterGroupSettled"/>.
    /// See <see cref="MarkOf"/>.
    /// </para>
    /// <para>
    /// Raised on the thread that played the sound, inside the single-writer region. A handler
    /// must post and return, like a handler of <see cref="NudgeScheduleAdvanced"/>.
    /// </para>
    /// </remarks>
    public event EventHandler<SoundMarkedEventArgs>? SoundMarked;

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
    private void Play(
        SessionId session,
        GroupKey group,
        SoundId sound,
        double gain,
        TimeSpan fade,
        SoundDecisionKind kind,
        int rung,
        TimeSpan waited)
    {
        // Global mute and pause fold in here, exactly as this method's remarks anticipated: one
        // more clause at the point of emission, and no change to scheduling. The ladder goes on
        // advancing silently, so resuming picks up the natural cadence instead of releasing a
        // backlog of reminders the operator asked not to hear — which matters most for pause,
        // which has no expiry and can span hours.
        //
        // T1.37: the same predicates, in the same order, now also NAME the reason for the
        // record. ReasonOf reads exactly what this condition reads, once, so the recorded reason
        // and the suppression cannot disagree.
        if (ReasonOf(_clock.Now, session, group) is { } reason)
        {
            _sink.SoundSuppressed(kind, session, group, sound, reason);
            return;
        }

        // Master volume is folded in here and nowhere else (Impl Part 7). The adapter receives a
        // finished number; it does not know there is such a thing as a master volume, which is
        // what keeps "how loud is this" answerable by reading one method.
        //
        // T1.55 (issue #72): the record says what the player did. A dropped sound is recorded as
        // dropped, never as played. THE RULES DO NOT CHANGE: the caller goes on as if the sound had
        // played, so a dropped notice still counts as announced and the nudge ladder still advances.
        // Nothing is replayed when a device returns: a stack of old sounds at that moment is noise,
        // and each would be about a state the operator may already have seen (Impl Part 7).
        var outcome = _player.Play(sound, gain * _options.MasterVolume, fade);

        if (outcome == SoundOutcome.Queued)
        {
            _sink.SoundPlayed(kind, session, group, sound, rung, waited);

            // T1.67 (issue #99): the speaker sign, for a sound that was queued and for nothing else. A
            // suppressed or a dropped sound made no noise, so it marks no row.
            if (MarkOf(session, group) is { } marked)
            {
                SoundMarked?.Invoke(this, new SoundMarkedEventArgs(marked, sound, _clock.Now));
            }
        }
        else
        {
            _sink.SoundDropped(kind, session, group, sound, rung, waited, outcome);
        }
    }

    /// <summary>
    /// Why a sound would be suppressed right now, or null when it would play — the same
    /// predicates <see cref="IsSilenced"/> and <see cref="IsMuted"/> ask, in the same order,
    /// asked once so the reason and the decision cannot disagree (T1.37).
    /// </summary>
    private SuppressionReason? ReasonOf(DateTimeOffset now, SessionId session, GroupKey group)
    {
        if (_monitoringPaused)
        {
            return SuppressionReason.MonitoringPaused;
        }

        var ticks = Volatile.Read(ref _allMutedUntilTicks);

        if (ticks != 0 && now.UtcTicks < ticks)
        {
            return SuppressionReason.AllMuted;
        }

        if (_mutedSessions.Contains(session))
        {
            return SuppressionReason.SessionMuted;
        }

        if (_mutedGroups.Contains(group))
        {
            return SuppressionReason.GroupMuted;
        }

        return null;
    }

    /// <summary>
    /// The session whose row a played sound marks (T1.67, issue #99), or null when no row gets the
    /// sign.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A session's own sound</strong> (a notice or a nudge) marks that session.
    /// </para>
    /// <para>
    /// <strong>A group's own sound</strong> (its finished notice, or its one reminder) arrives with
    /// no session, because it belongs to the group. It marks the member that the settle pass named
    /// (<see cref="RosterSettle.SettledBy"/>): the member whose state entry instant is the group's
    /// quiet instant, read from the groups as they stand. The reminder reads the same settle, so it
    /// marks the same member, and a quiet tick that restores the settle restores the member with it.
    /// A settle that named no member marks no row.
    /// </para>
    /// <para>
    /// <strong>Why the engine does not choose the member itself.</strong> The first version matched the
    /// quiet instant against this type's own copy of each session's group. That copy changes only in
    /// <see cref="OnSessionChanged"/>, so after a roster edit it still held the old group, and a roster
    /// formed over finished sessions marked no row (the T1.67 review). The settle pass has the fresh
    /// groups; the rule is still Core's.
    /// </para>
    /// </remarks>
    private SessionId? MarkOf(SessionId session, GroupKey group)
    {
        if (!session.IsEmpty)
        {
            return session;
        }

        return _groups.TryGetValue(group, out var settled) && !settled.SettledBy.IsEmpty
            ? settled.SettledBy
            : null;
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
    /// <remarks>
    /// A roster member never nudges on <see cref="SessionState.Unread"/>: its done notice belongs
    /// to the group, and so does the reminder. Nudging per member would reinstate the noise the
    /// suppression removes, one step later.
    /// </remarks>
    private DateTimeOffset? FirstNudgeAt(Session session, GroupKey effectiveGroup) => session.State switch
    {
        _ when IsGroupDone(session.State, effectiveGroup) => null,

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

    /// <summary>What the engine remembers about one settled roster group.</summary>
    /// <remarks>
    /// Deliberately thinner than <see cref="Tracked"/>: a group has one sounding state — done —
    /// so there is no state to remember and no ladder step to climb. TS §IV.5 gives an unread
    /// result at most one soft nudge, and that is what a settled group gets.
    /// </remarks>
    private sealed class TrackedGroup
    {
        public required DateTimeOffset? NextNudgeAt { get; set; }

        /// <summary>When the group went quiet, for recognising the same settle again (T1.44).</summary>
        public DateTimeOffset? QuietSince { get; init; }

        /// <summary>The member whose change settled the group, whose row its sounds mark (T1.67).</summary>
        public SessionId SettledBy { get; init; }
    }

    /// <summary>What the engine remembers about one session.</summary>
    private sealed class Tracked
    {
        public required SessionState State { get; init; }

        public required DateTimeOffset EnteredAt { get; init; }

        public required GroupKey Group { get; set; }

        public required int Step { get; set; }

        public required DateTimeOffset? NextNudgeAt { get; set; }

        /// <summary>
        /// Whether this entry's notice was made (T1.72, issue #107): by the session itself (played, held back
        /// or dropped), or by its roster group's settle. A roster group's settle is silent when every Unread
        /// member's entry says so. Kept with the entry, so a T1.44 restore keeps it.
        /// </summary>
        public bool Announced { get; set; }
    }
}
