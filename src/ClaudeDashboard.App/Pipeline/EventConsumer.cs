using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ClaudeDashboard.App.Pipeline;

/// <summary>
/// The single reader of the event channel, and the only thread that mutates the Registry or
/// the sound engine (Impl §2.2, §4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One loop, two jobs, and that is the whole point.</strong> This service reads the
/// channel <em>and</em> runs the nudge tick, in a single <see cref="ExecuteAsync"/>, because the
/// Registry and the sound engine are lock-free on the assumption that one thread touches them.
/// Two <c>BackgroundService</c>s, or a <c>PeriodicTimer</c> loop beside the channel loop, would
/// satisfy Impl §4's wording and break its meaning: the T1.5 review demonstrated that driving
/// the engine's evaluate and its change notification from two threads throws within a few
/// hundred iterations, because one enumerates what the other modifies.
/// </para>
/// <para>
/// The tick is here at all because nothing else in the plan owns it. T1.5 builds the engine and
/// exposes <c>Evaluate(now)</c>; T1.14 builds the audio adapter; T1.7 builds the host. Without
/// this loop calling it, every sound-policy test would pass while no nudge ever fired in
/// production.
/// </para>
/// <para>
/// <strong>Routing only.</strong> Nothing here decides anything about a session. It applies
/// events, forwards change notifications, and asks the engine to evaluate; every judgement
/// about states, ordering, grouping and sound lives in Core.
/// </para>
/// </remarks>
public sealed class EventConsumer : BackgroundService
{
    /// <summary>
    /// How often the nudge schedule is evaluated.
    /// </summary>
    /// <remarks>
    /// TS §IV.5's shortest interval is two minutes, so this bounds how late a nudge can be at
    /// fifteen seconds — imperceptible against a two-minute schedule. Finer buys nothing but
    /// wakeups on a machine the operator is working on; coarser makes every nudge late by up to
    /// the interval, which at a minute would be half again as long as the first rung.
    /// </remarks>
    public static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(15);

    /// <summary>The shortest this loop will ever sleep, so a past deadline cannot spin it.</summary>
    internal static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(10);

    private readonly EventPipeline _pipeline;
    private readonly SessionRegistry _registry;
    private readonly SoundPolicyEngine _sound;
    private readonly IClock _clock;
    private readonly SingleWriterGuard _guard;
    private readonly ILogger _logger;
    private readonly TimeSpan _tickInterval;
    private readonly IUiTick _uiTick;
    private readonly RosterStore _rosters;
    private readonly RosterGroupWatch _watch;

    /// <summary>When a roster group is next due to settle on its own, or null.</summary>
    private DateTimeOffset? _settleDue;
    private readonly EventArchive _archive;

    /// <summary>Creates the consumer.</summary>
    /// <param name="uiTick">
    /// Where the tick is echoed for the UI's age and staleness display (T1.11) and the tray's
    /// tooltip (T1.13). Required since T1.12b: it used to be optional, which meant a lost
    /// registration left every age on screen frozen with the suite still green.
    /// </param>
    /// <param name="archive">
    /// Where events go to be recorded (T1.17). <strong>Required, and for the same reason
    /// <paramref name="uiTick"/> is.</strong> A default here would let an unregistered archive
    /// resolve to nothing at all, and the dashboard would run perfectly while recording no
    /// history — a failure with no symptom until Phase 5 went looking for the data.
    /// </param>
    public EventConsumer(
        EventPipeline pipeline,
        SessionRegistry registry,
        SoundPolicyEngine sound,
        IClock clock,
        SingleWriterGuard guard,
        ILogger logger,
        IUiTick uiTick,
        EventArchive archive,
        RosterStore rosters,
        TimeSpan? tickInterval = null,
        RosterGroupWatch? watch = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(uiTick);
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(rosters);

        _archive = archive;
        _rosters = rosters;
        _watch = watch ?? new RosterGroupWatch();
        _pipeline = pipeline;
        _registry = registry;
        _sound = sound;
        _clock = clock;
        _guard = guard;
        _logger = logger;
        _tickInterval = tickInterval ?? DefaultTickInterval;
        _uiTick = uiTick;
    }

