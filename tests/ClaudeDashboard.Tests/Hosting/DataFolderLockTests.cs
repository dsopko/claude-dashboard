using System.Diagnostics;
using System.IO;
using ClaudeDashboard.App.Hosting;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// The data folder lock: one dashboard per data folder, across Windows sessions (Impl §5.3, issue #150).
/// </summary>
/// <remarks>
/// <para>
/// Every lock here is named from a scratch root of its own, so parallel tests and parallel test
/// processes never meet, and no test takes the lock for the operator's data folder.
/// </para>
/// <para>
/// A second Windows session cannot be made in a test. What the second session meets is a process
/// that holds the <c>Global\</c> mutex, and that is what the cross-process test makes.
/// </para>
/// </remarks>
public sealed class DataFolderLockTests
{
    /// <summary>
    /// The frozen vector: the name is the same in every process.
    /// </summary>
    /// <remarks>
    /// The value was made outside the test host, with <c>sha256sum</c> over the normalised path
    /// <c>c:\dashboard-data</c> (the same tool gives the gate's frozen value from
    /// <c>1|c:\dashboard-data</c>). <see cref="string.GetHashCode()"/> would fail this, and every test
    /// that computed the name twice in one process would still pass.
    /// </remarks>
    [Fact]
    public void The_lock_name_is_the_same_in_every_process() =>
        Assert.Equal(DataFolderLock.NamePrefix + "9334ac1cfbc0def1", DataFolderLock.NameFor(@"C:\dashboard-data"));

    /// <summary>
    /// <strong>No session in the identity</strong>: that is the whole difference from the gate.
    /// </summary>
    /// <remarks>
    /// The gate gives one folder a different name in each session; the lock gives it one name. The
    /// frozen vector above holds the lock's name to the folder alone; this holds that it is not
    /// either session's gate name.
    /// </remarks>
    [Fact]
    public void The_lock_name_has_no_session_in_it()
    {
        const string Root = @"C:\dashboard-data";

        var inSessionOne = SingleInstanceGate.NameFor(Root, sessionId: 1);
        var inSessionTwo = SingleInstanceGate.NameFor(Root, sessionId: 2);
        var hex = DataFolderLock.NameFor(Root)[DataFolderLock.NamePrefix.Length..];

        Assert.NotEqual(inSessionOne, inSessionTwo);
        Assert.False(inSessionOne.EndsWith(hex, StringComparison.Ordinal), "the lock has session 1's gate name");
        Assert.False(inSessionTwo.EndsWith(hex, StringComparison.Ordinal), "the lock has session 2's gate name");
    }

