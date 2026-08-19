namespace NResilience.Probes;

/// <summary>
/// The arms of the A/B, defined once and shared by the hard gate, the benchmark harness and the
/// Native AOT probe. Sharing them is the point: Appendix B's headline comparison was inferred
/// across two harnesses, and the whole purpose of Phase 0a is to replace it with numbers taken
/// on one.
///
/// Every arm is a <c>Func&lt;ValueTask&lt;int&gt;&gt;</c> built from a cached static delegate, so no
/// arm carries a closure another arm avoids.
/// </summary>
public static class Scenarios
{
    private static readonly Func<CancellationToken, Task<int>> SuspendCallback = Gate.SuspendAsync;
    private static readonly Func<CancellationToken, Task<int>> CompleteCallback = Gate.CompleteAsync;

    /// <summary>
    /// A caller token that <i>can</i> be cancelled but never is. This is the production case —
    /// an ASP.NET request abort token, a host shutdown token — and it changes the cost of the
    /// per-attempt link by an order of magnitude, so it gets its own arms rather than being
    /// quietly folded into <c>CancellationToken.None</c>.
    /// </summary>
    public static readonly CancellationTokenSource CallerSource = new();

    public static readonly FusedExecutor None = new(FusedPolicy.None);
    public static readonly FusedExecutor NoTimeout = new(FusedPolicy.NoTimeout);
    public static readonly FusedExecutor Default = new(FusedPolicy.Default);
    public static readonly FusedExecutor Full = new(FusedPolicy.Full);
    public static readonly LeanFusedExecutor Lean = new();

    /// <summary>Decomposition arm: the real loop with the inline attempt log removed. Not a shipping shape.</summary>
    public static readonly FusedExecutor NoTimeoutNoLog = new(FusedPolicy.NoTimeout, recordAttempts: false);

    /// <summary>Decomposition arm: as <see cref="NoTimeoutNoLog"/>, with the attempt timeout back on.</summary>
    public static readonly FusedExecutor DefaultNoLog = new(FusedPolicy.Default, recordAttempts: false);

    // ---- Suspending path: the path every real I/O call takes. ----

    /// <summary>The un-wrapped callback. Everything else is reported as overhead above this.</summary>
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

    /// <summary>Static lambda plus state: no closure, no capture, and the state is a value type.</summary>
    public static ValueTask<int> FusedNoTimeoutSyncState() =>
        NoTimeout.RunAsync(static (int _, CancellationToken ct) => Gate.CompleteAsync(ct), 0);

    /// <summary>
    /// The same call with an attempt timeout in the policy. The difference between this and
    /// <see cref="FusedNoTimeoutSyncState"/> is the per-attempt linked source — and it is the
    /// reason "full policy, sync-completing, 0 bytes" needs a qualifier.
    /// </summary>
    public static ValueTask<int> FusedDefaultSyncState() =>
        Default.RunAsync(static (int _, CancellationToken ct) => Gate.CompleteAsync(ct), 0);

    public static ValueTask<int> FusedFullSyncState() =>
        Full.RunAsync(static (int _, CancellationToken ct) => Gate.CompleteAsync(ct), 0);

    /// <summary>The stateless overload: the caller's own closure and delegate, which any lambda costs.</summary>
    public static ValueTask<int> FusedNoTimeoutSyncCallback() => NoTimeout.RunAsync(CompleteCallback);

    // ---- Retry. ----

    /// <summary>
    /// Two transient failures then a success, with backoff off and no timeout source, so the
    /// number describes the retry machinery and nothing else. The fault is a cached exception
    /// instance: throwing a fresh one each attempt would measure exception construction, which
    /// both arms pay identically and neither arm's design controls.
    /// </summary>
    public static RetryArm BuildFusedRetry(int failures = 2) => new(failures);

    public sealed class RetryArm
    {
        private readonly Gate.FailCounter _counter;
        private readonly FusedExecutor _executor;
        private readonly Func<Gate.FailCounter, CancellationToken, Task<int>> _callback = Gate.SuspendThenFailAsync;

        public RetryArm(int failures)
        {
            _counter = new Gate.FailCounter(failures);
            _executor = new FusedExecutor(FusedPolicy.NoTimeout with
            {
                Attempts = failures + 1,
                UseBackoff = false,
                Budget = new ProbeBudget(capacity: int.MaxValue / 2),
            });
        }

        public void Reset() => _counter.Reset();

        public ValueTask<int> RunAsync() => _executor.RunAsync(_callback, _counter);
    }
}
