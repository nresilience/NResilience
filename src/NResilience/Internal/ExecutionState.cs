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
///     The automatic per-policy retry budget lives here for the same reason, and the table's lifetime
///     gives it exactly the scope the design needs: it is created on the policy's first execution and
///     collected with the policy, so it is private to that instance without a field ever holding it.
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

    private readonly LatencyWindow? _latency;

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
