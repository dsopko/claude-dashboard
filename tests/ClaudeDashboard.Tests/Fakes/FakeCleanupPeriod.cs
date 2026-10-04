using ClaudeDashboard.App.Configuration;
using ClaudeDashboard.Core;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// What the archive writer would have read of Claude Code's <c>cleanupPeriodDays</c> (T1.68), so no
/// test reads the operator's <c>~/.claude/settings.json</c>. By default the key is absent: 30 days.
/// </summary>
/// <remarks>The writer reads it on its own thread, so the value and the count are handed over safely.</remarks>
public sealed class FakeCleanupPeriod(CleanupPeriodRead? value = null) : ICleanupPeriodSource
{
    private readonly Lock _gate = new();
    private CleanupPeriodRead _value = value ?? CleanupPeriodRead.Absent;
    private int _reads;

    /// <summary>What the next read finds.</summary>
    public CleanupPeriodRead Value
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }

        set
        {
            lock (_gate)
            {
                _value = value;
            }
        }
    }

    /// <summary>How many times the writer read it.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <inheritdoc/>
    public CleanupPeriodRead Read()
    {
        Interlocked.Increment(ref _reads);
        return Value;
    }
}
