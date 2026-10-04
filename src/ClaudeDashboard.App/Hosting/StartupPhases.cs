using System.Diagnostics;
using System.Globalization;
using Serilog;

namespace ClaudeDashboard.App.Hosting;

/// <summary>
/// A stopwatch around each phase of <c>Program.Main</c>, logged once at the end of the start (T1.66,
/// issue #86). It has no limit: it says where a slow start went.
/// </summary>
/// <remarks>
/// <c>Program</c> marks each phase on its own thread. <see cref="Log"/> writes the one line and
/// publishes the phases with <see cref="Volatile"/>, so the consumer can copy them into the health
/// snapshot. Before that they are not shown.
/// </remarks>
public sealed class StartupPhases
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<StartupPhase> _phases = [];
    private TimeSpan _last;
    private IReadOnlyList<StartupPhase>? _finished;

    /// <summary>The phases with their times once the start has logged them; null before. Any thread.</summary>
    public IReadOnlyList<StartupPhase>? Finished => Volatile.Read(ref _finished);

    /// <summary>Ends the phase named <paramref name="phase"/> now. The starting thread only.</summary>
    public void Mark(string phase)
    {
        ArgumentException.ThrowIfNullOrEmpty(phase);

        var now = _watch.Elapsed;
        _phases.Add(new StartupPhase(phase, (now - _last).TotalMilliseconds));
        _last = now;
    }

    /// <summary>
    /// Writes the one start-up line, with each phase and the total, and publishes the phases. Only the
    /// first call writes; a later one returns false.
    /// </summary>
    public bool Log(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (Volatile.Read(ref _finished) is not null)
        {
            return false;
        }

        IReadOnlyList<StartupPhase> phases = [.. _phases];
        Volatile.Write(ref _finished, phases);

        logger.Information(
            "Started in {TotalMs:l} ms: {Phases:l}",
            _last.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture),
            string.Join(" ", phases.Select(phase => string.Create(CultureInfo.InvariantCulture, $"{phase.Name}={phase.Milliseconds:0}ms"))));

        return true;
    }
}

/// <summary>One phase of the start and how long it took (T1.66).</summary>
/// <param name="Name">The phase's identifier.</param>
/// <param name="Milliseconds">How long it took.</param>
public sealed record StartupPhase(string Name, double Milliseconds);
