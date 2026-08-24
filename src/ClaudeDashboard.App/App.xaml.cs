using System.Windows;
using System.Windows.Threading;
using ClaudeDashboard.App.Hosting;

namespace ClaudeDashboard.App;

/// <summary>
/// The WPF application the Generic Host owns (Impl §3.1, §5.1).
/// </summary>
/// <remarks>
/// Deliberately thin. It owns the dispatcher and the WPF lifetime and nothing else: the host
/// is built and started around it by <see cref="Program"/>, and everything the app does is
/// resolved from that host. The main window, its hide-on-close behavior and the tray are
/// T1.11 and T1.13; there is no <c>StartupUri</c>, so the process starts headless.
/// </remarks>
public partial class App : Application
{
    private readonly UnhandledExceptionPolicy _exceptionPolicy;

    /// <summary>Creates the application with the policy that decides what unhandled faults do.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="exceptionPolicy"/> is null.</exception>
    public App(UnhandledExceptionPolicy exceptionPolicy)
    {
        ArgumentNullException.ThrowIfNull(exceptionPolicy);

        _exceptionPolicy = exceptionPolicy;
        InitializeComponent();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>
    /// Keeps the process alive through an exception on the UI thread (Impl §10.1). See
    /// <see cref="UnhandledExceptionPolicy"/> for why this is unconditional.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        e.Handled = _exceptionPolicy.HandleDispatcherException(e.Exception);
}
