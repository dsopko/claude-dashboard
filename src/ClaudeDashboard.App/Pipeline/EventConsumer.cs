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

    private readonly EventPipeline _pipeline;
    private readonly SessionRegistry _registry;
    private readonly SoundPolicyEngine _sound;
    private readonly IClock _clock;
    private readonly SingleWriterGuard _guard;
    private readonly ILogger _logger;
    private readonly TimeSpan _tickInterval;
    private readonly IUiTick _uiTick;

    /// <summary>Creates the consumer.</summary>
    /// <param name="uiTick">
    /// Where the tick is echoed for the UI's age and staleness display (T1.11) and the tray's
    /// tooltip (T1.13). Required since T1.12b: it used to be optional, which meant a lost
    /// registration left every age on screen frozen with the suite still green.
    /// </param>
    public EventConsumer(
        EventPipeline pipeline,
        SessionRegistry registry,
        SoundPolicyEngine sound,
        IClock clock,
        SingleWriterGuard guard,
        ILogger logger,
        IUiTick uiTick,
        TimeSpan? tickInterval = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(uiTick);

        _pipeline = pipeline;
        _registry = registry;
        _sound = sound;
        _clock = clock;
        _guard = guard;
        _logger = logger;
        _tickInterval = tickInterval ?? DefaultTickInterval;
        _uiTick = uiTick;
    }

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

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information(
            "Event consumer started. Nudge evaluation every {TickSeconds}s, on the same loop as the channel read.",
            _tickInterval.TotalSeconds);

        using var ticker = new PeriodicTimer(_tickInterval);

        var readable = _pipeline.Reader.WaitToReadAsync(stoppingToken).AsTask();
        var ticked = ticker.WaitForNextTickAsync(stoppingToken).AsTask();

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
                readable = _pipeline.Reader.WaitToReadAsync(stoppingToken).AsTask();
            }
            else
            {
                if (!await Completed(ticked).ConfigureAwait(false))
                {
                    break;
                }

                Tick();
                ticked = ticker.WaitForNextTickAsync(stoppingToken).AsTask();
            }
        }

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
