using ClaudeDashboard.App.Configuration;
using Serilog;

namespace ClaudeDashboard.App.Hosting;

/// <summary>
/// Says where ingress is once it is bound, and takes the statement back on the way out
/// (issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the <em>when</em>. <see cref="PortFile"/> and <see cref="ListeningFile"/> are
/// the <em>how</em>.</strong> Those two own the format, the atomicity and the failure handling of
/// one file each. This owns the single question neither of them can answer: whether a dashboard
/// that has just bound a socket is in a position to invite hook traffic at all.
/// </para>
/// <para>
/// <strong>Nothing is announced unless ingress is actually bound.</strong> Hook payloads carry the
/// operator's prompts. If the configured port is held by something that is not us, announcing it
/// would point <c>post-status.cmd</c> at a stranger and post their prompts to it, on every turn,
/// until somebody noticed. So a dashboard that cannot hear announces nothing, says so, and leaves
/// whatever is on the port alone. That guard is carried over from <c>HookLifecycle</c> unchanged,
/// and it is the reason this type exists rather than two calls at the call site.
/// </para>
/// <para>
/// <strong>It replaced a type that wrote Claude Code's settings, and it writes none.</strong>
/// <c>HookLifecycle</c> merged eight HTTP handlers into the operator's file at every start and
/// removed them at every quit; issue #29 replaced that with one command handler installed once.
/// What is left of the old lifecycle is these two files in the dashboard's own data folder, which
/// is why this now lives beside <see cref="IngressStatus"/> rather than in <c>Setup</c> — after
/// T1.28, <c>Setup</c> is exactly the code that touches the operator's file.
/// </para>
/// </remarks>
public sealed class IngressAnnouncement
{
    private readonly DashboardPaths _paths;
    private readonly IngressStatus _ingress;
    private readonly ILogger _logger;

    /// <summary>Announces the port <paramref name="ingress"/> describes.</summary>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public IngressAnnouncement(DashboardPaths paths, IngressStatus ingress, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(ingress);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _ingress = ingress;
        _logger = logger;
    }

    /// <summary>
    /// Records the bound port in <c>port.txt</c> and announces it in <c>listening.txt</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called after the socket is bound, never before. Between announcing and binding there would
    /// be a window in which the script posts to a port nothing answers, which is a smaller version
    /// of the state the whole task exists to remove.
    /// </para>
    /// <para>
    /// <strong>Two files, and only one of them is ever deleted.</strong> <c>port.txt</c> is written
    /// here exactly as it always was — it is the port this user tries first at the next start
    /// (Impl §3.1) and the only thing that tells a second launch where a running instance is
    /// (§5.3), so it outlives the process on purpose. <c>listening.txt</c> is the one that says
    /// <em>now</em>, and <see cref="Withdraw"/> takes it away.
    /// </para>
    /// <para>
    /// <strong>Neither failure is fatal.</strong> A dashboard that cannot write its own data folder
    /// still runs and still shows the operator their sessions; it receives nothing until a later
    /// start succeeds. Refusing to start would trade the application for a text file.
    /// </para>
    /// </remarks>
    /// <returns>Whether the announcement was made.</returns>
    public bool Announce()
    {
        if (!_ingress.CanReceiveHooks)
        {
            _logger.Warning(
                "Not announcing ingress: port {Port} is held by another process, so an announcement " +
                "would send hook payloads — including prompts — to whatever holds it.",
                _ingress.Port);

            return false;
        }

        if (!PortFile.Write(_paths, _ingress.Port))
        {
            _logger.Warning("Could not write {PortFile}.", _paths.PortFile);
        }

        if (!ListeningFile.Write(_paths, _ingress.Port))
        {
            _logger.Warning(
                "Could not write {ListeningFile}, so Claude Code's hook will find no dashboard and this " +
                "one will receive nothing until it is written.",
                _paths.ListeningFile);

            return false;
        }

        _logger.Information(
            "Ingress announced on port {Port} in {ListeningFile}.", _ingress.Port, _paths.ListeningFile);

        return true;
    }

    /// <summary>
    /// Withdraws the announcement, so the hook finds no dashboard and does nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>It deletes <c>listening.txt</c> and nothing else. Never <c>port.txt</c>.</strong>
    /// That is the single easiest mistake to make in this feature and the most expensive: the loss
    /// would be invisible until the next start, and would cost the port continuity of Impl §3.1
    /// and the <c>POST /show</c> hand-over of §5.3, neither of which announces its own absence.
    /// </para>
    /// <para>
    /// <strong>Called from four places, and all four are needed.</strong> The process exception
    /// handlers, WPF's <c>SessionEnding</c> for a logoff, the ordinary quit, and the <c>finally</c>
    /// in <c>Main</c> — which is the one the old lifecycle missed. Both <c>catch</c> blocks in
    /// <c>Main</c> return without reaching the ordinary quit, so a throw after the bind and before
    /// the window ran left the announcement standing on an otherwise orderly exit.
    /// </para>
    /// <para>
    /// <strong>Idempotent, because more than one of those runs for a single exit.</strong> A file
    /// that was never there is a success.
    /// </para>
    /// <para>
    /// <strong>Residual: a kill reaches none of the four.</strong> <c>TerminateProcess</c> — Task
    /// Manager, <c>taskkill /f</c>, <c>Process.Kill</c> — a CLR fast-fail, and power loss all leave
    /// the file behind naming the last bound port. Until the next start the script posts to
    /// whatever holds that port. It is the exposure Impl §9.3 already records for a hard kill, and
    /// the unconditional overwrite in <see cref="Announce"/> is what closes it.
    /// </para>
    /// </remarks>
    /// <returns>Whether the announcement is gone.</returns>
    public bool Withdraw() => ListeningFile.Delete(_paths);
}
