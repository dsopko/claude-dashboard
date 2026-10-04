using System.IO;
using System.Text.Json;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Ingress;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Setup;

/// <summary>
/// <c>--replay</c>: rebuilds the decisions record from an existing database's history
/// (T1.37, issue #48).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The real rules, never a re-implementation.</strong> The events run through the real
/// <see cref="SessionRegistry"/> and the real <see cref="SoundPolicyEngine"/>, with the real
/// <see cref="DecisionRecorder"/> assembling the rows — the same objects the live dashboard
/// uses, driven by a clock that reads each event's archived timestamp. The replay differs from
/// the live path in exactly one place: the event's row id already exists, so the decisions are
/// written against it instead of being inserted beside a new row.
/// </para>
/// <para>
/// <strong>What replay cannot know, it says rather than guesses.</strong> Mutes, roster edits
/// and the bound audio device were never archived, so the rebuilt record contains no
/// suppressions from them and every sound row is a <em>would have</em> — no device, no audio.
/// Acknowledgments archived before T1.37 do not exist in the table at all, and an archived ack
/// carries no source, so it replays as <see cref="AckSource.Manual"/>. The summary line carries
/// all of this.
/// </para>
/// <para>
/// <strong>Replay forgets every session at each start, as the live dashboard does (T1.60, issue
/// #78).</strong> The live Registry starts empty at every process start, and since T1.60 each start
/// is a row in <c>runs</c>. When the next tick or event is at or after a run's <c>started_at</c>,
/// replay first starts a new Registry and sound engine state, empty, and then goes on. It does
/// the same at a run's <c>stopped_at</c> when the run stopped cleanly: a dashboard that is off sends
/// no nudges, and an overnight gap would otherwise make dozens of question nudges for each waiting
/// session. A run with no stop (a crash or a kill) is forgotten at the next start. The times
/// are compared as parsed instants, never as text. Since T1.62 every time in the file is UTC in one
/// form, so text would agree, but a time that would not parse is left in its old form, and an
/// instant is the comparison that cannot be wrong.
/// </para>
/// <para>
/// <strong>History older than the first run row replays as one uninterrupted run, as it did before
/// T1.60.</strong> Restarts before then were never archived. A session that went quiet before such
/// a restart was forgotten live but stays tracked here, so the nudge ladder and the silence sweep
/// keep working on it: over the operator's real database, one uninterrupted run, one session that
/// never sent another event produced a question nudge every ten minutes for four weeks (4,076 of
/// 5,168 nudge rows). The summary line says how many runs replay saw, and whether older history
/// had none.
/// </para>
/// <para>
/// <strong>Ticks.</strong> Between events, ticks are synthesised at the live fifteen-second
/// cadence, and one more runs at each event's own instant, BEFORE that event applies — see the
/// loop for why. The consequence, stated rather than hidden: that tick can sweep a session the
/// event is about to refresh, when its silence is within one tick past the threshold. Live, the
/// event might have arrived first. It is bounded to one tick, and over the operator's real
/// history it produced no sweep the live log did not also carry.
/// </para>
/// <para>
/// It never modifies a row of <c>events</c> or <c>runs</c>. It writes only <c>decisions</c> rows, and refuses a
/// database whose <c>decisions</c> table is not empty: it appends, so a second run would double every
/// row. <strong>One exception, by the store and not by replay (T1.62):</strong> replay opens its file
/// through the store, so a file that was never converted has its times converted to UTC first. The
/// instants, the rows, the ids and the payloads are unchanged; only the form of <c>ts</c> changes.
/// </para>
/// </remarks>
public static class ReplaySwitch
{
    /// <summary>The switch.</summary>
    public const string Replay = "--replay";

