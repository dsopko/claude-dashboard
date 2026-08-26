using System.IO;
using ClaudeDashboard.App.Hosting;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// The single-instance gate (Impl §5.3).
/// </summary>
/// <remarks>
/// Everything here is about a Win32 object whose whole purpose is to be visible to another
/// process, which is the one thing a single test host cannot see. So these tests pin the parts
/// that <em>are</em> observable in process — the naming rule, the thread affinity, the recovery
/// from an abandoned handle — and the cross-process behaviour is evidenced by launching two
/// copies of the app, which is recorded in the task's status report rather than here.
/// </remarks>
public sealed class SingleInstanceTests
{
    /// <summary>
    /// The frozen vector. This is the one place a literal name is right, and it is right for the
    /// reason a literal is usually wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Elsewhere — the <c>/health</c> body test — the expected name is computed from
    /// <see cref="SingleInstanceGate.NameFor"/>, because a literal there would be a second copy
    /// of the naming rule and the copy is what drifts. Here the claim is the opposite one: that
    /// the rule produces the <em>same</em> answer in a process that did not compute it. This
    /// value was produced outside the test host, by hashing the normalised path with a separate
    /// tool, so asserting against it is a comparison across processes and not a tautology.
    /// </para>
    /// <para>
    /// It is what catches <see cref="string.GetHashCode()"/>, which .NET randomises per process.
    /// Under that implementation two instances compute two different names, both acquire, both
    /// believe they are first, and the only symptom is the port bind failing with a message about
    /// the port. Every test that computed the name twice in one process would still pass.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_gate_name_is_the_same_in_every_process()
    {
        Assert.Equal(
            SingleInstanceGate.NamePrefix + "0407ec7d32fc3c63",
            SingleInstanceGate.NameFor(@"C:\dashboard-data", sessionId: 1));
    }

    /// <summary>
    /// The identity carries the logon session, not only the folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate's scope is two things — the <c>Local\</c> logon session and the data folder — and
    /// the name is what <c>/health</c> answers with. If it carried only the folder, two logon
    /// sessions sharing one root would report the same identity, and the second dashboard would
    /// read the first as its own duplicate, raise a window on a desktop this user cannot see, and
    /// exit having logged success. A silent failure, and the outcome falls the wrong way: the
    /// case designed for is loud and this one was quiet.
    /// </para>
    /// <para>
    /// Not hypothetical. <c>CLAUDE_DASHBOARD_HOME</c> is what makes a shared root configurable,
    /// and Impl Part 8 gives a portable install — the case where two accounts share one folder —
    /// as a reason for the variable.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_same_folder_in_a_different_logon_session_is_a_different_gate() =>
        Assert.NotEqual(
            SingleInstanceGate.NameFor(@"C:\dashboard-data", sessionId: 1),
            SingleInstanceGate.NameFor(@"C:\dashboard-data", sessionId: 2));

