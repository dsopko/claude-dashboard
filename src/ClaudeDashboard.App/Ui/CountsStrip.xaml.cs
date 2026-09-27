using System.Windows.Controls;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The status counts as one piece of markup, placed on whichever line has room for them (T1.43,
/// issue #54). See the remarks in <c>CountsStrip.xaml</c> and on the counts row in
/// <c>MainWindow.xaml</c>.
/// </summary>
public partial class CountsStrip : UserControl
{
    /// <summary>Creates the counts.</summary>
    public CountsStrip() => InitializeComponent();

    /// <summary>
    /// The strip inside, for MainWindow's placement decision to read and for tests.
    /// </summary>
    /// <remarks>
    /// A property rather than the generated field, because a binding path can walk a property and
    /// not a field: the counts row binds its visibility to <c>Strip.HasDropped</c> on the
    /// caption's instance.
    /// </remarks>
    public FittingStrip Strip => StripPart;
}
