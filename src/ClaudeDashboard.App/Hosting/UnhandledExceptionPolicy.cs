using System.Globalization;
using System.Reflection;
using ClaudeDashboard.App.Adapters;
using ClaudeDashboard.Core.Ports;
using Serilog;

namespace ClaudeDashboard.App.Hosting;

/// <summary>
/// What the process does about an exception nobody caught (Impl §10.1).
/// </summary>
/// <remarks>
/// <para>
/// Impl §10.1 wires three global handlers so "a fault downgrades a feature; it does not kill
/// the process". The decisions live here rather than in the event wiring so they can be tested
/// as behavior — the wiring itself is three subscriptions in <see cref="AppHost"/>.
/// </para>
/// <para>
/// <strong>Why the dispatcher case is marked handled unconditionally.</strong> Swallowing every
/// exception is normally how a program keeps running in a corrupted state, and that objection
/// does not apply here — because of where the domain lives. The Registry and the sound engine
/// mutate on the single consumer thread (Impl §4), never on the WPF dispatcher, so an exception
/// reaching the dispatcher is by construction from the UI layer: a binding, a converter, a
/// render pass, a click handler. It cannot have interrupted a domain write half-way. What it
/// can do, if left unhandled, is kill a process whose entire value is being present and
/// watching — and Impl §10.1 then restarts it on a one-minute loop that the operator sees only
/// as the dashboard flickering in and out. A dropped frame is a better failure than that.
/// </para>
/// <para>
/// <strong>The storm guard.</strong> The cost of that decision is that a fault which recurs on
/// every render is swallowed on every render. T1.11 made it reachable: a converter that throws
/// throws again for each of fifteen rows, on every tick, and at one log line apiece the file
/// fills with the failure and buries the diagnostics that would explain it. So a repeat is
/// counted rather than written, and one summary carrying the count is logged per window. The
/// count is the half that matters: "the same converter failed 900 times in a minute" is a
/// different sentence from "something failed".
/// </para>
/// <para>
/// <strong>Keyed on the fault, not on the message.</strong> A converter throwing on fifteen rows
/// is one fault fifteen times, so the key is the exception's type and the method it was thrown
/// from. Keying on the message would over-fragment — messages embed the values that differ per
/// row — and would turn one storm back into fifteen.
/// </para>
/// <para>
/// <strong>Nothing about which exceptions are handled changes.</strong> The guard decides only
/// what reaches the log; <see cref="HandleDispatcherException"/> still returns
/// <see langword="true"/> every time.
/// </para>
/// </remarks>
public sealed class UnhandledExceptionPolicy
{
    /// <summary>
    /// How long one fault's repeats are counted before a summary is written.
    /// </summary>
    /// <remarks>
    /// A minute is long enough that a render-loop storm collapses to one line, and short enough
    /// that the operator watching the log sees the count move while it is still happening.
    /// </remarks>
    public static readonly TimeSpan DefaultSuppressionWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Guards the storm table. The only lock in the application, and it is not the Registry's:
    /// dispatcher faults arrive on the UI thread while <see cref="Flush"/> runs on whichever
    /// thread is shutting the process down, and losing a count to a race would defeat the
    /// diagnostic this exists to produce.
    /// </summary>
    private readonly Lock _gate = new();

    private readonly Dictionary<string, Storm> _storms = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private readonly IClock _clock;
    private readonly TimeSpan _window;

    /// <summary>Creates the policy.</summary>
    /// <param name="logger">Where faults are written.</param>
    /// <param name="clock">The clock the suppression window is measured against.</param>
    /// <param name="suppressionWindow">
    /// How long repeats of one fault are counted rather than written; defaults to
    /// <see cref="DefaultSuppressionWindow"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
    public UnhandledExceptionPolicy(ILogger logger, IClock? clock = null, TimeSpan? suppressionWindow = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? new SystemClock();
        _window = suppressionWindow ?? DefaultSuppressionWindow;
    }

    /// <summary>Exceptions seen so far, across all three sources. Diagnostic only.</summary>
    public int ObservedCount { get; private set; }

    /// <summary>Repeats counted rather than written. Diagnostic only.</summary>
    public int SuppressedCount { get; private set; }

    /// <summary>
    /// An exception reached the WPF dispatcher.
    /// </summary>
    /// <returns>
    /// Always <see langword="true"/> — see the remarks on this type. The caller assigns it to
    /// <c>DispatcherUnhandledExceptionEventArgs.Handled</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is null.</exception>
    public bool HandleDispatcherException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        ObservedCount++;

        var key = KeyOf(exception);
        var now = _clock.Now;
        var write = false;
        Summary? due = null;

        lock (_gate)
        {
            if (!_storms.TryGetValue(key, out var storm))
            {
                // The first sighting of this fault is always written in full. A guard that
                // suppressed it would hide the stack trace, which is the only thing here that
                // says what actually broke.
                _storms[key] = new Storm(now);
                write = true;
            }
            else if (now - storm.WindowStart < _window)
            {
                storm.Suppressed++;
                SuppressedCount++;
            }
            else if (storm.Suppressed == 0)
            {
                // A window went by with nothing in it, so this is an occasional fault rather
                // than a storm. Occasional faults are written in full, every time.
                storm.Restart(now);
                write = true;
            }
            else
            {
                due = storm.Close(now);
                SuppressedCount++;
            }
        }

