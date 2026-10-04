namespace ClaudeDashboard.Core;

/// <summary>What the dashboard found of Claude Code's <c>cleanupPeriodDays</c> (T1.68, issue #102).</summary>
public enum CleanupPeriodKind
{
    /// <summary>No file, a file that could not be read, or one that is not a JSON object.</summary>
    NotRead = 1,

    /// <summary>The file was read, and the key is not in it.</summary>
    Absent = 2,

    /// <summary>The key holds a JSON number small enough to hold: see <see cref="CleanupPeriodRead.Number"/>.</summary>
    Number = 3,

    /// <summary>The key holds something that is not a number: text, <c>true</c>, <c>null</c>, an object.</summary>
    NotANumber = 4,

    /// <summary>The key holds a JSON number too large to hold, above zero.</summary>
    HugeNumber = 5,

    /// <summary>The key holds a JSON number too large to hold, below zero.</summary>
    HugeNegativeNumber = 6,
}

/// <summary>
/// What was read of Claude Code's <c>cleanupPeriodDays</c>: the kind, and the number when it is one.
/// The host reads the file; <see cref="HistoryRetention"/> judges what it found.
/// </summary>
/// <param name="Kind">What was found.</param>
/// <param name="Number">The value, for <see cref="CleanupPeriodKind.Number"/> only.</param>
public readonly record struct CleanupPeriodRead(CleanupPeriodKind Kind, decimal? Number = null)
{
    /// <summary>No file, or a file that could not be read or parsed.</summary>
    public static CleanupPeriodRead NotRead => new(CleanupPeriodKind.NotRead);

    /// <summary>The file was read, and the key is not there.</summary>
    public static CleanupPeriodRead Absent => new(CleanupPeriodKind.Absent);

    /// <summary>The key holds <paramref name="value"/>.</summary>
    public static CleanupPeriodRead Of(decimal value) => new(CleanupPeriodKind.Number, value);
}

/// <summary>Why the history keeps what it keeps (T1.68).</summary>
public enum RetentionCause
{
    /// <summary>Claude Code's <c>cleanupPeriodDays</c>, a valid value.</summary>
    Setting = 1,

    /// <summary>The key is not there: Claude Code's default, 30 days.</summary>
    Default = 2,

    /// <summary>Claude Code's settings could not be read or parsed: nothing is deleted.</summary>
    NotRead = 3,

    /// <summary>The value is not a whole number of 1 or more: nothing is deleted.</summary>
    NotValid = 4,

    /// <summary>The value is too large to count back from now: nothing is deleted.</summary>
    TooLarge = 5,
}

/// <summary>
/// The rule in use for one prune: the days to keep, or none, which deletes nothing; and why.
/// </summary>
/// <param name="Cause">Why.</param>
/// <param name="Days">The days to keep, or null to delete nothing.</param>
public sealed record RetentionRule(RetentionCause Cause, int? Days);

/// <summary>
/// How long the dashboard keeps its history: as long as Claude Code keeps its own sessions, by
/// Claude Code's <c>cleanupPeriodDays</c> (T1.68, issue #102, the operator's ruling of 2026-10-04).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why it follows Claude Code.</strong> The history holds a copy of what Claude Code sent: the
/// operator's prompts and Claude's answers. A retention of its own (T1.64) could keep text that
/// Claude Code had already deleted. So the dashboard deletes what is older than Claude Code's number.
/// </para>
/// <para>
/// <strong>The rule is Claude Code's.</strong> A whole number of 1 or more keeps that many days. No key
/// keeps Claude Code's default, 30 days. A file that could not be read or parsed, or a value that is
/// not valid (<c>0</c>, a negative number, a fraction, text, <c>true</c>, <c>null</c>), deletes nothing:
/// Claude Code pauses its own cleanup in the same cases. A value too large to count back from now also
/// deletes nothing.
/// </para>
/// <para>
/// Pure: what was read and an instant in, the rule out. It reads no file and no clock.
/// </para>
/// </remarks>
public static class HistoryRetention
{
    /// <summary>Claude Code's default <c>cleanupPeriodDays</c>, kept when the key is not there.</summary>
    public const int DefaultDays = 30;

    /// <summary>The rule for a prune at <paramref name="now"/>, from what was read.</summary>
    public static RetentionRule Decide(CleanupPeriodRead read, DateTimeOffset now) => read.Kind switch
    {
        CleanupPeriodKind.Absent => Counted(DefaultDays, RetentionCause.Default, now),
        CleanupPeriodKind.Number when read.Number is { } value => Judge(value, now),
        CleanupPeriodKind.HugeNumber => new RetentionRule(RetentionCause.TooLarge, null),
        CleanupPeriodKind.NotANumber or CleanupPeriodKind.HugeNegativeNumber or CleanupPeriodKind.Number =>
            new RetentionRule(RetentionCause.NotValid, null),
        _ => new RetentionRule(RetentionCause.NotRead, null),
    };

    private static RetentionRule Judge(decimal value, DateTimeOffset now)
    {
        // A whole number of 1 or more. 30.0 is the number 30, as Claude Code reads it.
        if (value < 1 || value != decimal.Truncate(value))
        {
            return new RetentionRule(RetentionCause.NotValid, null);
        }

        return value > int.MaxValue
            ? new RetentionRule(RetentionCause.TooLarge, null)
            : Counted((int)value, RetentionCause.Setting, now);
    }

    /// <summary>
    /// The days, unless they reach back before the calendar starts: 99999 days from 2026 is 1752 and
    /// is kept as given; 99999999 days cannot be counted back, and deletes nothing.
    /// </summary>
    private static RetentionRule Counted(int days, RetentionCause cause, DateTimeOffset now) =>
        days >= (now - DateTimeOffset.MinValue).TotalDays
            ? new RetentionRule(RetentionCause.TooLarge, null)
            : new RetentionRule(cause, days);
}