    /// <summary>Ticks are synthesised at the live loop's own cadence between events.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The database path when <c>--replay</c> was asked for, or null. The path is the next
    /// argument, and it is required: replay is run against a COPY the operator chose, never
    /// implicitly against the live file.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="args"/> is null.</exception>
    public static string? Requested(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], Replay, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Count ? args[i + 1] : string.Empty;
            }
        }

        return null;
    }

    /// <summary>Runs the replay. Returns the process exit code.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> or <paramref name="logger"/> is null.</exception>
    public static int Run(string databasePath, Action<string> report, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(databasePath))
        {
            report($"{Replay} needs the database to replay: {Replay} <path-to-dashboard.db>. Run it against a copy.");
            return 1;
        }

        if (!File.Exists(databasePath))
        {
            report($"FAILED: {databasePath} does not exist. Nothing was written.");
            return 1;
        }

        using var store = new SqliteEventStore(databasePath, logger);

        IReadOnlyList<SqliteEventStore.ArchivedEvent> history;
        IReadOnlyList<(string StartedAt, string? StoppedAt)> runs;

        try
        {
            // Replay appends; it is not idempotent. A second run would double every row, and a
            // copy the live dashboard already recorded into would hold both sets, mixed. So it
            // refuses a non-empty table rather than guess which rows to keep.
            if (store.CountDecisions() is var existing and > 0)
            {
                report($"REFUSED: {databasePath} already holds {existing} decisions rows. Replay appends, " +
                    "so running it again would double them, and a copy the live dashboard recorded " +
                    "into would hold both sets. Replay a copy with an empty decisions table. Nothing was written.");
                return 1;
            }

            history = store.ReadEvents();
            runs = store.ReadRuns();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            report($"FAILED: could not read events from {databasePath}: {ex.GetType().Name}. Nothing was written.");
            return 1;
        }

        // Each start, and each clean stop, as an instant. Parsed, never compared as text (see the
        // remarks). A start begins a run; both forget every session.
        var starts = new List<DateTimeOffset>();
        var forgets = new List<DateTimeOffset>();
        var unparsedRuns = 0L;

        foreach (var (startedAt, stoppedAt) in runs)
        {
            if (DateTimeOffset.TryParse(startedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var start))
            {
                starts.Add(start);
                forgets.Add(start);
            }
            else
            {
                unparsedRuns++;
            }

            if (stoppedAt is null)
            {
                // A crash or a kill: the sessions stay until the next start.
                continue;
            }

            if (DateTimeOffset.TryParse(stoppedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var stop))
            {
                forgets.Add(stop);
            }
            else
            {
                unparsedRuns++;
            }
        }

        starts.Sort();
        forgets.Sort();

        var clock = new ReplayClock();
        var archive = new EventArchive(logger);
        var mapper = new HookEventMapper(clock);
        var run = new ReplayRun(archive, clock, logger);
        var nextStart = 0;
        var nextForget = 0;
        var beforeFirstRun = 0L;

        // A start or a clean stop at or before this instant: the Registry and the sound engine start
        // again, empty, as a live start does, and as a dashboard that is off holds nothing. Called
        // before each tick and each event.
        void EnterRunsUpTo(DateTimeOffset at)
        {
            while (nextStart < starts.Count && starts[nextStart] <= at)
            {
                nextStart++;
            }

            var crossed = false;

            while (nextForget < forgets.Count && forgets[nextForget] <= at)
            {
                nextForget++;
                crossed = true;
            }

            if (crossed)
            {
                run = new ReplayRun(archive, clock, logger);
            }
        }

        var written = 0L;
        var skipped = 0L;
        var ticks = 0L;
        DateTimeOffset? previous = null;

        long Tick(DateTimeOffset tick)
        {
            clock.Now = tick;
            run.Recorder.BeginTick(tick);

            try
            {
                foreach (var silent in run.Registry.SweepSilent(tick, SilenceWatch.DefaultThreshold))
                {
                    run.Recorder.Swept(silent);
                }

                run.Engine.Evaluate(tick);
            }
            finally
            {
                run.Recorder.Complete();
            }

            return Drain(store, archive, eventId: null);
        }

        foreach (var row in history)
        {
            if (!DateTimeOffset.TryParse(row.Ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at))
            {
                skipped++;
                continue;
            }

            // The 15-second cadence the live loop runs between this event and the last: the
            // silence sweep and the nudge ladder are tick-driven, and skipping the quiet hours
            // would skip the very sweeps the record exists to explain.
            //
            // One more tick runs at the event's own instant, before the event applies. The live
            // loop's ticks fall at an arbitrary phase, so it can tick anywhere inside a silence
            // window; ticks synthesised in phase with the events measure the silence as exactly
            // the threshold at the critical instant, which the sweep's strictly-greater rule
            // rightly declines — measured against the operator's real database, where the
            // 09-12 18:52 sweep went missing over a 165-millisecond phase artifact. Ticking at
            // the event's instant restores the guarantee: any sweep the live loop made, this
            // makes too, because the same LastHeardAt has only grown more silent by then.
            if (previous is { } last)
            {
                for (var tick = last + TickInterval; tick < at; tick += TickInterval)
                {
                    EnterRunsUpTo(tick);
                    ticks++;
                    written += Tick(tick);
                }

                EnterRunsUpTo(at);

                if (at > last)
                {
                    ticks++;
                    written += Tick(at);
                }
            }

            EnterRunsUpTo(at);

            if (starts.Count > 0 && nextStart == 0)
            {
                beforeFirstRun++;
            }

            // Forward only: a stale straggler must not drag the tick cursor backwards into
            // hours already ticked.
            previous = previous is { } p && p > at ? p : at;
            clock.Now = at;

            if (Reconstruct(row, mapper, at) is not { } inboundEvent)
            {
                skipped++;
                continue;
            }

            run.Recorder.BeginEvent(inboundEvent);

            try
            {
                var before = run.Registry.Sessions.TryGetValue(inboundEvent.SessionId, out var tracked)
                    ? tracked
                    : null;

                ApplyOutcome outcome;

                try
                {
                    outcome = run.Registry.Apply(inboundEvent);
                }
                catch (Exception ex)
                {
                    run.Recorder.ApplyFailed(inboundEvent, ex);
                    logger.Warning(
                        "Replaying event {EventId} ({EventType}) threw {ExceptionType}; recorded and continuing.",
                        row.Id,
                        row.EventType,
                        ex.GetType().Name);
                    continue;
                }

                var after = run.Registry.Sessions.TryGetValue(inboundEvent.SessionId, out var changed)
                    ? changed
                    : null;

                run.Recorder.RecordOutcome(inboundEvent, before, outcome, after);
            }
            finally
            {
                run.Recorder.Complete();
                written += Drain(store, archive, row.Id);
            }
        }

        report($"Replayed {history.Count} events and {ticks} synthesised ticks; wrote {written} decisions rows; skipped {skipped} rows that would not parse.");
        report("Replay cannot know what was never archived: mutes, roster edits and the audio device are absent, " +
            "restarts before the first runs row are absent, every sound row is a would-have, acks archived before " +
            "T1.37 do not appear, and an archived ack replays as Manual.");

        if (starts.Count == 0)
        {
            report("The database has no runs rows (they are written from T1.60), so replay runs the whole history as one " +
                "uninterrupted process; the live dashboard forgot every session at each restart. Nudge and sweep rows are " +
                "what a dashboard that never restarted would have done, and a session that went quiet before a restart " +
                "can be nudged here for as long as the history runs.");
        }
        else
        {
            report($"Replay saw {starts.Count} runs and forgot every session at each start and each clean stop, as the live dashboard does. " +
                (beforeFirstRun > 0
                    ? $"{beforeFirstRun} events came before the first run and replayed as one uninterrupted run, as before " +
                        "T1.60: their nudge and sweep rows are what a dashboard that never restarted would have done."
                    : "No history came before the first run."));
        }

        if (unparsedRuns > 0)
        {
            report($"Ignored {unparsedRuns} runs times that would not parse.");
        }

        return 0;

    }

    /// <summary>Moves the record the recorder just built into the database, against the known id.</summary>
    /// <remarks>
    /// The recorder hands its record to the archive channel exactly as it does live — the same
    /// code path, which is what the parity acceptance rests on. Replay reads it straight back
    /// out and writes the decisions against the event id that already exists.
    /// </remarks>
    private static long Drain(SqliteEventStore store, EventArchive archive, long? eventId)
    {
        var written = 0L;

        while (archive.Reader.TryRead(out var record))
        {
            if (record.Decisions.Count > 0 && store.AppendDecisions(eventId, record.Decisions))
            {
                written += record.Decisions.Count;
            }
        }

        return written;
    }

    /// <summary>An archived row back into the event it was (T1.37).</summary>
    /// <remarks>
    /// Hook rows go back through the real <see cref="HookEventMapper"/>, so the reconstruction
    /// obeys the same rules ingress did. The Ack is the one event the mapper refuses by design —
    /// the forgery guard — so it is rebuilt directly from the row; its source was never archived
    /// and replays as Manual.
    /// </remarks>
    private static InboundEvent? Reconstruct(
        SqliteEventStore.ArchivedEvent row,
        HookEventMapper mapper,
        DateTimeOffset at)
    {
        if (string.Equals(row.EventType, "Ack", StringComparison.Ordinal))
        {
            return new Ack
            {
                SessionId = new SessionId(row.SessionId),
                Timestamp = at,
                Cwd = row.Cwd,
                Source = AckSource.Manual,
            };
        }

        try
        {
            var payload = JsonSerializer.Deserialize<HookPayload>(row.Payload.Reveal(), PayloadOptions);

            if (payload is null)
            {
                return null;
            }

            var mapping = mapper.Map(payload, row.Payload);

            return mapping.Event;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What a live start creates empty (T1.60): the Registry, the sound engine and the recorder that
    /// watches them, wired as AppHost wires them. One per run.
    /// </summary>
    private sealed class ReplayRun
    {
        public ReplayRun(EventArchive archive, ReplayClock clock, ILogger logger)
        {
            var guard = new SingleWriterGuard();
            var rosters = new RosterStore(new DiscardingSink());

            Registry = new SessionRegistry(guard);
            Recorder = new DecisionRecorder(Registry, rosters, archive, logger);
            Engine = new SoundPolicyEngine(new SilentPlayer(), clock, guard, new SoundPolicyOptions(), Recorder);

            // The live composition's own subscription, mirrored: the engine hears every change on
            // the thread that applied it (AppHost wires this identically). Replay has no roster book
            // (rosters were never archived), so the effective group is the workspace one.
            var engine = Engine;
            Registry.SessionChanged += (_, e) =>
                engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, rosters.Book));
        }

        public SessionRegistry Registry { get; }

        public DecisionRecorder Recorder { get; }

        public SoundPolicyEngine Engine { get; }
    }

    private sealed class ReplayClock : IClock
    {
        public DateTimeOffset Now { get; set; }
    }

    private sealed class SilentPlayer : ISoundPlayer
    {
        // Queued: the replay rebuilds the record a live run would have written, and it cannot know
        // whether that run had an output device. Its rows say "played", as the replay always has.
        public SoundOutcome Play(SoundId sound, double gain, TimeSpan fade) => SoundOutcome.Queued;
    }

    private sealed class DiscardingSink : IEventSink
    {
        public bool TryPublish(InboundEvent inboundEvent) => true;
    }
}
