using System.Diagnostics;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// Collects WPF's binding diagnostics while a window is realized. A misspelled path fails silently in WPF: the
/// element shows nothing, and only this says so. One copy for every realized-window test (T1.71: the two windows
/// are realized together).
/// </summary>
internal sealed class BindingErrorWatch : IDisposable
{
    private readonly Listener _listener = new();
    private readonly SourceLevels _previous;

    public BindingErrorWatch()
    {
        PresentationTraceSources.Refresh();
        _previous = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(_listener);
    }

    public IReadOnlyList<string> Problems => _listener.Problems;

    public void Dispose()
    {
        PresentationTraceSources.DataBindingSource.Listeners.Remove(_listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = _previous;
        _listener.Dispose();
    }

    private sealed class Listener : TraceListener
    {
        public List<string> Problems { get; } = [];

        public override void Write(string? message) => Record(message);

        public override void WriteLine(string? message) => Record(message);

        private void Record(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Problems.Add(message);
            }
        }
    }
}
