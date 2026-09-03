using Microsoft.Extensions.Time.Testing;
using NResilience.Internal;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The seventh gate: hedging stops paying for itself once hedges have stopped winning.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Hedge.SuppressAt" /> asks whether the dependency is healthy enough to be sent
///         extra load. This asks the other question - whether the extra load is buying anything - and a
///         dependency can answer them differently. One that is uniformly slow because it is overloaded
///         is failing neither often enough to suppress nor independently enough for a second leg to win,
///         so <see cref="HedgeSuppressionTests" />' gate lets every slow call hedge and none of those
///         hedges shortens anything.
///     </para>
///     <para>
///         The loop itself is exercised against <see cref="WinWindow" /> on a fake clock, because it is
///         a control loop and the thing worth asserting is the trajectory rather than one decision. The
///         end-to-end tests below then pin the wiring: that the executor consults it, that it counts the
///         right two things, and that a suppressed hedge says so.
///     </para>
/// </remarks>
public sealed class HedgeFeedbackTests
{
    /// <summary>Four rings, so a quarter of this is one decision point.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan Slice = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(10);

    // ---- Helpers ----

    /// <summary>
    ///     A loop that acts on a single hedge, so the end-to-end tests do not have to drive twenty races
    ///     through a fake clock to reach a decision.
    /// </summary>
    private static WinRate Feedback => WinRate.AtLeast(0.9) with
    {
        Window = Window,
        MinimumSamples = 1,
    };

    // ---- The loop ----

    /// <summary>
    ///     Nothing has happened, so nothing is held back. The cold-start rule: a process that has never
    ///     hedged hedges at the rate <see cref="Hedge.Quantile" /> asked for.
    /// </summary>
    [Fact]
    public void A_cold_loop_admits_every_hedge()
    {
        var window = New(out var time);

        for (var i = 0; i < 100; i++)
        {
            Assert.True(window.Admits());
            window.Started();
            time.Advance(Slice);
        }

        Assert.Equal(1, window.Allowance);
    }

    /// <summary>
    ///     A dependency hedging actually helps is left alone. This is the case the feature must not
    ///     regress, and it is the reason the floor is a fraction rather than a majority: a hedge that
    ///     loses its race is the ordinary outcome even when hedging is working.
    /// </summary>
    [Fact]
    public void Hedges_that_keep_winning_keep_their_full_rate()
    {
        var window = New(out var time);

        // A third of them win, against a floor of a fifth.
        var (admitted, allowances) = Simulate(window, time, 8, 100, 33);

        Assert.All(allowances, allowance => Assert.Equal(1, allowance));
        Assert.Equal(800, admitted);
    }

    /// <summary>
    ///     The feature. A dependency whose second leg is exactly as slow as its first wins nothing, the
    ///     allowance halves at every decision point, and five of them - a window and a quarter - take it
    ///     from the full hedge rate to a twentieth of it, where <see cref="WinRate.MinimumAllowance" />
    ///     holds it.
    /// </summary>
    [Fact]
    public void Hedges_that_never_win_retreat_to_the_floor()
    {
        var window = New(out var time);

        var (admitted, allowances) = Simulate(window, time, 8, 100, 0);

        Assert.Equal([1, 0.5, 0.25, 0.125, 0.0625, 0.05, 0.05, 0.05], allowances);

        // 800 hedges were offered and 208 started, and 100 of those were the first slice - before the
        // loop had any evidence at all. What the feature bounds is the load, not the tail: the retreat
        // is what the dependency stops receiving.
        Assert.Equal(208, admitted);
    }

    /// <summary>
    ///     The retreat is multiplicative and the return is additive, which is the asymmetry the whole
    ///     design turns on: the cost of hedging too much is borne by the dependency and the cost of
    ///     hedging too little is borne by this process. So one bad decision point costs half the
    ///     allowance and two good ones win back a quarter each.
    /// </summary>
    [Fact]
    public void The_retreat_halves_and_the_return_adds()
    {
        var window = New(out var time);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(window.Admits());
            window.Started();
        }

