using System.Linq;

using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.App.Hosting;
using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;
using ClaudeDashboard.Tests.Fakes;
using Serilog.Core;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The glyph, the tooltip and the menu commands (Impl §5.1, §5.2).
/// </summary>
/// <remarks>
/// These drive the view model directly. Whether Windows drew the icon in the notification area
/// is the one thing here that cannot be observed in-process; everything else — the colour, the
/// sentence, what each command publishes, and what the tick recomputes — is asserted without a
/// shell.
/// </remarks>
public sealed class TrayViewModelTests : IDisposable
{
    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    /// <summary>Ingress bound and listening, which is what all but the fault tests assume.</summary>
    private static readonly IngressStatus Healthy = IngressStatus.Healthy(DashboardSettings.DefaultPort);

    private readonly RegistryHarness _harness = new();
    private readonly RecordingEventSink _sink = new();
    private readonly FakeClock _clock = new();
    private readonly FakeSoundModes _modes = new();
    private readonly TrayViewModel _tray;

    public TrayViewModelTests()
    {
        _tray = new TrayViewModel(_harness.Projection, _modes, _sink, _clock, Healthy, Logger.None);
    }

    public void Dispose()
    {
        _tray.Dispose();
        _harness.Dispose();
    }

    private IReadOnlyList<SoundCommand> Commands => [.. _sink.Published.OfType<SoundCommand>()];

    // ---- The glyph -------------------------------------------------------------------------------

    /// <summary>An empty dashboard is grey and says so.</summary>
    [Fact]
    public void It_starts_grey_and_quiet()
    {
        Assert.Equal(TrayColour.Grey, _tray.Colour);
        Assert.Equal(TrayTooltip.AllQuiet, _tray.Tooltip);
    }

    /// <summary>The colour follows the worst session, without anything being ticked.</summary>
    [Fact]
    public void The_colour_follows_the_worst_session()
    {
        _harness.Working("busy", At);
        Assert.Equal(TrayColour.Blue, _tray.Colour);

        _harness.Blocked("asking", At.AddMinutes(1), "agent_needs_input");
        Assert.Equal(TrayColour.Amber, _tray.Colour);

        _harness.Blocked("permission", At.AddMinutes(2), "permission_prompt");
        Assert.Equal(TrayColour.Red, _tray.Colour);
    }

    /// <summary>
    /// A lone question is amber in the tray, while its row is red — the ratified consequence.
    /// </summary>
    /// <remarks>
    /// Asserted through the live projection rather than against <c>TrayVisuals</c> alone, so this
    /// covers the composition too: if the tray were wired to the row palette, the roll-up would
    /// be right and the glyph still wrong.
    /// </remarks>
    [Fact]
    public void A_lone_question_is_amber_in_the_tray_and_red_in_its_row()
    {
        _harness.Working("asking", At);
        _harness.Blocked("asking", At.AddMinutes(1), "agent_needs_input");

        Assert.Equal(TrayColour.Amber, _tray.Colour);
        Assert.Equal(Accent.Red, RowVisuals.AccentOf(SessionState.NeedsQuestion));
    }

    /// <summary>The tooltip carries the broken-out counts.</summary>
    [Fact]
    public void The_tooltip_counts_what_is_there()
    {
        _harness.Working("busy", At);
        _harness.Working("asking", At);
        _harness.Blocked("asking", At.AddMinutes(1), "agent_needs_input");

        Assert.Equal("1 question · 1 working", _tray.Tooltip);
    }

    // ---- Pause and mute --------------------------------------------------------------------------

    /// <summary>
    /// Paused greys the glyph, and the bitmap is not the all-quiet one.
    /// </summary>
    /// <remarks>
    /// Both halves matter. That the colour goes grey is the exception to "the tray tells the
    /// truth"; that the <em>image</em> differs from all-quiet grey is what stops "nothing is
    /// happening" and "I switched it off" looking identical one click apart.
    /// </remarks>
    [Fact]
    public void Paused_greys_the_glyph_with_a_bitmap_of_its_own()
    {
        _harness.Blocked("permission", At, "permission_prompt");
        var burning = _tray.Icon;
        Assert.Equal(TrayColour.Red, _tray.Colour);

        _modes.IsMonitoringPaused = true;
        _tray.Tick(At.AddMinutes(1));

        Assert.True(_tray.IsPaused);
        Assert.NotEqual(burning, _tray.Icon);
        Assert.NotEqual(Fill(TrayIcons.For(TrayColour.Grey)), Fill(_tray.Icon));
        Assert.Null(Fill(_tray.Icon));
    }

