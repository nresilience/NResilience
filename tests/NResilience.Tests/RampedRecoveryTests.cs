using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     Ramped recovery: what a breaker does between "the probes succeeded" and "everything is
///     admitted again", when it is configured to do anything at all.
///     <para>
///         Driven through the admission and recording entry points for the reason
///         <see cref="BreakerTests" /> gives, with the executor's own view of a ramp refusal covered at
///         the bottom and the claim the feature is for covered by a deterministic simulation at the
///         very bottom.
///     </para>
/// </summary>
public sealed class RampedRecoveryTests
{
    /// <summary>Ten seconds of break, no jitter, and a ramp over a quarter of it.</summary>
    private static BreakerSettings Ramped(FakeTimeProvider time, Recovery? recovery = null) => new()
    {
        Time = time,
        BreakDuration = TimeSpan.FromSeconds(10),
        BreakJitter = Jitter.None,
        Recovery = recovery ?? Recovery.Over(0.25),
    };

    private static void Sample(Breaker breaker, VerdictKind kind, int count = 1, TimeSpan duration = default)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(breaker.TryEnter(out _, out _), "admission was refused before the test expected it");
            breaker.Record(kind, duration);
        }
    }

    /// <summary>Trips the breaker, waits the break out, and lands the probes that start the ramp.</summary>
    private static void Recover(Breaker breaker, FakeTimeProvider time, TimeSpan? probeDuration = null)
    {
        Sample(breaker, VerdictKind.Transient, 5);
        time.Advance(TimeSpan.FromSeconds(10));
        Sample(breaker, VerdictKind.Ok, 2, probeDuration ?? TimeSpan.Zero);
    }

    /// <summary>Offers the breaker calls and reports how many it admitted, recording each one.</summary>
    private static int Offer(Breaker breaker, int calls, VerdictKind kind = VerdictKind.Ok, TimeSpan duration = default)
    {
        var admitted = 0;

        for (var i = 0; i < calls; i++)
        {
            if (!breaker.TryEnter(out _, out _))
                continue;

            admitted++;
            breaker.Record(kind, duration);
        }

        return admitted;
    }

    // ---- The state itself ----

    /// <summary>
    ///     The cliff is still the default. Nothing about a breaker that was never told to ramp changes,
    ///     which is what keeps this feature from being a behaviour change nobody asked for.
    /// </summary>
    [Fact]
    public void A_breaker_without_a_recovery_still_closes_on_a_cliff()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(new BreakerSettings { Time = time, BreakDuration = TimeSpan.FromSeconds(10), BreakJitter = Jitter.None });

        Recover(breaker, time);

        Assert.Null(breaker.Settings.Recovery);
        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Equal(100, Offer(breaker, 100));
    }

    [Fact]
    public void The_probes_succeeding_starts_a_ramp_rather_than_closing()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Ramped(time));

        Recover(breaker, time);

        Assert.Equal(BreakerState.Recovering, breaker.State);

        // The ramp is the tail of the open it is recovering from, so the timestamp survives it.
        Assert.NotNull(breaker.OpenedAt);

        // 5% of the offered calls, which is where Recovery.Initial starts the ramp. The rest are
        // refused exactly as an open breaker refuses them.
        Assert.Equal(5, Offer(breaker, 100, VerdictKind.Ok));
    }

    /// <summary>
    ///     The clock half of the ramp. A dependency answering fast is handed its traffic back over the
    ///     ramp rather than in the millisecond the last probe landed.
    /// </summary>
    [Fact]
    public void The_admitted_fraction_grows_across_the_ramp()
    {
        var time = new FakeTimeProvider();

        // A 10 s break at a quarter is a 2.5 s ramp, and Initial is 5%.
        var breaker = new Breaker(Ramped(time));
        Recover(breaker, time);

        var start = Offer(breaker, 200);

        time.Advance(TimeSpan.FromMilliseconds(1250));
        var half = Offer(breaker, 200);

        time.Advance(TimeSpan.FromMilliseconds(1250));

        Assert.InRange(start, 5, 20);
        Assert.InRange(half, 90, 130);

        // The clock ran out, and so the ramp is over: a read reports the close the next call performs.
        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Equal(200, Offer(breaker, 200));
    }

    [Fact]
    public void The_ramp_length_comes_from_the_break_just_served()
    {
        var time = new FakeTimeProvider();

        // The second open serves 20 s, so its ramp is 5 s rather than the first one's 2.5 s.
        var breaker = new Breaker(Ramped(time));
        Recover(breaker, time);

        Offer(breaker, 40, VerdictKind.Transient);
        Assert.Equal(BreakerState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(20));
        Sample(breaker, VerdictKind.Ok, 2);
        Assert.Equal(BreakerState.Recovering, breaker.State);

        time.Advance(TimeSpan.FromMilliseconds(2600));
        Assert.Equal(BreakerState.Recovering, breaker.State);

        time.Advance(TimeSpan.FromMilliseconds(2500));
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void The_ramp_length_is_clamped_at_both_ends()
    {
        var recovery = Recovery.Over(0.25) with { MinimumLength = TimeSpan.FromSeconds(2), MaximumLength = TimeSpan.FromSeconds(20) };

        // A one-second break would be a 250 ms ramp, which is a cliff with extra state.
        Assert.Equal(TimeSpan.FromSeconds(2), recovery.RampFor(TimeSpan.FromSeconds(1)));

        Assert.Equal(TimeSpan.FromSeconds(10), recovery.RampFor(TimeSpan.FromSeconds(40)));

        // Two minutes of break is thirty seconds of ramp without the ceiling, and the ceiling is the
        // bound on what this feature can cost.
        Assert.Equal(TimeSpan.FromSeconds(20), recovery.RampFor(TimeSpan.FromMinutes(2)));
    }

    // ---- Growing, stalling, and failing ----

    /// <summary>
    ///     The evidence half. A dependency that is up and is not ready answers slowly, and there is no
    ///     other way for a breaker to say that: the ramp stops where it is instead of completing.
    /// </summary>
    [Fact]
    public void A_ramp_whose_traffic_is_slow_stalls_instead_of_completing()
    {
        var time = new FakeTimeProvider();

        // An absolute threshold rather than the measured one, so "slow" is a fixed fact here and the
        // baseline's warm-up is somebody else's test.
        var breaker = new Breaker(Ramped(time) with { SlowCallThreshold = TimeSpan.FromSeconds(1) });
        Recover(breaker, time);

        // Every admitted call answers, and every one of them takes three seconds.
        var slow = TimeSpan.FromSeconds(3);

        for (var step = 0; step < 10; step++)
        {
            Offer(breaker, 100, VerdictKind.Ok, slow);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        // Ten seconds is four times the 2.5 s ramp, and it is still recovering: the clock cannot
        // finish a ramp the evidence has not paid for.
        Assert.Equal(BreakerState.Recovering, breaker.State);
        Assert.InRange(Offer(breaker, 200, VerdictKind.Ok, slow), 5, 20);

        // The dependency gets faster, and the ramp resumes from where it stalled.
        for (var step = 0; step < 5; step++)
            Offer(breaker, 100);

        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void A_failure_during_the_ramp_reopens_the_breaker()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Ramped(time));

        Recover(breaker, time);
        Assert.Equal(BreakerState.Recovering, breaker.State);

        // One transient failure, without waiting for any of the trip conditions a closed breaker is
        // held to. The ramp is a hypothesis, and this withdraws it.
        Offer(breaker, 40, VerdictKind.Transient);

        Assert.Equal(BreakerState.Open, breaker.State);
        Assert.NotNull(breaker.OpenedAt);
    }

    [Fact]
    public void A_ramp_that_fails_reopens_with_the_grown_break()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Ramped(time));

        Recover(breaker, time);
        Offer(breaker, 40, VerdictKind.Transient);

        // The first break was 10 s. A ramp that failed is not a clean close, so the second is 20 s -
        // without that, a dependency that fails every ramp is probed on a fixed cadence forever.
        time.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(BreakerState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    [Fact]
    public void A_ramp_that_completes_forgets_the_accumulated_growth()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Ramped(time));

        Recover(breaker, time);
        Offer(breaker, 40, VerdictKind.Transient);

        // Reopened at 20 s. Ride the second ramp all the way out, and the growth is forgotten.
        time.Advance(TimeSpan.FromSeconds(20));
        Sample(breaker, VerdictKind.Ok, 2);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(200, Offer(breaker, 200));
        Assert.Equal(BreakerState.Closed, breaker.State);

        Sample(breaker, VerdictKind.Transient, 5);
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    // ---- Administration ----

    /// <summary>
    ///     Somebody decided the dependency is fine. Warming one an operator has already vouched for is
    ///     not part of what anyone means by "reset".
    /// </summary>
    [Fact]
    public void Reset_closes_without_a_ramp()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Ramped(time));

        Recover(breaker, time);
        Assert.Equal(BreakerState.Recovering, breaker.State);

        breaker.Reset();

        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Equal(100, Offer(breaker, 100));
    }

    [Fact]
    public void Isolating_a_recovering_breaker_stops_the_ramp()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Ramped(time));

        Recover(breaker, time);
        breaker.Isolate();

        Assert.Equal(BreakerState.Isolated, breaker.State);
        Assert.Equal(0, Offer(breaker, 100));

        // The ramp state went with it: reset, trip, recover, and the ramp starts from the beginning.
        breaker.Reset();
        Recover(breaker, time);
        Assert.InRange(Offer(breaker, 100), 5, 20);
    }

    // ---- Configuration ----

    [Fact]
    public void A_ramp_that_could_not_work_is_refused_at_construction()
    {
        var time = new FakeTimeProvider();

        var caught = Assert.Throws<ResilienceConfigurationException>(() => new Breaker(new BreakerSettings
        {
            Time = time,
            Recovery = Recovery.Over(0) with
            {
                MinimumLength = TimeSpan.FromSeconds(10),
                MaximumLength = TimeSpan.FromSeconds(5),
                InitialFraction = 1,
            },
        }));

        Assert.Contains("Recovery.Length", caught.Message, StringComparison.Ordinal);
        Assert.Contains("Recovery.MaximumLength", caught.Message, StringComparison.Ordinal);
        Assert.Contains("Recovery.InitialFraction", caught.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_that_names_a_default_equals_one_that_left_it_alone()
    {
        var spelled = Recovery.Over(0.25) with
        {
            MinimumLength = TimeSpan.FromSeconds(1),
            MaximumLength = TimeSpan.FromSeconds(30),
            InitialFraction = 0.05,
        };

        Assert.Equal(Recovery.Over(0.25), spelled);
        Assert.Equal(Recovery.Over(0.25).GetHashCode(), spelled.GetHashCode());
        Assert.Equal("0.25x the break, from 5% (min 1s, max 30s)", Recovery.Over(0.25).ToString());
    }

    // ---- Through the executor ----

    /// <summary>
    ///     A refused caller during a ramp gets exactly what an open breaker's refusal gives them, which
    ///     is the whole reason this feature needs no executor contact.
    /// </summary>
    [Fact]
    public async Task A_refusal_during_the_ramp_is_the_ordinary_breaker_rejection()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Ramped(time));

        Recover(breaker, time);

        // Nothing has been offered yet, so the ramp is at Recovery.Initial and one call in twenty
        // gets through. This one does not.
        var policy = TestPolicy.On(time) with { Breaker = breaker, Attempts = 1 };
        var call = policy.TryRunAsync(_ => Task.FromResult(1)).AsTask();

        time.Advance(TimeSpan.FromMilliseconds(100));
        var result = await call;

        Assert.Equal(StopReason.DependencyUnavailable, result.StopReason);

        var rejected = Assert.IsType<CallRejectedException>(result.Exception);

        // Nothing honest to say: the very next call is likely to be admitted, and a hint the caller
        // would honour is worse than none.
        Assert.Null(rejected.RetryAfter);
    }

    /// <summary>
    ///     The ramp starts where the breaker stops refusing everything, so that is where
    ///     <see cref="CallEventKind.BreakerClosed" /> is raised. Its completion is silent: a second one
    ///     would make the event sequence claim the breaker closed twice for one recovery.
    /// </summary>
    [Fact]
    public void Starting_the_ramp_is_what_reports_the_close()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(Ramped(time));

        Sample(breaker, VerdictKind.Transient, 5);
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.True(breaker.TryEnter(out var halfOpened, out _));
        Assert.Equal(BreakerTransition.HalfOpened, halfOpened);
        Assert.Equal(BreakerTransition.None, breaker.Record(VerdictKind.Ok, TimeSpan.Zero));

        Assert.True(breaker.TryEnter(out _, out _));
        Assert.Equal(BreakerTransition.Closed, breaker.Record(VerdictKind.Ok, TimeSpan.Zero));
        Assert.Equal(BreakerState.Recovering, breaker.State);

        // Riding the ramp out raises nothing further.
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(breaker.TryEnter(out var completed, out _));
        Assert.Equal(BreakerTransition.None, completed);
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    // ---- The claim the feature is for ----

    /// <summary>
    ///     The argument for the feature, made rather than asserted: against a dependency that needs ten
    ///     seconds to warm and fails what it cannot serve, a ramped breaker delivers more successful
    ///     calls over a minute than a cliffed one.
    ///     <para>
    ///         The cliffed breaker's failure mode is the one the feature exists to stop. Two probes
    ///         prove the dependency can serve two calls, the cliff hands it two hundred a second, the
    ///         overload knocks its capacity back down, the breaker re-opens with a doubled break, and it
    ///         spends more of each period cold than the last. The ramp gives it a trickle it can
    ///         actually serve, and the trickle is what lets it warm.
    ///     </para>
    /// </summary>
    [Fact]
    public void A_ramped_breaker_delivers_more_than_a_cliffed_one_against_a_dependency_that_has_to_warm()
    {
        var cliffed = Simulate(null);
        var ramped = Simulate(Recovery.Over(0.25));

        // Measured over the forty-five seconds after the dependency comes back, which is the only
        // stretch the two breakers can differ over. The numbers are 8,250 and 3,002.
        Assert.True(
            ramped > 2 * cliffed,
            $"a ramped breaker served {ramped} calls after the outage and a cliffed one served {cliffed}");
    }

    /// <summary>
    ///     Sixty seconds against a dependency whose success is a function of the load offered to it, on
    ///     a fake clock, with no randomness anywhere: same inputs, same number, every run.
    /// </summary>
    /// <param name="recovery">How the breaker hands the traffic back, or null for the cliff.</param>
    /// <returns>How many of the calls offered after the outage were served.</returns>
    /// <remarks>
    ///     <para>
    ///         The dependency serves <c>capacity</c> calls per 100 ms step comfortably, serves up to
    ///         three times that slowly, and fails the rest. Capacity warms towards <c>Full</c> over ten
    ///         seconds of being used, at a quarter of that rate while nothing is being sent to it - some
    ///         warmth comes back on its own and most of it does not - and is knocked back whenever it is
    ///         handed more than twice what it can serve. Latency is inversely proportional to capacity,
    ///         so a cold dependency is a slow one, which is the signal the ramp's evidence half reads.
    ///     </para>
    ///     <para>
    ///         The partial idle warming is what keeps the cliffed arm from being a straw man: without it
    ///         a cliffed breaker never recovers at all inside the minute, which is a stronger claim than
    ///         this model can support. With it, the cliff still loses - it spends its first two breaks
    ///         probing a dependency too cold to answer fast, and by the time one of its probes lands the
    ///         ramped breaker has been serving for twenty seconds.
    ///     </para>
    ///     <para>
    ///         The healthy prelude is not decoration: it is how the breaker learns what normal latency
    ///         is, exactly as a process warms up before its traffic arrives. Without it there is no
    ///         baseline and the ramp has only its clock.
    ///     </para>
    /// </remarks>
    private static int Simulate(Recovery? recovery)
    {
        const int Offered = 20;         // calls per 100 ms step - 200 a second
        const int Full = 20;            // what the dependency serves per step when it is warm
        const double WarmPerStep = 0.2; // cold to full in ten seconds
        const double OverloadPenalty = 1.0;

        var time = new FakeTimeProvider();
        var step = TimeSpan.FromMilliseconds(100);
        var fast = TimeSpan.FromMilliseconds(10);

        var breaker = new Breaker(new BreakerSettings
        {
            Time = time,
            BreakDuration = TimeSpan.FromSeconds(5),
            BreakJitter = Jitter.None,
            Recovery = recovery,
        });

        // The healthy prelude, and the baseline it teaches the breaker.
        for (var warm = 0; warm < 30; warm++)
        {
            Offer(breaker, 2, VerdictKind.Ok, fast);
            time.Advance(step);
        }

        var served = 0;
        var capacity = (double)Full;

        for (var tick = 0; tick < 600; tick++)
        {
            // Five seconds of hard outage in the middle of the minute, then a dependency that is back
            // and cold.
            var down = tick is >= 100 and < 150;

            if (down)
                capacity = 0;
            else if (tick == 150)
                capacity = 1;

            var room = (int)capacity;
            var admitted = 0;

            for (var call = 0; call < Offered; call++)
            {
                if (!breaker.TryEnter(out _, out _))
                    continue;

                admitted++;

                // A cold dependency is a slow one: at a twentieth of full capacity it answers twenty
                // times slower than normal, and a queue on top of that multiplies it again.
                var cap = Math.Max(capacity, 1);
                var cold = fast * (Full / cap);

                if (admitted <= cap)
                {
                    Serve(cold);
                }
                else if (admitted <= 3 * cap)
                {
                    Serve(cold * (admitted / cap));
                }
                else
                {
                    breaker.Record(VerdictKind.Transient, fast);
                }

                void Serve(TimeSpan latency)
                {
                    if (tick >= 150)
                        served++;

                    breaker.Record(VerdictKind.Ok, latency);
                }
            }

            if (!down)
            {
                capacity = admitted > 2 * Math.Max(room, 1) ? Math.Max(1, capacity - OverloadPenalty)
                    : admitted == 0 ? Math.Min(Full, capacity + (WarmPerStep / 4))
                    : Math.Min(Full, capacity + WarmPerStep);
            }

            time.Advance(step);
        }

        return served;
    }
}
