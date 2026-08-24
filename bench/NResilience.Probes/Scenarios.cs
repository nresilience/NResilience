namespace NResilience.Probes;

/// <summary>
///     Defines the A/B arms, shared by the hard gate, the benchmark harness, and the
///     Native AOT probe. Sharing these arms ensures consistency: the baseline comparison
///     in Appendix B used two different harnesses, whereas this project uses a single
///     harness for all measurements.
///     Every arm is a <c>Func&lt;ValueTask&lt;int&gt;&gt;</c> built from a cached static delegate,
///     ensuring no arm carries a closure that another avoids.
/// </summary>
public static class Scenarios
{
    private static readonly Func<CancellationToken, Task<int>> SuspendCallback = Gate.SuspendAsync;
    private static readonly Func<CancellationToken, Task<int>> CompleteCallback = Gate.CompleteAsync;

    /// <summary>
    ///     A caller token that can be cancelled but never is. This represents the production case
    ///     - such as an ASP.NET request abort token or a host shutdown token. Because this changes
    ///     the cost of the per-attempt link by an order of magnitude, the loop uses dedicated
    ///     arms instead of folding it into <c>CancellationToken.None</c>.
    /// </summary>
    public static readonly CancellationTokenSource CallerSource = new();

    public static readonly FusedExecutor None = new(FusedPolicy.None);
    public static readonly FusedExecutor NoTimeout = new(FusedPolicy.NoTimeout);
    public static readonly FusedExecutor Default = new(FusedPolicy.Default);
    public static readonly FusedExecutor Full = new(FusedPolicy.Full);
    public static readonly LeanFusedExecutor Lean = new();

    /// <summary>A decomposition arm that uses the real loop but removes the inline attempt log. This is not a shipping shape.</summary>
    public static readonly FusedExecutor NoTimeoutNoLog = new(FusedPolicy.NoTimeout, false);

    /// <summary>A decomposition arm similar to <see cref="NoTimeoutNoLog" />, but with the attempt timeout enabled.</summary>
    public static readonly FusedExecutor DefaultNoLog = new(FusedPolicy.Default, false);

    // ---- Suspending path: the path every real I/O call takes. ----

    /// <summary>The unwrapped callback. All other measurements report overhead relative to this baseline.</summary>
    public static ValueTask<int> RawSuspending() => new(Gate.SuspendAsync(CancellationToken.None));

    public static ValueTask<int> NoneSuspending() => None.RunAsync(SuspendCallback);

    public static ValueTask<int> LeanSuspending() => Lean.RunAsync(SuspendCallback);

    public static ValueTask<int> FusedNoTimeoutSuspending() => NoTimeout.RunAsync(SuspendCallback);

    public static ValueTask<int> FusedDefaultSuspending() => Default.RunAsync(SuspendCallback);

    public static ValueTask<int> FusedFullSuspending() => Full.RunAsync(SuspendCallback);

    public static ValueTask<int> FusedNoTimeoutNoLogSuspending() => NoTimeoutNoLog.RunAsync(SuspendCallback);

    public static ValueTask<int> FusedDefaultNoLogSuspending() => DefaultNoLog.RunAsync(SuspendCallback);

    public static ValueTask<int> FusedDefaultSuspendingCancellable() => Default.RunAsync(SuspendCallback, CallerSource.Token);

    // ---- Synchronous fast path: where the 0-byte budgets live. ----

    public static ValueTask<int> RawSync() => new(Gate.CompleteAsync(CancellationToken.None));

    public static ValueTask<int> NoneSync() => None.RunAsync(CompleteCallback);

    /// <summary>A static lambda with state; it uses no closure or capture, and the state is a value type.</summary>
    public static ValueTask<int> FusedNoTimeoutSyncState() =>
        NoTimeout.RunAsync(static (_, ct) => Gate.CompleteAsync(ct), 0);

    /// <summary>
    ///     The same call but with an attempt timeout in the policy. The difference between this
    ///     and <see cref="FusedNoTimeoutSyncState" /> is the per-attempt linked source - this is
    ///     why "full policy, completes synchronously, 0 bytes" requires a qualifier.
    /// </summary>
    public static ValueTask<int> FusedDefaultSyncState() =>
        Default.RunAsync(static (_, ct) => Gate.CompleteAsync(ct), 0);

    public static ValueTask<int> FusedFullSyncState() =>
        Full.RunAsync(static (_, ct) => Gate.CompleteAsync(ct), 0);

    /// <summary>The stateless overload that uses the caller's own closure and delegate, which is the cost of any lambda.</summary>
    public static ValueTask<int> FusedNoTimeoutSyncCallback() => NoTimeout.RunAsync(CompleteCallback);

    // ---- Retry. ----

    /// <summary>
    ///     This arm simulates two transient failures followed by a success, with backoff disabled
    ///     and no timeout source. This isolates the cost of the retry machinery. The fault uses
    ///     a cached exception instance; throwing a new exception each attempt would measure
    ///     exception construction, which both arms incur identically.
    /// </summary>
    public static RetryArm BuildFusedRetry(int failures = 2) => new(failures);

    public sealed class RetryArm
    {
        private readonly Func<Gate.FailCounter, CancellationToken, Task<int>> _callback = Gate.SuspendThenFailAsync;
        private readonly Gate.FailCounter _counter;
        private readonly FusedExecutor _executor;

        public RetryArm(int failures)
        {
            _counter = new Gate.FailCounter(failures);

            _executor = new FusedExecutor(FusedPolicy.NoTimeout with
            {
                Attempts = failures + 1,
                UseBackoff = false,
                Budget = new ProbeBudget(int.MaxValue / 2),
            });
        }

        public void Reset() => _counter.Reset();

        public ValueTask<int> RunAsync() => _executor.RunAsync(_callback, _counter);
    }
}
