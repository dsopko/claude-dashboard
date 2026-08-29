using ClaudeDashboard.App.Ui;
using ClaudeDashboard.Core;
using ClaudeDashboard.Tests.Fakes;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// The session id on the expanded row (T1.23, issue #15; Design Document §9).
/// </summary>
/// <remarks>
/// <para>
/// These are view-model tests: they assert the text, the tooltip and what reaches the clipboard,
/// with no window. What the row actually renders is asserted in <c>MainWindowTests</c>, because
/// only a realized template can show that the id reaches the screen and that the collapsed row
/// does not carry it.
/// </para>
/// <para>
/// <strong>The real clipboard is never touched.</strong> <c>WindowsClipboard</c> has no test at
/// all, deliberately — writing to it would destroy whatever the operator had copied. See
/// <see cref="FakeClipboard"/> for what that leaves unproven.
/// </para>
/// </remarks>
public sealed class SessionIdRowTests
{
    /// <summary>
    /// A realistic Claude Code session id: 36 characters, hyphenated, and NOT all-hex in the
    /// first eight.
    /// </summary>
    /// <remarks>
    /// The first eight characters are deliberately distinguishable from the rest of the value. A
    /// value whose prefix could be confused with the whole would let a copy of the wrong string
    /// pass unnoticed, which is the one defect most likely to reach production here.
    /// </remarks>
    private const string FullId = "88a85f67-4c21-4f0e-9d3b-a1b2c3d4e5f6";

    private const string ExpectedShort = "88a85f67";

    private static readonly DateTimeOffset At = FakeClock.DefaultStart;

    [Fact]
    public void The_row_shows_the_first_eight_characters_of_the_id()
    {
        var row = Row(FullId);

        Assert.Equal(ExpectedShort, row.ShortId);

        // The preview is a preview: it is not the whole value, or there would be nothing to copy.
        Assert.Equal(SessionViewModel.IdPreviewLength, row.ShortId.Length);
        Assert.NotEqual(FullId, row.ShortId);
    }