        // Three halvings from one losing slice, because the window is four rings deep and that slice
        // is still in it at each of the next three decision points. Evidence that has not rolled out
        // yet is still evidence.
        Assert.Equal(new[] { 0.5, 0.25, 0.125 }, Walk(window, time, 3));

        // Then it rolls out, the window has nothing left to judge, and the clock hands the allowance
        // back a quarter at a time.
        Assert.Equal(new[] { 0.375, 0.625, 0.875, 1d }, Walk(window, time, 4));
    }

    /// <summary>
    ///     The claim the feature is for, and the failure mode it has to avoid. A dependency hedging
    ///     cannot help is retreated from within a window; when the same dependency starts answering
    ///     independently again - one slow shard rather than a saturated fleet - the allowance comes
    ///     back, without anybody redeploying a number.
    ///     <para>
    ///         The return is what makes this a loop rather than a latch. A retreat starves the window of
    ///         evidence by construction, so an evidence-driven return would pin the allowance at the
    ///         floor for the life of the process and hedging would never be tried again.
    ///     </para>
    /// </summary>
    [Fact]
    public void The_allowance_follows_a_dependency_whose_hedges_stop_and_start_winning()
    {
        var window = New(out var time);

        // Hedging works: a third of the hedges win, against a floor of a fifth.
        var working = Simulate(window, time, 8, 100, 33);

        Assert.All(working.Allowances, allowance => Assert.Equal(1, allowance));

        // The dependency saturates. Every leg is now as slow as every other, so nothing wins. The two
        // slices at full rate are the lag: the window still holds the rings in which hedging worked.
        var saturated = Simulate(window, time, 8, 100, 0);

        Assert.Equal([1, 1, 0.5, 0.25, 0.125, 0.0625, 0.05, 0.05], saturated.Allowances);

        // It recovers, and the trickle the floor leaves is what notices: five hedges a slice with a
        // third of them winning clears the floor, and then the return adds a quarter at a time.
        var recovered = Simulate(window, time, 8, 100, 33);

        Assert.Equal([0.05, 0.3, 0.55, 0.8, 1, 1, 1, 1], recovered.Allowances);

        Assert.True(
            recovered.Admitted > saturated.Admitted,
            $"recovered {recovered.Admitted} should exceed the {saturated.Admitted} admitted while saturated");
    }

    /// <summary>
    ///     Deficit accounting rather than a coin flip, so the admitted fraction is evenly spaced and a
    ///     simulation of this loop runs the same way twice. At half the rate every other hedge starts.
    /// </summary>
    [Fact]
    public void The_admitted_fraction_is_spaced_rather_than_sampled()
    {
        var window = New(out var time);

        Simulate(window, time, 1, 100, 0);
        Assert.Equal(0.5, window.Allowance);

        // Inside one slice, so no further decision is taken and the allowance stays where it is.
        Assert.False(window.Admits());
        Assert.True(window.Admits());
        Assert.False(window.Admits());
        Assert.True(window.Admits());
    }

    /// <summary>
    ///     A hedge that never started is not evidence about whether hedging works. The retry budget can
    ///     refuse one after the loop has admitted it, and the executor counts the start separately for
    ///     exactly that reason - so a policy at its budget limit does not teach the loop that hedging
    ///     has stopped winning.
    /// </summary>
    [Fact]
    public void An_admitted_hedge_that_never_starts_is_not_counted()
    {
        var window = New(out var time);

        // Admitted and then dropped, ten times over, which would be ten losses if Admits() counted.
        for (var i = 0; i < 10; i++)
        {
            Assert.True(window.Admits());
        }

        time.Advance(Slice);

        Assert.Equal(1, window.Allowance);
    }

    /// <summary>
    ///     A window with almost no hedges in it has no opinion worth acting on: a win rate over three
    ///     hedges is a coin flip, and retreating on one would turn hedging off for any policy whose
    ///     traffic is thin enough that <c>1 - Quantile</c> of it is a handful of calls a minute.
    /// </summary>
    [Fact]
    public void Too_few_hedges_to_judge_is_not_a_reason_to_retreat()
    {
        var window = New(out var time);

        // Nine hedges, none of them winning, against a minimum of ten.
        Simulate(window, time, 3, 3, 0);

        Assert.Equal(1, window.Allowance);
    }

    /// <summary>
    ///     <see cref="WinRate.MinimumAllowance" /> at zero is no floor at all rather than an off switch:
    ///     the retreat keeps halving, so hedging is suspended in all but name and the clock is still
    ///     what brings it back.
    /// </summary>
    [Fact]
    public void A_zero_floor_lets_the_retreat_run_past_the_default_one()
    {
        var window = New(out var time, WinRate.AtLeast() with { MinimumAllowance = 0, Window = Window });

        var (_, allowances) = Simulate(window, time, 8, 100, 0);

        // Past the 0.05 the default floor holds it at, and still never zero: a geometric retreat does
        // not arrive, it only stops mattering.
        Assert.Equal([1, 0.5, 0.25, 0.125, 0.0625, 0.03125, 0.015625, 0.0078125], allowances);

        // Four quiet slices and it is back at full rate, because nothing in the window says otherwise
        // any more. The return is on the clock for exactly this reason.
        time.Advance(Window);
        Assert.Equal(1, window.Allowance);
    }

    // ---- The wiring ----

    /// <summary>
    ///     End to end: one hedge that lost, one decision point, and the next call that gets slow enough
    ///     to hedge does not.
    /// </summary>
    [Fact]
    public async Task A_hedge_that_lost_holds_the_next_one_back()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out var events, Feedback);

        await WarmAsync(policy, time, 20);

        // The hedge starts and the original answers first, which is a loss.
        Assert.Equal(2, await LoseAsync(policy, time));
        Assert.Single(events.OfKind(CallEventKind.HedgeStarted));

        events.Clear();
        time.Advance(Slice);

        // One hedge, none won, a floor of 0.9: the allowance halves, and half of one hedge is none.
        var calls = await LoseAsync(policy, time);

        Assert.Single(events.OfKind(CallEventKind.HedgeSuppressed));
        Assert.False(events.Contains(CallEventKind.HedgeStarted));
        Assert.Equal(1, calls);
    }

    /// <summary>
    ///     The other side of the same line: a hedge that won is evidence for hedging, and the next slow
    ///     call is hedged exactly as it would have been without the loop configured.
    /// </summary>
    [Fact]
    public async Task A_hedge_that_won_leaves_the_next_one_alone()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out var events, Feedback);

        await WarmAsync(policy, time, 20);

        Assert.Equal(2, await WinAsync(policy, time));

        events.Clear();
        time.Advance(Slice);

        Assert.Equal(2, await WinAsync(policy, time));

        Assert.Single(events.OfKind(CallEventKind.HedgeStarted));
        Assert.False(events.Contains(CallEventKind.HedgeSuppressed));
    }

    /// <summary>
    ///     The same losing hedge without the loop configured. Nothing is held back, which is what makes
    ///     the test above about the feature rather than about the clock.
    /// </summary>
    [Fact]
    public async Task Without_the_loop_a_losing_hedge_changes_nothing()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out var events, null);

        await WarmAsync(policy, time, 20);

        await LoseAsync(policy, time);
        events.Clear();
        time.Advance(Slice);

        Assert.Equal(2, await LoseAsync(policy, time));
        Assert.Single(events.OfKind(CallEventKind.HedgeStarted));
        Assert.False(events.Contains(CallEventKind.HedgeSuppressed));
    }

    /// <summary>
    ///     A suppressed hedge carries the two numbers <see cref="CallEventKind.HedgeStarted" /> carries,
    ///     so the pair count against each other: the copy that would have started, and the latency
    ///     threshold that fired.
    /// </summary>
    [Fact]
    public async Task A_suppressed_hedge_reports_the_threshold_that_fired()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out var events, Feedback);

        await WarmAsync(policy, time, 20);
        await LoseAsync(policy, time);

        events.Clear();
        time.Advance(Slice);
        await LoseAsync(policy, time);

        var suppressed = Assert.Single(events.OfKind(CallEventKind.HedgeSuppressed));

        Assert.Equal(2, suppressed.AttemptNumber);
        Assert.NotNull(suppressed.Delay);
        Assert.True(suppressed.Delay >= Fast);
    }

    // ---- Configuration ----

    /// <summary>
    ///     A win rate is only meaningful strictly inside <c>(0, 1)</c>. Zero is feedback that can never
    ///     act, and one calls every window losing - a hedge that loses its race is the ordinary case.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void A_minimum_outside_the_unit_interval_is_refused(double minimum)
    {
        var problem = Refuse(WinRate.AtLeast(minimum));

        Assert.Contains(problem, p => p.Contains("WinRate.Minimum", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The losing configuration worth naming: an allowance that cannot fall is a feedback loop that
    ///     cannot act, and it would sit in a policy looking like it was doing something.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    public void An_allowance_that_cannot_retreat_is_refused(double allowance)
    {
        var problem = Refuse(WinRate.AtLeast() with { MinimumAllowance = allowance });

        Assert.Contains(problem, p => p.Contains("WinRate.MinimumAllowance", StringComparison.Ordinal));
    }

    [Fact]
    public void A_window_and_a_sample_count_have_to_be_positive()
    {
        Assert.Contains(
            Refuse(WinRate.AtLeast() with { Window = TimeSpan.Zero }),
            p => p.Contains("WinRate.Window", StringComparison.Ordinal));

        Assert.Contains(
            Refuse(WinRate.AtLeast() with { MinimumSamples = 0 }),
            p => p.Contains("WinRate.MinimumSamples", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The struct's own rule: naming a default explicitly is the same configuration as leaving it
    ///     alone, and a hedge carrying one is not equal to a hedge carrying none.
    /// </summary>
    [Fact]
    public void Naming_a_default_changes_nothing()
    {
        Assert.Equal(0.2, WinRate.AtLeast().Minimum);
        Assert.Equal(WinRate.AtLeast(), WinRate.AtLeast() with { MinimumSamples = 10 });
        Assert.NotEqual(WinRate.AtLeast(), WinRate.AtLeast() with { MinimumSamples = 11 });

        Assert.NotEqual(Hedge.At(), Hedge.At() with { WinRate = WinRate.AtLeast() });

        Assert.Equal(
            Hedge.At() with { WinRate = WinRate.AtLeast() },
            Hedge.At() with { WinRate = WinRate.AtLeast() with { Window = TimeSpan.FromMinutes(1) } });
    }

    private static WinWindow New(out FakeTimeProvider time, WinRate? feedback = null)
    {
        time = new FakeTimeProvider();

        return new WinWindow(feedback ?? WinRate.AtLeast() with { Window = Window }, time);
    }

    /// <summary>
    ///     Offers <paramref name="hedgesPerSlice" /> hedges at each of <paramref name="slices" />
    ///     decision points, of which <paramref name="winsPerSlice" /> would win.
    /// </summary>
    /// <returns>
    ///     How many hedges the loop admitted in total, and the allowance it was running at in each
    ///     slice - which is the trajectory the tests assert.
    /// </returns>
    /// <remarks>
    ///     The wins are attributed to the hedges that were actually admitted, so a retreat reduces the
    ///     wins along with the load. That is the honest simulation: a dependency that would have
    ///     answered from a second leg does not answer from a leg that was never started.
    /// </remarks>
    private static (int Admitted, double[] Allowances) Simulate(
        WinWindow window,
        FakeTimeProvider time,
        int slices,
        int hedgesPerSlice,
        int winsPerSlice)
    {
        var admitted = 0;
        var allowances = new double[slices];

        for (var slice = 0; slice < slices; slice++)
        {
            allowances[slice] = window.Allowance;
            var winsLeft = winsPerSlice;

            for (var i = 0; i < hedgesPerSlice; i++)
            {
                if (!window.Admits())
                    continue;

                admitted++;
                window.Started();

                // The wins are the first ones of the slice. Which of them wins is not something the
                // loop reads, and spreading them out would only make the arithmetic harder to follow.
                if (winsLeft-- > 0)
                    window.Won();
            }

            time.Advance(Slice);
        }

        return (admitted, allowances);
    }

    /// <summary>The allowance at each of the next <paramref name="slices" /> decision points.</summary>
    private static double[] Walk(WinWindow window, FakeTimeProvider time, int slices)
    {
        var walk = new double[slices];

        for (var i = 0; i < slices; i++)
        {
            time.Advance(Slice);
            walk[i] = window.Allowance;
        }

        return walk;
    }

    private static List<string> Refuse(WinRate feedback)
    {
        var policy = Resilience.Default with { Hedge = Hedge.At() with { WinRate = feedback } };

        return Assert.Throws<ResilienceConfigurationException>(policy.Validate).Problems.ToList();
    }

    /// <summary>A hedging policy with a listener on the same instance. No breaker: this gate needs none.</summary>
    private static Resilience Hedging(FakeTimeProvider time, out EventRecorder events, WinRate? feedback)
    {
        var recorder = new EventRecorder();
        events = recorder;

        return TestPolicy.Instant with
        {
            Name = "api",
            Time = time,
            Hedge = Hedge.At() with { Window = TimeSpan.FromHours(1), WinRate = feedback },
            OnEvent = recorder.Record,
        };
    }

    /// <summary>Records fast successes into the latency estimate, exactly as <see cref="HedgeTests" /> does.</summary>
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
    ///     One call whose hedge starts and then loses: the first leg is released as soon as a second one
    ///     is running, so the answer comes from the attempt the hedge was racing.
    /// </summary>
    private static Task<int> LoseAsync(Resilience policy, FakeTimeProvider time) => RaceAsync(policy, time, true);

    /// <summary>One call whose hedge starts and wins, because the first leg never comes back on its own.</summary>
    private static Task<int> WinAsync(Resilience policy, FakeTimeProvider time) => RaceAsync(policy, time, false);

    /// <summary>
    ///     Runs one call whose first attempt blocks, moving the clock from outside so an armed hedge
    ///     timer can fire, and decides which leg answers.
    /// </summary>
    /// <returns>How many times the callback was invoked.</returns>
    private static async Task<int> RaceAsync(Resilience policy, FakeTimeProvider time, bool firstWins)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var call = policy.TryRunAsync(async ct =>
        {
            var number = Interlocked.Increment(ref calls);

            if (number == 1)
            {
                await gate.Task.WaitAsync(ct);

                return 1;
            }

            // The hedge. It answers at once when it is meant to win, and waits to be cancelled when
            // the first leg is.
            if (firstWins)
                await Task.Delay(Timeout.Infinite, ct);

            return number;
        }).AsTask();

        for (var i = 0; i < 20 && !call.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));

            // A real yield: the loop's continuation runs on the thread pool, where the fake clock
            // cannot advance it.
            await Task.Delay(1);

            // The hedge is running, so releasing the first leg now makes it the winner.
            if (firstWins && Volatile.Read(ref calls) > 1)
                gate.TrySetResult();
        }

        gate.TrySetResult();
        await call;

        return calls;
    }
}
