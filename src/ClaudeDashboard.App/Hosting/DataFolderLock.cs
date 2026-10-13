using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeDashboard.App.Hosting;

/// <summary>
/// The third single-instance interlock: one dashboard per data folder, across Windows sessions
/// (Impl §5.3, issue #150).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A sibling of <see cref="SingleInstanceGate"/>, not a change to it.</strong> The gate is
/// per logon session on purpose: a second session as the same user must not have a window raised
/// on a desktop it cannot see. But two sessions as one account (Windows Server, with "restrict each
/// user to a single session" off) then got a gate each and started two dashboards on one data
/// folder: one <c>dashboard.db</c>, one <c>listening.txt</c>, and the second dashboard took the
/// hooks from the first without a word. This lock has no session in its name, so the second start
/// finds it held, and exits with a message box (<see cref="StartupAction.ExitForOtherSession"/>).
/// </para>
/// <para>
/// <strong><c>Global\</c>, and keyed by the folder alone.</strong> A <c>Local\</c> mutex is per
/// logon session and could not see the other session at all. A <c>Global\</c> mutex needs no
/// privilege. Two users have two data folders, so two names, and one user's lock never stops
/// another user's dashboard.
/// </para>
/// <para>
/// <strong>"Cannot tell" starts.</strong> Another account pointed at the same folder through
/// <c>CLAUDE_DASHBOARD_HOME</c> owns the mutex, and Windows refuses this account a handle to it.
/// That is <see cref="CouldNotTell"/>: the dashboard starts, and logs one Warning that sharing a
/// data folder between accounts is not supported. Degrade, never crash.
/// </para>
/// <para>
/// Thread-affine, as the gate is: acquired and disposed on the process entry thread, and a second
/// acquire on the owning thread is granted again (so the tests take it from another thread).
/// </para>
/// </remarks>
public sealed class DataFolderLock : IDisposable
{
    /// <summary>The namespace and stem every lock name shares.</summary>
    public const string NamePrefix = @"Global\ClaudeDashboard.DataFolder.";

    private readonly Mutex? _mutex;
    private bool _held;

    private DataFolderLock(Mutex? mutex, string name, bool held, bool couldNotTell, bool tookOverFromACrash, string? problem)
    {
        _mutex = mutex;
        _held = held;
        Name = name;
        CouldNotTell = couldNotTell;
        TookOverFromACrash = tookOverFromACrash;
        Problem = problem;
    }

    /// <summary>The mutex name this lock used.</summary>
    public string Name { get; }

    /// <summary>Whether this process holds the lock.</summary>
    public bool IsHeld => _held;

    /// <summary>
    /// Whether the mutex could not be created or opened, so this process cannot tell whether
    /// another dashboard has the folder. The start goes on.
    /// </summary>
    public bool CouldNotTell { get; }

    /// <summary>Whether another process holds the lock: a dashboard on this folder in another session.</summary>
    public bool IsHeldByAnother => !_held && !CouldNotTell;

    /// <summary>
    /// Whether the lock was taken over from a holder that died without releasing it. As rare, and
    /// as one-sided, as <see cref="SingleInstanceGate.TookOverFromACrash"/>.
    /// </summary>
    public bool TookOverFromACrash { get; }

    /// <summary>What Windows said when the mutex could not be opened, for the log. Null otherwise.</summary>
    public string? Problem { get; }

    /// <summary>The lock name for <paramref name="dataFolderRoot"/>.</summary>
    /// <remarks>
    /// SHA-256 of the normalised root, for the gate's reasons: the name must be the same in every
    /// process, and <see cref="string.GetHashCode()"/> is not. The root is normalised by
    /// <see cref="SingleInstanceGate.Normalize"/>, so the two names agree on what one folder is.
    /// <strong>No session in the identity:</strong> that is the whole difference from the gate.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="dataFolderRoot"/> is null, empty, or whitespace.</exception>
    public static string NameFor(string dataFolderRoot)
    {
        if (string.IsNullOrWhiteSpace(dataFolderRoot))
        {
            throw new ArgumentException("The data folder lock needs a data folder root.", nameof(dataFolderRoot));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(SingleInstanceGate.Normalize(dataFolderRoot)));

        return NamePrefix + Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>Tries to take the lock for <paramref name="dataFolderRoot"/>, without waiting.</summary>
    /// <remarks>
    /// Returns a lock in every case. <see cref="IsHeld"/>, <see cref="IsHeldByAnother"/> and
    /// <see cref="CouldNotTell"/> are the answer; none of them is an error.
    /// </remarks>
    public static DataFolderLock Acquire(string dataFolderRoot)
    {
        var name = NameFor(dataFolderRoot);
        Mutex mutex;

        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Another account's mutex (access denied), or another kind of object with this name.
            // Either way this process cannot tell, and a dashboard that cannot tell starts.
            return new DataFolderLock(null, name, held: false, couldNotTell: true, tookOverFromACrash: false, ex.Message);
        }

        bool held;
        var abandoned = false;

        try
        {
            held = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // A holder died without releasing while another handle kept the object alive. The wait
            // succeeded, and this process owns the lock.
            held = true;
            abandoned = true;
        }

        return new DataFolderLock(mutex, name, held, couldNotTell: false, abandoned, problem: null);
    }

    /// <summary>Releases the lock if this process holds it, and closes the handle.</summary>
    /// <remarks>
    /// Idempotent, and it never throws, as the gate's. Windows closes the handle at process exit
    /// in any case; this gives the lock up at the same point as the gate.
    /// </remarks>
    public void Dispose()
    {
        if (_held)
        {
            _held = false;

            try
            {
                _mutex!.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not this thread's to release. Closing the handle below gives it up at exit.
            }
        }

        _mutex?.Dispose();
    }
}
