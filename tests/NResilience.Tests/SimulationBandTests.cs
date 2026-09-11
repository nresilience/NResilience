using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     A band: the same scenario over several seeds, reported as a range per measurement.
///     <para>
///         The point of the type is the one thing a single report cannot say - whether the number it
///         carries is a property of the configuration or of the seed. So the tests that matter here
///         are the ones about spread: that a band widens where the seed matters and collapses where it
///         does not, and that two overlapping bands decline to name a winner.
///     </para>
/// </summary>
public sealed class SimulationBandTests
{
    private static readonly int[] Seeds = [1, 2, 3, 4, 5, 6, 7, 8];

    private static readonly Dependency Fast =
        Dependency.Healthy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200));

    /// <summary>Browned out hard enough to make the retry budget bind, so a budget test measures one.</summary>
    private static readonly Dependency Slow = Fast.Brownout(TimeSpan.FromSeconds(5), slower: 20, TimeSpan.FromSeconds(15));

    private static readonly Resilience Api = TestPolicy.Instant with
    {
        Attempts = 3,
        Deadline = TimeSpan.FromSeconds(10),
        AttemptTimeout = TimeSpan.FromSeconds(2),
        Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(50)),
    };

    private static Simulation Run(Resilience policy, Dependency dependency, int seconds = 30) =>
        Simulate.Policy(policy)
            .Against(dependency)
            .Under(Load.Constant(perSecond: 200))
            .For(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void The_same_seeds_produce_a_byte_identical_band()
    {
        var dependency = Fast.Failing(0.05);

        var first = Run(Api, dependency).RunAll(Seeds);
        var second = Run(Api, dependency).RunAll(Seeds);

        Assert.Equal(first.ToString(), second.ToString());
    }

    [Fact]
    public void A_band_holds_one_report_per_seed_in_the_order_they_were_given()
    {
        var band = Run(Api, Fast).RunAll(9, 4, 6);

        Assert.Equal([9, 4, 6], band.Seeds);
        Assert.Equal([9, 4, 6], band.Reports.Select(report => report.Seed));
        Assert.Equal(TimeSpan.FromSeconds(30), band.Duration);
        Assert.Equal(band.Reports[0].EngineVersion, band.EngineVersion);
    }

    [Fact]
    public void One_seed_is_a_band_with_no_spread()
    {
        var band = Run(Api, Fast.Failing(0.1)).RunAll(42);
        var report = Run(Api, Fast.Failing(0.1)).Run(seed: 42);

        Assert.Equal(0, band.Amplification.Spread);
        Assert.Equal(report.Amplification, band.Amplification.Median);
        Assert.Equal(report.Amplification, band.Amplification.Minimum);
        Assert.Equal(report.Amplification, band.Amplification.Maximum);
    }

    [Fact]
    public void The_median_is_a_value_one_of_the_runs_actually_produced()
    {
        var band = Run(Api, Fast.Failing(0.1)).RunAll(Seeds);
        var measured = band.Reports.Select(report => report.Availability).ToArray();

        Assert.Contains(band.Availability.Median, measured);
        Assert.Equal(measured.Min(), band.Availability.Minimum);
        Assert.Equal(measured.Max(), band.Availability.Maximum);
        Assert.InRange(band.Availability.Median, band.Availability.Minimum, band.Availability.Maximum);
    }

    [Fact]
    public void The_seed_moves_the_numbers_a_tuning_decision_turns_on()
    {
        var band = Run(Api, Fast.Failing(0.2)).RunAll(Seeds);

        // The whole reason the type exists: the worst one-second window is not the same window on
        // every seed, so a decision read off one of them is a decision read off the draw.
        Assert.True(band.Amplification.Spread > 0, $"amplification did not move across eight seeds: {band.Amplification}");
    }

    [Fact]
    public void A_band_declines_to_name_a_winner_when_the_two_overlap()
    {
        var sick = Fast.Failing(0.5);

        var unbudgeted = Run(Api with { Budget = RetryBudget.None }, sick).RunAll(Seeds);
        var budgeted = Run(Api with { Budget = RetryBudget.Automatic }, sick).RunAll(Seeds);

        // A real difference, and one that holds on every seed rather than on the one that was picked.
        Assert.True(
            budgeted.LoadMultiplier.Separates(unbudgeted.LoadMultiplier),
            $"budgeted drew {budgeted.LoadMultiplier} and unbudgeted drew {unbudgeted.LoadMultiplier}");

        // And the same policy against itself is the case where there is nothing to report.
        Assert.False(budgeted.LoadMultiplier.Separates(budgeted.LoadMultiplier));
    }

    [Fact]
    public void Recovery_is_counted_as_well_as_timed()
    {
        var dependency = Fast.Outage(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        var band = Run(Api, dependency, seconds: 60).RunAll(Seeds);

        Assert.Equal(Seeds.Length, band.Recovered);
        Assert.NotNull(band.TimeToRecover);
        Assert.True(band.TimeToRecover.Value.Minimum >= TimeSpan.Zero);
        Assert.True(band.TimeToRecover.Value.Maximum >= band.TimeToRecover.Value.Median);
    }

    [Fact]
    public void A_dependency_that_was_never_impaired_has_nothing_to_recover_from()
    {
        var band = Run(Api, Fast).RunAll(Seeds);

        Assert.Null(band.TimeToRecover);
        Assert.Equal(0, band.Recovered);
    }

    [Fact]
    public void A_band_equals_the_same_seeds_run_one_at_a_time()
    {
        // The seeds of a band run at the same time as each other. This is the whole safety argument
        // for that: a run built its own clock, breaker, limiter and jitter stream, so whether the
        // seeds ran together or one after another cannot change a single number. Compared against
        // isolated runs rather than a serial band, because an isolated run cannot be affected by
        // batching of any kind.
        var alone = Seeds.Select(seed => Run(Api, Fast).Run(seed).ToString()).ToArray();

        var band = Run(Api, Fast).RunAll(Seeds);

        Assert.Equal(alone, band.Reports.Select(report => report.ToString()));
    }

    [Fact]
    public void A_band_whose_policy_holds_a_live_budget_equals_the_same_seeds_run_one_at_a_time()
    {
        // A budget that is not Automatic or None is a live bucket on the policy, and the policy is
        // one object for the whole band - so this would once have had the seeds draining each other's
        // tokens. Each run rebases the budget onto its own clock, which is what makes a seed's numbers
        // its own.
        var policy = Api with { Budget = RetryBudget.Of(fraction: 0.1, minimumPerSecond: 3) };

        var alone = Seeds.Select(seed => Run(policy, Slow).Run(seed).ToString()).ToArray();
        var band = Run(policy, Slow).RunAll(Seeds);

        Assert.Equal(alone, band.Reports.Select(report => report.ToString()));
    }

    [Fact]
    public void A_band_needs_at_least_one_seed()
    {
        var simulation = Run(Api, Fast);

        Assert.Throws<ArgumentException>(() => simulation.RunAll());
    }

    [Fact]
    public void A_repeated_seed_is_refused_rather_than_run_twice()
    {
        var simulation = Run(Api, Fast);

        // The second copy would narrow the band it was added to widen.
        var thrown = Assert.Throws<ArgumentException>(() => simulation.RunAll(1, 2, 1));

        Assert.Contains("same seed twice", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_band_prints_every_measurement_as_a_range()
    {
        var band = Run(Api, Fast.Failing(0.1)).RunAll(1, 2, 3);
        var text = band.ToString();

        Assert.Contains("3 seeds from 1", text, StringComparison.Ordinal);
        Assert.Contains("Availability", text, StringComparison.Ordinal);
        Assert.Contains("Amplification", text, StringComparison.Ordinal);
        Assert.Contains("Recovered        0 of 3", text, StringComparison.Ordinal);
    }
}
