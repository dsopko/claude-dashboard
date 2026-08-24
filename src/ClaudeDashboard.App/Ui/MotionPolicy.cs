using System.ComponentModel;
using System.Windows;
using ClaudeDashboard.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeDashboard.App.Ui;

/// <summary>
/// What a row is allowed to do, visually (Design Document §9: "red blinks; working breathes;
/// nothing else moves").
/// </summary>
public enum MotionKind
{
    /// <summary>Still. Every state that is not blocked-on-you or working.</summary>
    None = 0,

    /// <summary>The blocked-on-you pulse. Red only — an error is amber and does not blink.</summary>
    Blink = 1,

    /// <summary>The slow working breath.</summary>
    Breathe = 2,
}

/// <summary>
/// Whether motion is permitted at all (the operating system's reduced-motion setting).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists rather than animating unconditionally.</strong> A blinking row is
/// exactly what a motion-sensitive operator needs suppressed, and this app's whole job is to sit
/// on a second monitor being looked at all day. The two things it animates are the two things
/// hardest to ignore.
/// </para>
/// <para>
/// <strong>Read from WPF, not from Win32.</strong> <see cref="SystemParameters.ClientAreaAnimation"/>
/// is Windows' "Show animations" accessibility switch — the same setting the web exposes as
/// <c>prefers-reduced-motion</c> — surfaced by WPF itself, so honouring it costs no interop
/// (T1.11 may make no OS call beyond WPF).
/// </para>
/// <para>
/// <strong>Observed live, not read once.</strong> WPF raises
/// <see cref="SystemParameters.StaticPropertyChanged"/> when it sees the setting change, so
/// turning animations off takes effect without a restart. That notification arrives only when
/// something in the process is pumping window messages; with no window yet, the startup read
/// still stands and the dashboard is simply as it was. Degrading to "the value we read at
/// startup" is the documented failure mode, not an exception.
/// </para>
/// </remarks>
public sealed class MotionPolicy : ObservableObject, IDisposable
{
    private static readonly Lazy<MotionPolicy> Lazy = new(() => new MotionPolicy());

    private readonly Func<bool> _probe;
    private readonly bool _observing;
    private bool _isMotionAllowed;
    private bool _disposed;

    /// <summary>Creates a policy that follows the operating system.</summary>
    public MotionPolicy()
        : this(() => SystemParameters.ClientAreaAnimation, observeChanges: true)
    {
    }

    /// <summary>Creates a policy over an arbitrary probe. For tests.</summary>
    /// <param name="probe">Answers whether animation is currently permitted.</param>
    /// <param name="observeChanges">Whether to follow <see cref="SystemParameters"/> as well.</param>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> is null.</exception>
    public MotionPolicy(Func<bool> probe, bool observeChanges)
    {
        ArgumentNullException.ThrowIfNull(probe);

        _probe = probe;
        _isMotionAllowed = probe();
        _observing = observeChanges;

        if (observeChanges)
        {
            SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        }
    }

    /// <summary>
    /// The process-wide policy, following the operating system.
    /// </summary>
    /// <remarks>
    /// The default for anything constructed without one, so that forgetting to inject the policy
    /// fails safe — towards honouring the setting rather than towards animating regardless.
    /// </remarks>
    public static MotionPolicy System => Lazy.Value;

    /// <summary>Whether rows may animate.</summary>
    public bool IsMotionAllowed
    {
        get => _isMotionAllowed;
        private set => SetProperty(ref _isMotionAllowed, value);
    }

    /// <summary>Re-reads the probe and raises a change if the answer moved.</summary>
    public void Refresh() => IsMotionAllowed = _probe();

    /// <summary>Stops following the system setting.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_observing)
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        }

        _disposed = true;
    }

    /// <summary>
    /// The motion <paramref name="state"/> asks for, before this policy is applied.
    /// </summary>
    /// <remarks>
    /// The whole rule, in one place. <see cref="SessionState.Error"/> is deliberately still:
    /// it sits in the Needs-You band and reads amber, and Design Document §9 grants the blink to
    /// red alone.
    /// </remarks>
    public static MotionKind Wanted(SessionState state) => state switch
    {
        SessionState.NeedsPermission or SessionState.NeedsQuestion => MotionKind.Blink,
        SessionState.Working => MotionKind.Breathe,
        _ => MotionKind.None,
    };

    /// <summary>The motion <paramref name="state"/> actually gets under this policy.</summary>
    public MotionKind Allow(SessionState state) =>
        IsMotionAllowed ? Wanted(state) : MotionKind.None;

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(SystemParameters.ClientAreaAnimation))
        {
            Refresh();
        }
    }
}
