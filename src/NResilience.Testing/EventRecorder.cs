namespace NResilience.Testing;

/// <summary>
/// A recording <see cref="Resilience.OnEvent"/> listener: it keeps every <see cref="CallEvent"/> a
/// policy raised, in order, so a test can assert on what the policy did rather than on how long it
/// took.
/// </summary>
/// <example>
/// <code>
/// var events = new EventRecorder();
/// var policy = Resilience.Default with { Time = time, OnEvent = events.Record };
///
/// await policy.TryRunAsync(attempt => calls.NextAsync(attempt), cancellationToken);
///
/// Assert.Equal(
///     [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded],
///     events.Kinds);
/// </code>
/// </example>
/// <remarks>
/// Assert on the whole sequence where you can. A telemetry surface that raises the right events in
/// the wrong order still produces a log people believe, and only an ordered assertion catches that;
/// <see cref="Kinds"/> exists to make the ordered assertion the short one to write.
/// </remarks>
public sealed class EventRecorder
{
    private readonly List<CallEvent> _events = [];
    private readonly object _gate = new();

    /// <summary>
    /// The listener. Assign it to <see cref="Resilience.OnEvent"/>:
    /// <c>policy with { OnEvent = recorder.Record }</c>.
    /// </summary>
    /// <param name="callEvent">The event to record.</param>
    /// <remarks>
    /// A method group rather than an implicit conversion, so the subscription is visible at the
    /// call site. Multicast composition is the platform's answer to wanting two listeners:
    /// <c>OnEvent = recorder.Record + logIt</c>.
    /// </remarks>
    public void Record(CallEvent callEvent)
    {
        lock (_gate)
        {
            _events.Add(callEvent);
        }
    }

    /// <summary>Every event raised so far, in the order it was raised.</summary>
    public IReadOnlyList<CallEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>The kind of every event raised so far, in order. The usual assertion surface.</summary>
    public IReadOnlyList<CallEventKind> Kinds
    {
        get
        {
            lock (_gate)
            {
                return [.. _events.Select(static e => e.Kind)];
            }
        }
    }

    /// <summary>How many events have been raised.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>The event at <paramref name="index"/>, in the order raised.</summary>
    public CallEvent this[int index]
    {
        get
        {
            lock (_gate)
            {
                return _events[index];
            }
        }
    }

    /// <summary>Every recorded event of one <paramref name="kind"/>, in order.</summary>
    public IReadOnlyList<CallEvent> OfKind(CallEventKind kind)
    {
        lock (_gate)
        {
            return [.. _events.Where(e => e.Kind == kind)];
        }
    }

    /// <summary>The single recorded event of one <paramref name="kind"/>.</summary>
    /// <exception cref="InvalidOperationException">
    /// The kind was raised no times, or more than once. The message says which, and lists what was
    /// actually raised — the assertion that fails here is usually about the shape of the whole call.
    /// </exception>
    public CallEvent Single(CallEventKind kind)
    {
        IReadOnlyList<CallEvent> matches = OfKind(kind);

        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"Expected exactly one {kind} event, found {matches.Count}. Recorded: {this}.");
    }

    /// <summary>Whether an event of <paramref name="kind"/> was raised at least once.</summary>
    public bool Contains(CallEventKind kind)
    {
        lock (_gate)
        {
            return _events.Exists(e => e.Kind == kind);
        }
    }

    /// <summary>How many events of <paramref name="kind"/> were raised.</summary>
    public int CountOf(CallEventKind kind)
    {
        lock (_gate)
        {
            return _events.Count(e => e.Kind == kind);
        }
    }

    /// <summary>Forgets everything recorded so far, so one recorder can span several calls.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _events.Clear();
        }
    }

    /// <summary>
    /// The recorded kinds in order, comma-separated — a readable assertion-failure message rather
    /// than a type name.
    /// </summary>
    public override string ToString()
    {
        lock (_gate)
        {
            return _events.Count == 0 ? "(no events)" : string.Join(", ", _events.Select(static e => e.Kind));
        }
    }
}
