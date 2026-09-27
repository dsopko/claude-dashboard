using System.Collections;
using System.Collections.Immutable;

namespace ClaudeDashboard.Core;

/// <summary>
/// The kinds of background work that put a session in <see cref="SessionState.Waiting"/>
/// (T1.41, issue #52).
/// </summary>
/// <remarks>
/// <para>
/// <strong>An allow-list, by the operator's ruling on the issue.</strong> A <c>Stop</c>'s
/// <c>background_tasks</c> entry counts only when its <c>type</c> is one of these. Each reports
/// back when it finishes, and Claude Code wakes the session with a <c>&lt;task-notification&gt;</c>
/// prompt. <c>monitor</c> does not count: it is a long-lived watcher that may never fire. A type
/// never seen before does not count either; it falls back to today's behaviour and the decisions
/// record says it was seen, so it can be classified later.
/// </para>
/// <para>
/// The <c>type</c> field is set by Claude Code, so the rule reads a wire token and never the
/// agent's description text.
/// </para>
/// </remarks>
public enum BackgroundTaskKind
{
    /// <summary><c>shell</c>: a background command. Reports back when it exits.</summary>
    Shell = 1,

    /// <summary><c>subagent</c>: a background agent. Reports back when it finishes.</summary>
    Subagent = 2,
}

/// <summary>
/// One running background task a <c>Stop</c> listed, of a kind on the allow-list (T1.41).
/// </summary>
/// <remarks>
/// <strong>Only the id, the kind and the description.</strong> The payload's <c>command</c> is
/// never read into this type: it can carry prompts or secrets (T1.24), and nothing here needs it.
/// The description is agent-written text — data, rendered and never executed, never logged.
/// </remarks>
/// <param name="Id">Claude Code's task id, stable across the Stops that list it.</param>
/// <param name="Kind">Which allowed kind it is.</param>
/// <param name="Description">What the agent said the task is. Operator-adjacent text.</param>
public sealed record BackgroundTask(string Id, BackgroundTaskKind Kind, string Description);

/// <summary>A task the session is waiting on, and when it was first listed (T1.41).</summary>
/// <param name="Id">Claude Code's task id.</param>
/// <param name="Kind">Which allowed kind it is.</param>
/// <param name="Description">What the agent said the task is. Operator-adjacent text.</param>
/// <param name="FirstSeenAt">The Stop that first listed this id; carried across later Stops.</param>
public sealed record WaitingTask(string Id, BackgroundTaskKind Kind, string Description, DateTimeOffset FirstSeenAt);

/// <summary>
/// The background tasks a session is waiting on, oldest first — with value equality, so a
/// <see cref="Session"/> that carries it still compares by value (T1.41).
/// </summary>
public sealed class WaitingTasks : IReadOnlyList<WaitingTask>, IEquatable<WaitingTasks>
{
    /// <summary>Nothing running.</summary>
    public static readonly WaitingTasks Empty = new(ImmutableArray<WaitingTask>.Empty);

    private readonly ImmutableArray<WaitingTask> _tasks;

    private WaitingTasks(ImmutableArray<WaitingTask> tasks) => _tasks = tasks;

    /// <inheritdoc/>
    public int Count => _tasks.Length;

    /// <inheritdoc/>
    public WaitingTask this[int index] => _tasks[index];

    /// <summary>
    /// The list a new <c>Stop</c> produces: exactly its running tasks, each keeping the instant it
    /// was first listed if <paramref name="previous"/> already had it.
    /// </summary>
    /// <remarks>
    /// An id that is no longer listed is dropped — its task has finished. An id listed for the
    /// first time is first seen <paramref name="at"/>. The order is the Stop's.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static WaitingTasks Following(WaitingTasks previous, IReadOnlyList<BackgroundTask> running, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(running);

        if (running.Count == 0)
        {
            return Empty;
        }

        var builder = ImmutableArray.CreateBuilder<WaitingTask>(running.Count);

        foreach (var task in running)
        {
            var firstSeen = previous._tasks.FirstOrDefault(known => string.Equals(known.Id, task.Id, StringComparison.Ordinal))
                ?.FirstSeenAt ?? at;

            builder.Add(new WaitingTask(task.Id, task.Kind, task.Description, firstSeen));
        }

        return new WaitingTasks(builder.MoveToImmutable());
    }

    /// <inheritdoc/>
    public IEnumerator<WaitingTask> GetEnumerator() => ((IEnumerable<WaitingTask>)_tasks).GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public bool Equals(WaitingTasks? other) =>
        other is not null && (ReferenceEquals(this, other) || _tasks.SequenceEqual(other._tasks));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as WaitingTasks);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);

        foreach (var task in _tasks)
        {
            hash.Add(task);
        }

        return hash.ToHashCode();
    }
}
