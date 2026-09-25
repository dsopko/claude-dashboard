using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Pipeline;
using ClaudeDashboard.App.Storage;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// Builds a real <see cref="DecisionRecorder"/> for tests that construct an
/// <see cref="EventConsumer"/> by hand (T1.37).
/// </summary>
/// <remarks>
/// A real recorder over the test's own registry and archive, not a stub: the recorder is on the
/// consumer's hot path, and a test that stubbed it out would be exercising a pipeline the
/// product does not run. Its rows land in the archive channel the test already owns, where they
/// can be asserted or ignored.
/// </remarks>
internal static class TestDecisions
{
    public static DecisionRecorder For(SessionRegistry registry, EventArchive archive) =>
        new(registry, new RosterStore(new RecordingEventSink()), archive, Serilog.Core.Logger.None);
}
