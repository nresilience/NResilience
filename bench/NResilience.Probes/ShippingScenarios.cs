// The probe namespace is nested inside NResilience and defines its own Verdict, Backoff and
// AttemptBuffer as stand-ins, so an unqualified name here would bind to the stand-in
// rather than to the shipping type. `Lib` makes every reference to the library unambiguous.
using NResilience.Extensions;
using Lib = NResilience;

namespace NResilience.Probes;

/// <summary>
/// The shipping arms: these measurements repeat the stand-in tests against the shipping 
/// <see cref="Lib.Resilience"/> executor.
///
/// <para>
/// The stand-in was built in two passes to break a circularity: the first required no library 
/// code, allowing a hand-written fused loop to establish the Polly baseline and the achievable floor. 
/// The second re-runs the identical harness against the real implementation. The 
/// instrument remains identical: the same <see cref="Gate"/>, <see cref="AllocationProbe"/>, 
/// process, and run. Any performance difference is therefore attributable to the executor.
/// </para>
/// <para>
/// Stand-in arms from <see cref="Scenarios"/> are still measured because a stand-in-versus-shipping 
/// delta is only meaningful if both sides are captured in one process under one GC 
/// and one tier state. These serve as reference rows; the gates assert against the arms here.
/// </para>
/// </summary>
public static class ShippingScenarios
{
    private static readonly Func<CancellationToken, Task<int>> SuspendCallback = Gate.SuspendAsync;
    private static readonly Func<CancellationToken, Task<int>> CompleteCallback = Gate.CompleteAsync;

    /// <summary>
    /// The trivial shipping shape: implements retry and classification without a deadline 
    /// or attempt timeout. This is the smallest non-passthrough policy possible and is 
    /// used for the trivial-policy comparison. The inline attempt log is mandatory in 
    /// the shipping executor, so no "no log" variant exists.
    /// </summary>
    public static readonly Lib.Resilience Trivial = Lib.Resilience.Default with
    {
        Deadline = Timeout.InfiniteTimeSpan,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
    };

    // ---- Suspending path: the path every real I/O call takes. ----

    public static ValueTask<int> NoneSuspending() => Lib.Resilience.None.RunAsync(SuspendCallback);

    public static ValueTask<int> TrivialSuspending() => Trivial.RunAsync(SuspendCallback);

    public static ValueTask<int> DefaultSuspending() => Lib.Resilience.Default.RunAsync(SuspendCallback);

    /// <summary>
    /// The production case: a caller token that <i>can</i> be cancelled and never is. Shares
    /// <see cref="Scenarios.CallerSource"/> with the stand-in and Polly arms, so all three link
    /// against a source whose registration storage is equally warm.
    /// </summary>
    public static ValueTask<int> DefaultSuspendingCancellable() => Lib.Resilience.Default.RunAsync(SuspendCallback, Scenarios.CallerSource.Token);

    /// <summary>
    /// <c>TryRunAsync</c> always materializes the attempt log because the caller explicitly 
    /// requests a result object. This is measured so the cost of this request is published 
    /// rather than unexpected.
    /// </summary>
    public static ValueTask<Lib.CallResult<int>> TryRunDefaultSuspending() => Lib.Resilience.Default.TryRunAsync(SuspendCallback);

    /// <summary>
    /// <see cref="Lib.Resilience.Default"/> with an attached listener, representing the cost 
    /// of telemetry when a listener is active.
    ///
    /// <para>
    /// The listener is intentionally empty. The benchmark measures the executor's side 
    /// of the contract: raising events and boxing each attempt's result for a 
    /// cross-cutting listener that is not generic over <c>T</c>. The delegate is a 
    /// cached static to avoid allocating a delegate per operation, which would 
    /// incorrectly charge telemetry for the caller's coding style.
    /// </para>
    /// </summary>
    public static readonly Lib.Resilience DefaultWithListener = Lib.Resilience.Default with
    {
        OnEvent = static _ => { },
    };

    public static ValueTask<int> DefaultListenerSuspending() => DefaultWithListener.RunAsync(SuspendCallback);

