using System.Threading.Tasks.Sources;

namespace NResilience.Probes;

/// <summary>
///     The <see cref="ValueTask" /> counterpart of <see cref="Gate" />, so the two callback shapes are
///     measured against the same executor in the same sweep.
///     <para>
///         The synchronous arm is driven by an <see cref="IValueTaskSource{TResult}" /> rather than by
///         <c>new ValueTask&lt;int&gt;(42)</c>, because the source-backed shape is the one the BCL
///         actually hands out - <c>Socket</c>, <c>Channel</c>, <c>PipeReader</c>, <c>Stream</c> - and it
///         is the shape where converting to a <see cref="Task" /> costs a real allocation. Measuring the
///         value-backed shape instead would report a saving that the callers this exists for never see.
///     </para>
///     <para>
///         One core is reused across calls, exactly as a pooled source does, so the arm measures the
///         executor rather than the allocation of a fresh source per operation. Reading one handed-out
///         token twice throws, which is what makes these arms a check on the loop re-invoking the
///         callback rather than re-awaiting its task.
///     </para>
/// </summary>
public sealed class ValueGate : IValueTaskSource<int>
{
    /// <summary>The shared source. Single-threaded by construction: every arm awaits before looping.</summary>
    public static readonly ValueGate Instance = new();

    private ManualResetValueTaskSourceCore<int> _core;

    public int GetResult(short token) => _core.GetResult(token);

    public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);

    /// <summary>Never suspends, and is backed by a pooled source rather than by a value.</summary>
    public static ValueTask<int> CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Instance._core.Reset();
        Instance._core.SetResult(Gate.Value);
        return new ValueTask<int>(Instance, Instance._core.Version);
    }

    /// <summary>
    ///     What the same callback costs written the way it has to be written without a
    ///     <see cref="ValueTask" /> overload to bind to: a task materialized for an answer that is
    ///     already in hand.
    /// </summary>
    public static Task<int> CompleteAsTaskAsync(CancellationToken cancellationToken) => CompleteAsync(cancellationToken).AsTask();

    /// <summary>Always suspends. Task-backed, so the arm prices the executor rather than the conversion.</summary>
    public static ValueTask<int> SuspendAsync(CancellationToken cancellationToken) => new(Gate.SuspendAsync(cancellationToken));
}
