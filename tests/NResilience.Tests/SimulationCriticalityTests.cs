using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     A load offered at more than one <see cref="Criticality" />, and what a policy that reads the
///     level does with it.
///     <para>
///         The feature reallocates rather than creates: it holds back half the retry budget from work
///         nobody is waiting on, and spends what it saved on work somebody is. So the aggregate
///         availability barely moves, and a test that only watched that number would conclude the
///         setting does nothing. The measurement is per level, which is why the report carries one.
///     </para>
/// </summary>
public sealed class SimulationCriticalityTests
{
    private static readonly int[] Seeds = [1, 2, 3, 4, 5];

    private static readonly Dependency Flaky =
        Dependency.Healthy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200)).Failing(0.5);

    private static readonly Resilience Api = TestPolicy.Instant with
    {
        Attempts = 3,
        Deadline = TimeSpan.FromSeconds(10),
        AttemptTimeout = TimeSpan.FromSeconds(2),
        Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(50)),
        Budget = RetryBudget.Automatic,
    };

    private static Simulation Run(Resilience policy, Load load, Dependency? dependency = null, int seconds = 30) =>
        Simulate.Policy(policy)
            .Against(dependency ?? Flaky)
            .Under(load)
            .For(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void A_load_with_no_mix_is_entirely_critical()
    {
        var load = Load.Constant(perSecond: 200);

        Assert.Equal(1, load.ShareOf(Criticality.Critical));
        Assert.Equal(0, load.ShareOf(Criticality.Sheddable));

        var report = Run(Api, load).Run(seed: 7);

        Assert.Equal(report.Calls, report.CallsAt(Criticality.Critical));
        Assert.Equal(0, report.CallsAt(Criticality.Sheddable));
        Assert.Equal(report.Availability, report.AvailabilityAt(Criticality.Critical));
    }

    [Fact]
    public void A_share_of_nothing_draws_nothing_from_the_seed()
    {
        // The guard on every report the simulator produced before there was a level to name: a draw
        // taken unconditionally would shift the whole random stream and silently rewrite all of them.
        var plain = Run(Api, Load.Constant(perSecond: 200)).Run(seed: 7);
        var zeroed = Run(Api, Load.Constant(perSecond: 200).Mix(Criticality.Sheddable, fraction: 0)).Run(seed: 7);

        Assert.Equal(plain.ToString(), zeroed.ToString());
    }

    [Fact]
    public void The_mix_is_the_share_that_arrives()
    {
        var load = Load.Constant(perSecond: 200)
            .Mix(Criticality.Sheddable, fraction: 0.5)
            .Mix(Criticality.SheddablePlus, fraction: 0.2);

        Assert.Equal(0.5, load.ShareOf(Criticality.Sheddable));
        Assert.Equal(0.2, load.ShareOf(Criticality.SheddablePlus));
        Assert.Equal(0.3, load.ShareOf(Criticality.Critical), tolerance: 1e-9);

        var report = Run(Api, load).Run(seed: 7);

        Assert.Equal(
            report.Calls,
            Enum.GetValues<Criticality>().Sum(report.CallsAt));

        Assert.Equal(0.5, (double)report.CallsAt(Criticality.Sheddable) / report.Calls, tolerance: 0.03);
        Assert.Equal(0.2, (double)report.CallsAt(Criticality.SheddablePlus) / report.Calls, tolerance: 0.03);
        Assert.Equal(0.3, (double)report.CallsAt(Criticality.Critical) / report.Calls, tolerance: 0.03);
        Assert.Equal(0, report.CallsAt(Criticality.CriticalPlus));
    }

    [Fact]
    public void The_same_seed_labels_the_same_calls()
    {
        var load = Load.Constant(perSecond: 200).Mix(Criticality.Sheddable, fraction: 0.5);

        var first = Run(Api, load).Run(seed: 7);
        var second = Run(Api, load).Run(seed: 7);

        Assert.Equal(first.ToString(), second.ToString());
        Assert.Equal(first.CallsAt(Criticality.Sheddable), second.CallsAt(Criticality.Sheddable));
    }

    [Fact]
    public void A_criticality_aware_policy_spends_the_budget_it_saved_on_work_somebody_is_waiting_on()
    {
        var load = Load.Constant(perSecond: 200).Mix(Criticality.Sheddable, fraction: 0.5);

        // Long enough that the effect clears the seed noise. At 6,000 calls the two bands overlap:
        // the shift is real and about the size of the sampling spread on a coin-flip dependency, which
        // is the finding a single seed would have reported as a clean win.
        var unaware = Run(Api, load, seconds: 180).RunAll(Seeds);
        var aware = Run(Api with { UseAmbientCriticality = true }, load, seconds: 180).RunAll(Seeds);

        // The backfill gets less, on every seed.
        Assert.True(
            aware.AvailabilityAt(Criticality.Sheddable).Separates(unaware.AvailabilityAt(Criticality.Sheddable)),
            $"aware sheddable {aware.AvailabilityAt(Criticality.Sheddable)}, unaware {unaware.AvailabilityAt(Criticality.Sheddable)}");

        Assert.True(aware.AvailabilityAt(Criticality.Sheddable).Maximum
                    < unaware.AvailabilityAt(Criticality.Sheddable).Minimum);

        // And the checkout gets more, on every seed - which is the half of the trade that makes the
        // setting worth having rather than merely a way to serve fewer calls.
        Assert.True(
            aware.AvailabilityAt(Criticality.Critical).Separates(unaware.AvailabilityAt(Criticality.Critical)),
            $"aware critical {aware.AvailabilityAt(Criticality.Critical)}, unaware {unaware.AvailabilityAt(Criticality.Critical)}");

        Assert.True(aware.AvailabilityAt(Criticality.Critical).Minimum
                    > unaware.AvailabilityAt(Criticality.Critical).Maximum);
    }

    [Fact]
    public void The_aggregate_availability_hides_the_whole_trade()
    {
        var load = Load.Constant(perSecond: 200).Mix(Criticality.Sheddable, fraction: 0.5);

        var unaware = Run(Api, load, seconds: 180).RunAll(Seeds);
        var aware = Run(Api with { UseAmbientCriticality = true }, load, seconds: 180).RunAll(Seeds);

        // Reallocation, not creation: the dependency serves about as many calls either way, and the
        // number that moved is which calls they were. A run read only for Availability would report
        // that this setting does nothing.
        Assert.False(
            aware.Availability.Separates(unaware.Availability),
            $"aware {aware.Availability}, unaware {unaware.Availability}");
    }

    [Fact]
    public void A_policy_that_does_not_read_criticality_treats_every_call_alike()
    {
        var load = Load.Constant(perSecond: 200).Mix(Criticality.Sheddable, fraction: 0.5);
        var report = Run(Api, load).Run(seed: 7);

        Assert.Equal(
            report.AvailabilityAt(Criticality.Sheddable),
            report.AvailabilityAt(Criticality.Critical),
            tolerance: 0.02);
    }

    [Fact]
    public void Sheddable_work_is_never_hedged()
    {
        // A slow tail, so there is something for a hedge to want.
        var slow = Dependency.Healthy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(400));

        var policy = Api with { Hedge = Hedge.At(quantile: 0.9), UseAmbientCriticality = true };

        var critical = Run(policy, Load.Constant(perSecond: 200), slow).Run(seed: 7);
        var mixed = Run(policy, Load.Constant(perSecond: 200).Mix(Criticality.Sheddable, fraction: 0.5), slow)
            .Run(seed: 7);

        // Refused before a timer is armed, and silently - HedgeSuppressed reports a judgment about
        // hedging and this is a bound on the call - so the observable is that fewer hedges start.
        Assert.True(
            mixed.CountOf(CallEventKind.HedgeStarted) < critical.CountOf(CallEventKind.HedgeStarted),
            $"mixed started {mixed.CountOf(CallEventKind.HedgeStarted)} hedges and all-critical started {critical.CountOf(CallEventKind.HedgeStarted)}");
    }

    [Fact]
    public void Critical_cannot_be_set_because_it_is_the_remainder()
    {
        var load = Load.Constant(perSecond: 200);

        var thrown = Assert.Throws<ArgumentOutOfRangeException>(
            () => load.Mix(Criticality.Critical, fraction: 0.3));

        Assert.Contains("remainder", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mix_that_leaves_nothing_over_is_refused()
    {
        var load = Load.Constant(perSecond: 200)
            .Mix(Criticality.Sheddable, fraction: 0.8)
            .Mix(Criticality.SheddablePlus, fraction: 0.4);

        var thrown = Assert.Throws<ResilienceConfigurationException>(load.Validate);

        Assert.Contains("Critical, which takes the remainder", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_share_outside_zero_to_one_is_refused()
    {
        var load = Load.Constant(perSecond: 200).Mix(Criticality.Sheddable, fraction: 1.5);

        var thrown = Assert.Throws<ResilienceConfigurationException>(load.Validate);

        Assert.Contains("Sheddable share must be between 0 and 1", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_printed_report_names_the_levels_only_when_the_load_named_them()
    {
        var plain = Run(Api, Load.Constant(perSecond: 200)).Run(seed: 7);
        var mixed = Run(Api, Load.Constant(perSecond: 200).Mix(Criticality.Sheddable, fraction: 0.5)).Run(seed: 7);

        Assert.DoesNotContain("Sheddable", plain.ToString(), StringComparison.Ordinal);
        Assert.Contains("Sheddable", mixed.ToString(), StringComparison.Ordinal);
        Assert.Contains("Critical", mixed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("CriticalPlus", mixed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_run_leaves_no_ambient_criticality_behind()
    {
        Run(Api, Load.Constant(perSecond: 200).Mix(Criticality.Sheddable, fraction: 0.5)).Run(seed: 7);

        Assert.Equal(Criticality.Critical, AmbientCriticality.Current);
    }
}
