using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeDashboard.App.Hosting;

/// <summary>
/// The first of the two single-instance interlocks: a named <see cref="Mutex"/> (Impl §5.3).
/// </summary>
/// <remarks>
/// <para>
/// The loopback port bind is the second interlock, and the two must observe <em>the same
/// thing</em> or they can disagree while both look healthy. The port a second instance signals
/// comes from the settings file under the data folder root, so the mutex name is keyed to that
/// same root. A fixed name plus a root that <c>CLAUDE_DASHBOARD_HOME</c> can move would let a
/// second instance find the mutex held by an instance that never bound the port its own
/// settings name; it would then report the first one unreachable while a dashboard was plainly
/// running. With one data folder — the shipping case — keying is indistinguishable from a fixed
/// name.
/// </para>
/// <para>
/// <strong>A <see cref="Mutex"/> is thread-affine, and this is the opposite of
/// <c>SingleWriterGuard</c>.</strong> That guard is mutual exclusion without affinity and is
/// re-entrant for whichever thread owns it; this is a Win32 object owned by the thread that
/// waited on it, and only that thread may release it. Two consequences that matter here: the
/// same thread asking twice is granted twice (so a same-thread second <see cref="Acquire"/>
/// reports "first instance", which is why the test for that runs on another thread), and
/// <see cref="Dispose"/> must run on the acquiring thread. In this app both happen on the
/// process entry thread, which is also the WPF UI thread.
/// </para>
/// <para>
/// <strong>Session-local, not machine-wide.</strong> The unprefixed and <c>Local\</c> namespaces
/// are per logon session, which is the right scope for a per-logon tray app: two users signed in
/// at once each get their own dashboard, which is correct, and neither can block the other.
/// </para>
/// </remarks>
public sealed class SingleInstanceGate : IDisposable
{
    /// <summary>The namespace and stem every gate name shares.</summary>
    public const string NamePrefix = @"Local\ClaudeDashboard.SingleInstance.";

    private readonly Mutex _mutex;
    private bool _held;

    private SingleInstanceGate(Mutex mutex, string name, bool held, bool tookOverFromACrash)
    {
        _mutex = mutex;
        _held = held;
        Name = name;
        TookOverFromACrash = tookOverFromACrash;
    }

    /// <summary>The mutex name this gate used.</summary>
    public string Name { get; }

    /// <summary>Whether this process holds the gate, and so is the resident instance.</summary>
    public bool IsFirstInstance => _held;

    /// <summary>
    /// Whether the gate was taken over from a process that died holding it.
    /// </summary>
    /// <remarks>
    /// Windows reports an abandoned mutex by throwing <see cref="AbandonedMutexException"/> from
    /// the wait that <em>succeeded</em> — the waiter owns it. Swallowing that is what makes a
    /// crash recoverable rather than a permanent block on restarting, and it is worth a log line
    /// because it is the only evidence at startup that the previous run did not exit cleanly.
    /// </remarks>
    public bool TookOverFromACrash { get; }

    /// <summary>
    /// The gate name for <paramref name="dataFolderRoot"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>SHA-256, and deliberately not <see cref="string.GetHashCode()"/>.</strong> .NET
    /// randomises string hashing per process, so <c>GetHashCode</c> would give the two instances
    /// two different names. Both would acquire, both would believe they were first, and the only
    /// symptom would be the port bind failing with a message about the port rather than about
    /// the instance — while every in-process test passed, because within one process the hash is
    /// stable. What is needed here is determinism across processes, not collision resistance:
    /// this is a name, not a secret.
    /// </para>
    /// <para>
    /// Normalised first, because Windows paths are case-insensitive and reach here by several
    /// routes: fully resolved so <c>C:\x\..\y</c> and <c>C:\y</c> agree, trailing separator
    /// stripped so <c>C:\y\</c> agrees too, then lowercased.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="dataFolderRoot"/> is null, empty, or whitespace.</exception>
    public static string NameFor(string dataFolderRoot)
    {
        if (string.IsNullOrWhiteSpace(dataFolderRoot))
        {
            throw new ArgumentException("The gate needs a data folder root.", nameof(dataFolderRoot));
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(dataFolderRoot)));

        return NamePrefix + Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>
    /// Tries to take the gate for <paramref name="dataFolderRoot"/>, without waiting.
    /// </summary>
    /// <remarks>
    /// Returns a gate either way. <see cref="IsFirstInstance"/> is the answer; a failure to
    /// acquire is an ordinary outcome, not an error, because "someone else is already running"
    /// is exactly what this exists to detect.
    /// </remarks>
    public static SingleInstanceGate Acquire(string dataFolderRoot)
    {
        var name = NameFor(dataFolderRoot);
        var mutex = new Mutex(initiallyOwned: false, name);

        bool held;
        var abandoned = false;

        try
        {
            held = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without releasing. The wait succeeded and this process
            // now owns it — criterion: a crash must not permanently block a restart.
            held = true;
            abandoned = true;
        }

        return new SingleInstanceGate(mutex, name, held, abandoned);
    }

    /// <summary>Releases the gate if this process holds it, and closes the handle.</summary>
    /// <remarks>
    /// Idempotent, and it never throws. Releasing from a thread that does not own the mutex
    /// raises <see cref="ApplicationException"/>; that would be a bug in the caller rather than
    /// anything the operator can act on, and a throw on the way out would replace a clean exit
    /// with a crash — which is the one thing that leaves the gate abandoned.
    /// </remarks>
    public void Dispose()
    {
        if (_held)
        {
            _held = false;

            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not this thread's to release. Closing the handle below still gives it up when
                // the process exits.
            }
        }

        _mutex.Dispose();
    }

    private static string Normalize(string root)
    {
        string resolved;

        try
        {
            resolved = Path.GetFullPath(root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Unresolvable, but the name still has to be deterministic — two instances given the
            // same bad root must agree with each other.
            resolved = root;
        }

        return resolved
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
    }
}