    /// <summary>…and the same session and folder still agree, which is the control for it.</summary>
    [Fact]
    public void The_same_folder_in_the_same_logon_session_is_the_same_gate() =>
        Assert.Equal(
            SingleInstanceGate.NameFor(@"C:\dashboard-data", sessionId: 7),
            SingleInstanceGate.NameFor(@"C:\DASHBOARD-DATA\", sessionId: 7));

    /// <summary>The session-less overload uses this process's session, not a constant.</summary>
    /// <remarks>
    /// The hop production takes. Without this, an overload that ignored the session and hashed the
    /// folder alone would satisfy every test above, because they all pass a session explicitly.
    /// </remarks>
    [Fact]
    public void The_default_overload_carries_this_processs_session()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();

        Assert.Equal(
            SingleInstanceGate.NameFor(@"C:\dashboard-data", self.SessionId),
            SingleInstanceGate.NameFor(@"C:\dashboard-data"));
    }

    /// <summary>
    /// The normalisation, one claim at a time. Two instances reach the gate by different routes —
    /// a shortcut's working directory, an environment variable someone typed — and must agree.
    /// </summary>
    [Theory]
    [InlineData(@"C:\dashboard-data", @"C:\dashboard-data\")]
    [InlineData(@"C:\dashboard-data", @"C:\DASHBOARD-DATA")]
    [InlineData(@"C:\dashboard-data", @"C:\x\..\dashboard-data")]
    [InlineData(@"C:\dashboard-data", @"C:/dashboard-data")]
    public void Paths_that_mean_the_same_folder_get_the_same_gate(string left, string right) =>
        Assert.Equal(SingleInstanceGate.NameFor(left), SingleInstanceGate.NameFor(right));

    /// <summary>
    /// The control for the theory above: different folders must not collide. Without this, a
    /// naming rule that returned a constant would satisfy every "these agree" case.
    /// </summary>
    [Fact]
    public void Different_folders_get_different_gates() =>
        Assert.NotEqual(
            SingleInstanceGate.NameFor(@"C:\dashboard-data"),
            SingleInstanceGate.NameFor(@"C:\other-data"));

    /// <summary>The name is session-local, never machine-wide (Impl §5.3).</summary>
    /// <remarks>
    /// A <c>Global\</c> gate would let one signed-in user stop another's dashboard from starting.
    /// </remarks>
    [Fact]
    public void The_gate_is_scoped_to_the_logon_session()
    {
        Assert.StartsWith(@"Local\", SingleInstanceGate.NameFor(@"C:\dashboard-data"), StringComparison.Ordinal);
        Assert.DoesNotContain(@"Global\", SingleInstanceGate.NameFor(@"C:\dashboard-data"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_gate_needs_a_root() =>
        Assert.Throws<ArgumentException>(() => SingleInstanceGate.NameFor("  "));

    /// <summary>The first process through takes it.</summary>
    [Fact]
    public void The_first_instance_holds_the_gate()
    {
        using var gate = SingleInstanceGate.Acquire(UniqueRoot());

        Assert.True(gate.IsFirstInstance);
        Assert.False(gate.TookOverFromACrash);
    }

    /// <summary>
    /// The second instance does not — and the wait has to happen on another thread to mean
    /// anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the assertion that could most easily have been produced another way.</strong>
    /// A <see cref="Mutex"/> is thread-affine and re-entrant for its owner, so a second
    /// <see cref="SingleInstanceGate.Acquire"/> on <em>this</em> thread is granted and would
    /// report "first instance" — the test would then pass against a gate that never excluded
    /// anything, because the exclusion it was asserting is not the exclusion the app relies on.
    /// The app's second instance is another process, and the nearest thing a single host can
    /// offer is another thread.
    /// </para>
    /// <para>
    /// Note the contrast with <c>SingleWriterGuard</c>, which is mutual exclusion <em>without</em>
    /// affinity. The two look alike and behave oppositely here.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_second_instance_does_not_hold_the_gate()
    {
        var root = UniqueRoot();
        using var first = SingleInstanceGate.Acquire(root);

        var second = RunOnItsOwnThread(() => SingleInstanceGate.Acquire(root));

        using (second)
        {
            Assert.True(first.IsFirstInstance);
            Assert.False(second.IsFirstInstance);
            Assert.Equal(first.Name, second.Name);
        }
    }

    /// <summary>
    /// The same thread asking twice is granted twice — recorded so the behaviour above is read
    /// as a property of <see cref="Mutex"/> and not as a flaw in the test.
    /// </summary>
    [Fact]
    public void The_owning_thread_is_granted_the_gate_again()
    {
        var root = UniqueRoot();
        using var first = SingleInstanceGate.Acquire(root);
        using var again = SingleInstanceGate.Acquire(root);

        Assert.True(again.IsFirstInstance);
    }

    /// <summary>Releasing it lets the next instance in, which is what a clean exit relies on.</summary>
    [Fact]
    public void Disposing_the_gate_frees_it_for_the_next_instance()
    {
        var root = UniqueRoot();

        var first = SingleInstanceGate.Acquire(root);
        Assert.True(first.IsFirstInstance);
        first.Dispose();

        // On another thread, so that a gate which merely leaned on re-entrancy would fail here.
        var next = RunOnItsOwnThread(() => SingleInstanceGate.Acquire(root));

        using (next)
        {
            Assert.True(next.IsFirstInstance);
        }
    }

    /// <summary>Disposing twice is not an error, because the exit path may run more than once.</summary>
    [Fact]
    public void Disposing_the_gate_twice_is_harmless()
    {
        var gate = SingleInstanceGate.Acquire(UniqueRoot());

        gate.Dispose();
        gate.Dispose();
    }

    /// <summary>
    /// A holder that dies without releasing must not block the restart for ever (Impl §5.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The thread is abandoned deliberately: it takes the gate and ends without releasing, which
    /// is what Windows means by an abandoned mutex and is the nearest in-process stand-in for a
    /// process that was killed. The next waiter is granted the gate <em>and</em> told, by an
    /// exception thrown from a wait that succeeded.
    /// </para>
    /// <para>
    /// <see cref="SingleInstanceGate.TookOverFromACrash"/> is asserted as well as
    /// <see cref="SingleInstanceGate.IsFirstInstance"/>, and the pair is the point: a gate that
    /// simply let the exception escape would fail the process instead of recovering, and one that
    /// swallowed it without recording it would leave the only evidence of an unclean shutdown
    /// nowhere at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void An_abandoned_gate_is_recovered_and_recorded()
    {
        var root = UniqueRoot();

        // Not disposed, and not released: this thread ends holding it. Deliberately no assertion
        // inside — an xUnit failure on a bare thread is an unhandled exception that takes the
        // whole test process with it, rather than a red test.
        var abandoner = new Thread(() => _ = SingleInstanceGate.Acquire(root));

        abandoner.Start();
        Assert.True(abandoner.Join(TimeSpan.FromSeconds(10)), "The abandoning thread did not finish.");

        using var recovered = SingleInstanceGate.Acquire(root);

        Assert.True(recovered.IsFirstInstance);
        Assert.True(recovered.TookOverFromACrash);
    }

    /// <summary>A root nothing else in this run uses, so tests cannot collide through the gate.</summary>
    private static string UniqueRoot() =>
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private static T RunOnItsOwnThread<T>(Func<T> work)
    {
        T? result = default;
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

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The worker thread did not finish.");

        if (failure is not null)
        {
            throw new InvalidOperationException("The worker thread failed.", failure);
        }

        return result!;
    }
}
