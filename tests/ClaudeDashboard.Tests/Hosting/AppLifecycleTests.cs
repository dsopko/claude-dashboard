using System.Windows;
using ClaudeDashboard.Tests.Ui;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// The application lifecycle Impl §5.1 requires, asserted against the real <c>App</c>.
/// </summary>
/// <remarks>
/// The application under test is the harness's, because WPF permits one per process. Before
/// T1.11 that meant "exactly one test here may construct an Application", enforced by a comment;
/// now there is one application, the harness owns it, and the rule is an arrangement rather than
/// something to remember. See <see cref="StaHarness"/>.
/// </remarks>
[Collection(WpfApplicationSuite.Name)]
public sealed class AppLifecycleTests(StaHarness harness)
{
    private readonly StaHarness _harness = harness;

    /// <summary>
    /// Impl §5.1: the app does not exit when its window closes — the window is hidden, and the
    /// process exits only via the tray's Quit.
    /// </summary>
    /// <remarks>
    /// This is set in <c>App.xaml</c>, which is compiled as a <c>Page</c> rather than as the
    /// <c>ApplicationDefinition</c> (see <c>Program</c>). That is an unusual enough arrangement
    /// that the property being applied at all is worth pinning: if it ever stopped being, the
    /// process would exit with its last window, which is exactly the failure §5.1 exists to
    /// prevent — and with T1.11's window now closing to hidden, that failure would be a
    /// dashboard that quits when the operator closes it.
    /// </remarks>
    [Fact]
    public void The_application_does_not_exit_with_its_last_window()
    {
        var mode = _harness.Invoke(() => _harness.Application.ShutdownMode);

        Assert.Equal(ShutdownMode.OnExplicitShutdown, mode);
    }

    /// <summary>
    /// The harness must not absorb a failed assertion, because <c>App</c> marks every dispatcher
    /// exception handled and would otherwise turn a red test green.
    /// </summary>
    /// <remarks>
    /// A test about the tests, and worth its line: every other assertion in the WPF suite runs
    /// inside <see cref="StaHarness.Invoke{T}"/>, so if this were broken they would all pass
    /// regardless of what the UI did.
    /// </remarks>
    [Fact]
    public void A_failure_on_the_ui_thread_reaches_the_test()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => _harness.Invoke(() => throw new InvalidOperationException("deliberate")));

        Assert.Equal("deliberate", thrown.Message);
    }
}
