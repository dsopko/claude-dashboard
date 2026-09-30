using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core.Ports;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// A tray view model for tests that build a <see cref="MainWindow"/>, which binds its header's
/// Mute all to the tray (T1.47).
/// </summary>
internal static class TestTrays
{
    /// <summary>A tray over <paramref name="projection"/>, with modes and a sink the test can read.</summary>
    public static TrayViewModel For(
        SessionProjection projection,
        SettableSoundModes? modes = null,
        RecordingEventSink? sink = null,
        FakeClock? clock = null) =>
        new(
            projection,
            modes ?? new SettableSoundModes(),
            sink ?? new RecordingEventSink(),
            clock ?? new FakeClock(),
            IngressStatus.Healthy(DashboardSettings.DefaultPort),
            Logger.None);
}

/// <summary>The global sound modes, set by the test rather than by the engine.</summary>
internal sealed class SettableSoundModes : ISoundModeReader
{
    public bool IsMonitoringPaused { get; set; }

    public DateTimeOffset? AllMutedUntil { get; set; }
}
