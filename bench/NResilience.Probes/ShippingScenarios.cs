// The probe namespace is nested inside NResilience and defines its own Verdict, Backoff and
// AttemptBuffer as Phase 0a stand-ins, so an unqualified name here would bind to the stand-in
// rather than to the shipping type. `Lib` makes every reference to the library unambiguous.
using Lib = NResilience;

namespace NResilience.Probes;

/// <summary>
/// The Phase 0b arms: the same measurements Phase 0a took against a hand-written stand-in, taken
/// against the shipping <see cref="Lib.Resilience"/> executor.
///
/// <para>
/// Phase 0a's split into 0a and 0b existed to break a circularity — 0a needed no library code, so
/// a stand-in loop established both the Polly figure and the achievable floor before anything was
/// built on it, and 0b re-runs the identical harness against the real thing. Nothing about the
/// instrument changes here: the same <see cref="Gate"/>, the same <see cref="AllocationProbe"/>,
/// the same process, the same run. Only the executor under test is different, so any movement is
/// attributable to it.
/// </para>
/// <para>
/// The stand-in arms in <see cref="Scenarios"/> are kept and still measured, because a 0a-versus-0b
/// delta is only meaningful if both sides were taken in one process under one GC in one tier state.
/// They are reference rows now; the gates assert against the arms here.
/// </para>
/// </summary>
public static class ShippingScenarios
{
    private static readonly Func<CancellationToken, Task<int>> SuspendCallback = Gate.SuspendAsync;
    private static readonly Func<CancellationToken, Task<int>> CompleteCallback = Gate.CompleteAsync;

    /// <summary>
    /// The trivial shipping shape: retry and classification, with no deadline and no attempt
    /// timeout. This is as small as a non-passthrough policy gets, and it is the arm the
    /// trivial-policy comparison uses — the inline attempt log is not optional in the shipping
    /// executor, so there is no "no log" variant to measure.
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
    /// <c>TryRunAsync</c> always materialises the attempt log, because its caller has explicitly
    /// asked for a result object. Measured so the cost of asking is a published number rather than
    /// a surprise.
    /// </summary>
    public static ValueTask<Lib.CallResult<int>> TryRunDefaultSuspending() => Lib.Resilience.Default.TryRunAsync(SuspendCallback);

    /// <summary>
    /// <see cref="Lib.Resilience.Default"/> with a listener attached: the price of telemetry when
    /// somebody is actually listening.
    ///
    /// <para>
    /// The listener does nothing, on purpose. What is being priced is the executor's side of the
    /// contract — raising the events and boxing each attempt's result for a cross-cutting listener
    /// that has no <c>T</c> to be generic over — not whatever a real listener would then do with
    /// them. The delegate is a cached static, because a lambda written inline at the call site
    /// would allocate a delegate per operation and charge telemetry for the caller's own style.
    /// </para>
    /// </summary>
    public static readonly Lib.Resilience DefaultWithListener = Lib.Resilience.Default with
    {
        OnEvent = static _ => { },
    };

    public static ValueTask<int> DefaultListenerSuspending() => DefaultWithListener.RunAsync(SuspendCallback);

    // ---- Synchronous fast path: where the 0-byte budgets live. ----

    public static ValueTask<int> NoneSync() => Lib.Resilience.None.RunAsync(CompleteCallback);

    /// <summary>Static lambda plus state: no closure, no capture, and the state is a value type.</summary>
    public static ValueTask<int> TrivialSyncState() =>
        Trivial.RunAsync(static (int _, CancellationToken ct) => Gate.CompleteAsync(ct), 0);

    /// <summary>The stateless overload: the caller's own closure and delegate, which any lambda costs.</summary>
    public static ValueTask<int> TrivialSyncCallback() => Trivial.RunAsync(CompleteCallback);

    /// <summary>
    /// The same call with an attempt timeout in the policy. The difference between this and
    /// <see cref="TrivialSyncState"/> is the per-attempt linked source — the reason "full policy,
    /// sync-completing, 0 bytes" needs a qualifier rather than a fix.
    /// </summary>
    public static ValueTask<int> DefaultSyncState() =>
        Lib.Resilience.Default.RunAsync(static (int _, CancellationToken ct) => Gate.CompleteAsync(ct), 0);

    // ---- Retry. ----

    /// <summary>
    /// Two transient failures then a success, matched to the Polly retry arm: three total
    /// attempts, zero delay, no timeout source. The fault is a cached exception instance, so the
    /// figure describes the retry machinery rather than exception construction, which both arms
    /// pay identically.
    ///
    /// <para>
    /// The retry budget is turned <b>off</b> on this arm, and that is a finding rather than a
    /// convenience. An arm that retries twice per operation, thousands of times a second, with no
    /// intervening success to fund it, is precisely the traffic pattern the budget exists to refuse -
    /// so with the shipping default it stops retrying after about thirty operations and the arm
    /// measures rejections instead of retries. Polly has no budget to disable, so leaving it on would
    /// also make the A/B a comparison of two different behaviours. What the budget costs when it is
    /// on is measured by the Default arms, whose successful attempts each take the deposit path.
    /// </para>
    /// </summary>
    public static RetryArm BuildRetry(int failures = 2) => new(failures);

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
