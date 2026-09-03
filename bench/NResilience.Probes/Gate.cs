namespace NResilience.Probes;

/// <summary>
///     The suspension primitive shared by every arm of the A/B test.
///     Allocation comparisons on the suspending path are only meaningful if every arm suspends
///     the same number of times in the same way. Therefore, the gate is a single <c>async Task&lt;int&gt;</c>
///     method that always yields. Its own cost - one state-machine box plus one thread-pool work
///     item - is identical for the raw baseline, the fused arms, and the Polly arms, providing
///     the basis for measuring "overhead above the raw callback".
///     <c>Task.Yield()</c> is used instead of a socket or a timer because it suspends
///     deterministically on every call, without an I/O completion port, a second thread for
///     synchronization, or variance to average away. A loopback-socket cross-check in the
///     benchmark project handles noise statistically.
/// </summary>
public static class Gate
{
    public const int Value = 42;

    /// <summary>Always suspends.</summary>
    public static async Task<int> SuspendAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return Value;
    }

    /// <summary>Never suspends. The synchronous fast path, where the 0-byte budgets apply.</summary>
    public static Task<int> CompleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Value);
    }

    /// <summary>Suspends, then fails transiently for the first <paramref name="failures" /> calls of each operation.</summary>
    public static async Task<int> SuspendThenFailAsync(FailCounter counter, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        if (counter.Next())
            throw counter.Fault;

        return Value;
    }

    /// <summary>
    ///     Suspends, then is refused by local admission control for the first
    ///     <paramref name="counter" /> calls of each operation.
    /// </summary>
    /// <remarks>
    ///     This is a separate arm from <see cref="SuspendThenFailAsync" /> because the refusal path
    ///     differs from the transient path: the executor recognizes the exception rather than
    ///     the classifier, the verdict carries an origin flag, and the retry budget is not charged.
    ///     The refusal path is not the hot path, so its cost is recorded rather than minimized.
    ///     The gate ensures that changes do not significantly degrade this performance.
    /// </remarks>
    public static async Task<int> SuspendThenLimitAsync(LimitCounter counter, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        if (counter.Next())
            throw counter.Refusal;

        return Value;
    }

    /// <summary>A deterministic per-operation refusal sequence. This represents the limiter's state without a limiter.</summary>
    public sealed class LimitCounter
    {
        private readonly int _refusals;
        private int _seen;

        public LimitCounter(int refusals)
        {
            _refusals = refusals;

            // A cached instance, so the figure describes the refusal machinery rather than
            // exception construction.
            Refusal = new RateLimitedException(limiter: "probe");
        }

        public RateLimitedException Refusal { get; }

        public bool Next() => _seen++ < _refusals;

        public void Reset() => _seen = 0;
    }

    /// <summary>A deterministic per-operation failure sequence. The caller resets this between operations.</summary>
    public sealed class FailCounter
    {
        private readonly int _failures;
        private int _seen;

        public FailCounter(int failures)
        {
            _failures = failures;
            Fault = new IOException("probe transient fault");
        }

        public IOException Fault { get; }

        public bool Next() => _seen++ < _failures;

        public void Reset() => _seen = 0;
    }
}