    /// <summary>How many roster groups have settled. Diagnostic only.</summary>
    public long SettledCount { get; private set; }

    /// <summary>How many settles turned out to be wrong. Diagnostic only.</summary>
    public long MisMarkedCount { get; private set; }

    /// <summary>When a roster group is next due to settle, or null. Diagnostic only.</summary>
    internal DateTimeOffset? SettleDue => _settleDue;

    /// <summary>How many events have been applied to the Registry. Diagnostic only.</summary>
    public long AppliedCount { get; private set; }

    /// <summary>How many events the Registry declined. Diagnostic only.</summary>
    public long DeclinedCount { get; private set; }

    /// <summary>How many completions were rejected as uncorrelated. Diagnostic only.</summary>
    public long UncorrelatedCount { get; private set; }

    /// <summary>How many nudge evaluations have run. Diagnostic only.</summary>
    public long TickCount { get; private set; }

    /// <summary>How many global sound commands have been applied. Diagnostic only.</summary>
    public long SoundCommandCount { get; private set; }

    /// <summary>Where the tick is echoed for the UI.</summary>
    /// <remarks>
    /// Exposed so the composition can be asserted. If this were left unregistered the process
    /// would behave exactly as it does now in every other respect — and every age on screen would
    /// silently stop advancing, which is the failure T1.11's wiring exists to prevent and the
    /// kind a green suite hides best.
    /// </remarks>
    internal IUiTick UiTick => _uiTick;

    /// <summary>Where events are handed over to be recorded.</summary>
    /// <remarks>
    /// Exposed for the same reason <see cref="UiTick"/> is: without an assertion on the
    /// composition, an archive that was never registered would leave the dashboard behaving
    /// exactly as it does now while writing no history at all.
    /// </remarks>
    internal EventArchive Archive => _archive;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information(
            "Event consumer started. Nudge evaluation every {TickSeconds}s, on the same loop as the channel read.",
            _tickInterval.TotalSeconds);

        // A computed delay rather than a PeriodicTimer, because two deadlines share this loop now:
        // the ordinary tick, and a roster group due to settle. See WaitFor.
        var nextTick = _clock.Now + _tickInterval;

        var readable = _pipeline.Reader.WaitToReadAsync(stoppingToken).AsTask();

        // WHY the timer was armed, recorded when it is armed. Deciding it afterwards by comparing
        // the clock would be wrong the moment the clock is a test's: a held clock never reaches
        // the tick deadline, so every wake would be read as a settle and the tick would never run.
        var waitingForSettle = SettleIsSooner(nextTick);
        var ticked = Task.Delay(WaitFor(_clock.Now, nextTick, _settleDue), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            Task finished;
            try
            {
                finished = await Task.WhenAny(readable, ticked).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (finished == readable)
            {
                if (!await Completed(readable).ConfigureAwait(false))
                {
                    // The channel is closed and drained; the host is stopping.
                    break;
                }

                DrainAvailable();

                // An event can settle a group as surely as time can: the last member stopping is
                // what starts the window, and the next member starting is what cancels it.
                Settle(_clock.Now);

                readable = _pipeline.Reader.WaitToReadAsync(stoppingToken).AsTask();

                if (ticked.IsCompleted)
                {
                    waitingForSettle = SettleIsSooner(nextTick);
                    ticked = Task.Delay(WaitFor(_clock.Now, nextTick, _settleDue), stoppingToken);
                }
            }
            else
            {
                if (!await Completed(ticked).ConfigureAwait(false))
                {
                    break;
                }

                var woke = _clock.Now;

                if (waitingForSettle)
                {
                    // Woken by a settle deadline rather than by the tick. The tick keeps its own
                    // schedule — nextTick is untouched — so the settle PRECEDED it rather than
                    // displacing it, and the ordinary cadence is unchanged.
                    Settle(woke);
                    _uiTick.Tick(woke);
                }
                else
                {
                    Tick();
                    nextTick = woke + _tickInterval;
                }

                waitingForSettle = SettleIsSooner(nextTick);
                ticked = Task.Delay(WaitFor(_clock.Now, nextTick, _settleDue), stoppingToken);
            }
        }

        // Nothing else will offer events, so the writer may drain and stop. Doing this here rather
        // than leaving it to shutdown ordering means the last events of a run are written whether
        // the writer is stopped before this service or after it.
        _archive.Complete();

        _logger.Information(
            "Event consumer stopped after {Applied} applied, {Declined} declined, {Ticks} nudge evaluations.",
            AppliedCount,
            DeclinedCount,
            TickCount);
    }

