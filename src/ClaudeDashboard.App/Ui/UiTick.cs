namespace ClaudeDashboard.App.Ui;

/// <summary>
/// Carries the event consumer's periodic tick across to the UI thread (T1.9 → Impl §4 → T1.11).
/// </summary>
/// <remarks>
/// An interface so the consumer depends on "something wants the time" rather than on the WPF
/// layer — and so the wiring can be asserted without a dispatcher.
/// </remarks>
public interface IUiTick
{
    /// <summary>The clock has advanced to <paramref name="now"/>.</summary>
    /// <remarks>Called on the consumer thread. Implementations must post and return.</remarks>
    void Tick(DateTimeOffset now);
}

/// <summary>
/// The one thing that makes an age advance while nothing is happening.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists at all.</strong> A dashboard whose whole job is to show how long an
/// agent has been blocked must count while nothing arrives — that is precisely the situation it
/// is reporting on. <see cref="MainViewModel.Tick"/> does the counting, and nothing called it:
/// the view model deliberately starts no timer, because the event consumer owns the only
/// periodic loop in the process and a second one is what that design exists to prevent (T1.9,
/// Impl §2.2 and §4). This is the wire between them, and it is one wire on purpose.
/// </para>
/// <para>
/// The same tick decides staleness — Design Document §6's "quiet for N minutes" — so a second
/// timer for the collapse rules would have been the same mistake in a different file.
/// </para>
/// <para>
/// <strong>It touches nothing shared.</strong> The consumer thread does one volatile read and a
/// post; the work runs on the UI thread against rows the UI thread owns. It never reads the
/// Registry, the projection, or the sound engine, so it raises no single-writer question at
/// either end.
/// </para>
/// <para>
/// <strong>Before there is a window, ticks are dropped</strong> — the dashboard starts headless
/// (T1.7) and there is nothing whose age could be wrong. This is the same degrade as
/// <see cref="WpfDispatcher"/>'s, for the same reason.
/// </para>
/// </remarks>
public sealed class UiTick(IUiDispatcher dispatcher) : IUiTick
{
    private readonly IUiDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    private MainViewModel? _target;

    /// <summary>How many ticks reached a view model. Diagnostic only.</summary>
    public long DeliveredCount { get; private set; }

    /// <summary>
    /// Names the view model the tick drives, once the UI exists.
    /// </summary>
    /// <remarks>
    /// Attached rather than injected because the view model is built on the UI thread while this
    /// is resolved on the host's, and constructing it from the consumer would touch a
    /// UI-thread-owned collection from the wrong thread — the exact failure the marshalling
    /// point exists to prevent.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is null.</exception>
    public void Attach(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Volatile.Write(ref _target, viewModel);
    }

    /// <inheritdoc/>
    public void Tick(DateTimeOffset now)
    {
        if (Volatile.Read(ref _target) is not { } target)
        {
            return;
        }

        DeliveredCount++;
        _dispatcher.Post(() => target.Tick(now));
    }
}
