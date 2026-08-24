using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ClaudeDashboard.App.Hosting;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The one WPF <see cref="Application"/> this process gets, and a thread to run it on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a harness instead of a note in the docs.</strong> A <see cref="Window"/> is
/// thread-affine exactly as <see cref="Application"/> is, and WPF permits one application per
/// process. Before this existed, that was a rule every test author had to remember — "only one
/// test may construct an Application" — enforced by nothing. Owning the application here turns
/// the rule into an arrangement: there is one, it belongs to the fixture, and a test that wants
/// a window asks the fixture for a thread rather than making its own.
/// </para>
/// <para>
/// <strong>It must not swallow a failure, and that took arranging.</strong>
/// <c>App</c> marks every dispatcher exception handled (Impl §10.1), which is right for a
/// dashboard that must not die of a bad render and wrong for a test: an assertion that failed
/// inside <see cref="Dispatcher.Invoke(Action)"/> would be absorbed by that handler and the test
/// would pass green having asserted nothing. So <see cref="Invoke{T}"/> catches on the UI thread
/// and rethrows on the caller's, keeping the stack. A test below proves it.
/// </para>
/// </remarks>
public sealed class StaHarness : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private Application? _application;
    private Exception? _startupFailure;
    private bool _disposed;

    /// <summary>Starts the UI thread and the application on it.</summary>
    /// <exception cref="InvalidOperationException">The application did not start.</exception>
    public StaHarness()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Test UI thread" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException("The test UI thread did not start within 30 seconds.");
        }

        if (_startupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(_startupFailure).Throw();
        }
    }

    /// <summary>The process's single WPF application.</summary>
    public Application Application =>
        _application ?? throw new InvalidOperationException("The application is not running.");

    /// <summary>Runs <paramref name="work"/> on the UI thread and returns its result.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.</exception>
    public T Invoke<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var result = default(T)!;
        Exception? failure = null;

        Application.Dispatcher.Invoke(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                // Captured rather than allowed to escape: escaping would reach App's dispatcher
                // handler, which marks everything handled, and the assertion would vanish.
                failure = ex;
            }
        });

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread.</summary>
    public void Invoke(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        Invoke(() =>
        {
            work();
            return true;
        });
    }

    /// <summary>
    /// Lets the UI thread finish everything queued at <paramref name="upTo"/> or above —
    /// layout, bindings, the storyboards a trigger has just started, and the work a click posts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The priority is the whole fix.</strong> Pushing a frame runs a message loop until a
    /// marker posted at <paramref name="upTo"/> comes back, which drains everything <em>above</em>
    /// that priority — so the marker goes low. <c>Background</c> is below <c>Input</c>,
    /// <c>Loaded</c>, <c>Render</c> and <c>DataBind</c>, which is everything layout and a click
    /// need. This used to post its marker at <c>Loaded</c>, and <c>Loaded</c> sits <em>above</em>
    /// <c>Input</c>: a button's automation peer posts its click at <c>Input</c>, so the marker came
    /// back before the click had run and the invocation simply never happened. That cost an
    /// afternoon.
    /// </para>
    /// <para>
    /// <strong>And what is not true, because a wrong explanation of this was briefly in
    /// circulation.</strong> <see cref="Dispatcher.Invoke(Action, DispatcherPriority)"/> called
    /// from the UI thread is <em>not</em> a no-op. At any priority other than
    /// <see cref="DispatcherPriority.Send"/> it queues a <see cref="DispatcherOperation"/> and
    /// waits on it, and that wait pushes a frame of its own — so the old spelling did run a real
    /// message loop and did drain everything above <c>Loaded</c>. Only <c>Send</c> runs the
    /// delegate inline and drains nothing, and only when the caller is already the dispatcher
    /// thread — which is exactly the case the branch below is for, where there is nothing to drain
    /// and only a hop to make. Left uncorrected, "any <c>Invoke</c> from the UI thread is a no-op"
    /// would have someone rewriting a correct pattern somewhere else.
    /// </para>
    /// <para>
    /// There is deliberately no default: both call sites name <c>Background</c>, and an unused
    /// default would be a value nothing pins and the next caller inherits.
    /// </para>
    /// <para>
    /// <see cref="Dispatcher.PushFrame"/> is kept because it names the priority being waited for
    /// out loud, and the priority is the thing that has to be right. It is a clearer spelling of
    /// what the old line already did; it is not what repaired anything.
    /// </para>
    /// </remarks>
    public void Pump(DispatcherPriority upTo)
    {
        var dispatcher = Application.Dispatcher;

        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => Drain(dispatcher, upTo));
            return;
        }

        Drain(dispatcher, upTo);
    }

    private static void Drain(Dispatcher dispatcher, DispatcherPriority upTo)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(upTo, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Finds the first <typeparamref name="T"/> under <paramref name="root"/> that matches.</summary>
    /// <remarks>
    /// A visual-tree walk rather than a name lookup, because what is being asserted is what the
    /// template actually produced — a name would only find an element this test already assumed
    /// was there.
    /// </remarks>
    public static T? Find<T>(DependencyObject? root, Func<T, bool>? where = null)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        if (root is T match && (where is null || where(match)))
        {
            return match;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (Find(VisualTreeHelper.GetChild(root, i), where) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Everything of type <typeparamref name="T"/> under <paramref name="root"/>.</summary>
    public static IReadOnlyList<T> FindAll<T>(DependencyObject? root)
        where T : DependencyObject
    {
        var found = new List<T>();
        Collect(root, found);
        return found;

        static void Collect(DependencyObject? node, List<T> into)
        {
            if (node is null)
            {
                return;
            }

            if (node is T match)
            {
                into.Add(match);
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Collect(VisualTreeHelper.GetChild(node, i), into);
            }
        }
    }

    /// <summary>Shuts the application down and joins the thread.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_application is { } app)
        {
            app.Dispatcher.InvokeAsync(app.Shutdown);
        }

        _thread.Join(TimeSpan.FromSeconds(10));
        _ready.Dispose();
    }

    private void Run()
    {
        try
        {
            var app = new ClaudeDashboard.App.App(new UnhandledExceptionPolicy(Logger.None));
            _application = app;
            app.Startup += (_, _) => _ready.Set();
            app.Run();
        }
        catch (Exception ex)
        {
            _startupFailure = ex;
            _ready.Set();
        }
    }
}

/// <summary>
/// Everything that touches the WPF application, run one at a time against one harness.
/// </summary>
/// <remarks>
/// Serialized because <see cref="Application.Current"/> is process-wide state and the harness's
/// dispatcher is one thread: two tests driving it at once would interleave layout passes.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfApplicationSuite : ICollectionFixture<StaHarness>
{
    /// <summary>The collection name.</summary>
    public const string Name = "WPF Application";
}