    /// <summary>
    /// This arm uses the shipping log listener chained behind the empty listener, with a 
    /// logger where all levels are disabled.
    ///
    /// <para>
    /// This arm verifies the performance promise: a call with disabled logging levels 
    /// must allocate exactly the same amount as a call with a listener alone. This 
    /// measures the listener's own path - one <c>switch</c> and one <c>IsEnabled</c> 
    /// call per event, and the <c>[LoggerMessage]</c> guard that returns before 
    /// formatting strings.
    /// </para>
    /// </summary>
    public static readonly Lib.Resilience DefaultWithLogging =
        DefaultWithListener.WithLogging(SilentLogger.Instance);

    public static ValueTask<int> DefaultLoggingSuspending() => DefaultWithLogging.RunAsync(SuspendCallback);

    // ---- Synchronous fast path: where the 0-byte budgets live. ----

    public static ValueTask<int> NoneSync() => Lib.Resilience.None.RunAsync(CompleteCallback);

    /// <summary>Static lambda plus state: no closure, no capture, and the state is a value type.</summary>
    public static ValueTask<int> TrivialSyncState() =>
        Trivial.RunAsync(static (int _, CancellationToken ct) => Gate.CompleteAsync(ct), 0);

    /// <summary>The stateless overload: the caller's own closure and delegate, which any lambda costs.</summary>
    public static ValueTask<int> TrivialSyncCallback() => Trivial.RunAsync(CompleteCallback);

    /// <summary>
    /// The same call with an attempt timeout in the policy. The difference between this and
    /// <see cref="TrivialSyncState"/> is the per-attempt linked source - the reason "full policy,
    /// completes synchronously, 0 bytes" needs a qualifier rather than a fix.
    /// </summary>
    public static ValueTask<int> DefaultSyncState() =>
        Lib.Resilience.Default.RunAsync(static (int _, CancellationToken ct) => Gate.CompleteAsync(ct), 0);

    // ---- Retry. ----

    /// <summary>
    /// Simulates two transient failures followed by a success, matching the Polly retry arm. 
    /// This uses three total attempts, zero delay, and no timeout source to isolate the 
    /// retry machinery. The fault uses a cached exception instance to avoid measuring 
    /// exception construction, which both arms incur identically.
    ///
    /// <para>
    /// The retry budget is disabled for this arm. An arm that retries twice per operation 
    /// thousands of times per second without intervening success is exactly the pattern 
    /// the budget prevents. With shipping defaults, such an arm would stop retrying 
    /// after approximately thirty operations and measure rejections instead. Because 
    /// Polly has no budget to disable, the budget is turned off here to ensure the 
    /// comparison reflects identical behaviors. The cost of the budget is measured 
    /// by the Default arms.
    /// </para>
    /// </summary>
    public static RetryArm BuildRetry(int failures = 2) => new(failures);

    /// <summary>
    /// Simulates two refusals from local admission control followed by a success. Unlike 
    /// the retry arm, the retry budget remains enabled. A self-imposed refusal is not 
    /// charged to the budget, meaning the budget cannot be exhausted by this arm - 
    /// this behavior is the primary subject of the scale tests.
    /// </summary>
    public static LimitArm BuildLimited(int refusals = 2) => new(refusals);

    public sealed class LimitArm
    {
        private readonly Gate.LimitCounter _counter;
        private readonly Lib.Resilience _policy;
        private readonly Func<Gate.LimitCounter, CancellationToken, Task<int>> _callback = Gate.SuspendThenLimitAsync;

        public LimitArm(int refusals)
        {
            _counter = new Gate.LimitCounter(refusals);
            _policy = Trivial with
            {
                Attempts = refusals + 1,
                Backoff = Lib.Backoff.None,
            };
        }

        public void Reset() => _counter.Reset();

        public ValueTask<int> RunAsync() => _policy.RunAsync(_callback, _counter);
    }

    public sealed class RetryArm
    {
        private readonly Gate.FailCounter _counter;
        private readonly Lib.Resilience _policy;
        private readonly Func<Gate.FailCounter, CancellationToken, Task<int>> _callback = Gate.SuspendThenFailAsync;

        public RetryArm(int failures)
        {
            _counter = new Gate.FailCounter(failures);
            _policy = Trivial with
            {
                Attempts = failures + 1,
                Backoff = Lib.Backoff.None,
                Budget = Lib.RetryBudget.None,
            };
        }

        public void Reset() => _counter.Reset();

        public ValueTask<int> RunAsync() => _policy.RunAsync(_callback, _counter);
    }
}
