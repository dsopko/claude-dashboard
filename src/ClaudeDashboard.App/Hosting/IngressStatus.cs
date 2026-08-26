using System.Globalization;

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
/// </remarks>
public sealed class IngressStatus
{
    private IngressStatus(int port, string? fault)
    {
        Port = port;
        Fault = fault;
    }

    /// <summary>Ingress is bound to <paramref name="port"/> and hooks will arrive.</summary>
    public static IngressStatus Healthy(int port) => new(port, null);

    /// <summary>
    /// The configured port could not be used, so no hook addressed to it will ever arrive.
    /// </summary>
    /// <remarks>
    /// The tooltip line is short on purpose: it goes in front of the counts, and a tray tooltip
    /// that runs to a paragraph is one nobody reads. The long form is in the log.
    /// </remarks>
    public static IngressStatus Unavailable(int port) =>
        new(port, string.Create(CultureInfo.CurrentCulture, $"port {port} taken · not receiving hooks"));

    /// <summary>The port ingress was asked to use — the one hooks are addressed to.</summary>
    public int Port { get; }

    /// <summary>The tooltip line, or null when there is nothing wrong.</summary>
    public string? Fault { get; }

    /// <summary>Whether hooks can reach this process.</summary>
    public bool CanReceiveHooks => Fault is null;
}
