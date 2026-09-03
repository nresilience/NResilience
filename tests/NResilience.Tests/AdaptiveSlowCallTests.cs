using Microsoft.Extensions.Time.Testing;

namespace NResilience.Tests;

/// <summary>
///     The adaptive slow-call trip: <c>BreakerSettings.SlowCalls</c>, which defines a brownout as a
///     multiple of measured normal latency instead of a constant an operator has to guess.
///     <para>
///         Two properties are what the design is for, and they pull in opposite directions. It has to
///         <b>open</b> when a dependency slows down relative to itself, which the obvious version of this
///         feature - a threshold read from a high quantile of the trip window - cannot do at all, because
///         the threshold chases the degradation and roughly <c>1 - quantile</c> of calls are slow by
///         construction whatever is happening. And it has to <b>stay closed</b> for a dependency that is
///         simply slow in absolute terms, which is what a constant threshold cannot do without being
///         re-guessed per dependency.
///     </para>
/// </summary>
public sealed class AdaptiveSlowCallTests
{
    /// <summary>Slow is three times normal, and half the trip window being slow opens it.</summary>
    private static BreakerSettings Adaptive(FakeTimeProvider time) => new()
    {
        SlowCalls = SlowCalls.Above(3),
        SlowCallRatio = 0.5,
        MinimumCalls = 20,
        ConsecutiveFailures = 100,
        Time = time,
    };

