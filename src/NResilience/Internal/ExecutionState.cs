using System.Runtime.CompilerServices;

namespace NResilience.Internal;

/// <summary>
///     Per-policy-instance runtime state that must not be visible to the record's equality.
///     <para>
///         A record's synthesized <c>Equals</c> and <c>GetHashCode</c> compare every instance field, so a
///         private "already validated" flag would make two identically-configured policies stop being
///         equal - and change hash code - as a side effect of one of them having executed, silently
///         corrupting any <c>Dictionary&lt;Resilience, …&gt;</c>. This table is keyed by reference
///         identity, which the record's equality cannot see.
///     </para>
///     <para>
///         The automatic per-policy retry budget lives here for the same reason, and the table's lifetime
///         gives it exactly the scope the design needs: it is created on the policy's first execution and
///         collected with the policy, so it is private to that instance without a field ever holding it.
///     </para>
/// </summary>
internal sealed class ExecutionState
{
    private static readonly ConditionalWeakTable<Resilience, ExecutionState> Table = new();

    private static readonly ConditionalWeakTable<Resilience, ExecutionState>.CreateValueCallback Create =
        static policy =>
        {
            policy.Validate();
            return new ExecutionState(policy);
        };

    /// <summary>
    ///     The last policy this thread executed, and its state. Steady state is one reference
    ///     comparison; the table is only consulted when a call site changes policy, and only the first
    ///     such call validates.
    /// </summary>
    [ThreadStatic] private static Resilience? t_lastPolicy;

    [ThreadStatic] private static ExecutionState? t_lastState;

    private readonly RetryBudget? _automaticBudget;

    private readonly LatencyWindow? _backoffBase;

    private readonly LatencyWindow? _ceiling;

    /// <summary>
    ///     The effective <see cref="Resilience.AttemptCeiling" /> for this policy instance, resolved once.
    ///     Non-null exactly when <see cref="_ceiling" /> is.
    /// </summary>
    private readonly AttemptCeiling? _ceilingSettings;

    private readonly LatencyWindow? _latency;

    private readonly WinWindow? _winRate;

    /// <summary>
    ///     The last measured backoff base reported for this policy instance, in ticks. Zero until one
    ///     is, which no real base can be.
    /// </summary>
    private long _lastBackoffBaseTicks;

    /// <summary>
    ///     The last measured ceiling reported for this policy instance, in ticks. Zero until one is,
    ///     which no real ceiling can be.
    /// </summary>
    private long _lastCeilingTicks;

    private ExecutionState(Resilience policy)
    {
        // A policy that cannot retry has nothing to spend and nobody to fund, so it gets no budget
        // at all rather than one that is never consulted. An *explicit* budget on such a policy is
        // still honored, because a shared one is funded by its successful traffic.
        _automaticBudget = policy.Attempts > 1 ? RetryBudget.CreateAutomatic(policy.Time) : null;

        // Only a hedging policy pays for the latency estimate, and it pays once per policy instance
        // rather than once per call. Validate() has already run, so the quantile and the window are
        // known good here.
        _latency = policy.Hedge is { } hedge ? new LatencyWindow(hedge.Quantile, hedge.Window, policy.Time) : null;

        // Read once, here, and the answer kept. AttemptCeiling is a computed property whose defaulted-on
        // path builds a 64-byte struct and compares its floor against AttemptTimeout on every read, and
        // the executor reads it twice per attempt. A field on the policy cannot hold the answer - a
        // record's synthesized equality compares every instance field - but this table is exactly the
        // home for per-instance derived state equality must not see.
        _ceilingSettings = policy.AttemptCeiling;

        // A second window rather than a shared one, because one window answers one quantile - and these
        // two want opposite things from the same distribution. A hedge reads a high quantile of a short
        // window so the threshold moves with the dependency; a ceiling reads a high quantile of a long
        // one so it does not. See LatencyWindow's remarks: this is the case they flagged.
        _ceiling = _ceilingSettings is { } ceiling
            ? new LatencyWindow(ceiling.Quantile, ceiling.Window, policy.Time)
            : null;

        // A third window, and the same argument again: this one reads a *low* quantile of a long
        // window, because a backoff base is a measure of what healthy looked like rather than of the
        // tail. It is the reading SlowCalls takes, and the breaker's copy of it is not reachable here -
        // a Breaker is a live object two policies may share, and it may not be configured at all.
        _backoffBase = policy.Backoff.MeasuredBase is { } measured
            ? new LatencyWindow(measured.Quantile, measured.Window, policy.Time)
            : null;

        // Not a latency window at all: this one counts hedges won against hedges started, and holds the
        // allowance that count moves. Same scope argument as the hedge's own estimate - the HTTP handler
        // derives a policy per host, and whether hedging wins against one host says nothing about
        // another.
        _winRate = policy.Hedge is { WinRate: { } feedback } ? new WinWindow(feedback, policy.Time) : null;
    }

