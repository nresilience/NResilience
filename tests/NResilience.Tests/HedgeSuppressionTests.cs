using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The sixth gate: hedging stops once the dependency's error rate has climbed to
///     <see cref="Hedge.SuppressAt" /> of the rate that would open its breaker.
/// </summary>
/// <remarks>
///     <para>
///         The gate exists because "the breaker is closed" and "the dependency is healthy" are not the
///         same statement. A breaker's default trip is five consecutive failures, so a dependency
///         returning errors on a third of its calls sits closed indefinitely while this process hedges
///         every slow one and adds load it cannot use.
///     </para>
///     <para>
///         The clock discipline is <see cref="HedgeTests" />': warming shapes the latency estimate
///         inside a synchronously-completing callback so no hedge can fire, and only
///         <see cref="RaceAsync" /> - which blocks an attempt and moves the clock from outside - can
///         ever produce one.
///     </para>
/// </remarks>
public sealed class HedgeSuppressionTests
{
    /// <summary>Long enough that nothing under test rolls out of a window it was meant to stay in.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(10);

    /// <summary>
    ///     The feature. The breaker is closed the whole time - the trip is 50% and the window sits at
    ///     29% - and the hedge that would have fired does not.
    /// </summary>
    [Fact]
    public async Task A_closed_breaker_over_an_elevated_error_rate_suppresses_hedging()
    {
        var time = new FakeTimeProvider();
        var breaker = Breaking(time);
        var policy = Hedging(time, breaker, out var events);

        await WarmAsync(policy, time, 20);
        await FailAsync(policy, 8);

        Assert.Equal(BreakerState.Closed, breaker.State);

        var race = await RaceAsync(policy, time);

        Assert.Equal(0, events.CountOf(CallEventKind.HedgeStarted));
        Assert.Equal(1, race.Calls);
    }

    /// <summary>
    ///     The other side of the same line. Two transient errors against the same dependency are not an
    ///     elevated rate, and hedging is untouched - a policy that stopped hedging at the first sign of
    ///     trouble would never hedge anything.
    /// </summary>
    [Fact]
    public async Task A_couple_of_failures_leave_hedging_alone()
    {
        var time = new FakeTimeProvider();
        var breaker = Breaking(time);
        var policy = Hedging(time, breaker, out var events);

        await WarmAsync(policy, time, 20);
        await FailAsync(policy, 2);

        var race = await RaceAsync(policy, time);

        Assert.Single(events.OfKind(CallEventKind.HedgeStarted));
        Assert.Equal(2, race.Calls);
    }

    /// <summary>
    ///     The off switch. At <c>SuppressAt = 1</c> the only rate that suppresses hedging is the one that
    ///     opens the breaker, which is the gate above it - so the same 29% window that suppressed hedging
    ///     at the default hedges here.
    /// </summary>
    [Fact]
    public async Task Suppressing_only_at_the_trip_point_leaves_hedging_on()
    {
        var time = new FakeTimeProvider();
        var breaker = Breaking(time);
        var policy = Hedging(time, breaker, out var events, 1);

        await WarmAsync(policy, time, 20);
        await FailAsync(policy, 8);

        var race = await RaceAsync(policy, time);

        Assert.Single(events.OfKind(CallEventKind.HedgeStarted));
        Assert.Equal(2, race.Calls);
    }

    /// <summary>
    ///     One failure is not a rate, whatever the arithmetic says. Two calls and one error is a 33%
    ///     window against a 25% suppression point, and hedging survives it - the same rule the relative
    ///     trip applies, for the same reason.
    /// </summary>
    [Fact]
    public async Task A_single_failure_is_not_an_elevated_rate()
    {
        var time = new FakeTimeProvider();
        var breaker = Breaking(time, 2);
        var policy = Hedging(time, breaker, out var events, minimumSamples: 2);

        await WarmAsync(policy, time, 2);
        await FailAsync(policy, 1);

        var race = await RaceAsync(policy, time);

        Assert.Single(events.OfKind(CallEventKind.HedgeStarted));
        Assert.Equal(2, race.Calls);
    }

    /// <summary>
    ///     Nothing to be a fraction of, nothing suppressed. A breaker that only counts consecutive
    ///     failures has no rate-based trip point, so this gate is not armed against it at all.
    /// </summary>
    [Fact]
    public async Task A_breaker_with_no_rate_based_trip_suppresses_nothing()
    {
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings
        {
            ConsecutiveFailures = 50,
            FailureRatio = null,
            Failures = null,
            SlowCalls = null,
            TripWindow = Window,
            Time = time,
        });

        var policy = Hedging(time, breaker, out var events);

        await WarmAsync(policy, time, 20);
        await FailAsync(policy, 8);

        var race = await RaceAsync(policy, time);

