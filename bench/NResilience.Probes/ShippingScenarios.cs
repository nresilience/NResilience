// The probe namespace is nested inside NResilience and defines its own Verdict, Backoff and
// AttemptBuffer as stand-ins, so an unqualified name here would bind to the stand-in
// rather than to the shipping type. `Lib` makes every reference to the library unambiguous.

using NResilience.Extensions;
using Lib = NResilience;

namespace NResilience.Probes;

/// <summary>
///     The shipping arms: these measurements repeat the stand-in tests against the shipping
///     <see cref="Lib.Resilience" /> executor.
///     <para>
///         The stand-in was built in two passes to break a circularity: the first required no library
///         code, allowing a hand-written fused loop to establish the Polly baseline and the achievable floor.
///         The second re-runs the identical harness against the real implementation. The
///         instrument remains identical: the same <see cref="Gate" />, <see cref="AllocationProbe" />,
///         process, and run. Any performance difference is therefore attributable to the executor.
///     </para>
///     <para>
///         Stand-in arms from <see cref="Scenarios" /> are still measured because a stand-in-versus-shipping
///         delta is only meaningful if both sides are captured in one process under one GC
///         and one tier state. These serve as reference rows; the gates assert against the arms here.
///     </para>
/// </summary>
public static class ShippingScenarios
{
    private static readonly Func<CancellationToken, Task<int>> SuspendCallback = Gate.SuspendAsync;
    private static readonly Func<CancellationToken, Task<int>> CompleteCallback = Gate.CompleteAsync;
    private static readonly Func<CancellationToken, ValueTask<int>> ValueSuspendCallback = ValueGate.SuspendAsync;

