namespace NResilience.Probes;

/// <summary>
/// The suspension primitive every arm of the A/B shares.
///
/// A comparison of allocation on the suspending path is only meaningful if every arm suspends
/// the same number of times in the same way, so the gate is a single <c>async Task&lt;int&gt;</c>
/// method that always yields. Its own cost — one state-machine box plus one thread-pool work
/// item — is identical in the raw baseline, the fused arms and the Polly arms, and is therefore
/// what "overhead above the raw callback" is measured against.
///
/// <c>Task.Yield()</c> is used rather than a socket or a timer because it suspends
/// deterministically, on every call, with no I/O completion port, no second thread to
/// synchronise with, and no variance to average away. A loopback-socket cross-check lives in
/// the benchmark project, where noise can be handled statistically.
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

    /// <summary>Suspends, then fails transiently for the first <paramref name="failures"/> calls of each operation.</summary>
    public static async Task<int> SuspendThenFailAsync(FailCounter counter, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        if (counter.Next())
        {
            throw counter.Fault;
        }

        return Value;
    }

    /// <summary>Deterministic per-operation failure sequence. Reset between operations by the caller.</summary>
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
