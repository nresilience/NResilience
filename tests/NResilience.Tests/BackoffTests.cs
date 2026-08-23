namespace NResilience.Tests;

/// <summary>Tests for the delay between one attempt and the next.</summary>
public sealed class BackoffTests
{
    [Fact]
    public void Throttling_and_transient_failure_get_curves_an_order_of_magnitude_apart()
    {
        Backoff backoff = Backoff.Exponential() with { Jitter = Jitter.None };

        Assert.Equal(TimeSpan.FromMilliseconds(100), Delay(backoff, Verdict.Transient, 2));
        Assert.Equal(TimeSpan.FromSeconds(1), Delay(backoff, Verdict.Throttled(), 2));
    }

    [Fact]
    public void The_curve_grows_by_the_factor_per_attempt()
    {
        Backoff backoff = Backoff.Exponential() with { Jitter = Jitter.None };

        Assert.Equal(TimeSpan.FromMilliseconds(100), Delay(backoff, Verdict.Transient, 2));
        Assert.Equal(TimeSpan.FromMilliseconds(200), Delay(backoff, Verdict.Transient, 3));
        Assert.Equal(TimeSpan.FromMilliseconds(400), Delay(backoff, Verdict.Transient, 4));
    }

    [Fact]
    public void The_cap_is_hard_and_defaults_to_thirty_seconds()
    {
        Backoff backoff = Backoff.Exponential() with { Jitter = Jitter.None };
        Assert.Equal(TimeSpan.FromSeconds(30), Delay(backoff, Verdict.Transient, 30));
    }

    [Fact]
    public void Full_jitter_spreads_over_the_whole_interval()
    {
        Backoff backoff = Backoff.Exponential(transientBase: TimeSpan.FromSeconds(1));

        var draws = new List<TimeSpan>();
        for (int i = 0; i < 500; i++)
        {
            draws.Add(Delay(backoff, Verdict.Transient, 2));
        }

        Assert.All(draws, d => Assert.InRange(d, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.True(draws.Min() < TimeSpan.FromMilliseconds(300), "full jitter should reach the bottom of the interval");
        Assert.True(draws.Max() > TimeSpan.FromMilliseconds(700), "full jitter should reach the top of the interval");
    }

    [Fact]
    public void Equal_jitter_keeps_a_floor_under_the_delay()
    {
        Backoff backoff = Backoff.Exponential(transientBase: TimeSpan.FromSeconds(1)) with { Jitter = Jitter.Equal };

        for (int i = 0; i < 200; i++)
        {
            Assert.InRange(Delay(backoff, Verdict.Transient, 2), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void Server_pushback_beats_every_curve_and_is_not_jittered()
    {
        Assert.Equal(TimeSpan.FromSeconds(4), Delay(Backoff.Exponential(), Verdict.Throttled(TimeSpan.FromSeconds(4)), 2));
    }

    [Fact]
    public void Server_pushback_is_still_capped()
    {
        Backoff backoff = Backoff.Exponential(max: TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), Delay(backoff, Verdict.Throttled(TimeSpan.FromHours(1)), 2));
    }

    [Fact]
    public void None_never_waits()
    {
        Assert.Equal(TimeSpan.Zero, Delay(Backoff.None, Verdict.Transient, 5));
    }

    [Fact]
    public void Constant_is_constant()
    {
        Backoff backoff = Backoff.Constant(TimeSpan.FromMilliseconds(250)) with { Jitter = Jitter.None };

        Assert.Equal(TimeSpan.FromMilliseconds(250), Delay(backoff, Verdict.Transient, 2));
        Assert.Equal(TimeSpan.FromMilliseconds(250), Delay(backoff, Verdict.Transient, 9));
    }

    [Fact]
    public void Custom_is_handed_the_attempt_that_is_about_to_happen()
    {
        int seen = 0;
        Backoff backoff = Backoff.Custom(next =>
        {
            seen = next.Number;
            return TimeSpan.FromMilliseconds(next.Number);
        });

        Assert.Equal(TimeSpan.FromMilliseconds(4), Delay(backoff, Verdict.Transient, 4));
        Assert.Equal(4, seen);
    }

    [Fact]
    public void An_unconstructed_value_behaves_as_the_default()
    {
        Backoff unconstructed = default;

        Assert.Equal(
            Delay(Backoff.Default with { Jitter = Jitter.None }, Verdict.Transient, 3),
            Delay(unconstructed with { Jitter = Jitter.None }, Verdict.Transient, 3));
    }

    [Fact]
    public void An_uncapped_exponential_with_many_attempts_does_not_produce_a_negative_delay()
    {
        // max is InfiniteTimeSpan (uncapped), and a very large attempt number pushes
        // Math.Pow(factor, n) into the infinite range. The runtime clamps (long)infinity to
        // long.MaxValue, so this does not wrap to negative - but the guard makes the intent
        // explicit and returns TimeSpan.Zero rather than a 29000-year delay.
        Backoff uncapped = Backoff.Exponential(max: Timeout.InfiniteTimeSpan) with { Jitter = Jitter.None };

        TimeSpan delay = Delay(uncapped, Verdict.Transient, 1100);

        Assert.True(delay >= TimeSpan.Zero, $"expected non-negative delay, got {delay}");
    }

    [Fact]
    public void An_uncapped_exponential_saturates_to_a_finite_positive_delay()
    {
        // A finite-but-huge ticks value clamps to long.MaxValue ticks, which is a positive TimeSpan,
        // rather than wrapping or collapsing to zero.
        Backoff uncapped = Backoff.Exponential(max: Timeout.InfiniteTimeSpan) with { Jitter = Jitter.None };

        TimeSpan delay = Delay(uncapped, Verdict.Transient, 1000);

        Assert.True(delay > TimeSpan.Zero, $"expected a positive delay, got {delay}");
    }

    [Fact]
    public void A_nan_factor_is_rejected_by_validation()
    {
        Backoff nan = Backoff.Exponential(factor: double.NaN);

        var problems = new List<string>();
        nan.Validate(problems);

        Assert.Contains(problems, p => p.Contains("factor") && p.Contains("NaN"));
    }

    private static TimeSpan Delay(Backoff backoff, Verdict previous, int attemptNumber) =>
        backoff.Compute(new NextAttempt(attemptNumber, previous, null, Timeout.InfiniteTimeSpan, default));
}
