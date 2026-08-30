using System.Globalization;
using System.IO;
using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Hosting;

/// <summary>
/// Saying where ingress is once it is bound, and taking the statement back (issue #29).
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is what is left of <c>HookLifecycle</c>, and the guard it carried is the reason it
/// is still a type.</strong> The old lifecycle wrote Claude Code's settings at every start; this
/// writes two files in the dashboard's own folder. What survived unchanged is the check that
/// nothing is announced unless ingress is genuinely bound — and that check was never tested by any
/// of the parts, only by the composition.
/// </para>
/// <para>
/// <strong>Every port here is deliberately not <c>DefaultPort</c>.</strong> A test on the default
/// port cannot tell "the bound port" from "the compiled-in constant", which is precisely the defect
/// that would announce a port nothing answers.
/// </para>
/// </remarks>
public sealed class IngressAnnouncementTests : IDisposable
{
    private const int BoundPort = 61345;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "claude-dashboard-tests", Guid.NewGuid().ToString("N"));

    private readonly DashboardPaths _paths;

    public IngressAnnouncementTests()
    {
        _paths = new DashboardPaths(_root);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private IngressAnnouncement Announcement(IngressStatus ingress) =>
        new(_paths, ingress, Logger.None);

    // ---- Whether to announce at all -----------------------------------------------------------------

    /// <summary>
    /// <strong>A dashboard whose port is held by something else announces nothing at all.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A security property, not tidiness.</strong> An announcement tells
    /// <c>post-status.cmd</c> where to post, and hook payloads carry the operator's prompts —
    /// <c>UserPromptSubmit</c> carries their prompt text, on every turn, in every session. If the
    /// port is held by a process T1.15 has already classified as <em>not ours</em>, announcing it
    /// hands that process their prompts until somebody notices.
    /// </para>
    /// <para>
    /// The path is live: <c>Program</c> calls this unconditionally after the host starts, and T1.15
    /// deliberately keeps starting when a stranger holds the port. One <c>if</c> stands between the
    /// two.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_dashboard_that_cannot_hear_announces_nothing()
    {
        Assert.False(Announcement(IngressStatus.Unavailable(BoundPort)).Announce());

        // Not "no announcement" — no files at all. Nothing was written anywhere.
        Assert.False(File.Exists(_paths.ListeningFile));
        Assert.False(File.Exists(_paths.PortFile));
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    /// <summary>A pinned port held by somebody else is refused for the same reason.</summary>
    /// <remarks>
    /// The other way a dashboard is deaf. It reaches the same guard by a different route, and a
    /// guard written against <c>Unavailable</c> specifically would let this one through.
    /// </remarks>
    [Fact]
    public void A_dashboard_whose_pinned_port_is_taken_announces_nothing()
    {
        Assert.False(Announcement(IngressStatus.PinnedPortTaken(BoundPort)).Announce());

        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    /// <summary>…and a dashboard that can hear does announce.</summary>
    /// <remarks>
    /// The control, without which an implementation that never announces satisfies both tests
    /// above.
    /// </remarks>
    [Fact]
    public void A_dashboard_that_can_hear_announces()
    {
        Assert.True(Announcement(IngressStatus.Healthy(BoundPort)).Announce());

        Assert.Equal(BoundPort, ListeningFile.Read(_paths));
    }

    /// <summary>An unavailable ingress leaves a file an earlier run wrote exactly as it is.</summary>
    /// <remarks>
    /// The test above proves nothing is created. This proves nothing is edited — and it is the
    /// worse case of the two: a deaf dashboard that <em>corrected</em> a stale announcement to its
    /// own unbound port would point the script at silence and look healthy doing it.
    /// </remarks>
    [Fact]
    public void An_unavailable_ingress_does_not_touch_files_that_are_already_there()
    {
        File.WriteAllText(_paths.ListeningFile, "52789");
        File.WriteAllText(_paths.PortFile, "52789");

        Announcement(IngressStatus.Unavailable(BoundPort)).Announce();

        Assert.Equal("52789", File.ReadAllText(_paths.ListeningFile));
        Assert.Equal("52789", File.ReadAllText(_paths.PortFile));
    }

    // ---- Which port ---------------------------------------------------------------------------------

    /// <summary>Both files carry the <strong>bound</strong> port, not the compiled-in default.</summary>
    /// <remarks>
    /// The port is derived per user since T1.21 (Impl §3.1), so a number taken from anywhere but
    /// the bind is wrong for every user rather than only for one who overrode a setting. The port
    /// asserted here is outside the derivation range as well as different from the default: were it
    /// inside, this could pass against an implementation that derived the number again instead of
    /// reading what was bound.
    /// </remarks>
    [Fact]
    public void The_announcement_carries_the_bound_port()
    {
        Assert.NotEqual(DashboardSettings.DefaultPort, BoundPort);
        Assert.False(
            BoundPort >= DashboardSettings.DefaultPort
            && BoundPort < DashboardSettings.DefaultPort + PortSelection.DefaultRange,
            $"port {BoundPort} is inside the derivation range, so this test could not tell a bound " +
            "port from a re-derived one");

        Announcement(IngressStatus.Healthy(BoundPort)).Announce();

        Assert.Equal(BoundPort, ListeningFile.Read(_paths));
        Assert.Equal(BoundPort, PortFile.Read(_paths));
    }

    /// <summary>
    /// <c>port.txt</c> is written, in the dashboard's own folder, holding the bound port.
    /// </summary>
    /// <remarks>
    /// Carried over from the old lifecycle word for word, because the behaviour is carried over
    /// word for word. It is Impl §3.1's first attempt at a port and the only thing that tells a
    /// second launch where a running instance is (§5.3).
    /// </remarks>
    [Fact]
    public void The_port_file_records_the_bound_port_in_the_dashboards_own_folder()
    {
        Announcement(IngressStatus.Healthy(BoundPort)).Announce();

        Assert.True(File.Exists(_paths.PortFile));
        Assert.Equal(
            BoundPort.ToString(CultureInfo.InvariantCulture),
            File.ReadAllText(_paths.PortFile).Trim());
        Assert.Equal(_paths.Root, Path.GetDirectoryName(_paths.PortFile));
    }

    // ---- Withdrawing ---------------------------------------------------------------------------------

    /// <summary>Withdrawing takes the announcement away.</summary>
    [Fact]
    public void Withdrawing_takes_the_announcement_away()
    {
        var announcement = Announcement(IngressStatus.Healthy(BoundPort));
        announcement.Announce();

        Assert.True(announcement.Withdraw());
        Assert.Null(ListeningFile.Read(_paths));
    }

    /// <summary>
    /// <strong>WITHDRAWING DOES NOT DELETE <c>port.txt</c>. THIS IS THE TEST FROM §6.5.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>port.txt</c> looks like the file this feature wants and it is not. Since T1.21 it is an
    /// <strong>input</strong>: <c>PortSelection</c> reads it for the port to try first, which is
    /// what keeps a user on the same port across restarts, and a second launch reads it to find the
    /// running instance for <c>POST /show</c>. Deleting it on shutdown breaks both, and breaks them
    /// silently — the symptom is a port that wanders between restarts and a second launch that
    /// starts a deaf duplicate, neither of which points at the line that caused it.
    /// </para>
    /// <para>
    /// <strong>Asserted three ways, because they fail apart.</strong> That the file is there; that
    /// it still holds the bound port rather than having been emptied; and that the two paths are
    /// different files at all, which is what a well-meant refactor to "one port file" would break
    /// first.
    /// </para>
    /// </remarks>
    [Fact]
    public void Withdrawing_never_deletes_the_port_file()
    {
        var announcement = Announcement(IngressStatus.Healthy(BoundPort));
        announcement.Announce();

        announcement.Withdraw();

        Assert.NotEqual(_paths.PortFile, _paths.ListeningFile);
        Assert.True(File.Exists(_paths.PortFile));
        Assert.Equal(BoundPort, PortFile.Read(_paths));
    }

    /// <summary>Withdrawing twice is a no-op, because four exit paths can each call it.</summary>
    /// <remarks>
    /// The process exception handlers, <c>SessionEnding</c>, the ordinary quit and the
    /// <c>finally</c> in <c>Main</c> — and more than one runs for a single exit. A logoff runs
    /// <c>SessionEnding</c>, then the ordinary quit, then the <c>finally</c>.
    /// </remarks>
    [Fact]
    public void Withdrawing_twice_is_a_no_op()
    {
        var announcement = Announcement(IngressStatus.Healthy(BoundPort));
        announcement.Announce();

        Assert.True(announcement.Withdraw());
        Assert.True(announcement.Withdraw());
        Assert.True(announcement.Withdraw());
    }

    /// <summary>Withdrawing without having announced succeeds and writes nothing.</summary>
    /// <remarks>
    /// The <c>finally</c> in <c>Main</c> runs on paths where the bind never happened. A withdrawal
    /// that reported failure there would make every failed startup log a second, misleading
    /// warning on the way out.
    /// </remarks>
    [Fact]
    public void Withdrawing_without_announcing_succeeds()
    {
        Assert.True(Announcement(IngressStatus.Healthy(BoundPort)).Withdraw());
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    /// <summary>Announcing again after a withdrawal announces again.</summary>
    /// <remarks>
    /// The cycle a restart performs. It also asserts the overwrite that closes issue #29's
    /// residual: a file left by a hard kill names the old port until the next start replaces it.
    /// </remarks>
    [Fact]
    public void A_restart_replaces_a_stale_announcement()
    {
        File.WriteAllText(_paths.ListeningFile, "52789");

        Announcement(IngressStatus.Healthy(BoundPort)).Announce();

        Assert.Equal(BoundPort, ListeningFile.Read(_paths));
    }

    [Fact]
    public void It_needs_all_of_its_collaborators()
    {
        var ingress = IngressStatus.Healthy(BoundPort);

        Assert.Throws<ArgumentNullException>(() => new IngressAnnouncement(null!, ingress, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new IngressAnnouncement(_paths, null!, Logger.None));
        Assert.Throws<ArgumentNullException>(() => new IngressAnnouncement(_paths, ingress, null!));
    }
}
