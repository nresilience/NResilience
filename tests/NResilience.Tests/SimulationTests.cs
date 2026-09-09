using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The simulator: a policy run against a modeled dependency on a virtual clock, and the report it
///     produces.
///     <para>
///         The first test is the important one. Everything else here checks that a number means what
///         it says; determinism is what makes any of the numbers worth asserting on, and a failure of
///         it means something in the library is reading a clock or a random source it should not be.
///     </para>
/// </summary>
public sealed class SimulationTests
{
    private static readonly Dependency Fast =
        Dependency.Healthy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200));

    private static readonly Resilience Api = TestPolicy.Instant with
    {
        Attempts = 3,
        Deadline = TimeSpan.FromSeconds(10),
        AttemptTimeout = TimeSpan.FromSeconds(2),
        Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(50)),
    };

    private static Simulation Run(Resilience policy, Dependency dependency, int perSecond = 200, int seconds = 30) =>
        Simulate.Policy(policy)
            .Against(dependency)
            .Under(Load.Constant(perSecond))
            .For(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void The_same_seed_produces_a_byte_identical_report()
    {
        var dependency = Fast.Brownout(TimeSpan.FromSeconds(10), slower: 8, TimeSpan.FromSeconds(10)).Failing(0.05);

        var first = Run(Api, dependency).Run(seed: 42);
        var second = Run(Api, dependency).Run(seed: 42);

        Assert.Equal(first.ToString(), second.ToString());
    }

    [Fact]
    public void A_different_seed_produces_a_different_run()
    {
        var dependency = Fast.Failing(0.05);

        var first = Run(Api, dependency).Run(seed: 1);
        var second = Run(Api, dependency).Run(seed: 2);

        Assert.NotEqual(first.ToString(), second.ToString());
    }

    [Fact]
    public void A_healthy_dependency_costs_one_attempt_per_call()
    {
        var report = Run(Api, Fast).Run(seed: 7);

        Assert.Equal(1.0, report.Availability);
        Assert.Equal(report.Calls, report.Succeeded);
        Assert.Equal(0, report.BreakerOpens);

        // Not exactly one: the measured attempt ceiling cuts off the slowest few percent of a healthy
        // tail and the retry succeeds, which is the feature working rather than the dependency failing.
        Assert.InRange(report.LoadMultiplier, 1.0, 1.1);
    }

    [Fact]
    public void The_offered_rate_is_what_arrives()
    {
        var report = Run(Api, Fast, perSecond: 500, seconds: 60).Run(seed: 3);

        // Spread arrivals over a minute, so the count is close to the rate rather than equal to it.
        Assert.InRange(report.Calls, 29_000, 31_000);
        Assert.Equal(TimeSpan.FromSeconds(60), report.Duration);
        Assert.Equal(3, report.Seed);
    }

    [Fact]
    public void Retries_show_up_as_load_the_dependency_feels()
    {
        var quiet = Run(Api, Fast).Run(seed: 11);
        var flaky = Run(Api, Fast.Failing(0.2)).Run(seed: 11);

        Assert.True(
            flaky.LoadMultiplier > quiet.LoadMultiplier,
            $"a dependency failing one call in five drew {flaky.LoadMultiplier} and a healthy one drew {quiet.LoadMultiplier}");

        Assert.True(flaky.Amplification >= flaky.LoadMultiplier);
        Assert.True(flaky.CountOf(CallEventKind.Retrying) > 0);
    }

    [Fact]
    public void A_retry_budget_holds_the_load_multiplier_down_where_an_unbudgeted_policy_does_not()
    {
        var sick = Fast.Failing(0.5);

        var unbudgeted = Run(Api, sick).Run(seed: 5);
        var budgeted = Run(Api with { Budget = RetryBudget.Automatic }, sick).Run(seed: 5);

        Assert.True(
            budgeted.LoadMultiplier < unbudgeted.LoadMultiplier,
            $"a budgeted policy drew {budgeted.LoadMultiplier} and an unbudgeted one drew {unbudgeted.LoadMultiplier}");

        Assert.True(budgeted.CountOf(CallEventKind.RejectedByBudget) > 0);
    }

    [Fact]
    public void An_outage_trips_the_breaker_and_the_caller_recovers_after_it()
    {
        var policy = Api with { Breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 5 }) };
        var dependency = Fast.Outage(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

        var report = Run(policy, dependency, seconds: 60).Run(seed: 13);

        Assert.True(report.BreakerOpens > 0);
        Assert.True(report.CountOf(CallEventKind.RejectedByBreaker) > 0);
        Assert.True(report.Availability < 1);
        Assert.NotNull(report.TimeToRecover);
    }

    [Fact]
    public void A_brownout_shows_up_in_the_tail_rather_than_in_the_median()
    {
        var calm = Run(Api, Fast, seconds: 60).Run(seed: 21);
        var brownout = Run(Api, Fast.Brownout(TimeSpan.FromSeconds(20), slower: 8, TimeSpan.FromSeconds(20)), seconds: 60)
            .Run(seed: 21);

        Assert.True(brownout.Latency(0.99) > calm.Latency(0.99));
        Assert.True(brownout.Latency(0.99) > brownout.Latency(0.5));
    }

    [Fact]
    public void The_healthy_latency_lands_on_the_quantiles_it_was_given()
    {
        // One attempt and no retries, so a call's latency is the dependency's latency and nothing else.
        var single = TestPolicy.Instant with { Attempts = 1 };

        var report = Run(single, Fast, perSecond: 500, seconds: 60).Run(seed: 9);

        Assert.InRange(report.Latency(0.5), TimeSpan.FromMilliseconds(18), TimeSpan.FromMilliseconds(22));
        Assert.InRange(report.Latency(0.99), TimeSpan.FromMilliseconds(170), TimeSpan.FromMilliseconds(240));
    }

    [Fact]
    public void Peers_take_capacity_this_process_would_otherwise_have_had()
    {
        var bounded = Fast.Capacity(concurrent: 12);

        var alone = Run(Api, bounded).Run(seed: 17);
        var crowded = Simulate.Policy(Api)
            .Against(bounded)
            .Under(Load.Constant(200, peers: 4))
            .For(TimeSpan.FromSeconds(30))
            .Run(seed: 17);

        Assert.True(
            crowded.Availability < alone.Availability,
            $"one of four pods saw {crowded.Availability} and a pod on its own saw {alone.Availability}");
    }

    [Fact]
    public void A_report_reads_as_a_fixed_block_of_text()
    {
        var report = Run(Api, Fast).Run(seed: 42);

        var text = report.ToString();

        Assert.StartsWith("Simulation seed 42 over 00:00:30", text, StringComparison.Ordinal);
        Assert.Contains("Availability     1.0000", text, StringComparison.Ordinal);
        Assert.Contains("TimeToRecover    never", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quantile_outside_zero_to_one_is_refused()
    {
        var report = Run(Api, Fast, seconds: 1).Run(seed: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => report.Latency(1.5));
    }

    [Fact]
    public void An_incomplete_simulation_says_what_is_missing()
    {
        Assert.Contains("Against()", Assert.Throws<InvalidOperationException>(() => Simulate.Policy(Api).Run(seed: 1)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "Under()",
            Assert.Throws<InvalidOperationException>(() => Simulate.Policy(Api).Against(Fast).Run(seed: 1)).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "For()",
            Assert.Throws<InvalidOperationException>(() =>
                Simulate.Policy(Api).Against(Fast).Under(Load.Constant(1)).Run(seed: 1)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_losing_dependency_is_refused_at_construction()
    {
        var problems = Assert.Throws<ResilienceConfigurationException>(() =>
            Dependency.Healthy(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20))
                .Failing(2)
                .Brownout(TimeSpan.FromSeconds(1), slower: 0.5, TimeSpan.Zero)
                .Validate());

        Assert.Equal(4, problems.Problems.Count);
    }

    [Fact]
    public void A_losing_load_is_refused_at_construction()
    {
        var problems = Assert.Throws<ResilienceConfigurationException>(() => Load.Constant(0, peers: 0).Validate());

        Assert.Equal(2, problems.Problems.Count);
    }

    [Fact]
    public void A_simulation_of_five_minutes_costs_no_wall_clock_time()
    {
        var started = DateTime.UtcNow;

        var report = Run(Api, Fast.Brownout(TimeSpan.FromSeconds(30), slower: 8, TimeSpan.FromMinutes(1)), 500, seconds: 300)
            .Run(seed: 42);

        Assert.True(report.Calls > 100_000);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMinutes(1));
    }
}
