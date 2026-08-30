using CommunityToolkit.Mvvm.Input;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// The row that asks whether a freshly formed group should be remembered as a roster
/// (T1.26, issue #16 rule 2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A row rather than a dialog, and that is a testability decision as much as a design
/// one.</strong> A modal would need a port whose adapter is then deliberately untested — the shape
/// this project already carries once for the clipboard — and a second of those is a real cost. As a
/// row it is driven by the same harness that realizes a window and invokes a command, so both of
/// its paths are proved by the thing the operator actually uses.
/// </para>
/// <para>
/// <strong>AN UNANSWERED PROMPT IS A DECLINED ONE, AND THAT IS A CORRECTNESS ARGUMENT RATHER THAN A
/// CONVENIENCE.</strong> The window can be used and dismissed with this still showing. That is safe
/// only because the group is already formed and already unpersisted, so "no answer" and "no" leave
/// exactly the same state — which is what makes an ignorable prompt an acceptable one.
/// </para>
/// <para>
/// The name here is the <em>roster's</em> own label: typed, compared against nothing, and never a
/// member name. Member names are only ever copied from rows, because matching is exact and a typed
/// name that looks right but matches nothing would fail silently.
/// </para>
/// </remarks>
public sealed partial class RosterPromptViewModel : DashboardRow
{
    private readonly Action<string> _accept;
    private readonly Action _decline;
    private string _name;

    /// <summary>Builds the prompt for a group already formed under <paramref name="name"/>.</summary>
    /// <param name="name">The name the group was formed under, which the operator may edit.</param>
    /// <param name="accept">Remembers the roster under the name it is given.</param>
    /// <param name="decline">Leaves the group formed and unpersisted.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public RosterPromptViewModel(string name, Action<string> accept, Action decline)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(accept);
        ArgumentNullException.ThrowIfNull(decline);

        _name = name;
        _accept = accept;
        _decline = decline;
    }

    /// <summary>The roster's own name. The operator may edit it before accepting.</summary>
    public string Name
    {
        get => _name;
        set
        {
            var trimmed = value ?? string.Empty;
            if (string.Equals(_name, trimmed, StringComparison.Ordinal))
            {
                return;
            }

            _name = trimmed;
            OnPropertyChanged(nameof(Name));
            RememberCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>What the row says.</summary>
    public static string Question => "Remember this group as a roster?";

    /// <summary>Remembers the group, so it re-forms on the next start.</summary>
    [RelayCommand(CanExecute = nameof(CanRemember))]
    private void Remember() => _accept(Name.Trim());

    /// <summary>Leaves the group as it is: formed now, gone when these sessions end.</summary>
    [RelayCommand]
    private void Forget() => _decline();

    private bool CanRemember() => !string.IsNullOrWhiteSpace(Name);
}
