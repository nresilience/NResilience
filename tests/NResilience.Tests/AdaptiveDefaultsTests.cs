using Microsoft.Extensions.Time.Testing;

namespace NResilience.Tests;

/// <summary>
///     The three adaptive features that are on without anybody asking: the measured attempt ceiling,
///     the brownout trip and the relative failure trip.
///     <para>
///         All three are safe to default on for the same reason - each is measured against the
///         dependency's own behaviour, each is invisible until it has a baseline, and each can only
///         tighten a bound the caller already has. What these tests hold the defaults to is the
///         corollary: a default the caller never wrote must never turn a working configuration into a
///         configuration error, and must never bound a call the caller said was unbounded.
///     </para>
/// </summary>
public sealed class AdaptiveDefaultsTests
{
    private static void Sample(Breaker breaker, VerdictKind kind, int count, TimeSpan duration = default)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(breaker.TryEnter(out _), "admission was refused before the test expected it");
            breaker.Record(kind, duration);
        }
    }

    // ---- What the defaults are ----

    [Fact]
    public void A_default_breaker_trips_on_both_relative_conditions()
    {
        var settings = new BreakerSettings();

        Assert.Equal(SlowCalls.Above(3), settings.SlowCalls);
        Assert.Equal(Failures.Above(5), settings.Failures);
    }

    [Fact]
    public void A_default_policy_measures_its_own_attempt_ceiling()
    {
        Assert.Equal(AttemptCeiling.Above(3), Resilience.Default.AttemptCeiling);
        Assert.Equal(AttemptCeiling.Above(3), Resilience.Http.AttemptCeiling);
    }

    /// <summary>Each of the three is turned off by writing <c>null</c> over it, and nothing else.</summary>
    [Fact]
    public void Null_is_the_off_switch()
    {
        var settings = new BreakerSettings { SlowCalls = null, Failures = null };

        Assert.Null(settings.SlowCalls);
        Assert.Null(settings.Failures);
        Assert.Null((Resilience.Default with { AttemptCeiling = null }).AttemptCeiling);
    }

    /// <summary>
    ///     The one preset that promises to impose nothing keeps promising it - a measured ceiling is a
    ///     bound like any other, and passthrough turns every bound off.
    /// </summary>
    [Fact]
    public void The_passthrough_preset_measures_nothing()
    {
        Assert.Null(Resilience.None.AttemptCeiling);
        Assert.Null(Resilience.None.MeasuredAttemptCeiling);
    }

    // ---- What a default must never do: bound a call nobody bounded ----

    /// <summary>
    ///     An infinite <c>AttemptTimeout</c> is the caller saying the deadline is the only per-attempt
    ///     bound. A measured ceiling defaulted in underneath that would be a bound nothing they wrote
    ///     clamps, which is the one thing the tighten-only argument does not cover.
    /// </summary>
    [Fact]
    public void A_policy_with_no_attempt_ceiling_gets_no_measured_one()
    {
        var policy = Resilience.Default with { AttemptTimeout = Timeout.InfiniteTimeSpan };

        Assert.Null(policy.AttemptCeiling);
        Assert.Null(policy.MeasuredAttemptCeiling);
    }

    /// <summary>Writing it is a different statement, and that one is honoured.</summary>
    [Fact]
    public void Asking_for_a_measured_ceiling_without_an_attempt_ceiling_still_works()
    {
        var policy = Resilience.Default with
        {
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            AttemptCeiling = AttemptCeiling.Above(3),
        };

        Assert.Equal(AttemptCeiling.Above(3), policy.AttemptCeiling);
        policy.Validate();
    }

    // ---- What a default must never do: refuse a configuration the caller could write ----

    /// <summary>
    ///     A 20 ms ceiling is below <c>AttemptCeiling.Floor</c>, so the measured term could never lower
    ///     anything - which <c>Validate</c> refuses when the caller wrote both. Here the caller wrote
    ///     one, so the default steps aside rather than turning their policy into an error.
    /// </summary>
    [Fact]
    public void An_attempt_ceiling_under_the_floor_drops_the_default_rather_than_failing()
    {
        var policy = Resilience.Default with { AttemptTimeout = TimeSpan.FromMilliseconds(20) };

        Assert.Null(policy.AttemptCeiling);
        policy.Validate();

        Assert.Throws<ResilienceConfigurationException>(() =>
            (policy with { AttemptCeiling = AttemptCeiling.Above(3) }).Validate());
    }

    /// <summary>
    ///     The contamination race the two relative trips are held to is a race against the <i>trip</i>
    ///     window, which is configurable - so a baseline that only wins it at <c>Window</c>'s default is
    ///     not a default. A default widens its baseline to whatever the trip window needs.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(150)]
    [InlineData(300)]
    public void A_trip_window_the_default_baselines_could_not_outlast_widens_them(int windowSeconds)
    {
        var settings = new BreakerSettings { TripWindow = TimeSpan.FromSeconds(windowSeconds) };

        // The check the race would otherwise fail, stated the way BreakerSettings.Validate states it.
        settings.Validate();

        var failures = Assert.NotNull(settings.Failures);
        var slow = Assert.NotNull(settings.SlowCalls);

        Assert.True(failures.Window.TotalSeconds / failures.Multiple >= 2 * windowSeconds);
        Assert.True(slow.Quantile * slow.Window.TotalSeconds >= 2 * settings.SlowCallRatio * windowSeconds);
    }

    /// <summary>
    ///     Past an hour the baseline a trip window would need has stopped describing "normally", so the
    ///     default steps aside instead of measuring something nobody would recognize.
    /// </summary>
    [Fact]
    public void A_trip_window_too_long_to_have_a_baseline_gets_no_default_relative_trip()
    {
        var settings = new BreakerSettings { TripWindow = TimeSpan.FromMinutes(45) };

        settings.Validate();

        Assert.Null(settings.Failures);
        Assert.Null(settings.SlowCalls);
    }

    /// <summary>
    ///     The two are the same trip defined two ways, and they compose the way every constant in this
    ///     library composes with the measurement that refines it - so naming the absolute one leaves the
    ///     default relative one in place, and setting both is a configuration rather than an error.
    /// </summary>
    [Fact]
    public void An_absolute_slow_call_threshold_composes_with_the_default_relative_one()
    {
        var settings = new BreakerSettings { SlowCallThreshold = TimeSpan.FromSeconds(2) };

        settings.Validate();

        Assert.Equal(SlowCalls.Above(3), settings.SlowCalls);

        (settings with { SlowCalls = SlowCalls.Above(4) }).Validate();
    }

    /// <summary>
    ///     The absolute failure ratio is not the same trip - it is the ceiling the relative one can only
    ///     trip below - so naming it leaves the default in place.
    /// </summary>
    [Fact]
    public void An_absolute_failure_ratio_composes_with_the_default_relative_one()
    {
        var settings = new BreakerSettings { FailureRatio = 0.5 };

        settings.Validate();

        Assert.Equal(Failures.Above(5), settings.Failures);
    }

    // ---- What the defaults buy ----

    /// <summary>
    ///     The reason this is worth a defaults change at all: before it, every breaker the library built
    ///     for you - one per HTTP host, among others - could not see a dependency answering <c>200 OK</c>
    ///     at ten times its normal latency, which is the most common way a dependency fails.
    /// </summary>
    [Fact]
    public void A_breaker_nobody_configured_sees_a_brownout()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(new BreakerSettings { Time = time });

        Sample(breaker, VerdictKind.Ok, 20, TimeSpan.FromMilliseconds(100));
        Assert.Equal(BreakerState.Closed, breaker.State);

        Sample(breaker, VerdictKind.Ok, 20, TimeSpan.FromSeconds(1));

        Assert.Equal(BreakerState.Open, breaker.State);
    }

    /// <summary>
    ///     And the same for the error rate: a dependency that normally never fails, failing at ten
    ///     percent, opens a breaker nobody configured - well before the fifth <i>consecutive</i>
    ///     failure the classic condition waits for, which a partial failure may never produce.
    /// </summary>
    [Fact]
    public void A_breaker_nobody_configured_sees_an_error_rate_that_never_goes_consecutive()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(new BreakerSettings { Time = time });

        // Five minutes of healthy traffic, which is what arms the relative trip: a baseline of zero.
        for (var slice = 0; slice < 5; slice++)
        {
            Sample(breaker, VerdictKind.Ok, 50, TimeSpan.FromMilliseconds(10));
            time.Advance(TimeSpan.FromSeconds(60));
        }

        Assert.Equal(0, breaker.NormalFailureRate);
        Assert.Equal(BreakerState.Closed, breaker.State);

        // Two failures in twenty calls is ten percent, and never two in a row.
        for (var i = 0; i < 2; i++)
        {
            Sample(breaker, VerdictKind.Ok, 9, TimeSpan.FromMilliseconds(10));
            Sample(breaker, VerdictKind.Transient, 1, TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(BreakerState.Open, breaker.State);
    }

    /// <summary>
    ///     The cold-start rule, which is what makes all of this invisible until it has evidence: a
    ///     breaker with no baseline behaves exactly as a consecutive-failures breaker does.
    /// </summary>
    [Fact]
    public void A_cold_breaker_behaves_as_it_did_before_the_defaults_moved()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(new BreakerSettings { Time = time });

        Assert.Null(breaker.NormalLatency);
        Assert.Null(breaker.NormalFailureRate);

        // Four failures out of four calls is a hundred percent, and neither relative trip has a
        // baseline to compare it against.
        Sample(breaker, VerdictKind.Transient, 4, TimeSpan.FromSeconds(30));
        Assert.Equal(BreakerState.Closed, breaker.State);

        // The fifth consecutive failure is what opens it, exactly as it always did.
        Sample(breaker, VerdictKind.Transient, 1, TimeSpan.FromSeconds(30));
        Assert.Equal(BreakerState.Open, breaker.State);
    }
}
