using ClaudeDashboard.Core.Events;
using ClaudeDashboard.Core.Ports;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// An <see cref="IEventSink"/> that records what ingress handed it instead of running a pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Lets T1.8's mapping tests assert the whole ingress contract without a channel or a
/// consumer: that a sample payload became the right <see cref="InboundEvent"/>, and that
/// <c>/hook</c> answered <c>200</c> either way.
/// </para>
/// <para>
/// <see cref="Capacity"/> exists so a test can make the sink refuse. The bounded channel
/// behind the real sink can fill (Impl §4), and the branch where <c>TryPublish</c> returns
/// <see langword="false"/> — ingress logs the drop and still answers <c>200</c>, because it
/// is a pure observer (Impl §3.3) — is a branch that has to be reachable in a test.
/// </para>
/// </remarks>
public sealed class RecordingEventSink : IEventSink
{
    private readonly List<InboundEvent> _published = [];

    /// <summary>How many events this sink will accept before refusing. Null means unbounded.</summary>
    public int? Capacity { get; set; }

    /// <summary>Every accepted event, in order.</summary>
    public IReadOnlyList<InboundEvent> Published => _published;

    /// <summary>The most recent accepted event, or null if none was.</summary>
    public InboundEvent? Last => _published.Count == 0 ? null : _published[^1];

    /// <summary>How many events were refused because the sink was full.</summary>
    public int RefusedCount { get; private set; }

    /// <inheritdoc/>
    public bool TryPublish(InboundEvent inboundEvent)
    {
        ArgumentNullException.ThrowIfNull(inboundEvent);

        if (Capacity is { } capacity && _published.Count >= capacity)
        {
            RefusedCount++;
            return false;
        }

        _published.Add(inboundEvent);
        return true;
    }

    /// <summary>Every accepted event of one variant, in order.</summary>
    public IReadOnlyList<T> PublishedOf<T>()
        where T : InboundEvent => [.. _published.OfType<T>()];

    /// <summary>Forgets everything recorded so far, refusals included.</summary>
    public void Clear()
    {
        _published.Clear();
        RefusedCount = 0;
    }
}
