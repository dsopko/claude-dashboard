namespace ClaudeDashboard.Core;

/// <summary>
/// The tunable parts of the sound policy (TS §IV.5). Every default is named here rather than
/// written into the engine, because Phase 6 (T6.1) puts these in a settings UI.
/// </summary>
public sealed record SoundPolicyOptions
{
    /// <summary>TS §IV.5's widening ladder: first nudge at 2 minutes, then 5, then 10.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultNudgeLadder =
    [
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
    ];

    /// <summary>TS §IV.5's single soft nudge for an unread result.</summary>
    public static readonly TimeSpan DefaultUnreadNudgeAfter = TimeSpan.FromMinutes(5);

    /// <summary>Full volume — a notice is the first sound for an event and is not softened.</summary>
    public const double DefaultNoticeGain = 1.0;

    /// <summary>Softer than a notice, per TS §IV.5's "same melody, softer and quieter".</summary>
    public const double DefaultNudgeGain = 0.6;

    private readonly IReadOnlyList<TimeSpan> _nudgeLadder = DefaultNudgeLadder;
    private readonly double _noticeGain = DefaultNoticeGain;
    private readonly double _nudgeGain = DefaultNudgeGain;

    /// <summary>
    /// The gaps between nudges for a session that stays blocked: the first entry is TS §IV.5's
    /// T₁, and each later entry is the gap after the previous nudge.
    /// </summary>
    /// <remarks>
    /// The <em>last</em> interval repeats for as long as the session stays blocked. TS §IV.5
    /// gives three intervals and no terminator; stopping would leave a blocked agent silent
    /// forever, which is the failure this product exists to prevent, and widening past 10
    /// minutes would invent values the spec does not give. Repeating the widest interval is
    /// never faster and never louder, which is what §IV.5 actually constrains.
    /// </remarks>
    /// <exception cref="ArgumentException">Empty, or contains a non-positive interval.</exception>
    public IReadOnlyList<TimeSpan> NudgeLadder
    {
        get => _nudgeLadder;
        init
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value.Count == 0)
            {
                throw new ArgumentException("A nudge ladder needs at least one interval.", nameof(value));
            }

            if (value.Any(static interval => interval <= TimeSpan.Zero))
            {
                throw new ArgumentException("A nudge interval must be positive.", nameof(value));
            }

            _nudgeLadder = [.. value];
        }
    }

    /// <summary>
    /// How long after finishing an unread result gets its single soft nudge, or null for none
    /// — TS §IV.5's "at most one soft nudge (default 5 min) or none".
    /// </summary>
    public TimeSpan? UnreadNudgeAfter { get; init; } = DefaultUnreadNudgeAfter;

    /// <summary>
    /// Whether a session stopped by an error nudges like a blocked one. See the remarks on
    /// <see cref="SoundPolicyEngine"/> for why this is a setting rather than a constant.
    /// </summary>
    public bool NudgeOnError { get; init; } = true;

    /// <summary>The gain a notice plays at.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0…1.</exception>
    public double NoticeGain
    {
        get => _noticeGain;
        init => _noticeGain = InRange(value);
    }

    /// <summary>The gain a nudge plays at — softer than a notice, and constant across the ladder.</summary>
    /// <remarks>
    /// Constant, not decaying. TS §IV.5 says a nudge is softer than the notice and never
    /// louder; it does not ask for each nudge to be quieter than the last. Decaying would
    /// combine with the widening intervals to make a long-blocked session both rarer
    /// <em>and</em> fainter — compounding toward silence exactly when it has been blocked
    /// longest, which inverts the reason nudges exist.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Outside 0…1.</exception>
    public double NudgeGain
    {
        get => _nudgeGain;
        init => _nudgeGain = InRange(value);
    }

    /// <summary>
    /// The fade-in a nudge gets. Impl Part 7: a short fade-in is what makes a nudge feel
    /// softer rather than merely quieter. A notice never fades.
    /// </summary>
    public TimeSpan NudgeFadeIn { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Throws if these options describe a policy TS §IV.5 forbids. Called by the engine, so a
    /// bad configuration fails where it is introduced rather than by playing something wrong.
    /// </summary>
    /// <exception cref="ArgumentException">A nudge would be louder than a notice.</exception>
    public void Validate()
    {
        if (NudgeGain > NoticeGain)
        {
            throw new ArgumentException(
                $"A nudge must never be louder than a notice (TS §IV.5): nudge gain {NudgeGain} " +
                $"exceeds notice gain {NoticeGain}.",
                nameof(NudgeGain));
        }
    }

    private static double InRange(double gain) =>
        gain is >= 0.0 and <= 1.0
            ? gain
            : throw new ArgumentOutOfRangeException(nameof(gain), gain, "Gain runs from 0 (silent) to 1 (full).");
}