        Assert.Single(events.OfKind(CallEventKind.HedgeStarted));
        Assert.Equal(2, race.Calls);
    }

    /// <summary>
    ///     The default configuration, end to end: no absolute <see cref="BreakerSettings.FailureRatio" />
    ///     at all, so the trip point is the measured baseline times <see cref="Failures.Multiple" />, and
    ///     a burst that leaves the five-minute baseline nearly untouched suppresses hedging against the
    ///     thirty-second window.
    /// </summary>
    [Fact]
    public async Task The_measured_baseline_is_what_the_burst_is_elevated_against()
    {
        var time = new FakeTimeProvider();

        // The stock adaptive breaker: no FailureRatio, Failures.Above(5) by default, and the default
        // 30 s trip window against the baseline's 5 minutes. ConsecutiveFailures is raised only so that
        // the burst below is judged as a rate rather than by the counter beside it.
        var breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 50, Time = time });
        var policy = Hedging(time, breaker, out var events);

        // Spread over 100 s, so the baseline holds all of them and the trip window holds the last 30.
        for (var i = 0; i < 100; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await WarmAsync(policy, time, 1);
        }

        Assert.NotNull(breaker.NormalFailureRate);

        await FailAsync(policy, 4);

        // 4 failures over 5 minutes is a 3.8% baseline, so the trip point is 19% and the suppression
        // point half of that; the same 4 failures over 30 seconds is 12% of the trip window.
        Assert.Equal(BreakerState.Closed, breaker.State);

        var race = await RaceAsync(policy, time);

        Assert.Equal(0, events.CountOf(CallEventKind.HedgeStarted));
        Assert.Equal(1, race.Calls);
    }

    /// <summary>
    ///     A fraction of a trip point is only meaningful inside <c>(0, 1]</c>, and zero is the losing
    ///     configuration worth naming: it reads as "suppress at no errors at all", which is a hedge that
    ///     never fires.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void A_suppression_point_outside_the_unit_interval_is_refused(double suppressAt)
    {
        var policy = Resilience.Default with { Hedge = Hedge.At() with { SuppressAt = suppressAt } };

        var problem = Assert.Throws<ResilienceConfigurationException>(policy.Validate);

        Assert.Contains(problem.Problems, p => p.Contains("Hedge.SuppressAt", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The struct's own rule: naming a default explicitly is the same configuration as leaving it
    ///     alone, so a hedge does not stop equalling itself because somebody wrote the number down.
    /// </summary>
    [Fact]
    public void Naming_the_default_suppression_point_changes_nothing()
    {
        Assert.Equal(0.5, Hedge.At().SuppressAt);
        Assert.Equal(Hedge.At(), Hedge.At() with { SuppressAt = 0.5 });
        Assert.NotEqual(Hedge.At(), Hedge.At() with { SuppressAt = 1 });
    }

    // ---- Helpers ----

    /// <summary>
    ///     A breaker whose rate-based trip is an absolute 50%, so the suppression point is a flat 25%
    ///     and no baseline has to be established first.
    /// </summary>
    private static Breaker Breaking(FakeTimeProvider time, int minimumCalls = 20) =>
        new(new BreakerSettings
        {
            ConsecutiveFailures = 50,
            FailureRatio = 0.5,
            Failures = null,
            SlowCalls = null,
            MinimumCalls = minimumCalls,
            TripWindow = Window,
            Time = time,
        });

    /// <summary>A hedging policy over <paramref name="breaker" />, with a listener on the same instance.</summary>
    private static Resilience Hedging(
        FakeTimeProvider time,
        Breaker breaker,
        out EventRecorder events,
        double? suppressAt = null,
        int? minimumSamples = null)
    {
        var recorder = new EventRecorder();
        events = recorder;

        var hedge = Hedge.At() with { Window = Window };

        if (suppressAt is { } fraction)
            hedge = hedge with { SuppressAt = fraction };

        if (minimumSamples is { } samples)
            hedge = hedge with { MinimumSamples = samples };

        return TestPolicy.Instant with
        {
            Name = "api",
            Time = time,
            Breaker = breaker,
            Hedge = hedge,
            OnEvent = recorder.Record,
        };
    }

    /// <summary>Records fast successes into the latency estimate and the breaker's window.</summary>
    private static async Task WarmAsync(Resilience policy, FakeTimeProvider time, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await policy.RunAsync(_ =>
            {
                time.Advance(Fast);
                return Task.FromResult(1);
            });
        }
    }

    /// <summary>
    ///     Records transient failures into the same breaker. Through a single-attempt policy on purpose:
    ///     a retrying one would multiply the failures it is asked for, and the count is the point.
    /// </summary>
    private static async Task FailAsync(Resilience policy, int times)
    {
        var once = policy with { Attempts = 1, Hedge = null, OnEvent = null };

        for (var i = 0; i < times; i++)
        {
            await once.TryRunAsync(_ => Task.FromException<int>(new IOException()));
        }
    }

    /// <summary>
    ///     Runs one call whose first attempt blocks until it is cancelled or the pump gives up, and moves
    ///     the clock from outside so an armed hedge timer can fire.
    /// </summary>
    private static async Task<(CallResult<int> Result, int Calls)> RaceAsync(Resilience policy, FakeTimeProvider time)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var call = policy.TryRunAsync(async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await gate.Task.WaitAsync(ct);
                return 1;
            }

            return 2;
        }).AsTask();

        for (var i = 0; i < 10 && !call.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));

            // A real yield: the loop's continuation runs on the thread pool, where the fake clock
            // cannot advance it.
            await Task.Delay(1);
        }

        gate.TrySetResult();

        return (await call, calls);
    }
}
