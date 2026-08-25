namespace ClaudeDashboard.App.Ui;

/// <summary>
/// Something that wants to know the clock moved, even though nothing happened.
/// </summary>
/// <remarks>
/// Two things need this and they need it for the same reason. A row's age advances while no
/// event arrives (T1.11), and the tray's tooltip has to notice a global mute lapsing — which
/// raises no event at all, because the mute is a predicate rather than a timer (Impl §5.2). In
/// both cases the truth changes with time alone, so something has to ask.
/// </remarks>
public interface IUiTickTarget
{
    /// <summary>The clock has advanced to <paramref name="now"/>.</summary>
    void Tick(DateTimeOffset now);
}

/// <summary>
/// Where the consumer's tick is echoed to the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// Called on the consumer thread; every target is invoked through <see cref="IUiDispatcher"/>,
/// so nothing here touches UI-thread state. Implementations must post and return.
/// </para>
/// <para>
/// Targets are attached rather than resolved, so that the consumer thread can never construct
/// UI-thread state: <c>Program</c> builds the window and the tray on the UI thread and hands
/// them over.
/// </para>
/// </remarks>
public interface IUiTick
{
    /// <summary>The clock has advanced to <paramref name="now"/>.</summary>
    /// <remarks>Called on the consumer thread. Implementations must post and return.</remarks>
    void Tick(DateTimeOffset now);
}

/// <inheritdoc cref="IUiTick"/>
public sealed class UiTick(IUiDispatcher dispatcher) : IUiTick
{
    private readonly IUiDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    /// <summary>
    /// Everything being ticked. Swapped whole rather than mutated, so the consumer thread
    /// always reads a complete array while the UI thread is attaching.
    /// </summary>
    private IUiTickTarget[] _targets = [];

    /// <summary>How many ticks have been posted. Diagnostic only.</summary>
    public long DeliveredCount { get; private set; }

    /// <summary>Starts ticking <paramref name="target"/>.</summary>
    /// <remarks>
    /// Called on the UI thread during startup. Attaching more than one is the ordinary case —
    /// the window and the tray both advance with the clock.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
    public void Attach(IUiTickTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        Volatile.Write(ref _targets, [.. Volatile.Read(ref _targets), target]);
    }

    /// <inheritdoc/>
    public void Tick(DateTimeOffset now)
    {
        var targets = Volatile.Read(ref _targets);

        if (targets.Length == 0)
        {
            return;
        }

        // One delivery per tick, not per target: this counts how often the UI was told the clock
        // moved, which is what the composition tests are asking about.
        DeliveredCount++;

        foreach (var target in targets)
        {
            _dispatcher.Post(() => target.Tick(now));
        }
    }
}