    /// <summary>
    /// <strong>No two glyphs look the same.</strong> Every colour, plus the off-duty ring, is
    /// pairwise distinct at the centre pixel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enumerated from <see cref="TrayColour"/> rather than compared in pairs, because every
    /// other test here compares only the two glyphs it cares about: a typo in one hex literal
    /// would give two severities the same glyph, and only a test that happened to compare
    /// <em>that</em> pair would notice. A sixth colour is covered the day it is added.
    /// </para>
    /// <para>
    /// <strong>The honest limit.</strong> Pairwise distinctness guards the palette, not the
    /// appearance: a pixel comparison cannot assert <em>visual</em> distinctness at 16px. Turn
    /// the off-duty ring into a filled darker grey and every centre is still pairwise distinct,
    /// because a sixth grey is a distinct value — while the operator requirement in Impl §5.2
    /// has failed, since two grey dots one click apart are what it forbids.
    /// </para>
    /// <para>
    /// The last assertion is what covers that case, and it is a different claim: the ring leaves
    /// its centre <em>unpainted</em>, so paused cannot converge on the all-quiet dot however the
    /// palette is tuned. Measured — that mutation reddens this test on the transparency line and
    /// not on the pairwise loop. Neither half substitutes for the other.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_glyph_is_distinguishable_from_every_other()
    {
        var glyphs = Enum.GetValues<TrayColour>()
            .Select(colour => (Name: colour.ToString(), Centre: Fill(TrayIcons.For(colour))))
            .Append((Name: "Paused", Centre: Fill(TrayIcons.For(TrayColour.Grey, paused: true))))
            .ToList();

        foreach (var a in glyphs)
        {
            foreach (var b in glyphs)
            {
                if (a.Name == b.Name)
                {
                    continue;
                }

                Assert.True(
                    a.Centre != b.Centre,
                    $"{a.Name} and {b.Name} render the same centre pixel ({a.Centre}), so the "
                    + "operator cannot tell them apart.");
            }
        }

        // …and the off-duty glyph is transparent in the middle rather than merely a sixth colour,
        // which is the property that keeps it distinct from grey by construction.
        Assert.Null(Fill(TrayIcons.For(TrayColour.Grey, paused: true)));
    }

    /// <summary>
    /// …and the roll-up underneath is untouched, so resuming shows the truth again immediately.
    /// </summary>
    /// <remarks>
    /// Pause hides the glyph, not the state. If it cleared the roll-up, resuming after an hour
    /// would show grey until the next event arrived — and the session that needed the operator
    /// would stay invisible for exactly as long as nothing happened.
    /// </remarks>
    [Fact]
    public void Pause_hides_the_colour_without_forgetting_it()
    {
        _harness.Blocked("permission", At, "permission_prompt");

        _modes.IsMonitoringPaused = true;
        _tray.Tick(At.AddMinutes(1));
        Assert.Equal(TrayColour.Red, _tray.Colour);

        _modes.IsMonitoringPaused = false;
        _tray.Tick(At.AddMinutes(2));

        Assert.False(_tray.IsPaused);
        // Compared by fill, not by ToString: every ImageSource stringifies to its type name, so
        // a ToString comparison is satisfied by any two glyphs at all.
        Assert.Equal(Fill(TrayIcons.For(TrayColour.Red)), Fill(_tray.Icon));
    }