    /// <summary>Validates the policy on its first execution, and caches the result per thread.</summary>
    public static void EnsureValidated(Resilience policy) => _ = StateFor(policy);

    /// <summary>
    ///     The budget this call should charge: the policy's own, or - for <see cref="RetryBudget.Automatic" />
    ///     - the bucket private to this policy instance. Null when there is no budget to consult, which is
    ///     the only case the executor pays nothing for.
    /// </summary>
    /// <remarks>
    ///     Read once per execution into a local rather than at each of the two points that need it. The
    ///     local costs 8 bytes of state-machine box; re-resolving after the attempt <c>await</c> would
    ///     cost a table lookup instead, and would miss the per-thread cache most of the time because a
    ///     continuation resumes on whichever thread-pool thread is free.
    /// </remarks>
    public static RetryBudget? BudgetFor(Resilience policy)
    {
        // A null budget is genuinely "no budget", and so is RetryBudget.None.
        if (policy.Budget is not { } configured || configured.IsNone)
            return null;

        if (!configured.IsAutomatic)
            return configured;

        // EnsureValidated ran first, from the entry point, so the warm path here is the reference
        // comparison it just primed.
        return StateFor(policy)._automaticBudget;
    }

    /// <summary>
    ///     The latency estimate this policy hedges against, or null when it does not hedge.
    /// </summary>
    /// <remarks>
    ///     Private to the policy instance, and that is exactly the scope hedging wants. The HTTP handler
    ///     derives one policy per host, so each host gets its own estimate for free - and it has to,
    ///     because the p95 of one host is not the p95 of another and hedging the fast host against the
    ///     slow host's tail would hedge everything.
    /// </remarks>
    public static LatencyWindow? LatencyFor(Resilience policy) => StateFor(policy)._latency;

    /// <summary>
    ///     The latency estimate this policy measures its attempt ceiling from, or null when it does not
    ///     have one.
    /// </summary>
    /// <remarks>
    ///     Resolved at each of the two points that need it - once before an attempt to read the ceiling,
    ///     once after a successful one to record it - rather than hoisted into a local by the caller. A
    ///     reference held across the attempt <c>await</c> would be a field in every caller's
    ///     state-machine box whether or not <see cref="Resilience.AttemptCeiling" /> was ever configured, and
    ///     this feature is not allowed to cost the callers who did not ask for it. The steady-state read
    ///     is the per-thread reference comparison <see cref="StateFor" /> primes; a continuation that
    ///     resumed on another pool thread pays one lock-free table lookup instead, and no allocation
    ///     either way.
    /// </remarks>
    public static LatencyWindow? AttemptCeilingFor(Resilience policy) => StateFor(policy)._ceiling;

