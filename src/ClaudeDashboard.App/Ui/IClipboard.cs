using Serilog;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// Puts text on the Windows clipboard, or says it could not (issue #15).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A port because the clipboard is shared state owned by the whole desktop.</strong> It is
/// not ours, another process can be holding it at any instant, and a test must never write to the
/// operator's real one — that would destroy whatever they had copied, to prove a point.
/// </para>
/// <para>
/// It lives in App rather than Core: Core has no operating system. Nothing in the domain knows a
/// clipboard exists.
/// </para>
/// </remarks>
public interface IClipboard
{
    /// <summary>Puts <paramref name="text"/> on the clipboard. True if it got there.</summary>
    /// <remarks>
    /// <strong>Never throws, and returns the outcome instead.</strong> The caller is a UI command
    /// on the dispatcher thread, which is the worst place in the application to let an exception
    /// out. A boolean makes the failure something the row can show rather than something that
    /// takes the window with it.
    /// </remarks>
    bool TrySet(string text);
}

/// <summary>The real Windows clipboard.</summary>
/// <remarks>
/// <para>
/// <strong>One attempt, and no retry loop. THE RETRYING OVERLOAD DOES NOT EXIST HERE.</strong>
/// <c>SetDataObject(data, copy, retryTimes, retryDelay)</c> is <em>WinForms</em>'
/// <c>Clipboard</c>. WPF's exposes only <c>SetDataObject(object)</c> and
/// <c>SetDataObject(object, bool)</c> — checked by reflecting over the type, after the four-argument
/// call failed to compile. An earlier version of this class was designed around retries that were
/// never available.
/// </para>
/// <para>
/// <strong>Retrying by hand would be worse than not retrying.</strong> This runs on the dispatcher
/// thread, so a loop that slept between attempts would freeze the window for as long as it looped
/// — trading a visible, recoverable failure the operator can act on for an invisible stall they
/// cannot. The failure surface on the row exists precisely so that one attempt is enough.
/// </para>
/// <para>
/// <c>copy: true</c> asks Windows to keep the value on the clipboard after this process exits,
/// which is what someone who copies an id and then closes the dashboard expects. Whether
/// <c>SetText</c> does the same is not asserted here — the explicit flag removes the question
/// rather than answering it.
/// </para>
/// <para>
/// This class is deliberately <strong>not covered by the suite</strong>. Exercising it would write
/// to the operator's real clipboard and destroy what they had copied; save-and-restore is both
/// racy and still destructive. If it is ever to be covered it is a hardware-card row with their
/// consent, not a test. What the suite covers is every caller of the port, through a fake.
/// </para>
/// </remarks>
public sealed class WindowsClipboard : IClipboard
{
    private readonly ILogger _logger;

    /// <summary>Creates the clipboard adapter.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is null.</exception>
    public WindowsClipboard(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <inheritdoc/>
    public bool TrySet(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            System.Windows.Clipboard.SetDataObject(text, copy: true);

            return true;
        }
        catch (Exception ex)
        {
            // Another process is holding the clipboard, or the desktop is in a state where it
            // cannot be opened at all. The operator loses the copy and
            // nothing else. The catch is deliberately not narrowed: the contract this port makes
            // is "never throws", and narrowing it would leave the one nobody predicted to reach
            // the dispatcher.
            _logger.Warning(ex, "The clipboard could not be written. The copy did not happen.");

            return false;
        }
    }
}
