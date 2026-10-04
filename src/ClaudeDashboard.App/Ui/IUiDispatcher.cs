using System.Windows;
using System.Windows.Threading;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The one crossing point from background work to the UI thread (Impl §4).
/// </summary>
/// <remarks>
/// An interface so the projection can be tested without a WPF <c>Application</c>, which is
/// process-wide and thread-affine (T1.7) and therefore cannot be stood up per test.
/// </remarks>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="work"/> for the UI thread and returns immediately.</summary>
    /// <remarks>
    /// Post and return. The caller is the single consumer thread, and blocking it on the UI
    /// would stall every session's events behind a render.
    /// </remarks>
    void Post(Action work);
}

/// <summary>Posts to the WPF dispatcher (Impl §4).</summary>
/// <remarks>
/// Resolves <see cref="Application.Current"/> lazily rather than at construction, because the
/// host builds and starts before the WPF application exists (T1.7). Before there is one — and
/// the dashboard starts headless — work is dropped rather than run inline: running it on the
/// consumer thread would mutate a bound collection from the wrong thread, which is the exact
/// failure this type exists to prevent.
/// </remarks>
public sealed class WpfDispatcher(Serilog.ILogger logger) : IUiDispatcher
{
    private readonly Serilog.ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>The UI hop's timing (T1.66). Set once at composition; none when null.</summary>
    public ClaudeDashboard.App.Pipeline.Timing? Hop { get; set; }

    /// <inheritdoc/>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null)
        {
            _logger.Debug("No WPF application yet; discarded a UI update.");
            return;
        }

        // The UI hop (T1.66): from this post to the moment the posted work runs, recorded on the UI
        // thread with Interlocked, so the consumer never waits on it.
        if (Hop is not { } hop)
        {
            dispatcher.InvokeAsync(work, DispatcherPriority.Background);
            return;
        }

        var posted = System.Diagnostics.Stopwatch.GetTimestamp();

        dispatcher.InvokeAsync(
            () =>
            {
                hop.Record(System.Diagnostics.Stopwatch.GetElapsedTime(posted));
                work();
            },
            DispatcherPriority.Background);
    }
}
