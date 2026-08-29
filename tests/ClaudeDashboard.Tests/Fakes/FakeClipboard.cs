using ClaudeDashboard.App.Ui;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// An <see cref="IClipboard"/> that records what it was handed instead of touching the real one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The real clipboard is never written by the suite, deliberately.</strong> A test that
/// wrote to it would destroy whatever the operator had copied, to prove a point about a row.
/// Save-and-restore is racy and still destructive. So <c>WindowsClipboard</c> is the one class
/// here with no test at all, and everything above it is tested through this.
/// </para>
/// <para>
/// <strong>What this cannot prove</strong>, recorded in the acceptance document beside the rest:
/// that the real Windows clipboard received anything, that the format is one another application
/// will accept, and that the operator can paste it. Those are only reachable on a machine with
/// their consent.
/// </para>
/// </remarks>
internal sealed class FakeClipboard : IClipboard
{
    private readonly List<string> _written = [];

    /// <summary>What <see cref="TrySet"/> answers. Succeeds unless a test says otherwise.</summary>
    public bool Succeeds { get; set; } = true;

    /// <summary>Everything it was asked to write, in order — including failed attempts.</summary>
    /// <remarks>
    /// Failed attempts are recorded too, so a test can tell "the copy was refused" from "the copy
    /// was never attempted". Those are different defects and they look identical on a clipboard
    /// that only keeps successes.
    /// </remarks>
    public IReadOnlyList<string> Written => _written;

    /// <summary>The last thing it was asked to write, or null.</summary>
    public string? Last => _written.Count == 0 ? null : _written[^1];

    /// <inheritdoc/>
    public bool TrySet(string text)
    {
        _written.Add(text);

        return Succeeds;
    }
}