    /// <summary>
    ///     The trivial shipping shape: implements retry and classification without a deadline
    ///     or attempt timeout. This is the smallest non-passthrough policy possible and is
    ///     used for the trivial-policy comparison. The inline attempt log is mandatory in
    ///     the shipping executor, so no "no log" variant exists.
    /// </summary>
    public static readonly Resilience Trivial = Resilience.Default with
    {
        Deadline = Timeout.InfiniteTimeSpan,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    ///     Passthrough reached by derivation rather than by naming <see cref="Lib.Resilience.None" />:
    ///     every bound turned off from <see cref="Lib.Resilience.Default" />, which carries
    ///     <see cref="Lib.RetryBudget.Automatic" />.
    ///     <para>
    ///         The marker is only free here because the shape cannot retry, and
    ///         <c>Resilience.IsPassthrough</c> admits it on exactly that ground. This arm is what keeps
    ///         the two facts from drifting apart: turn the attempt count back up in
    ///         <c>IsPassthrough</c> without turning the marker back off and this allocates an executor
    ///         frame where it handed back the callback's own task.
    ///     </para>
    /// </summary>
    public static readonly Resilience DerivedPassthrough = Resilience.Default with
    {
        Attempts = 1,
        Deadline = Timeout.InfiniteTimeSpan,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    ///     <see cref="Lib.Resilience.Default" /> with an attached listener, representing the cost
    ///     of telemetry when a listener is active.
    ///     <para>
    ///         The listener is intentionally empty. The benchmark measures the executor's side
    ///         of the contract: raising events and boxing each attempt's result for a
    ///         cross-cutting listener that is not generic over <c>T</c>. The delegate is a
    ///         cached static to avoid allocating a delegate per operation, which would
    ///         incorrectly charge telemetry for the caller's coding style.
    ///     </para>
    /// </summary>
    public static readonly Resilience DefaultWithListener = Resilience.Default with
    {
        OnEvent = static _ => { },
    };

    /// <summary>
    ///     This arm uses the shipping log listener chained behind the empty listener, with a
    ///     logger where all levels are disabled.
    ///     <para>
    ///         This arm verifies the performance promise: a call with disabled logging levels
    ///         must allocate exactly the same amount as a call with a listener alone. This
    ///         measures the listener's own path - one <c>switch</c> and one <c>IsEnabled</c>
    ///         call per event, and the <c>[LoggerMessage]</c> guard that returns before
    ///         formatting strings.
    ///     </para>
    /// </summary>
    public static readonly Resilience DefaultWithLogging =
        DefaultWithListener.WithLogging(SilentLogger.Instance);

    /// <summary>
    ///     The completed task <see cref="AdmitHook" /> hands back on every attempt. Cached rather than
    ///     built with <c>Task.FromResult</c> per call, so this arm measures only what the executor's
    ///     second, <see cref="Lib.Resilience.Admit" />-configured loop costs - not the cost of a hook
    ///     that allocates its own return value.
    /// </summary>
    private static readonly Task<Lib.Verdict> AdmitOk = Task.FromResult(Lib.Verdict.Ok);

    private static readonly Func<NextAttempt, Task<Lib.Verdict>> AdmitHook = static _ => AdmitOk;

    /// <summary>
    ///     <see cref="Lib.Resilience.Default" /> with <see cref="Lib.Resilience.Admit" /> configured to
    ///     always admit. This selects the second execution path - see
    ///     <c>Resilience.ExecuteWithAdmitAsync</c> - and measures the one hoisted awaiter field that
    ///     path adds, isolated from every other cost by comparing against <see cref="DefaultSuspending" />
    ///     in the same sweep.
    /// </summary>
    public static readonly Resilience DefaultWithAdmit = Resilience.Default with
    {
        Admit = AdmitHook,
    };

    /// <summary>
    ///     <see cref="Lib.Resilience.Default" /> with hedging configured. This selects the third
    ///     execution path - <c>Resilience.ExecuteHedgedAsync</c> - which is the one path in the library
    ///     that deliberately allocates: a list of legs, a task per leg, and the array
    ///     <see cref="Task.WhenAny(Task[])" /> races over.
    ///     <para>
    ///         No hedge actually fires here, and that is the measurement worth having. The callback comes
    ///         back in microseconds while <see cref="Lib.Hedge.MinimumDelay" /> floors the threshold at
    ///         10 ms, so this prices the <i>steady state</i> - what a caller pays per call for having
    ///         turned hedging on, on the calls that never needed it, which is almost all of them.
    ///     </para>
    /// </summary>
    public static readonly Resilience DefaultWithHedge = Resilience.Default with
    {
        Hedge = Hedge.At(0.95),
    };

    /// <summary>
    ///     <see cref="Lib.Resilience.Default" /> with the measured attempt ceiling turned off. Paired
    ///     with <see cref="DefaultSuspending" /> in the same sweep, this is what the defaulted-on
    ///     ceiling costs a call that never needed it: the per-attempt estimate lookup and the recorded
    ///     sample, priced against the identical policy without them.
    /// </summary>
    public static readonly Resilience DefaultWithoutCeiling = Resilience.Default with
    {
        AttemptCeiling = null,
    };

    /// <summary>
    ///     <see cref="Lib.Resilience.Default" /> with the retry budget turned off, for the same reason
    ///     <see cref="DefaultWithoutCeiling" /> exists. <see cref="Lib.RetryBudget.None" /> rather than
    ///     <c>null</c> so the arm names the disabled state the way a caller would.
    /// </summary>
    public static readonly Resilience DefaultWithoutBudget = Resilience.Default with
    {
        Budget = Lib.RetryBudget.None,
    };

    // ---- Suspending path: the path every real I/O call takes. ----

    public static ValueTask<int> NoneSuspending() => Resilience.None.RunAsync(SuspendCallback);

    public static ValueTask<int> DerivedPassthroughSuspending() => DerivedPassthrough.RunAsync(SuspendCallback);

    public static ValueTask<int> TrivialSuspending() => Trivial.RunAsync(SuspendCallback);

    public static ValueTask<int> DefaultSuspending() => Resilience.Default.RunAsync(SuspendCallback);

    /// <summary>
    ///     The production case: a caller token that <i>can</i> be cancelled and never is. Shares
    ///     <see cref="Scenarios.CallerSource" /> with the stand-in and Polly arms, so all three link
    ///     against a source whose registration storage is equally warm.
    /// </summary>
    public static ValueTask<int> DefaultSuspendingCancellable() => Resilience.Default.RunAsync(SuspendCallback, Scenarios.CallerSource.Token);

    /// <summary>
    ///     <c>TryRunAsync</c> always materializes the attempt log because the caller explicitly
    ///     requests a result object. This is measured so the cost of this request is published
    ///     rather than unexpected.
    /// </summary>
    public static ValueTask<CallResult<int>> TryRunDefaultSuspending() => Resilience.Default.TryRunAsync(SuspendCallback);

    public static ValueTask<int> DefaultListenerSuspending() => DefaultWithListener.RunAsync(SuspendCallback);

    public static ValueTask<int> DefaultAdmitSuspending() => DefaultWithAdmit.RunAsync(SuspendCallback);

    public static ValueTask<int> DefaultLoggingSuspending() => DefaultWithLogging.RunAsync(SuspendCallback);

    public static ValueTask<int> DefaultHedgeSuspending() => DefaultWithHedge.RunAsync(SuspendCallback);

    /// <summary>What <see cref="Lib.Resilience.AttemptCeiling" /> being on by default costs, per call.</summary>
    public static ValueTask<int> DefaultNoCeilingSuspending() => DefaultWithoutCeiling.RunAsync(SuspendCallback);

    /// <summary>What <see cref="Lib.RetryBudget.Automatic" /> being on by default costs, per call.</summary>
    public static ValueTask<int> DefaultNoBudgetSuspending() => DefaultWithoutBudget.RunAsync(SuspendCallback);

    // ---- Synchronous fast path: where the 0-byte budgets live. ----

    public static ValueTask<int> NoneSync() => Resilience.None.RunAsync(CompleteCallback);

    public static ValueTask<int> DerivedPassthroughSync() => DerivedPassthrough.RunAsync(CompleteCallback);

    /// <summary>Static lambda plus state: no closure, no capture, and the state is a value type.</summary>
    public static ValueTask<int> TrivialSyncState() =>
        Trivial.RunAsync(static (_, ct) => Gate.CompleteAsync(ct), 0);

    /// <summary>The stateless overload: the caller's own closure and delegate, which any lambda costs.</summary>
    public static ValueTask<int> TrivialSyncCallback() => Trivial.RunAsync(CompleteCallback);

    /// <summary>
    ///     The same call with an attempt timeout in the policy. The difference between this and
    ///     <see cref="TrivialSyncState" /> is the per-attempt linked source - the reason "full policy,
    ///     completes synchronously, 0 bytes" needs a qualifier rather than a fix.
    /// </summary>
    public static ValueTask<int> DefaultSyncState() =>
        Resilience.Default.RunAsync(static (_, ct) => Gate.CompleteAsync(ct), 0);

    // ---- ValueTask-returning callbacks. ----

    /// <summary>
    ///     The headline for a <see cref="ValueTask" />-returning callback: the same zero the
    ///     <see cref="Task" />-returning form reaches, on the path the callback already had its answer.
    /// </summary>
    public static ValueTask<int> TrivialValueSyncState() =>
        Trivial.RunAsync(static (_, ct) => ValueGate.CompleteAsync(ct), 0);

    /// <summary>
    ///     The same callback wrapped as a <see cref="Task" />, which is what a caller has to write when
    ///     there is no <see cref="ValueTask" /> overload to bind to. A reference row rather than a gate:
    ///     it prices <c>AsTask()</c>, and the gap between it and <see cref="TrivialValueSyncState" /> is
    ///     the whole reason the overloads exist.
    /// </summary>
    public static ValueTask<int> TrivialValueAsTaskSyncState() =>
        Trivial.RunAsync(static (_, ct) => ValueGate.CompleteAsTaskAsync(ct), 0);

    /// <summary>The same call with an attempt timeout, so the linked source is the only difference.</summary>
    public static ValueTask<int> DefaultValueSyncState() =>
        Resilience.Default.RunAsync(static (_, ct) => ValueGate.CompleteAsync(ct), 0);

    /// <summary>
    ///     A <see cref="ValueTask" /> callback that suspends. Gated against the
    ///     <see cref="Task" />-returning figure rather than against a budget of its own: the point is
    ///     that the second callback shape costs the state-machine box nothing, and a number that drifts
    ///     apart from <see cref="DefaultSuspending" /> means a hoisted awaiter field has appeared.
    /// </summary>
    public static ValueTask<int> DefaultValueSuspending() => Resilience.Default.RunAsync(ValueSuspendCallback);

    // ---- Retry. ----

    /// <summary>
    ///     Simulates two transient failures followed by a success, matching the Polly retry arm.
    ///     This uses three total attempts, zero delay, and no timeout source to isolate the
    ///     retry machinery. The fault uses a cached exception instance to avoid measuring
    ///     exception construction, which both arms incur identically.
    ///     <para>
    ///         The retry budget is disabled for this arm. An arm that retries twice per operation
    ///         thousands of times per second without intervening success is exactly the pattern
    ///         the budget prevents. With shipping defaults, such an arm would stop retrying
    ///         after approximately thirty operations and measure rejections instead. Because
    ///         Polly has no budget to disable, the budget is turned off here to ensure the
    ///         comparison reflects identical behaviors. The cost of the budget is measured
    ///         by the Default arms.
    ///     </para>
    /// </summary>
    public static RetryArm BuildRetry(int failures = 2) => new(failures);

    /// <summary>
    ///     Simulates two refusals from local admission control followed by a success. Unlike
    ///     the retry arm, the retry budget remains enabled. A self-imposed refusal is not
    ///     charged to the budget, meaning the budget cannot be exhausted by this arm -
    ///     this behavior is the primary subject of the scale tests.
    /// </summary>
    public static LimitArm BuildLimited(int refusals = 2) => new(refusals);

    public sealed class LimitArm
    {
        private readonly Func<Gate.LimitCounter, CancellationToken, Task<int>> _callback = Gate.SuspendThenLimitAsync;
        private readonly Gate.LimitCounter _counter;
        private readonly Resilience _policy;

        public LimitArm(int refusals)
        {
            _counter = new Gate.LimitCounter(refusals);

            _policy = Trivial with
            {
                Attempts = refusals + 1,
                Backoff = Backoff.None,
            };
        }

        public void Reset() => _counter.Reset();

        public ValueTask<int> RunAsync() => _policy.RunAsync(_callback, _counter);
    }

    public sealed class RetryArm
    {
        private readonly Func<Gate.FailCounter, CancellationToken, Task<int>> _callback = Gate.SuspendThenFailAsync;
        private readonly Gate.FailCounter _counter;
        private readonly Resilience _policy;

        public RetryArm(int failures)
        {
            _counter = new Gate.FailCounter(failures);

            _policy = Trivial with
            {
                Attempts = failures + 1,
                Backoff = Backoff.None,
                Budget = RetryBudget.None,
            };
        }

        public void Reset() => _counter.Reset();

        public ValueTask<int> RunAsync() => _policy.RunAsync(_callback, _counter);
    }

    // ---- Streaming. ----

    /// <summary>
    ///     The streaming shape under the shipping policy: a cold source that suspends before every
    ///     element, pulled once to the first element and then handed to the consumer, under
    ///     <see cref="Lib.Resilience.Default" />. Measured against
    ///     <see cref="StreamGate.RawSuspending" />, the identical enumeration with no policy in the
    ///     middle, so the difference is the streaming path's own cost and nothing else.
    /// </summary>
    public static ValueTask<int> DefaultStreamSuspending() =>
        StreamGate.DrainAsync(Resilience.Default.RunAsync(static ct => StreamGate.SuspendAsync(ct)));
}
