using System.ComponentModel;
using System.Globalization;
using ClaudeDashboard.App.Ui;

namespace ClaudeDashboard.App.Hosting;

/// <summary>
/// Whether the dashboard can actually hear anything, and what to say when it cannot (Impl §5.3).
/// </summary>
/// <remarks>
/// <para>
/// <strong>"No sessions" and "I cannot hear anything" look identical to the operator.</strong>
/// A dashboard whose port was taken by a stranger starts, shows an empty window, and a grey
/// tray — which is exactly what a quiet afternoon looks like. The log records the fault, but
/// the log is not where anyone looks at a glance. So the fault leads the tray tooltip, under the
/// same rule as pause and mute (Impl §5.2): when the glyph is not telling the plain truth, the
/// first words say why.
/// </para>
/// <para>
/// No new tray colour. Adding one would be a design change, and the design document is the
/// authority on what the glyph may say.
/// </para>
/// <para>
/// <strong>Each fault says what to do, in the tray and in the window</strong> (T1.57, issue #14).
/// The tray line is short and ends in the remedy; the window's notice row carries the long form.
/// It is the first source on the <see cref="NoticeBoard"/>, so the tooltip shows it once, first,
/// through the same path as every other notice. Nothing retries: the dashboard asks once, by
/// binding, and says what to do.
/// </para>
/// </remarks>
public sealed class IngressStatus : INotice
{
    private IngressStatus(int port, string? fault, string? notice)
    {
        Port = port;
        Fault = fault;
        Text = notice;
    }

    /// <inheritdoc/>
    /// <remarks>Never raised: the status is fixed for the life of the process.</remarks>
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }

    /// <summary>Ingress is bound to <paramref name="port"/> and hooks will arrive.</summary>
    public static IngressStatus Healthy(int port) => new(port, null, null);

    /// <summary>
    /// The configured port could not be used, so no hook addressed to it will ever arrive.
    /// </summary>
    /// <param name="port">The port named in the tray line: the derived port when the walk ran out.</param>
    /// <param name="firstTried">The first port tried; <paramref name="port"/> when not given.</param>
    /// <param name="lastTried">The last port tried; <paramref name="port"/> when not given.</param>
    /// <param name="settingsFile">The full path of <c>settings.json</c>, named in the window text.</param>
    /// <remarks>
    /// The tooltip line is short on purpose: it goes in front of the counts, and a tray tooltip
    /// that runs to a paragraph is one nobody reads. It ends in the remedy, and "restart" already
    /// says that nothing arrives until then (T1.57). The long form is the window notice and the log.
    /// </remarks>
    public static IngressStatus Unavailable(int port, int? firstTried = null, int? lastTried = null, string settingsFile = "settings.json") =>
        new(
            port,
            string.Create(CultureInfo.CurrentCulture, $"port {port} taken · free a port and restart"),
            $"The dashboard cannot receive anything: every port it tried is in use ({Number(firstTried ?? port)} to {Number(lastTried ?? port)}). " +
            $"Free one of them, or pin a free port with \"port\" in {settingsFile}, then restart the dashboard. " +
            "Claude Code's settings need no change: the hook finds the new port by itself.");

    /// <summary>The fault for a port choice that secured no port, or the one a start declined (T1.57).</summary>
    /// <param name="choice">How the port was chosen: the pin, and every candidate tried.</param>
    /// <param name="settingsFile">The full path of <c>settings.json</c>.</param>
    public static IngressStatus NotBound(PortChoice choice, string settingsFile)
    {
        ArgumentNullException.ThrowIfNull(choice);

        if (choice.PinRefused)
        {
            return PinnedPortTaken(choice.Port);
        }

        return choice.Attempts.Count == 0
            ? Unavailable(choice.Port, settingsFile: settingsFile)
            : Unavailable(choice.Port, choice.Attempts[0].Port, choice.Attempts[^1].Port, settingsFile);
    }

    /// <summary>
    /// A port the operator pinned in <c>settings.json</c> is held by something else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Its own line because it is a different situation, not a worse one.</strong>
    /// <see cref="Unavailable"/> describes a port taken out from under the dashboard — bad luck.
    /// This describes a port the operator chose, which the dashboard then <em>declined to move off
    /// on purpose</em>, and it says "pinned" so that the person reading knows the setting is the
    /// thing to change.
    /// </para>
    /// <para>
    /// The dashboard does not fall through to a derived port here, and the reason is stronger than
    /// respecting the setting: <strong>a pin is usually a contract with something outside the
    /// dashboard</strong> — a firewall rule, a proxy entry, a script that posts to it. For all of
    /// those, a dashboard quietly working on a <em>different</em> port is worse than one that does
    /// not work, because the outside thing still points at the pinned port, still fails, and the
    /// dashboard now looks healthy while it does. Falling through would satisfy the pin somewhere
    /// nobody is looking.
    /// </para>
    /// </remarks>
    public static IngressStatus PinnedPortTaken(int port) =>
        new(
            port,
            string.Create(CultureInfo.CurrentCulture, $"pinned port {port} taken · unpin it or free it, then restart"),
            $"The dashboard cannot receive anything: port {Number(port)} is pinned in settings.json and another program " +
            "holds it. Free that port, or change or remove the \"port\" setting, then restart the dashboard.");

    /// <summary>The port ingress was asked to use — the one hooks are addressed to.</summary>
    public int Port { get; }

    /// <summary>The tooltip line, or null when there is nothing wrong.</summary>
    public string? Fault { get; }

    /// <summary>The window's notice text: the long form, with the remedy. Null when there is nothing wrong.</summary>
    public string? Text { get; }

    /// <inheritdoc/>
    public string? TrayText => Fault;

    /// <inheritdoc/>
    public bool IsShown => Fault is not null;

    private static string Number(int port) => port.ToString(CultureInfo.CurrentCulture);

    /// <summary>Whether hooks can reach this process.</summary>
    public bool CanReceiveHooks => Fault is null;
}