    /// <summary>Awaits a branch, turning cancellation into "stop" rather than an exception.</summary>
    private static async Task<bool> Completed(Task branch)
    {
        try
        {
            return branch switch
            {
                Task<bool> hasMore => await hasMore.ConfigureAwait(false),
                _ => true,
            };
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies everything the channel can hand over without waiting, so a burst is one pass
    /// rather than one loop iteration per event.
    /// </summary>
    private void DrainAvailable()
    {
        while (_pipeline.Reader.TryRead(out var inboundEvent))
        {
            Apply(inboundEvent);
        }
    }

    private void Apply(InboundEvent inboundEvent)
    {
        try
        {
            using (_guard.Enter("applying an event"))
            {
                if (inboundEvent is SoundCommand command)
                {
                    // Global sound modes ride the same Channel as hooks so they land on this
                    // thread, in order with the events they silence — but they are not session
                    // state, so the Registry never sees one. See SoundCommand's remarks.
                    ApplySoundCommand(command);
                    return;
                }

                // Handed to the archive before the Registry sees it, and never awaited (T1.17).
                // This is a TryWrite onto a bounded channel: it cannot block, so a slow or dead
                // disk can never stall the one thread that owns the Registry and the sound engine.
                // Before, rather than after, so that an event the Registry declines as stale is
                // still recorded — the archive is a record of what arrived, not of what changed
                // something.
                _archive.TryArchive(inboundEvent);

                Report(inboundEvent, _registry.Apply(inboundEvent));
            }
        }
        catch (SingleWriterViolationException ex)
        {
            // A programming error, not a platform failure: loud, but it must not stop the
            // pipeline, because a dashboard that has stopped consuming is worse than one that
            // has logged a bug.
            _logger.Error(ex, "The single-writer invariant was violated while applying an event.");
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "Applying {HookEventName} for session {SessionId} failed. The pipeline continues.",
                inboundEvent.HookEventName,
                inboundEvent.SessionId.Value);
        }
    }

    /// <summary>Applies a global sound mode. Already inside the single-writer region.</summary>
    private void ApplySoundCommand(SoundCommand command)
    {
        switch (command.Kind)
        {
            case SoundCommandKind.MuteAll:
                _sound.SetAllMuted(muted: true, command.Until);
                break;

            case SoundCommandKind.UnmuteAll:
                _sound.SetAllMuted(muted: false);
                break;

            case SoundCommandKind.PauseMonitoring:
                _sound.SetMonitoringPaused(paused: true);
                break;

            case SoundCommandKind.ResumeMonitoring:
                _sound.SetMonitoringPaused(paused: false);
                break;

            default:
                // A kind this build does not know is a newer dashboard's command, not a reason
                // to stop consuming. Logged rather than thrown, like every other decline.
                _logger.Warning(
                    "Ignored an unrecognised sound command {Kind}.",
                    command.Kind);
                return;
        }

        SoundCommandCount++;
        _logger.Debug("Applied sound command {Kind}.", command.Kind);
    }

    /// <summary>
    /// Counts an outcome and logs it at the level it deserves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four routine outcomes go to <c>Debug</c>, which the file sink does not keep — stale
    /// duplicates happen constantly and are the guards working, so recording them would bury
    /// everything else.
    /// </para>
    /// <para>
    /// <see cref="ApplyOutcome.Uncorrelated"/> goes to <c>Warning</c>, with both prompt ids,
    /// because it is the one decline that should not be happening. If Claude Code's
    /// <c>Stop</c> turns out not to echo the prompt's id (unverified as of T1.8), this is not a
    /// rare event but every completion — every session stuck in <see cref="SessionState.Working"/>
    /// — and this line is the only thing that would say so. Reading the tracked id here is safe
    /// and correct: this is the single writer, and a declined event left the session untouched.
    /// </para>
    /// </remarks>
    private void Report(InboundEvent inboundEvent, ApplyOutcome outcome)
    {
        if (outcome.Changed())
        {
            AppliedCount++;
            return;
        }

        DeclinedCount++;

        if (outcome != ApplyOutcome.Uncorrelated)
        {
            _logger.Debug(
                "The Registry declined {HookEventName} for session {SessionId}: {Outcome}.",
                inboundEvent.HookEventName,
                inboundEvent.SessionId.Value,
                outcome);

            return;
        }

        UncorrelatedCount++;

        var tracked = _registry.Sessions.TryGetValue(inboundEvent.SessionId, out var session)
            ? session.Latest.PromptId
            : null;

        _logger.Warning(
            "Rejected {HookEventName} for session {SessionId}: its prompt_id {IncomingPromptId} does not match " +
            "the turn the session is tracking ({TrackedPromptId}). One of these is a delayed duplicate; if every " +
            "completion is rejected this way, Claude Code does not echo the prompt's id and the correlation guard " +
            "is wrong. {UncorrelatedCount} so far.",
            inboundEvent.HookEventName,
            inboundEvent.SessionId.Value,
            inboundEvent.PromptId ?? "(none)",
            tracked ?? "(none)",
            UncorrelatedCount);
    }




    /// <summary>Whether a roster group is due to settle before the next ordinary tick.</summary>
    private bool SettleIsSooner(DateTimeOffset nextTick) => _settleDue is { } due && due < nextTick;

    /// <summary>
    /// Runs the roster-group pass inside the single-writer region, and never lets it stop the
    /// pipeline.
    /// </summary>
    /// <remarks>
    /// It enumerates the Registry and touches the sound engine, so it belongs inside the region
    /// for exactly the reason <c>Evaluate</c> does: the hazard is walking a collection another
    /// writer could restructure, not two writes colliding.
    /// </remarks>
    private void Settle(DateTimeOffset now)
    {
        try
        {
            using (_guard.Enter("observing roster groups"))
            {
                ObserveRosterGroups(now);
            }
        }
        catch (SingleWriterViolationException ex)
        {
            _logger.Error(ex, "The single-writer invariant was violated while observing roster groups.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Observing roster groups failed. The pipeline continues.");
        }
    }
    /// <summary>
    /// The shortest time this loop may sleep for: until the next ordinary tick, or until a roster
    /// group is due to settle, whichever comes first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This exists because the ordinary tick is fifteen seconds and the settle window is
    /// one and a half.</strong> Evaluating a 1.5-second window only on a 15-second tick would
    /// deliver the group's finished state — and its chime — up to fifteen seconds after the work
    /// finished, which is not a settle window at all. The operator chose 1.5 s deliberately, and a
    /// tick interval is an implementation detail that must not overrule it.
    /// </para>
    /// <para>
    /// <strong>And it is a wake, not a poll.</strong> The deadline is known the instant a group
    /// goes quiet, so exactly one extra wake-up is needed per settle. A fast repeating timer would
    /// have caught the same window by firing thousands of times an hour on an idle machine — which
    /// is polling with a smaller number, and this product's first principle is that it never polls.
    /// </para>
    /// <para>
    /// <strong>The settle never displaces the tick; it only precedes it.</strong> When nothing is
    /// pending this returns exactly the time left until the next tick, so the ordinary cadence is
    /// untouched — and when something is pending, the tick that was due still falls due at its own
    /// time, because the caller advances the tick deadline only when it actually ticks.
    /// </para>
    /// <para>
    /// <strong>The floor is a rate limiter, not a guard, and the difference cost a fix cycle.</strong>
    /// <see cref="MinimumWait"/> stops a past deadline producing a zero-length wait; it does
    /// <em>not</em> stop the loop waking again on the same past instant. What keeps a past deadline
    /// out of here at all is <see cref="RosterSettle.PendingDeadlineOf"/>, which returns null once
    /// the window has elapsed. Before it did, a settled group reported an elapsed deadline for as
    /// long as it stayed unread and this loop woke about a hundred times a second — measured, on
    /// the feature's success path.
    /// </para>
    /// </remarks>
    internal static TimeSpan WaitFor(DateTimeOffset now, DateTimeOffset nextTick, DateTimeOffset? settleDue)
    {
        var until = settleDue is { } due && due < nextTick ? due : nextTick;
        var wait = until - now;

        return wait < MinimumWait ? MinimumWait : wait;
    }
    /// <summary>
    /// Looks at the roster groups as they stand and acts on whatever changed (issue #16).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called after every drain and on every tick, so a settle can be reached either by an event
    /// arriving or by time passing — which is the whole shape of the feature: a group settles
    /// because <em>nothing</em> happened for a while, and nothing happening produces no event.
    /// </para>
    /// <para>
    /// <strong>Groups are re-derived here rather than cached.</strong> Fifteen sessions bucketed
    /// into a handful of groups is far below the cost of the dispatcher hop that follows, and a
    /// cache that disagreed with the Registry would settle a group whose membership had moved —
    /// the same argument <see cref="GroupResolver"/> already makes.
    /// </para>
    /// <para>
    /// <strong>The mis-mark warning names the roster and never a member.</strong> A member name is
    /// a session title, and a title can be a model-written summary of the operator's prompt.
    /// </para>
    /// </remarks>
    private void ObserveRosterGroups(DateTimeOffset now)
    {
        var groups = GroupResolver.Resolve(_registry.Sessions.Values, _rosters.Book);

        foreach (var change in _watch.Observe(groups, now))
        {
            switch (change.Event)
            {
                case RosterGroupEvent.Settled:
                    _sound.OnRosterGroupSettled(change.Group, now);
                    SettledCount++;
                    break;

                case RosterGroupEvent.Unsettled:
                    _sound.OnRosterGroupUnsettled(change.Group);
                    break;

                case RosterGroupEvent.MisMarked:
                    MisMarkedCount++;
                    _logger.Warning(
                        "Group {Group} read finished and went back to working within {Seconds}s, so that " +
                        "finished was wrong and the settle window is too short.",
                        change.Group.Value,
                        RosterSettle.DefaultMisMarkWindow.TotalSeconds);
                    break;

                default:
                    break;
            }
        }

        _settleDue = _watch.NextDeadline(groups, now);
    }
    /// <summary>
    /// Asks the sound engine what has come due (TS §IV.5), and tells the UI what time it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both jobs, one instant.</strong> The clock is read once so the nudge that fires
    /// and the age that appears beside it cannot disagree about what "now" is.
    /// </para>
    /// <para>
    /// <strong>The UI echo is outside the guard, and separately guarded against failure.</strong>
    /// It posts to the dispatcher and touches nothing this thread owns, so it needs no exclusion
    /// — and a UI that throws must not stop nudges from firing, any more than a failed nudge may
    /// stop the clock on screen. The two are independent and are kept that way.
    /// </para>
    /// </remarks>
    private void Tick()
    {
        var now = _clock.Now;

        try
        {
            using (_guard.Enter("evaluating the nudge schedule"))
            {
                TickCount++;
                _sound.Evaluate(now);
                ObserveRosterGroups(now);
            }
        }
        catch (SingleWriterViolationException ex)
        {
            _logger.Error(ex, "The single-writer invariant was violated while evaluating nudges.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Evaluating the nudge schedule failed. The pipeline continues.");
        }

        try
        {
            _uiTick.Tick(now);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Posting the tick to the UI failed. The pipeline continues.");
        }
    }
}