    private static void Sample(Breaker breaker, VerdictKind kind, int count, TimeSpan duration = default)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(breaker.TryEnter(out _), "admission was refused before the test expected it");
            breaker.Record(kind, duration);
        }
    }

    // ---- Opening ----

    [Fact]
    public void A_brownout_opens_it_without_anybody_naming_a_millisecond()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Adaptive(time));

        // Twenty calls at the dependency's normal speed. Nothing is slow, because nothing is slower
        // than the dependency itself, and the baseline is now 100 ms.
        Sample(breaker, VerdictKind.Ok, 20, TimeSpan.FromMilliseconds(100));
        Assert.Equal(BreakerState.Closed, breaker.State);

        // It browns out to 10x. A constant threshold would need somebody to have guessed a number
        // between 100 ms and 1 s, per dependency, before this ever ran.
        Sample(breaker, VerdictKind.Ok, 20, TimeSpan.FromSeconds(1));

        Assert.Equal(BreakerState.Open, breaker.State);
    }

    /// <summary>
    ///     The property the whole design turns on, and the one the rejected version of this feature
    ///     fails. A baseline read over a long window at a low quantile does not follow the degradation:
    ///     the trip window fills with slow calls while the baseline still remembers what healthy was.
    /// </summary>
    [Fact]
    public void The_baseline_does_not_chase_the_brownout_it_is_measuring()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Adaptive(time));

        // Four minutes of healthy traffic, crossing several slices of the five-minute baseline.
        for (var slice = 0; slice < 4; slice++)
        {
            Sample(breaker, VerdictKind.Ok, 50, TimeSpan.FromMilliseconds(20));
            time.Advance(TimeSpan.FromSeconds(60));
        }

        var normal = breaker.NormalLatency;
        Assert.NotNull(normal);
        Assert.InRange(normal.Value, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(22.5));

        // The brownout starts. Each of these is ten times normal, and every one of them counts - the
        // baseline is 200 samples of healthy traffic deep and twenty slow calls do not move a median.
        Sample(breaker, VerdictKind.Ok, 20, TimeSpan.FromMilliseconds(200));

        Assert.Equal(BreakerState.Open, breaker.State);
    }

    /// <summary>
    ///     The portability claim, stated as a test: one configuration, two dependencies three orders of
    ///     magnitude apart, correct on both. No constant can do this.
    /// </summary>
    [Fact]
    public void One_configuration_fits_a_fast_dependency_and_a_slow_one()
    {
        var time = new FakeTimeProvider();
        var cache = new Breaker(Adaptive(time)) { Name = "cache" };
        var report = new Breaker(Adaptive(time)) { Name = "report" };

        // The cache answers in a millisecond and then degrades to ten.
        Sample(cache, VerdictKind.Ok, 20, TimeSpan.FromMilliseconds(1));
        Sample(cache, VerdictKind.Ok, 20, TimeSpan.FromMilliseconds(10));

        // The report generator takes four seconds and always has. It is slow; it is not degrading.
        Sample(report, VerdictKind.Ok, 40, TimeSpan.FromSeconds(4));

        Assert.Equal(BreakerState.Open, cache.State);
        Assert.Equal(BreakerState.Closed, report.State);
    }

    // ---- Staying closed ----

    [Fact]
    public void A_dependency_that_is_merely_slow_is_never_a_brownout()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Adaptive(time));

        for (var slice = 0; slice < 6; slice++)
        {
            Sample(breaker, VerdictKind.Ok, 50, TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(60));
        }

        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void Nothing_is_slow_until_the_baseline_has_enough_samples()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Adaptive(time) with { SlowCalls = SlowCalls.Above(3) with { MinimumSamples = 50 } });

        // Forty calls, wildly varying, and not one of them can be called slow: there is nothing yet to
        // call it slow relative to. A cold process does not guess a threshold.
        Sample(breaker, VerdictKind.Ok, 20, TimeSpan.FromMilliseconds(1));
        Sample(breaker, VerdictKind.Ok, 20, TimeSpan.FromSeconds(30));

        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Null(breaker.NormalLatency);
    }

    [Fact]
    public void Only_successful_attempts_define_normal()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Adaptive(time));

        Sample(breaker, VerdictKind.Ok, 20, TimeSpan.FromMilliseconds(10));
        time.Advance(TimeSpan.FromSeconds(80));

        // A throttled response is the dependency defending itself, and however long it took, it is not
        // a sample of how long this dependency takes to do the work.
        Sample(breaker, VerdictKind.Throttled, 200, TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(80));

        var normal = breaker.NormalLatency;

        Assert.NotNull(normal);
        Assert.InRange(normal.Value, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(11.25));
    }

    // ---- Recovery ----

    [Fact]
    public void The_baseline_outlives_the_break_so_a_slow_probe_is_still_not_a_recovery()
    {
        var time = new FakeTimeProvider();

        var breaker = new Breaker(Adaptive(time) with
        {
            BreakDuration = TimeSpan.FromSeconds(15),
            ProbeSuccesses = 1,
        });

        Sample(breaker, VerdictKind.Ok, 20, TimeSpan.FromMilliseconds(50));
        Sample(breaker, VerdictKind.Ok, 20, TimeSpan.FromSeconds(2));
        Assert.Equal(BreakerState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(16));
        Assert.True(breaker.TryEnter(out _));

        // The trip window was cleared when the breaker opened. The baseline was not - it is a
        // measurement of the dependency, not a decision about it - so a 2 s probe against a 50 ms
        // dependency is still recognisably a dependency that has not recovered.
        breaker.Record(VerdictKind.Ok, TimeSpan.FromSeconds(2));

        Assert.Equal(BreakerState.Open, breaker.State);
    }

    [Fact]
    public void Reset_forgets_the_decision_and_keeps_the_measurement()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Adaptive(time));

        Sample(breaker, VerdictKind.Ok, 20, TimeSpan.FromMilliseconds(50));
        Sample(breaker, VerdictKind.Ok, 20, TimeSpan.FromSeconds(2));
        Assert.Equal(BreakerState.Open, breaker.State);

        breaker.Reset();

        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.NotNull(breaker.NormalLatency);
    }

    // ---- Diagnostics ----

    [Fact]
    public void A_breaker_with_no_adaptive_trip_has_no_baseline_to_report()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(new BreakerSettings { SlowCalls = null, Time = time });

        Sample(breaker, VerdictKind.Ok, 100, TimeSpan.FromMilliseconds(10));

        Assert.Null(breaker.NormalLatency);
    }

    /// <summary>
    ///     A constant does not stop the breaker measuring. The baseline is a measurement of the
    ///     dependency rather than a decision about it, so it is there to read whether or not anybody
    ///     named a millisecond figure alongside it.
    /// </summary>
    [Fact]
    public void A_breaker_given_a_constant_still_reports_its_baseline()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(new BreakerSettings { SlowCallThreshold = TimeSpan.FromSeconds(2), Time = time });

        Sample(breaker, VerdictKind.Ok, 100, TimeSpan.FromMilliseconds(10));

        Assert.NotNull(breaker.NormalLatency);
    }

    // ---- Configuration ----

    /// <summary>
    ///     The rejected design, refused at construction. Deriving the threshold from a high quantile of
    ///     recent latency is the version of this feature that reads well and cannot ever trip: the
    ///     threshold moves with the brownout, so the fraction of slow calls is pinned near
    ///     <c>1 - quantile</c> and never reaches <c>SlowCallRatio</c>.
    /// </summary>
    [Theory]
    [InlineData(0.95)]
    [InlineData(0.99)]
    public void A_baseline_read_from_a_high_quantile_is_refused(double quantile)
    {
        var problem = Assert.Throws<ResilienceConfigurationException>(() =>
            new Breaker(new BreakerSettings { SlowCalls = SlowCalls.Above(3) with { Quantile = quantile } }));

        Assert.Contains(problem.Problems, p => p.Contains("SlowCalls.Quantile", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The other half of the fix, also refused: a baseline measured over a window the brownout can
    ///     fill before the trip window does is the same bug with a different knob.
    /// </summary>
    [Fact]
    public void A_baseline_window_that_loses_the_race_against_the_trip_window_is_refused()
    {
        var problem = Assert.Throws<ResilienceConfigurationException>(() =>
            new Breaker(new BreakerSettings
            {
                SlowCalls = SlowCalls.Above(3) with { Window = TimeSpan.FromSeconds(30) },
                Window = TimeSpan.FromSeconds(30),
            }));

        Assert.Contains(problem.Problems, p => p.Contains("never open on latency", StringComparison.Ordinal));
    }

    [Fact]
    public void The_defaults_win_that_race_comfortably()
    {
        var breaker = new Breaker(new BreakerSettings { SlowCalls = SlowCalls.Above(3) });

        Assert.Equal(TimeSpan.FromMinutes(5), breaker.Settings.SlowCalls!.Value.Window);
        Assert.Equal(0.5, breaker.Settings.SlowCalls!.Value.Quantile);
        Assert.Equal(20, breaker.Settings.SlowCalls!.Value.MinimumSamples);
    }

    /// <summary>
    ///     One rule for every bound in the library: state it as a constant, measure it, or both, and
    ///     when both the tighter one wins. Here the measured term is the tighter one - a dependency
    ///     whose median is 10 ms is slow at 30 ms, long before the 2-second constant - so the breaker
    ///     opens on a brownout the constant would have sat closed through.
    /// </summary>
    [Fact]
    public void A_constant_and_a_measured_threshold_compose_and_the_tighter_one_wins()
    {
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings
        {
            SlowCallThreshold = TimeSpan.FromSeconds(2),
            SlowCalls = SlowCalls.Above(3),
            Time = time,
        });

        Sample(breaker, VerdictKind.Ok, 30, TimeSpan.FromMilliseconds(10));

        Assert.Equal(BreakerState.Closed, breaker.State);

        // Well under the 2-second constant, and well over three times the 10 ms baseline.
        Sample(breaker, VerdictKind.Ok, 30, TimeSpan.FromMilliseconds(100));

        Assert.Equal(BreakerState.Open, breaker.State);
    }

    /// <summary>
    ///     And the other direction: a constant below the measured threshold is the one that bounds the
    ///     trip, so naming one can only make the breaker open sooner.
    /// </summary>
    [Fact]
    public void A_constant_below_the_measured_threshold_is_the_one_that_binds()
    {
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings
        {
            SlowCallThreshold = TimeSpan.FromMilliseconds(15),
            SlowCalls = SlowCalls.Above(3),
            Time = time,
        });

        Sample(breaker, VerdictKind.Ok, 30, TimeSpan.FromMilliseconds(10));

        Assert.Equal(BreakerState.Closed, breaker.State);

        // Under three times the 10 ms baseline, and over the constant.
        Sample(breaker, VerdictKind.Ok, 30, TimeSpan.FromMilliseconds(20));

        Assert.Equal(BreakerState.Open, breaker.State);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0.5)]
    [InlineData(double.NaN)]
    public void A_multiple_that_is_not_above_normal_is_refused(double multiple)
    {
        var problem = Assert.Throws<ResilienceConfigurationException>(() =>
            new Breaker(new BreakerSettings { SlowCalls = SlowCalls.Above(multiple) }));

        Assert.Contains(problem.Problems, p => p.Contains("SlowCalls.Multiple", StringComparison.Ordinal));
    }

    [Fact]
    public void Naming_a_default_explicitly_is_the_same_configuration_as_leaving_it_alone()
    {
        var left = SlowCalls.Above(3);
        var right = SlowCalls.Above(3) with { Quantile = 0.5, Window = TimeSpan.FromMinutes(5), MinimumSamples = 20 };

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void It_describes_itself_the_way_it_was_configured()
    {
        Assert.Equal("3x p50 over 300s (min 20 samples)", SlowCalls.Above(3).ToString());
    }
}
