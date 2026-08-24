using System.Windows;
using ClaudeDashboard.App.Hosting;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// Constructing a WPF <see cref="Application"/> sets <see cref="Application.Current"/> for the
/// whole process, so these run alone rather than alongside the parallel suite.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfApplicationSuite
{
    /// <summary>The collection name.</summary>
    public const string Name = "WPF Application";
}

/// <summary>
/// The application lifecycle Impl §5.1 requires, asserted against the real <see cref="App"/>.
/// </summary>
/// <remarks>
/// Exactly one test here may construct an <see cref="Application"/>: WPF permits one per
/// process and throws on a second, whichever test gets there first. That is a real constraint
/// but a narrow one — it bounds how many such tests there can be, not whether the type can be
/// verified at all.
/// </remarks>
[Collection(WpfApplicationSuite.Name)]
public sealed class AppLifecycleTests
{
    /// <summary>
    /// Impl §5.1: the app does not exit when its window closes — the window is hidden, and the
    /// process exits only via the tray's Quit.
    /// </summary>
    /// <remarks>
    /// This is set in <c>App.xaml</c>, which is compiled as a <c>Page</c> rather than as the
    /// <c>ApplicationDefinition</c> (see <c>Program</c>). That is an unusual enough arrangement
    /// that the property being applied at all is worth pinning: if it ever stopped being, the
    /// process would exit with its last window, which is exactly the failure §5.1 exists to
    /// prevent — and nothing would notice until there was a window to close, in T1.11 or later.
    /// </remarks>
    [Fact]
    public void The_application_does_not_exit_with_its_last_window()
    {
        var mode = OnStaThread(() =>
            new ClaudeDashboard.App.App(new UnhandledExceptionPolicy(Logger.None)).ShutdownMode);

        Assert.Equal(ShutdownMode.OnExplicitShutdown, mode);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a dedicated STA thread and returns its result, because
    /// WPF types can only be constructed on one.
    /// </summary>
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return failure is not null ? throw failure : result;
    }
}
