using System.Windows.Threading;
using Serilog;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// Raises the dashboard window for a <c>/show</c> post, including one that arrives before the
/// window exists (Impl §5.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The startup gap is a real failure, not a rounding error.</strong> <c>/show</c> answers
/// <c>200</c> unconditionally, so a second instance reads success, logs "asked it to surface its
/// window", and exits. If the resident instance had not yet built its window, dropping the
/// request means the operator double-clicked the shortcut, got nothing at all, and the log says
/// it worked. The dashboard starts to the tray, so nothing later brings the window up by itself.
/// </para>
/// <para>
/// So a request that arrives early is <em>latched</em> rather than discarded, and honoured the
/// moment the window is attached. The gap is short; the consequence of losing a request inside it
/// is indistinguishable from the failure this whole task exists to remove.
/// </para>
/// <para>
/// <strong>Why a lock rather than an interlocked flag.</strong> The two sides race: a request can
/// read "no window yet" at the instant the UI thread is attaching one, and a flag would let the
/// attach check the latch before the request sets it — losing exactly the request this type was
/// added to keep. Taking both the window and the latch under one lock removes the interleaving
/// instead of narrowing it. It runs once per launch, so there is nothing to contend.
/// </para>
/// </remarks>
public sealed class WindowSurfacer
{
    private readonly Lock _gate = new();
    private readonly ILogger _logger;

    private MainWindow? _window;
    private bool _pending;

    /// <summary>Creates the surfacer.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
    public WindowSurfacer(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <summary>
    /// Gives the surfacer the window, and honours a request that arrived before it existed.
    /// </summary>
    /// <remarks>
    /// Must be called on the UI thread, which owns the window — so a latched request is shown
    /// inline here rather than posted.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
    public void Attach(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        bool wanted;

        lock (_gate)
        {
            _window = window;
            wanted = _pending;
            _pending = false;
        }

        if (wanted)
        {
            _logger.Information("Surfacing the window for a /show that arrived before it existed.");
            window.ShowDashboard();
        }
    }

    /// <summary>Asks for the window. Safe from any thread.</summary>
    /// <remarks>
    /// Posted rather than invoked: this runs on a Kestrel request thread, and blocking it on the
    /// UI thread would hold an ingress connection open behind a render.
    /// <see cref="DispatcherPriority.Normal"/> rather than the projection's
    /// <see cref="DispatcherPriority.Background"/>, because somebody just double-clicked a
    /// shortcut and is waiting to see a window; there is at most one of these per launch, so it
    /// cannot flood the queue the way session updates could.
    /// </remarks>
    public void Request()
    {
        MainWindow? window;

        lock (_gate)
        {
            window = _window;

            if (window is null)
            {
                _pending = true;
            }
        }

        if (window is null)
        {
            _logger.Information("A /show arrived before the window existed; it will be surfaced at startup.");
            return;
        }

        window.Dispatcher.InvokeAsync(window.ShowDashboard, DispatcherPriority.Normal);
    }

    /// <summary>Whether a request is waiting for the window. Diagnostic, and for assertions.</summary>
    internal bool HasPendingRequest
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }
}
