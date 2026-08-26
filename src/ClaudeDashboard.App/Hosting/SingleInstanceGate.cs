using System.Diagnostics;
using System.Globalization;
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
/// <strong>Session-local, not machine-wide, and the name carries the session too.</strong> The
/// <c>Local\</c> namespace is per logon session, so the mutex is already scoped that way. The
/// <em>name</em> has to carry it as well, because the name is what <c>/health</c> answers with
/// and what a starting process compares against. Two logon sessions sharing one data folder —
/// which <c>CLAUDE_DASHBOARD_HOME</c> makes configurable, and which Impl Part 8 gives a portable
/// install as a reason for — would otherwise report the same identity, and the second dashboard
/// would read the first as its own duplicate, raise a window on a desktop this user cannot see,
/// and exit saying it had worked.
/// </para>
/// <para>
/// <strong>What this does not fix.</strong> Two signed-in users still cannot both receive hooks:
/// a loopback bind is machine-wide, so the second gets a dashboard that can hear nothing
/// (<see cref="StartupAction.StartWithoutIngress"/>). The scoping here decides only that the
/// second one is told so loudly instead of vanishing. The underlying limit is filed separately;
/// fixing it means the port stops being fixed, which Impl §3.1 decides deliberately.
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
    /// Whether the gate was taken over from a holder that died without releasing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows reports an abandoned mutex by throwing <see cref="AbandonedMutexException"/> from
    /// the wait that <em>succeeded</em> — the waiter owns it. Catching it is correct when it
    /// fires.
    /// </para>
    /// <para>
    /// <strong>It will hardly ever fire, and its absence proves nothing.</strong> An abandoned
    /// mutex needs the kernel object to outlive its owner, which needs some other handle still
    /// open. After an ordinary hard kill the last handle closes with the process and Windows
    /// destroys the object outright, so the next start finds nothing and creates a fresh one —
    /// observed on a real kill-and-restart, not reasoned about. What survives this is a holder
    /// that dies while another process or thread is mid-acquire.
    /// </para>
    /// <para>
    /// So this is a true positive when true and says nothing when false. It is not what makes a
    /// crash recoverable — the OS closing the handle is — and nothing should read a quiet start
    /// as evidence the last one was clean.
    /// </para>
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
    public static string NameFor(string dataFolderRoot) =>
        NameFor(dataFolderRoot, CurrentSessionId);

    /// <summary>
    /// The gate name for <paramref name="dataFolderRoot"/> in logon session
    /// <paramref name="sessionId"/>.
    /// </summary>
    /// <remarks>
    /// The session is part of the identity, not only part of the namespace — see this type's
    /// remarks for the case that makes the difference. Taking it as a parameter is what lets a
    /// test pin the naming rule to a value computed outside this process, which is the only way
    /// to observe that the rule is stable across processes at all.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="dataFolderRoot"/> is null, empty, or whitespace.</exception>
    public static string NameFor(string dataFolderRoot, int sessionId)
    {
        if (string.IsNullOrWhiteSpace(dataFolderRoot))
        {
            throw new ArgumentException("The gate needs a data folder root.", nameof(dataFolderRoot));
        }

        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{sessionId}|{Normalize(dataFolderRoot)}");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));

        return NamePrefix + Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>This process's Windows logon session.</summary>
    /// <remarks>
    /// Read once. It cannot change for the life of a process, and reading it opens a process
    /// handle that there is no reason to open on every health request.
    /// </remarks>
    private static int CurrentSessionId { get; } = ReadSessionId();

    private static int ReadSessionId()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.SessionId;
        }
        catch (InvalidOperationException)
        {
            // Degrade, never crash. Losing the session from the identity costs the two-users
            // distinction; failing to start costs the dashboard.
            return 0;
        }
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
            // A holder died without releasing while this handle kept the object alive. The wait
            // succeeded and this process now owns it. See TookOverFromACrash for why this is
            // rare, and for why it is not what makes a restart possible after a crash.
            held = true;
            abandoned = true;
        }

        return new SingleInstanceGate(mutex, name, held, abandoned);
    }

    /// <summary>Releases the gate if this process holds it, and closes the handle.</summary>
    /// <remarks>
    /// <para>
    /// Idempotent, and it never throws. Releasing from a thread that does not own the mutex
    /// raises <see cref="ApplicationException"/>; that would be a bug in the caller rather than
    /// anything the operator can act on, and a throw on the way out would replace a clean exit
    /// with a crash.
    /// </para>
    /// <para>
    /// <strong>This is not what frees the gate for the next launch.</strong> Windows closes every
    /// handle at process exit, so a dashboard that exits releases the gate whether this ran or
    /// not. The evidence is a kill-and-restart of a real dashboard: the next launch took the gate
    /// as an ordinary first instance. Deleting this release also leaves the suite green, but that
    /// shows only that no test depends on the call — an uncovered branch would do the same — so
    /// it is not evidence for the sentence above.
    /// </para>
    /// <para>
    /// What the release does matter for is a holder that carries on running after giving the gate
    /// up, which is what the in-process tests do and what this process never does.
    /// </para>
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
