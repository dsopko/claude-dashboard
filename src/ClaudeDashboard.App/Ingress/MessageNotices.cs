using System.ComponentModel;
using System.Globalization;
using ClaudeDashboard.App.Ui;

namespace ClaudeDashboard.App.Ingress;

/// <summary>
/// The notice that messages from Claude Code cannot reach the dashboard: the last self-test failed
/// (T1.61, issue #74).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Plain words on screen</strong> (the operator's ruling of 2026-10-03): "messages from
/// Claude Code", never "hook", and the cause where it can be known.
/// </para>
/// <para>
/// It clears when a later self-test passes, or when a real message from Claude Code is accepted
/// after the failed test. Read on the tray's tick: the self-test and ingress publish what they
/// found, and this never waits for either. No colour and no sound.
/// </para>
/// </remarks>
public sealed class SelfTestNotice : INotice, IUiTickTarget
{
    /// <summary>The window's words, before the cause.</summary>
    public const string WindowLead =
        "Messages from Claude Code cannot reach the dashboard: a test message did not arrive.";

    /// <summary>The tray's words.</summary>
    public const string TrayShort = "messages cannot arrive";

    private readonly HookHealth _health;
    private string? _text;

    /// <summary>Creates the notice over what the self-test and ingress publish.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="health"/> is null.</exception>
    public SelfTestNotice(HookHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        _health = health;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc/>
    public bool IsShown => _text is not null;

    /// <inheritdoc/>
    public string? Text => _text;

    /// <inheritdoc/>
    public string? TrayText => IsShown ? TrayShort : null;

    /// <summary>
    /// What a self-test found, in the notice's words: the round trip when it passed, the lead and
    /// the cause when it did not. The Settings button shows the same words.
    /// </summary>
    public static string Describe(SelfTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Passed)
        {
            return string.Create(CultureInfo.CurrentCulture, $"A test message arrived in {result.RoundTripMs} ms.");
        }

        return $"{WindowLead} {CauseText(result.Cause)}";
    }

    /// <summary>The cause, as a sentence.</summary>
    public static string CauseText(SelfTestCause cause) => cause switch
    {
        SelfTestCause.ScriptMissing => "The script that forwards them is missing.",
        SelfTestCause.CurlMissing => "curl.exe is not in System32, so the script cannot send them.",
        SelfTestCause.CouldNotRun => "The script could not be started.",
        _ => "The script ran and nothing arrived.",
    };

    /// <inheritdoc/>
    public void Tick(DateTimeOffset now)
    {
        var test = _health.LastSelfTest;
        var heardSince = test is not null && _health.LastHeardAt is { } heard && heard > test.At;
        var text = test is { Passed: false } && !heardSince ? Describe(test) : null;

        if (string.Equals(text, _text, StringComparison.Ordinal))
        {
            return;
        }

        _text = text;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrayText)));
    }
}

/// <summary>
/// The notice that messages from Claude Code are being refused: 3 or more posts with a wrong token
/// in 10 minutes (T1.61, issue #74).
/// </summary>
/// <remarks>
/// One refusal, as at a restart (event flow §11), never shows it; see
/// <see cref="HookHealth.RefusalsToShow"/>. It clears on the tick, 10 minutes after the last
/// refusal. The per-post Warning in the log stays as it is.
/// </remarks>
public sealed class RefusedNotice : INotice, IUiTickTarget
{
    /// <summary>The window's words.</summary>
    public const string WindowText =
        "Messages from Claude Code are being refused: their token does not match this dashboard's.";

    /// <summary>The tray's words.</summary>
    public const string TrayShort = "messages refused";

    private readonly HookHealth _health;

    /// <summary>Creates the notice over the refusals ingress counts.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="health"/> is null.</exception>
    public RefusedNotice(HookHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        _health = health;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc/>
    public bool IsShown { get; private set; }

    /// <inheritdoc/>
    public string? Text => IsShown ? WindowText : null;

    /// <inheritdoc/>
    public string? TrayText => IsShown ? TrayShort : null;

    /// <inheritdoc/>
    public void Tick(DateTimeOffset now)
    {
        // The refusals a flood left uncounted become one last row on this tick (the ruling of 2026-10-04).
        _health.FlushRefusals(now);

        var shown = _health.RefusalsShowAt(now);

        if (shown == IsShown)
        {
            return;
        }

        IsShown = shown;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TrayText)));
    }
}
