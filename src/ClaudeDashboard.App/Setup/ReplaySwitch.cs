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
/// It never modifies <c>events</c>. It writes only <c>decisions</c> rows.
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

        try
        {
            history = store.ReadEvents();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            report($"FAILED: could not read events from {databasePath}: {ex.GetType().Name}. Nothing was written.");
            return 1;
        }

        var clock = new ReplayClock();
        var guard = new SingleWriterGuard();
        var registry = new SessionRegistry(guard);
        var rosters = new RosterStore(new DiscardingSink());
        var archive = new EventArchive(logger);
        var recorder = new DecisionRecorder(registry, rosters, archive, logger);
        var engine = new SoundPolicyEngine(new SilentPlayer(), clock, guard, new SoundPolicyOptions(), recorder);
        var mapper = new HookEventMapper(clock);

        // The live composition's own subscription, mirrored: the engine hears every change on
        // the thread that applied it (AppHost wires this identically). Replay has no roster book
        // — rosters were never archived — so the effective group is the workspace one.
        registry.SessionChanged += (_, e) =>
            engine.OnSessionChanged(e.Session, GroupKeys.Effective(e.Session, rosters.Book));

        var written = 0L;
        var skipped = 0L;
        var ticks = 0L;
        DateTimeOffset? previous = null;

        long Tick(DateTimeOffset tick)
        {
            clock.Now = tick;
            recorder.BeginTick(tick);

            try
            {
                foreach (var silent in registry.SweepSilent(tick, SilenceWatch.DefaultThreshold))
                {
                    recorder.Swept(silent);
                }

                engine.Evaluate(tick);
            }
            finally
            {
                recorder.Complete();
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
                    ticks++;
                    written += Tick(tick);
                }

                if (at > last)
                {
                    ticks++;
                    written += Tick(at);
                }
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

            recorder.BeginEvent(inboundEvent);

            try
            {
                var before = registry.Sessions.TryGetValue(inboundEvent.SessionId, out var tracked)
                    ? tracked
                    : null;

                ApplyOutcome outcome;

                try
                {
                    outcome = registry.Apply(inboundEvent);
                }
                catch (Exception ex)
                {
                    recorder.ApplyFailed(inboundEvent, ex);
                    logger.Warning(
                        "Replaying event {EventId} ({EventType}) threw {ExceptionType}; recorded and continuing.",
                        row.Id,
                        row.EventType,
                        ex.GetType().Name);
                    continue;
                }

                var after = registry.Sessions.TryGetValue(inboundEvent.SessionId, out var changed)
                    ? changed
                    : null;

                recorder.RecordOutcome(inboundEvent, before, outcome, after);
            }
            finally
            {
                recorder.Complete();
                written += Drain(store, archive, row.Id);
            }
        }

        report($"Replayed {history.Count} events and {ticks} synthesised ticks; wrote {written} decisions rows; skipped {skipped} rows that would not parse.");
        report("Replay cannot know what was never archived: mutes, roster edits and the audio device are absent, " +
            "every sound row is a would-have, acks archived before T1.37 do not appear, and an archived ack replays as Manual.");

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

    private sealed class ReplayClock : IClock
    {
        public DateTimeOffset Now { get; set; }
    }

    private sealed class SilentPlayer : ISoundPlayer
    {
        public void Play(SoundId sound, double gain, TimeSpan fade)
        {
        }
    }

    private sealed class DiscardingSink : IEventSink
    {
        public bool TryPublish(InboundEvent inboundEvent) => true;
    }
}
