using NResilience.Extensions;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     A limiter in a simulation: the real one, acquired inside the attempt, on the run's virtual
///     clock.
///     <para>
///         A limiter is the other half of controlling amplification. A retry budget bounds the share
///         of traffic that is retried; a limiter bounds how much leaves the process at all, before
///         anything has gone wrong. Nothing here models one - the run builds the caller's limiter and
///         asks it, and the two kinds it refuses are refused because they would answer with the wall
///         clock rather than with the run's.
///     </para>
/// </summary>
public sealed class SimulationLimiterTests
{
    private static readonly Dependency Fast =
        Dependency.Healthy(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(300));

    private static readonly Resilience Api = TestPolicy.Instant with
    {
        Attempts = 3,
        Deadline = TimeSpan.FromSeconds(10),
        AttemptTimeout = TimeSpan.FromSeconds(2),
        Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(50)),
    };

    private static Simulation Run(Dependency? dependency = null, Resilience? policy = null, int seconds = 20) =>
        Simulate.Policy(policy ?? Api)
            .Against(dependency ?? Fast)
            .Under(Load.Constant(perSecond: 400))
            .For(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void A_run_with_no_limiter_refuses_nothing()
    {
        var report = Run().Run(seed: 7);

        Assert.Equal(0, report.RefusedByLimiter);
        Assert.DoesNotContain("LimiterRefusals", report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bulkhead_is_invisible_until_the_dependency_slows()
    {
        // Four hundred calls a second at 50 ms apiece is about twenty in flight, so forty permits is
        // headroom while the dependency is well.
        static Simulation Bulkhead(Dependency dependency) =>
            Run(dependency).WithLimiter(_ => Limit.Concurrency(permits: 40));

        var healthy = Bulkhead(Fast).Run(seed: 7);
        var slowing = Bulkhead(Fast.Brownout(TimeSpan.FromSeconds(5), slower: 8, TimeSpan.FromSeconds(10))).Run(seed: 7);

        // A guard that costs nothing while nothing is wrong is the whole argument for setting one.
        Assert.Equal(0, healthy.RefusedByLimiter);

        // And one that bites the moment calls start piling up is what stops the pile-up spreading.
        Assert.True(
            slowing.RefusedByLimiter > 0,
            $"a brownout put nothing past a forty-permit bulkhead; it refused {slowing.RefusedByLimiter}");
    }

    [Fact]
    public void A_bulkhead_holds_the_dependency_to_less_than_it_would_otherwise_feel()
    {
        var slowing = Fast.Brownout(TimeSpan.FromSeconds(5), slower: 8, TimeSpan.FromSeconds(10));

        var open = Run(slowing).Run(seed: 7);
        var bounded = Run(slowing).WithLimiter(_ => Limit.Concurrency(permits: 40)).Run(seed: 7);

        Assert.True(
            bounded.Reached < open.Reached,
            $"a bulkhead let {bounded.Reached} attempts out and no bulkhead let {open.Reached}");

        Assert.True(bounded.Amplification <= open.Amplification);
    }

    [Fact]
    public void A_refused_attempt_never_reaches_the_dependency()
    {
        var report = Run().WithLimiter(_ => Limit.Concurrency(permits: 5)).Run(seed: 7);

        Assert.True(report.RefusedByLimiter > 0);

        // The refusal is the reason the multiplier is below one, and below one is a policy whose
        // guards turned calls away before they left.
        Assert.True(
            report.LoadMultiplier < 1,
            $"a five-permit bulkhead against four hundred calls a second still let {report.LoadMultiplier} out per call");
    }

    [Fact]
    public void A_refusal_is_not_evidence_against_the_breaker()
    {
        // A healthy dependency and a bulkhead far too tight for the offered load: every failure in
        // this run is a refusal this process imposed on itself.
        var policy = Api with { Breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 5 }) };

        var report = Run(policy: policy).WithLimiter(_ => Limit.Concurrency(permits: 5)).Run(seed: 7);

        Assert.True(report.RefusedByLimiter > 0);

        // Opening one here would break a circuit against a service that was never contacted.
        Assert.Equal(0, report.BreakerOpens);
        Assert.Equal(0, report.CountOf(CallEventKind.RejectedByBreaker));
    }

    [Fact]
    public void An_adaptive_limiter_discovers_a_limit_on_the_runs_own_clock()
    {
        AdaptiveLimiter? limiter = null;

        var report = Run(seconds: 30)
            .WithLimiter(time => limiter = Limit.Adaptive(initial: 20, maximum: 200, time: time))
            .Run(seed: 7);

        Assert.NotNull(limiter);

        // Virtual time is the only time this run has, so a limit that moved and a latency baseline
        // that exists are both measurements of simulated seconds rather than of the few real
        // milliseconds the run took.
        Assert.NotEqual(20, limiter.CurrentLimit);
        Assert.NotNull(limiter.Baseline);

        // And it found room rather than taking it: the offered load gets through.
        Assert.True(report.Availability > 0.99, $"availability was {report.Availability}");
    }

    [Fact]
    public void The_same_seed_produces_a_byte_identical_report_with_a_limiter()
    {
        var first = Run().WithLimiter(_ => Limit.Concurrency(permits: 20)).Run(seed: 7);
        var second = Run().WithLimiter(_ => Limit.Concurrency(permits: 20)).Run(seed: 7);

        Assert.Equal(first.ToString(), second.ToString());
    }

    [Fact]
    public void A_band_builds_one_limiter_per_seed()
    {
        var built = new List<AdaptiveLimiter>();

        var band = Run()
            .WithLimiter(time =>
            {
                var limiter = Limit.Adaptive(initial: 20, maximum: 200, time: time);
                built.Add(limiter);

                return limiter;
            })
            .RunAll(1, 2, 3);

        // Sharing one would let the first seed's discovered limit decide the second seed's run, which
        // would make a band of a scenario a band of the order its seeds happened to be listed in.
        Assert.Equal(3, built.Count);
        Assert.Equal(3, built.Distinct().Count());
        Assert.Equal(3, band.Reports.Count);
    }

    [Fact]
    public void A_replenishing_limiter_is_refused()
    {
        var simulation = Run().WithLimiter(_ => Limit.PerSecond(permits: 100));

        var thrown = Assert.Throws<InvalidOperationException>(() => simulation.Run(seed: 7));

        Assert.Contains("refills against the wall clock", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_queueing_limiter_is_refused()
    {
        // The wait ends when somebody else releases a permit, and the platform resumes it on the
        // thread pool - a second thread inside a single-threaded run.
        var simulation = Run().WithLimiter(_ => Limit.Concurrency(permits: 5, queueLimit: 50));

        var thrown = Assert.Throws<InvalidOperationException>(() => simulation.Run(seed: 7));

        Assert.Contains("queueLimit 0", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_factory_that_builds_nothing_is_refused()
    {
        var simulation = Run().WithLimiter(_ => null!);

        var thrown = Assert.Throws<InvalidOperationException>(() => simulation.Run(seed: 7));

        Assert.Contains("WithLimiter must build a limiter", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_printed_report_counts_refusals_only_when_there_were_some()
    {
        var refused = Run().WithLimiter(_ => Limit.Concurrency(permits: 5)).Run(seed: 7);

        Assert.Contains("LimiterRefusals", refused.ToString(), StringComparison.Ordinal);
    }
}