        if (write)
        {
            _logger.Error(
                exception,
                "Unhandled exception on the UI thread. The process stays up and the affected UI work is abandoned.");
        }
        else if (due is { } summary)
        {
            Report(key, summary);
        }

        return true;
    }

    /// <summary>
    /// Writes the count for every fault whose window is still open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The tail is often the interesting part.</strong> A window only expires when the
    /// same fault happens again, because nothing here runs a timer — the process is allowed one
    /// periodic loop and it belongs to the event consumer (T1.9). So a storm that stops after
    /// forty seconds would take its last count with it, and "it stopped when the session ended"
    /// is exactly the thing that count would have said.
    /// </para>
    /// <para>
    /// Called when the process-wide handlers are unwired (which is the shutdown path) and before
    /// an AppDomain exception is written, since that one may be seconds from the end.
    /// </para>
    /// </remarks>
    public void Flush()
    {
        List<(string Key, Summary Summary)> pending;

        lock (_gate)
        {
            var now = _clock.Now;

            pending = [.. _storms
                .Where(entry => entry.Value.Suppressed > 0)
                .Select(entry => (entry.Key, entry.Value.Drain(now)))];
        }

        foreach (var (key, summary) in pending)
        {
            Report(key, summary);
        }
    }

    /// <summary>
    /// A <see cref="Task"/> faulted and nobody ever observed it.
    /// </summary>
    /// <returns>
    /// Always <see langword="true"/>: the caller marks the exception observed. These are
    /// benign by default on modern .NET — they no longer terminate the process — but an
    /// unobserved fault is still a bug that would otherwise be invisible, so it is logged.
    /// </returns>
    /// <remarks>
    /// Not rate-limited. An unobserved fault is raised once per dropped task by the finalizer,
    /// not once per render, so there is no storm to guard against and every one is worth a line.
    /// </remarks>
    public bool HandleUnobservedTaskException(Exception exception)
    {
        ObservedCount++;
        _logger.Error(exception, "A faulted task was never observed. Marking it observed.");

        return true;
    }

    /// <summary>
    /// An exception escaped to the AppDomain.
    /// </summary>
    /// <remarks>
    /// Nothing here can stop this one. When <paramref name="isTerminating"/> is true the CLR is
    /// already tearing the process down, so the only useful act is to get the reason on disk
    /// before it goes — which is why the caller flushes the log immediately afterwards, and why
    /// any counts still being held are written out first.
    /// </remarks>
    public void HandleDomainException(Exception? exception, bool isTerminating)
    {
        Flush();
        ObservedCount++;

        if (isTerminating)
        {
            _logger.Fatal(exception, "Unhandled exception; the process is terminating.");
        }
        else
        {
            _logger.Error(exception, "Unhandled exception reached the AppDomain.");
        }
    }

    /// <summary>
    /// Identifies a fault by what it is and where it was thrown from.
    /// </summary>
    /// <remarks>
    /// <see cref="Exception.TargetSite"/> is the throwing method, so two different converters
    /// failing the same way stay two faults. An exception that was constructed but never thrown
    /// has no site; that does not happen in production, where the handler only ever sees thrown
    /// ones.
    /// </remarks>
    private static string KeyOf(Exception exception)
    {
        var site = exception.TargetSite;

        var where = site is null
            ? "an unknown site"
            : string.Create(CultureInfo.InvariantCulture, $"{site.DeclaringType?.FullName}.{site.Name}");

        return string.Create(CultureInfo.InvariantCulture, $"{exception.GetType().FullName} at {where}");
    }

    private void Report(string key, Summary summary) =>
        _logger.Error(
            "{Fault} failed {RepeatCount} more times in {WindowSeconds:0}s and was not logged each time. " +
            "The first occurrence above has the stack trace.",
            key,
            summary.Count,
            summary.Elapsed.TotalSeconds);

    /// <summary>What one closed suppression window amounted to.</summary>
    private readonly record struct Summary(int Count, TimeSpan Elapsed);

    /// <summary>One fault's open suppression window.</summary>
    private sealed class Storm(DateTimeOffset windowStart)
    {
        public DateTimeOffset WindowStart { get; private set; } = windowStart;

        public int Suppressed { get; set; }

        /// <summary>Begins a fresh window with nothing counted in it.</summary>
        public void Restart(DateTimeOffset now)
        {
            WindowStart = now;
            Suppressed = 0;
        }

        /// <summary>
        /// Takes what this window counted and opens the next one, with the occurrence that
        /// closed this window already in it.
        /// </summary>
        public Summary Close(DateTimeOffset now)
        {
            var summary = new Summary(Suppressed, now - WindowStart);

            WindowStart = now;
            Suppressed = 1;
            return summary;
        }

        /// <summary>
        /// Takes what this window counted with nothing to carry into the next one — no occurrence
        /// closed it, the process did.
        /// </summary>
        public Summary Drain(DateTimeOffset now)
        {
            var summary = new Summary(Suppressed, now - WindowStart);

            Restart(now);
            return summary;
        }
    }
}
