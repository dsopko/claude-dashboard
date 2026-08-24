using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// One line in the dashboard body: a band header, a group header, or a session row.
/// </summary>
/// <remarks>
/// The body is a single flat sequence rather than a tree, because that is what Impl §5.5 asks
/// for — "the banded <c>ObservableCollection</c> the views bind to", with grouped and flat as a
/// toggle over the same collection. A view renders each row by type; the sequence is the same
/// shape in both modes and only its headers differ.
/// </remarks>
public abstract class DashboardRow : ObservableObject;
