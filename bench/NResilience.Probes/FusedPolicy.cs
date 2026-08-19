namespace NResilience.Probes;

/// <summary>
/// Phase 0a stand-in for the shipping <c>Resilience</c> record. Only the fields the executor
/// frame reads are present — this exists to give the fused loop realistic work to do, not to
/// prototype the public surface, which Phase 1 owns.
/// </summary>
public sealed record FusedPolicy
{
    public int Attempts { get; init; } = 3;

    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public bool UseBackoff { get; init; } = true;

    public TimeSpan TransientBase { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan ThrottledBase { get; init; } = TimeSpan.FromSeconds(1);

    public double BackoffFactor { get; init; } = 2.0;

    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(30);

    public bool Jitter { get; init; } = true;

    public ProbeBreaker? Breaker { get; init; }

    public ProbeBudget? Budget { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Passthrough. Every bound is off, so <c>RunAsync</c> can return the callback's task directly.</summary>
    public static FusedPolicy None { get; } = new()
    {
        Attempts = 1,
        Deadline = Timeout.InfiniteTimeSpan,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
        UseBackoff = false,
        Breaker = null,
        Budget = null,
    };

    /// <summary>Retry and classification, but no timeout source. Isolates the cost of the frame itself.</summary>
    public static FusedPolicy NoTimeout { get; } = new()
    {
        Deadline = Timeout.InfiniteTimeSpan,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
        Budget = new ProbeBudget(),
    };

    /// <summary>The shape a real caller gets from <c>Resilience.Default</c>: deadline, attempt timeout, budget.</summary>
    public static FusedPolicy Default { get; } = new()
    {
        Budget = new ProbeBudget(),
    };

    /// <summary>Everything on, including a breaker. The most expensive frame this design can produce.</summary>
    public static FusedPolicy Full { get; } = new()
    {
        Breaker = new ProbeBreaker(),
        Budget = new ProbeBudget(),
    };

    internal bool IsPassthrough =>
        Attempts == 1
        && Deadline == Timeout.InfiniteTimeSpan
        && AttemptTimeout == Timeout.InfiniteTimeSpan
        && Breaker is null
        && Budget is null;
}