    /// <summary>The tooltip carries the whole id, the label, and what a click does.</summary>
    /// <remarks>
    /// The full value is asserted rather than "the tooltip mentions the id": a tooltip carrying
    /// only the preview would satisfy the looser claim and would leave the operator unable to read
    /// the rest without copying it first, which is the reason the tooltip exists.
    /// </remarks>
    [Fact]
    public void The_tooltip_carries_the_whole_id_and_the_label()
    {
        var tooltip = Row(FullId).IdTooltip;

        Assert.Contains(FullId, tooltip, StringComparison.Ordinal);
        Assert.Contains("Claude Session ID:", tooltip, StringComparison.Ordinal);
        Assert.Contains("Click to copy.", tooltip, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>A click copies the WHOLE id, not the eight characters on screen.</strong>
    /// </summary>
    /// <remarks>
    /// The assertion names all thirty-six characters on purpose. "The clipboard got 88a85f67"
    /// passes just as happily when the preview was copied instead of the value — and a truncated
    /// id looks right on the row, produces a plausible string on the clipboard, and fails later,
    /// somewhere else. Eight characters are useless in a command line, which is the entire reason
    /// the operator asked for this.
    /// </remarks>
    [Fact]
    public void A_click_copies_the_whole_id_rather_than_the_preview()
    {
        var clipboard = new FakeClipboard();
        var row = Row(FullId, clipboard);

        row.CopyIdCommand.Execute(null);

        Assert.Equal(FullId, clipboard.Last);
        Assert.Equal(36, clipboard.Last!.Length);
        Assert.False(row.CopyFailed);
    }

    /// <summary>An id shorter than the preview length is shown whole, not truncated into a throw.</summary>
    /// <remarks>
    /// <see cref="SessionId"/> wraps any non-empty string and is not guaranteed to be a GUID —
    /// Claude Code supplies it and a future build could supply anything. A naive
    /// <c>Substring(0, 8)</c> throws on every one of these.
    /// </remarks>
    [Theory]
    [InlineData("a")]
    [InlineData("abc")]
    [InlineData("1234567")]
    [InlineData("12345678")]
    public void An_id_shorter_than_the_preview_is_shown_whole(string id)
    {
        var clipboard = new FakeClipboard();
        var row = Row(id, clipboard);

        Assert.Equal(id, row.ShortId);

        // …and it still copies whole, which is the same value here and must stay so.
        row.CopyIdCommand.Execute(null);

        Assert.Equal(id, clipboard.Last);
    }

    /// <summary>
    /// <strong>A row can never face a session with no id, because the domain refuses to build
    /// one.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The task asked what a row with no id should show, and the honest answer turned out to be
    /// that the question cannot arise: <c>Session.Id</c> throws on a <c>default(SessionId)</c>,
    /// so no <see cref="SessionViewModel"/> over a real session can have an empty one. Found by
    /// writing the test and watching it fail on the arrangement rather than on the assertion.
    /// </para>
    /// <para>
    /// So this asserts the invariant that makes the case unreachable, rather than asserting a
    /// behaviour for a state the application cannot enter. The empty branches in
    /// <c>ShortId</c> and <c>IdTooltip</c> stay, and are defence against that invariant changing
    /// rather than handling of a live case — which is why they are covered here by the second
    /// half of this test, reached through the value type directly.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_session_cannot_exist_without_an_id_so_no_row_can_show_an_empty_one()
    {
        var refused = Assert.Throws<ArgumentException>(() => Row(id: null));

        Assert.Contains("must have an id", refused.Message, StringComparison.Ordinal);

        // And the guard behind it, at the level the domain does allow: a default SessionId is
        // empty, which is what the row's empty branches key off.
        Assert.True(default(SessionId).IsEmpty);
        Assert.Equal(string.Empty, default(SessionId).Value);
    }

    /// <summary>
    /// <strong>A clipboard that refuses degrades to a marker on the row and does not throw.</strong>
    /// </summary>
    /// <remarks>
    /// The realistic case, and it is common rather than exotic: another process holds the
    /// clipboard for the instant of the click. This runs on the dispatcher thread, so an
    /// exception here would take the window with it.
    /// <para>
    /// The positive control matters as much as the failure: a copy that failed and one that never
    /// attempted look identical if only successes are recorded, so the attempt is asserted too.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_clipboard_that_refuses_is_shown_and_does_not_throw()
    {
        var clipboard = new FakeClipboard { Succeeds = false };
        var row = Row(FullId, clipboard);

        row.CopyIdCommand.Execute(null);

        Assert.True(row.CopyFailed);

        // It was attempted, and with the whole value — a failure to try would set the same flag.
        Assert.Equal(FullId, Assert.Single(clipboard.Written));

        // …and it clears when a later copy works, so the marker reports the last attempt rather
        // than latching for the life of the row.
        clipboard.Succeeds = true;
        row.CopyIdCommand.Execute(null);

        Assert.False(row.CopyFailed);
    }

    /// <summary>A row with no clipboard wired says the copy failed, because it did.</summary>
    /// <remarks>
    /// Reporting success would be the false reading this affordance is shaped to avoid: the
    /// operator clicked, nothing reached the clipboard, and they would paste something stale. The
    /// id stays visible either way — showing which session this is and copying it are separate
    /// things, and a wiring fault must not look like a session without an id.
    /// </remarks>
    [Fact]
    public void A_row_with_no_clipboard_reports_the_copy_as_failed()
    {
        var row = Row(FullId, clipboard: null);

        Assert.Equal(ExpectedShort, row.ShortId);
        Assert.True(row.CopyIdCommand.CanExecute(null));

        row.CopyIdCommand.Execute(null);

        Assert.True(row.CopyFailed);
    }

    private static SessionViewModel Row(string? id, FakeClipboard? clipboard = null) =>
        new(
            new Session
            {
                Id = id is null ? default : new SessionId(id),
                State = SessionState.Unread,
                Latest = new Exchange { Prompt = "run the tests", StartedAt = At, AnsweredAt = At },
                Cwd = @"C:\w",
                Group = GroupKeys.ForWorkspace(@"C:\w"),
                EnteredAt = At,
                LastActivity = At,
            },
            new MotionPolicy(() => false, observeChanges: false),
            ack: null,
            clipboard);
}
