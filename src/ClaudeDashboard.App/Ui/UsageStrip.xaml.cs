using System.Windows.Controls;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The plan's usage as one piece of markup, placed in the caption or on the row under it (MOD.7, issue #133).
/// See the remarks in <c>UsageStrip.xaml</c> and on the caption's slot in <c>MainWindow.xaml</c>.
/// </summary>
public partial class UsageStrip : UserControl, IFittingStripHost
{
    /// <summary>Creates the strip.</summary>
    public UsageStrip() => InitializeComponent();

    /// <inheritdoc/>
    public FittingStrip Strip => StripPart;

    /// <summary>The three pairs, in their order, for tests.</summary>
    internal IReadOnlyList<StackPanel> Pairs => [CurrentPart, WeekPart, FablePart];
}
