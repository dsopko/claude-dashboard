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

    private readonly int? _port;

    /// <summary>
    /// A port the operator has pinned, or <see langword="null"/> when they have not (Impl §3.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Nullable because "unset" is a real state, and the feature collapses without
    /// it.</strong> A pinned port is honoured ahead of everything else — attempt 0, before
    /// <c>port.txt</c> and before the derivation. That is only implementable if a pin can be told
    /// apart from a default: were this a plain <see langword="int"/>, every operator who has never
    /// opened <c>settings.json</c> would carry <see cref="DefaultPort"/> and be indistinguishable
    /// from someone who typed it, and honouring that as a pin would put <em>every</em> user back on
    /// one machine-wide port — reinstating the collision T1.21 exists to remove, for exactly the
    /// people it exists to help.
    /// </para>
    /// <para>
    /// <strong>A nullable value rather than a second "is set" flag</strong>, because a flag is a
    /// second field that can disagree with the field it describes: two sources of truth for one
    /// fact. JSON already distinguishes an absent key from a present one, so the round-trip carries
    /// "unset" for free.
    /// </para>
    /// <para>
    /// <strong>An out-of-range value becomes unset, NOT <see cref="DefaultPort"/>.</strong> The
    /// old coercion would turn a typo into a hard pin on the one port most likely to be contended,
    /// which is the worst outcome available: the operator would be pinned by accident to the port
    /// the whole feature exists to stop everyone sharing. Falling through to the derivation is
    /// what a mistyped port should do, and <c>SettingsStore</c> logs that it happened rather than
    /// leaving it silent.
    /// </para>
    /// </remarks>
    [JsonPropertyName("port")]
    public int? Port
    {
        get => _port;
        init => _port = value is > 0 and <= 65535 ? value : null;
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

    /// <summary>
    /// The operator's rosters: named sets of session names that group together (issue #16).
    /// </summary>
    /// <remarks>
    /// Absent for every operator who has never made one, which is the ordinary case and produces
    /// no rosters and no log line. A malformed value loses the whole file exactly as a malformed
    /// <c>port</c> does — see <see cref="RosterSettings"/> for why that consistency was chosen over
    /// isolating this one section.
    /// </remarks>
    [JsonPropertyName("rosters")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Rosters { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>
    /// Value equality over every member, <strong>the rosters compared by content</strong>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written by hand for the same measured reason <see cref="ClaudeDashboard.Core.Group"/> is:
    /// the synthesized version compares <see cref="Rosters"/> by <em>reference</em>, so a settings
    /// record that had been through the file compared unequal to the one that produced it even when
    /// every roster in it matched. That is not an abstract concern — it is what the round-trip test
    /// caught the moment this section was added.
    /// </para>
    /// <para>
    /// <c>SettingsPropertiesAreAllCompared</c> in the test suite asserts the exact set of properties
    /// this method has to cover, so a sixth member cannot be added and silently left out of it.
    /// </para>
    /// </remarks>
    public bool Equals(DashboardSettings? other) =>
        other is not null &&
        Port == other.Port &&
        Logging == other.Logging &&
        Sound == other.Sound &&
        Window == other.Window &&
        SameRosters(Rosters, other.Rosters);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Port);
        hash.Add(Logging);
        hash.Add(Sound);
        hash.Add(Window);

        // Count only: two books with the same rosters in a different dictionary order must hash
        // alike, and hashing the contents in enumeration order would not guarantee that.
        hash.Add(Rosters.Count);

        return hash.ToHashCode();
    }

    private static bool SameRosters(
        IReadOnlyDictionary<string, IReadOnlyList<string>> left,
        IReadOnlyDictionary<string, IReadOnlyList<string>> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (name, members) in left)
        {
            if (!right.TryGetValue(name, out var theirs) ||
                !(members ?? []).SequenceEqual(theirs ?? [], StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
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