    /// <summary>
    ///     The effective <see cref="Resilience.AttemptCeiling" /> for this policy and the window it
    ///     measures from, in one lookup. Null when the policy has no ceiling.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <param name="window">The window the ceiling is measured from. Non-null whenever the return is.</param>
    /// <returns>The ceiling, or null.</returns>
    /// <remarks>
    ///     Both halves together because the one caller that wants the settings wants the window in the
    ///     same breath, and <see cref="StateFor" /> holds both. Reading
    ///     <see cref="Resilience.AttemptCeiling" /> instead would recompute the default on every read:
    ///     the getter's defaulted-on path constructs an <see cref="NResilience.AttemptCeiling" /> and
    ///     compares its floor against the attempt timeout, which measured at 5.68 ns against 0.32 ns for
    ///     a stored one.
    /// </remarks>
    public static AttemptCeiling? CeilingFor(Resilience policy, out LatencyWindow? window)
    {
        var state = StateFor(policy);
        window = state._ceiling;
        return state._ceilingSettings;
    }

    /// <summary>
    ///     The latency estimate this policy measures its backoff base from, or null when it does not
    ///     have one.
    /// </summary>
    /// <remarks>
    ///     Resolved at the two points that need it - once on the retry decision to read the base, once
    ///     after a successful attempt to record it - rather than hoisted into a local, for the reason
    ///     <see cref="AttemptCeilingFor" /> gives.
    /// </remarks>
    public static LatencyWindow? BackoffBaseFor(Resilience policy) => StateFor(policy)._backoffBase;

    /// <summary>
    ///     The win-rate feedback loop for this policy, or null when it does not have one.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <returns>The loop, or null.</returns>
    /// <remarks>
    ///     Read once per hedged execution into a local, unlike <see cref="AttemptCeilingFor" />: the only
    ///     loop that reaches this is the hedged one, which already holds several such locals and is the
    ///     one path the allocation gates do not measure.
    /// </remarks>
    public static WinWindow? WinRateFor(Resilience policy) => StateFor(policy)._winRate;

    /// <summary>
    ///     Whether this measured ceiling differs from the last one reported for this policy instance,
    ///     and records it either way.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <param name="ceiling">The ceiling about to be applied.</param>
    /// <returns>True when it is worth telling a listener about.</returns>
    /// <remarks>
    ///     Keeps the event rate proportional to how much the estimate moves rather than to traffic.
    ///     <see cref="LatencyWindow" /> memoizes its answer per slice, so a steady dependency changes
    ///     this a handful of times per window and a listener sees a handful of events. Reached only when
    ///     a listener is configured and the measured term actually won.
    /// </remarks>
    public static bool CeilingChanged(Resilience policy, TimeSpan ceiling)
    {
        var ticks = ceiling.Ticks;

        return Interlocked.Exchange(ref StateFor(policy)._lastCeilingTicks, ticks) != ticks;
    }

    /// <summary>
    ///     Whether this measured backoff base differs from the last one reported for this policy
    ///     instance, and records it either way.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <param name="base">The base about to be applied.</param>
    /// <returns>True when it is worth telling a listener about.</returns>
    /// <remarks>
    ///     The same rate argument <see cref="CeilingChanged" /> makes, and it matters more here: this
    ///     one is read on the retry decision, so without it a listener would see an event per retry
    ///     during exactly the incident it is trying to read.
    /// </remarks>
    public static bool BackoffBaseChanged(Resilience policy, TimeSpan @base)
    {
        var ticks = @base.Ticks;

        return Interlocked.Exchange(ref StateFor(policy)._lastBackoffBaseTicks, ticks) != ticks;
    }

    /// <summary>
    ///     The state for a policy: the per-thread cache when this thread just used the same policy,
    ///     the table otherwise. Consulting the table is what validates the policy, once.
    /// </summary>
    private static ExecutionState StateFor(Resilience policy)
    {
        if (ReferenceEquals(t_lastPolicy, policy))
            return t_lastState!;

        var state = Table.GetValue(policy, Create);
        t_lastState = state;
        t_lastPolicy = policy;
        return state;
    }
}
