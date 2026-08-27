using System.Text.Json.Serialization;
using ClaudeDashboard.App.Hosting;

namespace ClaudeDashboard.App.Configuration;

/// <summary>
/// The shape of <c>settings.json</c> (Impl Part 8).
/// </summary>
/// <remarks>
/// <para>
/// This type lives in App, not Core, because it is the shape of a <em>file</em> — a host
/// artifact. Core may not read one, and settings.json also carries host and UI concerns (the
/// ingress port, and later the default view and always-on-top) that Core has no vocabulary
/// for. The domain thresholds that belong here keep their authoritative defaults in Core —
/// <see cref="ClaudeDashboard.Core.SoundPolicyOptions"/> holds TS §IV.5's ladder — and App
/// maps the file onto them. The mapping runs one way only: file → domain options.
/// </para>
/// <para>
/// Only the settings something actually reads today are here. Impl Part 8 also lists sound
/// choices, mutes, default view and always-on-top; each belongs to the task that first
/// consumes it, so that no setting exists which nothing honours.
/// </para>
/// </remarks>
public sealed record DashboardSettings
{
    /// <summary>
    /// The bottom of this machine's ingress range, in the private range (Impl §3.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>No longer the port anything binds, and the old reason for it being fixed was
    /// false.</strong> This said "fixed rather than dynamic because the hook URL registered in
    /// Claude Code's settings must stay stable". That was true while first-run setup wrote the
    /// hooks once. <strong>§9.3 now registers them at every start and removes them at every
    /// quit</strong>, and a running Claude Code session was measured picking up both the addition
    /// and the removal without restarting — so the URL is rebuilt from whatever port was actually
    /// bound, and never needed a fixed one.
    /// </para>
    /// <para>
    /// A comment whose stated reason has outlived the design it described is the defect this
    /// project spent 26 August removing, so it is corrected here rather than left standing beside
    /// the change that falsified it (T1.21).
    /// </para>
    /// </remarks>
    public const int DefaultPort = 52789;

    private readonly int _port = DefaultPort;

    /// <summary>The bottom of this user's ingress port range (Impl §3.1).</summary>
    /// <remarks>
    /// <para>
    /// <strong>The base, not the bound port.</strong> Ingress binds
    /// <see cref="PortSelection.Derive"/>'s offset above this, so that two users on one machine do
    /// not contend for a single loopback port — see <c>PortSelection</c> for why binding is the
    /// only question ever asked. To pin one specific port instead, write it into <c>port.txt</c>:
    /// that is §3.1's first attempt and it is honoured before the derivation.
    /// </para>
    /// <para>
    /// Out-of-range values fall back to <see cref="DefaultPort"/> rather than throwing: a typo
    /// in a hand-edited file must not stop the dashboard starting.
    /// </para>
    /// </remarks>
    [JsonPropertyName("port")]
    public int Port
    {
        get => _port;
        init => _port = value is > 0 and <= 65535 ? value : DefaultPort;
    }

    /// <summary>How the rolling log files are kept (Impl Part 8).</summary>
    [JsonPropertyName("logging")]
    public LoggingSettings Logging { get; init; } = new();

    /// <summary>How loud, and how insistent, the sounds are (Impl Part 7, Part 8).</summary>
    [JsonPropertyName("sound")]
    public SoundSettings Sound { get; init; } = new();

    /// <summary>Where the window was left, and whether it floats (Impl §5.4).</summary>
    [JsonPropertyName("window")]
    public WindowSettings Window { get; init; } = new();
}

/// <summary>Rolling-file log retention (Impl Part 8).</summary>
/// <remarks>
/// Nothing in the specs sizes these, so the defaults are chosen for the shape of this process:
/// it runs from logon to logoff every day, so files roll daily and a fortnight is kept — long
/// enough to look into "it went quiet last Tuesday" without unbounded growth. The size cap is
/// the backstop for a fault that logs in a loop, which is the realistic way a tray app fills a
/// disk.
/// </remarks>
public sealed record LoggingSettings
{
    /// <summary>Two weeks of daily files.</summary>
    public const int DefaultRetainedFiles = 14;

    /// <summary>16 MB per file before it rolls again.</summary>
    public const long DefaultFileSizeLimitBytes = 16L * 1024 * 1024;

    private readonly int _retainedFileCount = DefaultRetainedFiles;
    private readonly long _fileSizeLimitBytes = DefaultFileSizeLimitBytes;

    /// <summary>How many rolled files to keep.</summary>
    [JsonPropertyName("retainedFileCount")]
    public int RetainedFileCount
    {
        get => _retainedFileCount;
        init => _retainedFileCount = value > 0 ? value : DefaultRetainedFiles;
    }

    /// <summary>The size at which a file rolls within the day.</summary>
    [JsonPropertyName("fileSizeLimitBytes")]
    public long FileSizeLimitBytes
    {
        get => _fileSizeLimitBytes;
        init => _fileSizeLimitBytes = value > 0 ? value : DefaultFileSizeLimitBytes;
    }
}
