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
/// The residual risk is honest: an exception thrown on <em>every</em> render would be swallowed
/// on every render, showing up as a log flood rather than a crash. That becomes reachable when
/// there is a UI to throw from (T1.11), which is where a storm guard belongs — noted rather
/// than invented here, since there is nothing yet to storm.
/// </para>
/// </remarks>
public sealed class UnhandledExceptionPolicy(ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Exceptions seen so far, across all three sources. Diagnostic only.</summary>
    public int ObservedCount { get; private set; }

    /// <summary>
    /// An exception reached the WPF dispatcher.
    /// </summary>
    /// <returns>
    /// Always <see langword="true"/> — see the remarks on this type. The caller assigns it to
    /// <c>DispatcherUnhandledExceptionEventArgs.Handled</c>.
    /// </returns>
    public bool HandleDispatcherException(Exception exception)
    {
        ObservedCount++;
        _logger.Error(
            exception,
            "Unhandled exception on the UI thread. The process stays up and the affected UI work is abandoned.");

        return true;
    }

    /// <summary>
    /// A <see cref="Task"/> faulted and nobody ever observed it.
    /// </summary>
    /// <returns>
    /// Always <see langword="true"/>: the caller marks the exception observed. These are
    /// benign by default on modern .NET — they no longer terminate the process — but an
    /// unobserved fault is still a bug that would otherwise be invisible, so it is logged.
    /// </returns>
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
    /// before it goes — which is why the caller flushes the log immediately afterwards. This is
    /// the case Impl §10.1's scheduled-task restart exists to cover.
    /// </remarks>
    public void HandleDomainException(Exception? exception, bool isTerminating)
    {
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
}