    /// <summary>Machine-wide, so another session can see it.</summary>
    [Fact]
    public void The_lock_is_machine_wide() =>
        Assert.StartsWith(@"Global\", DataFolderLock.NameFor(@"C:\dashboard-data"), StringComparison.Ordinal);

    /// <summary>Normalised as the gate is, with the same cases as the gate's test.</summary>
    [Theory]
    [InlineData(@"C:\dashboard-data", @"C:\dashboard-data\")]
    [InlineData(@"C:\dashboard-data", @"C:\DASHBOARD-DATA")]
    [InlineData(@"C:\dashboard-data", @"C:\x\..\dashboard-data")]
    [InlineData(@"C:\dashboard-data", @"C:/dashboard-data")]
    public void Paths_that_mean_the_same_folder_get_the_same_lock(string left, string right) =>
        Assert.Equal(DataFolderLock.NameFor(left), DataFolderLock.NameFor(right));

    /// <summary>The control for the theory above: a constant name would pass it.</summary>
    [Fact]
    public void Different_folders_get_different_locks() =>
        Assert.NotEqual(DataFolderLock.NameFor(@"C:\dashboard-data"), DataFolderLock.NameFor(@"C:\other-data"));

    [Fact]
    public void A_lock_needs_a_root() =>
        Assert.Throws<ArgumentException>(() => DataFolderLock.NameFor("  "));

    [Fact]
    public void The_first_instance_holds_the_lock()
    {
        using var folder = DataFolderLock.Acquire(UniqueRoot());

        Assert.True(folder.IsHeld);
        Assert.False(folder.IsHeldByAnother);
        Assert.False(folder.CouldNotTell);
        Assert.False(folder.TookOverFromACrash);
    }

    /// <summary>
    /// A second acquire from another thread finds the lock held. On another thread, because a
    /// mutex is granted again to the thread that owns it.
    /// </summary>
    [Fact]
    public void A_second_acquire_finds_the_lock_held()
    {
        var root = UniqueRoot();
        using var first = DataFolderLock.Acquire(root);

        var second = RunOnItsOwnThread(() => DataFolderLock.Acquire(root));

        using (second)
        {
            Assert.True(first.IsHeld);
            Assert.False(second.IsHeld);
            Assert.True(second.IsHeldByAnother);
            Assert.False(second.CouldNotTell);
            Assert.Equal(first.Name, second.Name);
        }
    }

    /// <summary>
    /// Another process that holds the lock is seen from this one: the case of a dashboard in
    /// another Windows session.
    /// </summary>
    /// <remarks>
    /// The child is Windows PowerShell, which holds a mutex of the lock's name until its input
    /// closes. The name reaches it through an environment variable, so no quoting can change it.
    /// </remarks>
    [Fact]
    public void A_lock_held_by_another_process_is_held_by_another()
    {
        var root = UniqueRoot();
        var name = DataFolderLock.NameFor(root);

        var start = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            "-NoProfile -NonInteractive -Command \"$m = New-Object System.Threading.Mutex($false, $env:CD_TEST_LOCK); " +
            "if ($m.WaitOne(0)) { 'held' } else { 'not held' }; [Console]::Out.Flush(); [void][Console]::In.ReadLine(); " +
            "$m.ReleaseMutex(); $m.Dispose()\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["CD_TEST_LOCK"] = name;

        using var child = Process.Start(start)!;

        try
        {
            var said = child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Assert.Equal("held", said);

            using var folder = DataFolderLock.Acquire(root);

            Assert.False(folder.IsHeld);
            Assert.True(folder.IsHeldByAnother);
            Assert.Equal(name, folder.Name);
        }
        finally
        {
            child.StandardInput.Close();

            if (!child.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                child.Kill();
            }
        }
    }

    /// <summary>
    /// A lock that cannot be opened is "cannot tell", never "held", and never a throw.
    /// </summary>
    /// <remarks>
    /// The product case is another account's mutex, which Windows refuses this account. That needs a
    /// second account, so this makes the other refusal Windows gives for the same name: a
    /// semaphore already holds it. Both reach the same arm.
    /// </remarks>
    [Fact]
    public void A_lock_that_cannot_be_opened_cannot_tell_and_does_not_block_the_start()
    {
        var root = UniqueRoot();
        using var other = new Semaphore(0, 1, DataFolderLock.NameFor(root));

        using var folder = DataFolderLock.Acquire(root);

        Assert.True(folder.CouldNotTell);
        Assert.False(folder.IsHeld);
        Assert.False(folder.IsHeldByAnother);
        Assert.False(string.IsNullOrEmpty(folder.Problem));
    }

    [Fact]
    public void Disposing_the_lock_frees_it_for_the_next_instance()
    {
        var root = UniqueRoot();

        var first = DataFolderLock.Acquire(root);
        Assert.True(first.IsHeld);
        first.Dispose();

        var next = RunOnItsOwnThread(() => DataFolderLock.Acquire(root));

        using (next)
        {
            Assert.True(next.IsHeld);
        }
    }

    [Fact]
    public void Disposing_the_lock_twice_is_harmless()
    {
        var folder = DataFolderLock.Acquire(UniqueRoot());

        folder.Dispose();
        folder.Dispose();
    }

    /// <summary>A holder that dies without releasing does not block the restart, and is recorded.</summary>
    /// <remarks>The gate's test, for the lock: the abandoning thread's lock is kept so its handle stays open.</remarks>
    [Fact]
    public void An_abandoned_lock_is_taken_over_and_recorded()
    {
        var root = UniqueRoot();

        DataFolderLock? kept = null;
        var abandoner = new Thread(() => kept = DataFolderLock.Acquire(root));

        abandoner.Start();
        Assert.True(abandoner.Join(TimeSpan.FromSeconds(10)), "The abandoning thread did not finish.");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        using var recovered = DataFolderLock.Acquire(root);

        Assert.True(recovered.IsHeld);
        Assert.True(recovered.TookOverFromACrash);

        GC.KeepAlive(kept);
    }

    /// <summary>A root nothing else in this run uses, so tests cannot meet through the lock.</summary>
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
