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
    public static Resilience On(TimeProvider time) => Instant.UseClock(time);

    /// <summary>
    ///     This policy on the given clock, rebuilding the breaker it carries on that clock too. A
    ///     breaker is a live object and cannot be rebased, so the returned policy carries a new one
    ///     with the same settings and no accumulated state.
    /// </summary>
    /// <param name="policy">The policy.</param>
    /// <param name="time">The clock.</param>
    /// <returns>The policy, on one clock throughout.</returns>
    public static Resilience UseClock(this Resilience policy, TimeProvider time) =>
        policy with
        {
            Time = time,
            Breaker = policy.Breaker is { } breaker
                ? new Breaker(breaker.Settings with { Time = time })
                : null,
        };
}
