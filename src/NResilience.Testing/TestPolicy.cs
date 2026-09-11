using NResilience.Testing.Internal;

namespace NResilience.Testing;

/// <summary>Policies shaped for tests, where sleeping and wall-clock bounds are noise.</summary>
public static class TestPolicy
{
    /// <summary>
    ///     Retries on the classifier's say-so, sleeps nothing, and never times out. Storm protection
    ///     is off, which is why this is not a shape to ship.
    /// </summary>
    public static Resilience Instant { get; } = Resilience.Default with
    {
        Attempts = 3,
        Deadline = Timeout.InfiniteTimeSpan,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
        Backoff = Backoff.None,
        Budget = RetryBudget.None,
    };

    /// <summary><see cref="Instant" /> with <see cref="Classifier.Http" />.</summary>
    public static Resilience InstantHttp { get; } = Instant with { Classifier = Classifier.Http, Name = "http" };

    /// <summary>
    ///     <see cref="Instant" /> on a test clock, with any breaker the policy carries rebuilt on the
    ///     same clock - so one <see cref="TimeProvider" /> drives the policy, its breaker and its budget.
    /// </summary>
    /// <param name="time">The clock.</param>
    /// <returns>The policy.</returns>
    public static Resilience WithClock(TimeProvider time) => Instant.WithClock(time);

    /// <summary>
    ///     This policy on the given clock, rebuilding the live objects it carries on that clock too.
    ///     <para>
    ///         A breaker and a retry budget both accumulate state against a clock, and neither can be
    ///         rebased in place, so the returned policy carries new ones with the same settings and
    ///         nothing accumulated. Leaving either on its original clock is what would make a run on a
    ///         virtual clock depend on how much real time passed while it ran.
    ///     </para>
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <param name="time">The clock.</param>
    /// <returns>The policy, on one clock throughout.</returns>
    /// <remarks>
    ///     Named for <see cref="Resilience.Time" />, the property it sets, and shaped like the
    ///     <c>WithListener</c> / <c>WithLogging</c> / <c>WithTelemetry</c> family: it returns a new
    ///     policy and mutates nothing.
    /// </remarks>
    public static Resilience WithClock(this Resilience policy, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(time);

        return policy.WithClock(time, new BudgetClock(time));
    }

    /// <summary>
    ///     This policy on the given clock, taking its retry budget from <paramref name="budgets" /> so
    ///     that policies sharing a budget within one run keep sharing it.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <param name="time">The clock.</param>
    /// <param name="budgets">The run's budgets.</param>
    /// <returns>The policy, on one clock throughout.</returns>
    internal static Resilience WithClock(this Resilience policy, TimeProvider time, BudgetClock budgets) =>
        policy with
        {
            Time = time,
            Breaker = policy.Breaker is { } breaker
                ? new Breaker(breaker.Settings with { Time = time })
                : null,
            Budget = budgets.For(policy.Budget),
        };
}
