using System.IO;
using ClaudeDashboard.Tests.Architecture;

namespace ClaudeDashboard.Tests.Ui;

/// <summary>
/// Two tripwires around T1.26 that no behavioural test can carry (issue #16).
/// </summary>
/// <remarks>
/// Both read source text, which is a weak kind of test and is used here only where the thing being
/// protected is the <em>absence</em> of something, or a property of code that is not reachable from
/// a test. Each says what it is pinning and what breaks without it, so a reader who has to change
/// it knows what they are taking on.
/// </remarks>
public sealed class RosterUiGuardTests
{
    /// <summary>
    /// <strong>NO TEXT FIELD ANYWHERE BINDS TO A ROSTER MEMBER.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What breaks without it.</strong> T1.25 matches member names exactly — ordinal,
    /// case-sensitive — and that is sound only because a stored name is a copy of a title the
    /// session itself reported. The moment a name can be typed, "exact" stops being a comparison
    /// between two copies of one string, and the failure is silent: a name that looks right, matches
    /// nothing, and reports no error anywhere.
    /// </para>
    /// <para>
    /// So the operator ticks rows, and the only typed name in the feature is the <em>roster's</em>
    /// own label, which is compared against nothing. This asserts the exact set of text inputs in
    /// the window rather than merely omitting one — a convenience added later would otherwise be a
    /// one-line change with no test to argue with.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_only_text_input_in_the_window_is_the_roster_name()
    {
        var bindings = Markup()
            .SelectMany(TextBoxBindings)
            .OrderBy(binding => binding, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Name"], bindings);
    }

    /// <summary>
    /// <strong>The shutdown save re-reads the file rather than serialising what is in memory.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What this pins.</strong> A group the operator declined to remember lives in
    /// <c>RosterStore</c> and nowhere else. Nothing writes it, so it is gone when those sessions end
    /// — which is the whole of "declining leaves nothing behind".
    /// </para>
    /// <para>
    /// <strong>What breaks without it.</strong> The exit path saves the window position. It does so
    /// as <c>Load().Settings with { Window = … }</c>: it re-reads the file and overrides one
    /// section. A reasonable-looking refactor to "serialise the settings we already have" would
    /// write out every declined one-off roster, silently, on the way out — and the operator would
    /// find groups they had said no to, back again after a restart, with nothing in the log.
    /// </para>
    /// <para>
    /// Source text is a weak assertion and is used here because the exit path is not reachable from
    /// a test. It is a tripwire rather than a proof, and it is labelled as one.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_shutdown_save_rereads_the_settings_file()
    {
        var program = File.ReadAllText(
            Path.Combine(RepoLayout.Root.FullName, "src", "ClaudeDashboard.App", "Program.cs"));

        Assert.Contains("settingsStore.Load().Settings", program, StringComparison.Ordinal);
        Assert.Contains("settingsStore.Save(current with { Window =", program, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Markup() =>
        new[] { "RowTemplates.xaml", "MainWindow.xaml" }
            .Select(name => File.ReadAllText(
                Path.Combine(RepoLayout.Root.FullName, "src", "ClaudeDashboard.App", "Ui", name)));

    /// <summary>The property every <c>TextBox</c> in the markup binds its text to.</summary>
    private static IEnumerable<string> TextBoxBindings(string xaml)
    {
        foreach (var element in xaml.Split("<TextBox", StringSplitOptions.None).Skip(1))
        {
            var declaration = element[..element.IndexOf("/>", StringComparison.Ordinal)];
            var text = declaration.IndexOf("Text=\"{Binding ", StringComparison.Ordinal);

            Assert.True(text >= 0, $"A TextBox with no Text binding: {declaration.Trim()}");

            var from = text + "Text=\"{Binding ".Length;
            var to = declaration.IndexOfAny([',', '}'], from);

            yield return declaration[from..to].Trim();
        }
    }
}
