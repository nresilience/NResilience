using Microsoft.Extensions.Time.Testing;

namespace NResilience.Tests;

/// <summary>
///     The relative failure trip: <c>BreakerSettings.Failures</c>, which defines "too many errors" as a
///     multiple of the dependency's own measured error rate instead of an absolute ratio an operator
///     has to guess.
///     <para>
///         The two dependencies the absolute ratio cannot serve at once are what the design is for. A
///         payments API whose steady state is 0.02% transient is deeply broken at 5%, and
///         <c>FailureRatio = 0.5</c> has not noticed. A third-party search backend that has always run
///         at 30% transient trips on ordinary variance at the same setting, and the operator's fix is to
///         raise the number until it detects nothing. Both are configured here with the same
///         <c>Failures.Above(5)</c>.
///     </para>
/// </summary>
public sealed class RelativeFailureRatioTests
{
    /// <summary>
    ///     Too many failures is five times the recent rate. The consecutive trip is pushed out of the
    ///     way so the rate is the only thing under test.
    /// </summary>
    private static BreakerSettings Relative(FakeTimeProvider time) => new()
    {
        Failures = Failures.Above(5),
        MinimumCalls = 20,
        ConsecutiveFailures = 1000,
        Time = time,
    };

    private static void Sample(Breaker breaker, VerdictKind kind, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(breaker.TryEnter(out _), "admission was refused before the test expected it");
            breaker.Record(kind, TimeSpan.Zero);
        }
    }

    /// <summary>
    ///     A flaky dependency's baseline, then a clean trip window so the breaker is judging the
    ///     failures under test rather than the ones that established the baseline.
    /// </summary>
    private static void Baseline(Breaker breaker, FakeTimeProvider time, int ok, int failed)
    {
        Sample(breaker, VerdictKind.Ok, ok);
        Sample(breaker, VerdictKind.Transient, failed);

        // Past the whole 30-second trip window, which is cleared on the next write. The baseline
        // spans five minutes and keeps everything above.
        time.Advance(TimeSpan.FromSeconds(33));
    }

    // ---- Opening ----

    [Fact]
    public void A_dependency_five_times_worse_than_itself_opens_it()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Relative(time));

        // A backend that fails 30% of the time, all day, and always has. Nothing here is unusual.
        Baseline(breaker, time, ok: 140, failed: 60);
        Assert.Equal(BreakerState.Closed, breaker.State);

        // Now it fails everything. An absolute ratio anyone could have set for this dependency
        // without opening the circuit on a normal Tuesday would be somewhere above 0.3, guessed.
        Sample(breaker, VerdictKind.Transient, 20);

        Assert.Equal(BreakerState.Open, breaker.State);
    }

    [Fact]
    public void A_flaky_dependency_failing_at_its_usual_rate_stays_closed()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Relative(time));

        Baseline(breaker, time, ok: 140, failed: 60);

        // The same 30% again, which is what this dependency is. FailureRatio = 0.5 would be close
        // enough to normal that ordinary variance opens the circuit; 5x its own rate is not.
        Sample(breaker, VerdictKind.Ok, 14);
        Sample(breaker, VerdictKind.Transient, 6);

        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    /// <summary>
    ///     The other dependency, and the reason <c>AbsoluteFloor</c> is not optional. A baseline near
    ///     zero times any multiple is still near zero, so without a floor this feature is a breaker that
    ///     opens on one error against a dependency that has never misbehaved.
    /// </summary>
    [Fact]
    public void A_dependency_that_never_fails_trips_at_the_floor_rather_than_at_a_multiple_of_nothing()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Relative(time));

        Baseline(breaker, time, ok: 200, failed: 0);

        Sample(breaker, VerdictKind.Ok, 18);
        Sample(breaker, VerdictKind.Transient, 2);

        // 10% of the window against a floor of 5%. Five times a baseline of zero would be zero, and
        // the first error of the day would have opened the circuit.
        Assert.Equal(BreakerState.Open, breaker.State);
    }

    [Fact]
    public void One_failure_is_never_a_rate()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Relative(time));

        Baseline(breaker, time, ok: 200, failed: 0);

        Sample(breaker, VerdictKind.Ok, 19);
        Sample(breaker, VerdictKind.Transient, 1);

        // One failure in twenty calls is already 5%, so the floor alone would trip here. A rate
        // measured from a single event is not evidence about a rate, and the relative trip wants
        // two failures whatever the ratio says.
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    // ---- Composition with the absolute ratio ----

    /// <summary>
    ///     Rule four of the house pattern: an adaptive feature may only tighten. The absolute ratio
    ///     stays the ceiling, so a caller who set both gets whichever trips sooner and never a breaker
    ///     that tolerates more than the number they wrote down.
    /// </summary>
    [Fact]
    public void The_absolute_ratio_stays_the_ceiling()
    {
        // 60% of the window failed. Five times a 30% baseline is more than the whole window, so the
        // relative trip alone tolerates it; the absolute ceiling does not, and it wins.
        Assert.Equal(BreakerState.Open, SixtyPercentAgainstAFlakyBaseline(ceiling: 0.5));
        Assert.Equal(BreakerState.Closed, SixtyPercentAgainstAFlakyBaseline(ceiling: null));

        static BreakerState SixtyPercentAgainstAFlakyBaseline(double? ceiling)
        {
            var time = new FakeTimeProvider();
            var breaker = new Breaker(Relative(time) with { FailureRatio = ceiling });

            Baseline(breaker, time, ok: 140, failed: 60);
            Sample(breaker, VerdictKind.Ok, 8);
            Sample(breaker, VerdictKind.Transient, 12);

            return breaker.State;
        }
    }

    // ---- Cold start ----

    [Fact]
    public void A_cold_baseline_leaves_the_breaker_exactly_as_it_was()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Relative(time));

        // Twenty failures out of twenty calls, and the breaker stays closed: there is no baseline
        // yet, so there is nothing to be five times worse than. A cold process does not guess an
        // error rate, and the consecutive trip - pushed out of the way here - is what covers it.
        Sample(breaker, VerdictKind.Transient, 20);

        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Null(breaker.NormalFailureRate);
    }

    [Fact]
    public void A_cold_baseline_does_not_disarm_the_absolute_ratio()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Relative(time) with { FailureRatio = 0.5 });

        Sample(breaker, VerdictKind.Transient, 20);

        Assert.Equal(BreakerState.Open, breaker.State);
    }

    // ---- Recovery ----

    [Fact]
    public void The_baseline_outlives_the_break_and_the_reset()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Relative(time));

        Baseline(breaker, time, ok: 140, failed: 60);
        Sample(breaker, VerdictKind.Transient, 20);
        Assert.Equal(BreakerState.Open, breaker.State);

        // The trip window was cleared when the breaker opened. The baseline was not - it is a
        // measurement of the dependency, not a decision about it - and forgetting it would leave
        // the breaker unable to judge the next thirty seconds until it had re-learned the rate.
        Assert.NotNull(breaker.NormalFailureRate);

        breaker.Reset();

        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.NotNull(breaker.NormalFailureRate);
    }

    // ---- Diagnostics ----

    [Fact]
    public void The_measured_rate_is_the_number_the_trip_point_is_built_from()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Relative(time));

        Baseline(breaker, time, ok: 150, failed: 50);

        Assert.Equal(0.25, breaker.NormalFailureRate);
    }

    [Fact]
    public void A_breaker_with_no_relative_trip_has_no_rate_to_report()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(new BreakerSettings { FailureRatio = 0.5, Failures = null, Time = time });

        Sample(breaker, VerdictKind.Ok, 100);

        Assert.Null(breaker.NormalFailureRate);
    }

    // ---- Configuration ----

    /// <summary>
    ///     The same race <c>SlowCalls</c> is held to, in the shape errors take it: an outage raises the
    ///     baseline as it fills, and a baseline short enough to absorb the outage before the trip window
    ///     fills with it produces a breaker that cannot open on the error rate at all.
    /// </summary>
    [Fact]
    public void A_baseline_window_that_loses_the_race_against_the_trip_window_is_refused()
    {
        var problem = Assert.Throws<ResilienceConfigurationException>(() =>
            new Breaker(new BreakerSettings
            {
                Failures = Failures.Above(5) with { Window = TimeSpan.FromMinutes(1) },
                Window = TimeSpan.FromSeconds(30),
            }));

        Assert.Contains(problem.Problems, p => p.Contains("never open on the error rate", StringComparison.Ordinal));
    }

    [Fact]
    public void The_defaults_win_that_race()
    {
        var breaker = new Breaker(new BreakerSettings { Failures = Failures.Above(5) });
        var relative = breaker.Settings.Failures!.Value;

        Assert.Equal(TimeSpan.FromMinutes(5), relative.Window);
        Assert.Equal(100, relative.MinimumSamples);
        Assert.Equal(0.05, relative.AbsoluteFloor);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0.5)]
    [InlineData(double.NaN)]
    public void A_multiple_that_is_not_above_the_baseline_is_refused(double multiple)
    {
        var problem = Assert.Throws<ResilienceConfigurationException>(() =>
            new Breaker(new BreakerSettings { Failures = Failures.Above(multiple) }));

        Assert.Contains(problem.Problems, p => p.Contains("Failures.Multiple", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void A_floor_that_is_not_a_ratio_is_refused(double floor)
    {
        var problem = Assert.Throws<ResilienceConfigurationException>(() =>
            new Breaker(new BreakerSettings { Failures = Failures.Above(5) with { AbsoluteFloor = floor } }));

        Assert.Contains(problem.Problems, p => p.Contains("Failures.AbsoluteFloor", StringComparison.Ordinal));
    }

    [Fact]
    public void An_absolute_and_a_relative_ratio_compose_rather_than_conflict()
    {
        var breaker = new Breaker(new BreakerSettings { FailureRatio = 0.5, Failures = Failures.Above(5) });

        // Unlike SlowCallThreshold and SlowCalls, which are the same trip defined two ways, these
        // two are a ceiling and a measurement. Setting both is the recommended configuration.
        Assert.Equal(0.5, breaker.Settings.FailureRatio);
        Assert.Equal(5, breaker.Settings.Failures!.Value.Multiple);
    }

    [Fact]
    public void Naming_a_default_explicitly_is_the_same_configuration_as_leaving_it_alone()
    {
        var left = Failures.Above(5);

        var right = Failures.Above(5) with
        {
            Window = TimeSpan.FromMinutes(5),
            MinimumSamples = 100,
            AbsoluteFloor = 0.05,
        };

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void It_describes_itself_the_way_it_was_configured()
    {
        Assert.Equal("5x the rate over 300s (min 100 samples, floor 5%)", Failures.Above(5).ToString());
    }
}