    /// <summary>
    /// <strong>Mute leaves the glyph truthful.</strong> This is the whole difference from pause.
    /// </summary>
    [Fact]
    public void Mute_all_does_not_change_the_glyph()
    {
        _harness.Blocked("permission", At, "permission_prompt");
        var burning = Fill(_tray.Icon);
        Assert.NotNull(burning);

        _modes.AllMutedUntil = At.AddMinutes(30);
        _tray.Tick(At.AddMinutes(1));

        Assert.True(_tray.IsMuted);
        Assert.Equal(TrayColour.Red, _tray.Colour);
        // The glyph is still the true colour — the same fill, and emphatically not the paused
        // ring, whose fill is null. Muting is the volume knob; only pause takes the glyph off duty.
        Assert.Equal(burning, Fill(_tray.Icon));
        Assert.Equal(Fill(TrayIcons.For(TrayColour.Red)), Fill(_tray.Icon));
        Assert.StartsWith("muted 29 min", _tray.Tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>The tooltip goes stale unless the tick recomputes it, and this is that test.</strong>
    /// </summary>
    /// <remarks>
    /// A mute lapses by predicate: nothing fires, nothing changes, no event arrives. The only
    /// thing that can notice is the clock moving. So this asserts the tooltip changes with
    /// <em>no event at all</em> — the same shape as T1.11's row-age test, and for the same
    /// reason.
    /// </remarks>
    [Fact]
    public void The_countdown_advances_with_no_event_arriving()
    {
        _modes.AllMutedUntil = At.AddMinutes(30);
        _tray.Tick(At);
        Assert.StartsWith("muted 30 min", _tray.Tooltip, StringComparison.Ordinal);

        var sessionsBefore = _harness.Projection.Sessions.Count;

        _tray.Tick(At.AddMinutes(5));
        Assert.StartsWith("muted 25 min", _tray.Tooltip, StringComparison.Ordinal);

        // Nothing arrived. The clock alone moved the sentence.
        Assert.Equal(sessionsBefore, _harness.Projection.Sessions.Count);
    }

    /// <summary>…and a mute that has lapsed stops being reported at all.</summary>
    [Fact]
    public void A_lapsed_mute_disappears_from_the_tooltip()
    {
        _harness.Working("busy", At);
        _modes.AllMutedUntil = At.AddMinutes(30);

        _tray.Tick(At.AddMinutes(1));
        Assert.True(_tray.IsMuted);

        _tray.Tick(At.AddMinutes(31));

        Assert.False(_tray.IsMuted);
        Assert.Equal("1 working", _tray.Tooltip);
    }

    // ---- The ingress fault (T1.15) ----------------------------------------------------------------

    /// <summary>
    /// A tray built over a faulted ingress leads with the reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The formatter is tested on its own in <c>TrayTooltipTests</c>. This asserts the wire — that
    /// the view model reads <see cref="IngressStatus"/> at all and passes it on. Without it, a
    /// build that resolved the status and never used it would pass every tooltip test and put
    /// nothing whatever in front of the operator.
    /// </para>
    /// <para>
    /// Paired with the default tray built in the constructor, which is healthy: a view model that
    /// hard-coded a fault would fail every other assertion in this class, and one that hard-coded
    /// its absence fails here.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_faulted_ingress_leads_the_tooltip()
    {
        using var deaf = new TrayViewModel(
            _harness.Projection,
            _modes,
            _sink,
            _clock,
            IngressStatus.Unavailable(51999),
            Logger.None);

        Assert.StartsWith("port 51999 taken", deaf.Tooltip, StringComparison.Ordinal);
        Assert.Contains(TrayTooltip.AllQuiet, deaf.Tooltip, StringComparison.Ordinal);

        // The control, on the tray this class builds for everything else.
        Assert.Equal(TrayTooltip.AllQuiet, _tray.Tooltip);
    }

    /// <summary>
    /// The fault survives a tick, because a tick rebuilds the tooltip from scratch.
    /// </summary>
    /// <remarks>
    /// The tooltip is recomputed every fifteen seconds to age a mute. A fault that were applied
    /// once at construction would be wiped by the first tick and the tray would go back to
    /// claiming all quiet — within a quarter of a minute, and for ever after.
    /// </remarks>
    [Fact]
    public void A_faulted_ingress_still_leads_after_a_tick()
    {
        using var deaf = new TrayViewModel(
            _harness.Projection,
            _modes,
            _sink,
            _clock,
            IngressStatus.Unavailable(51999),
            Logger.None);

        deaf.Tick(At.AddMinutes(5));

        Assert.StartsWith("port 51999 taken", deaf.Tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fault does not touch the glyph.
    /// </summary>
    /// <remarks>
    /// Impl §5.2 fixes what the colours mean, and a sixth state is a design change that is not
    /// this task's to make. So the tray still shows the truth about the sessions it has, and the
    /// tooltip is where the reason there are none goes.
    /// </remarks>
    [Fact]
    public void A_faulted_ingress_does_not_change_the_glyph()
    {
        using var deaf = new TrayViewModel(
            _harness.Projection,
            _modes,
            _sink,
            _clock,
            IngressStatus.Unavailable(51999),
            Logger.None);

        Assert.Equal(_tray.Colour, deaf.Colour);
        Assert.False(deaf.IsPaused);
        Assert.False(deaf.IsMuted);
    }

    // ---- The menu, by effect ---------------------------------------------------------------------

    /// <summary>Mute all publishes a mute with no expiry, and toggles to unmute.</summary>
    [Fact]
    public void Mute_all_publishes_and_then_toggles()
    {
        Assert.Equal("Mute all", _tray.MuteAllLabel);

        _tray.MuteAllCommand.Execute(null);

        var muted = Assert.Single(Commands);
        Assert.Equal(SoundCommandKind.MuteAll, muted.Kind);
        Assert.Null(muted.Until);

        // The label follows the engine, not the click: nothing is optimistic here.
        _modes.AllMutedUntil = DateTimeOffset.MaxValue;
        _tray.Tick(At.AddMinutes(1));
        Assert.Equal("Unmute all", _tray.MuteAllLabel);

        _tray.MuteAllCommand.Execute(null);
        Assert.Equal(SoundCommandKind.UnmuteAll, Commands[^1].Kind);
    }

    /// <summary>The timed mute carries its own expiry, half an hour out.</summary>
    [Fact]
    public void The_timed_mute_carries_a_thirty_minute_expiry()
    {
        _tray.MuteAllForThirtyMinutesCommand.Execute(null);

        var command = Assert.Single(Commands);
        Assert.Equal(SoundCommandKind.MuteAll, command.Kind);
        Assert.Equal(_clock.Now.Add(TrayViewModel.TimedMuteDuration), command.Until);
    }

    /// <summary>Pause publishes, and the one item toggles to Resume.</summary>
    [Fact]
    public void Pause_publishes_and_the_item_toggles_to_resume()
    {
        Assert.Equal("Pause monitoring", _tray.PauseLabel);

        _tray.TogglePauseCommand.Execute(null);
        Assert.Equal(SoundCommandKind.PauseMonitoring, Assert.Single(Commands).Kind);

        _modes.IsMonitoringPaused = true;
        _tray.Tick(At.AddMinutes(1));
        Assert.Equal("Resume monitoring", _tray.PauseLabel);

        _tray.TogglePauseCommand.Execute(null);
        Assert.Equal(SoundCommandKind.ResumeMonitoring, Commands[^1].Kind);
    }

    /// <summary>Open and Quit raise their requests; the host does the rest.</summary>
    [Fact]
    public void Open_and_quit_ask_the_host()
    {
        var opened = 0;
        var quit = 0;

        _tray.OpenRequested += (_, _) => opened++;
        _tray.QuitRequested += (_, _) => quit++;

        _tray.OpenCommand.Execute(null);
        _tray.QuitCommand.Execute(null);

        Assert.Equal(1, opened);
        Assert.Equal(1, quit);
    }

    /// <summary>Settings is present and visibly inert until Phase 6.</summary>
    [Fact]
    public void Settings_is_a_disabled_stub()
    {
        Assert.False(_tray.OpenSettingsCommand.CanExecute(null));
    }

    /// <summary>
    /// A refused publish changes nothing on screen.
    /// </summary>
    /// <remarks>
    /// The channel is bounded and drops oldest. An optimistic label would then say "Unmute all"
    /// over an engine that was never muted, and the operator's next click would mute rather than
    /// unmute — the state on screen has to come from the engine or not at all.
    /// </remarks>
    [Fact]
    public void A_refused_publish_leaves_the_menu_saying_what_is_true()
    {
        _sink.Capacity = 0;

        _tray.MuteAllCommand.Execute(null);

        Assert.Empty(Commands);
        Assert.Equal("Mute all", _tray.MuteAllLabel);
        Assert.False(_tray.IsMuted);
    }

    [Fact]
    public void It_needs_all_of_its_collaborators()
    {
        Assert.Throws<ArgumentNullException>(
            () => new TrayViewModel(null!, _modes, _sink, _clock, Healthy, Logger.None));
        Assert.Throws<ArgumentNullException>(
            () => new TrayViewModel(_harness.Projection, null!, _sink, _clock, Healthy, Logger.None));
        Assert.Throws<ArgumentNullException>(
            () => new TrayViewModel(_harness.Projection, _modes, null!, _clock, Healthy, Logger.None));
        Assert.Throws<ArgumentNullException>(
            () => new TrayViewModel(_harness.Projection, _modes, _sink, null!, Healthy, Logger.None));
        Assert.Throws<ArgumentNullException>(
            () => new TrayViewModel(_harness.Projection, _modes, _sink, _clock, null!, Logger.None));
        Assert.Throws<ArgumentNullException>(
            () => new TrayViewModel(_harness.Projection, _modes, _sink, _clock, Healthy, null!));
    }

    /// <summary>
    /// The colour at the centre of a rendered glyph, or null where it is transparent.
    /// </summary>
    /// <remarks>
    /// Read off the rasterised pixels rather than off the geometry that produced them, so this
    /// asserts what the shell is actually handed. A filled dot has its colour in the middle; the
    /// off-duty ring is hollow, so its middle is transparent — which is what makes "paused" and
    /// "all quiet" distinguishable rather than merely differently constructed.
    /// </remarks>
    private static System.Drawing.Color? Fill(System.Drawing.Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        var centre = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);

        return centre.A == 0 ? null : System.Drawing.Color.FromArgb(centre.R, centre.G, centre.B);
    }

    /// <summary>The global sound modes, driven by the test rather than by the engine.</summary>
    private sealed class FakeSoundModes : ISoundModeReader
    {
        public bool IsMonitoringPaused { get; set; }

        public DateTimeOffset? AllMutedUntil { get; set; }
    }
}
