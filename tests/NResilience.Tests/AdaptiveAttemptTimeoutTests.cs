using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The adaptive per-attempt ceiling: <c>Resilience.AttemptCeiling</c>, which expresses the attempt timeout
///     as a multiple of measured latency instead of a constant an operator has to guess.
///     <para>
///         One property is what the design turns on, and it is the one the naive version of this feature
///         does not have. The measured term may only <b>lower</b> the ceiling. A high quantile is
///         contaminated by a brownout - that is unavoidable, and for <c>Hedge</c> it is the feature - so
///         an unclamped version would stretch its own timeout during exactly the incident it was supposed
///         to tighten for. The clamp turns that failure into "reverts to the configured constant", which
///         is a worst case worth having.
///     </para>
/// </summary>
public sealed class AdaptiveAttemptTimeoutTests
{
    /// <summary>Long enough that no test rolls a slice and loses the samples it just recorded.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(100);

    /// <summary>The configured ceiling every test clamps against. Far above anything measured.</summary>
    private static readonly TimeSpan Configured = TimeSpan.FromSeconds(30);

    // ---- The cold-start rule ----

    /// <summary>
    ///     A cold process does not guess a ceiling. Below <c>MinimumSamples</c> there is no measured
    ///     term and the attempt gets <see cref="Resilience.AttemptTimeout" />, exactly as it would
    ///     without <c>AttemptCeiling</c> configured.
    /// </summary>
    [Fact]
    public async Task No_ceiling_is_measured_before_the_estimate_has_enough_samples()
    {
        var time = new FakeTimeProvider();
        var policy = Adaptive(time, out var events);

        await WarmAsync(policy, time, Fast, 19);

        Assert.Null(policy.Measured.AttemptCeiling);
        Assert.False(events.Contains(CallEventKind.AttemptTimeoutAdapted));
    }

    /// <summary>One more sample crosses the minimum, and the ceiling appears without anyone naming a millisecond.</summary>
    [Fact]
    public async Task The_ceiling_appears_once_the_estimate_has_enough_samples()
    {
        var time = new FakeTimeProvider();
        var policy = Adaptive(time, out var events);

        await WarmAsync(policy, time, Fast, 20);

        var measured = policy.Measured.AttemptCeiling;

        Assert.NotNull(measured);

        // Three times a p95 that is 100 ms, give or take the estimator's bucket width - which rounds
        // up, never down, so the ceiling errs toward the caller rather than against them.
        Assert.InRange(measured.Value, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(340));

        // Reported once, when it moved, rather than once per attempt.
        await WarmAsync(policy, time, Fast, 20);
        Assert.Equal(1, events.CountOf(CallEventKind.AttemptTimeoutAdapted));
    }

    // ---- The feature ----

    /// <summary>
    ///     The point of the whole thing. An attempt that runs far past what this dependency normally
    ///     needs is cancelled at a multiple of the measurement, not at the 30-second constant - so the
    ///     retry that was going to succeed starts a hundred times sooner.
    /// </summary>
    [Fact]
    public async Task An_attempt_is_cancelled_at_the_measured_ceiling_rather_than_the_configured_one()
    {
        var time = new FakeTimeProvider();
        var policy = Adaptive(time, out _);

        await WarmAsync(policy, time, Fast, 40);

        var result = await HangAsync(policy, time, TimeSpan.FromMilliseconds(400));

        Assert.False(result.IsSuccess);
        var timeout = Assert.IsType<AttemptTimeoutException>(result.Exception);

        // The ceiling that fired is the measured one, and it is on the exception where a caller can
        // read it. Well under the configured 30 s, which is the claim.
        Assert.InRange(timeout.Timeout, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(340));
    }

    /// <summary>
    ///     The portability claim, stated as a test: one configuration, two dependencies three orders of
    ///     magnitude apart, a correct ceiling for both. No constant can do this.
    /// </summary>
    [Fact]
    public async Task One_multiple_is_correct_for_dependencies_orders_of_magnitude_apart()
    {
        var time = new FakeTimeProvider();

        // The floor is out of the way on purpose: this test is about the multiple porting, and the
        // default 50 ms floor would be what the quick dependency's ceiling came from instead.
        var noFloor = AttemptCeiling.Above() with { Window = Window, Floor = TimeSpan.FromTicks(1) };

        var quick = Adaptive(time, out _) with { AttemptCeiling = noFloor };
        var slow = Adaptive(time, out _) with { AttemptCeiling = noFloor };

        await WarmAsync(quick, time, TimeSpan.FromMilliseconds(2), 40);
        await WarmAsync(slow, time, TimeSpan.FromSeconds(2), 40);

        // Same AttemptCeiling.Above(3) on both, and the two ceilings are three orders of magnitude
        // apart because the dependencies are.
        Assert.InRange(quick.Measured.AttemptCeiling!.Value, TimeSpan.FromMilliseconds(6), TimeSpan.FromMilliseconds(7));
        Assert.InRange(slow.Measured.AttemptCeiling!.Value, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(6.8));
    }

    // ---- The clamp, which is the safety argument ----

    /// <summary>
    ///     The property the design turns on. A dependency slow enough that three times its p95 exceeds
    ///     the configured ceiling gets the configured ceiling - the measured term cannot lift it, so a
    ///     brownout converges on today's behaviour instead of on a longer timeout.
    /// </summary>
    [Fact]
    public async Task A_measured_ceiling_above_the_configured_one_is_clamped_to_it()
    {
        var time = new FakeTimeProvider();
        var policy = Adaptive(time, out var events);

        // A p95 of 20 s. Three times that is a minute, and the configured ceiling is 30 seconds.
        await WarmAsync(policy, time, TimeSpan.FromSeconds(20), 40);

        Assert.True(policy.Measured.AttemptCeiling > Configured);

        var result = await HangAsync(policy, time, TimeSpan.FromSeconds(31));

        var timeout = Assert.IsType<AttemptTimeoutException>(result.Exception);
        Assert.Equal(Configured, timeout.Timeout);

        // And nothing is reported, because nothing was adapted. The silence is the signal that the
        // dependency has slowed past the point where measuring it buys anything.
        Assert.False(events.Contains(CallEventKind.AttemptTimeoutAdapted));
    }

    /// <summary>
    ///     A policy that set no constant ceiling and asked for a measured one gets the measured one.
    ///     Passthrough has to be off for that to be reachable at all.
    /// </summary>
    [Fact]
    public async Task A_measured_ceiling_bounds_a_policy_whose_AttemptTimeout_is_infinite()
    {
        var time = new FakeTimeProvider();

        var policy = Adaptive(time, out _) with { AttemptTimeout = Timeout.InfiniteTimeSpan };

        await WarmAsync(policy, time, Fast, 40);

        var result = await HangAsync(policy, time, TimeSpan.FromMilliseconds(400));

        Assert.IsType<AttemptTimeoutException>(result.Exception);
    }

    // ---- Self-correction ----

    /// <summary>
    ///     Only successful attempts are sampled, which is what makes the feature self-correcting: a
    ///     ceiling tight enough to cancel calls that would have succeeded starves its own estimator and
    ///     the policy reverts to the configured constant rather than tightening further.
    /// </summary>
    [Fact]
    public async Task Timed_out_attempts_do_not_feed_the_estimate()
    {
        var time = new FakeTimeProvider();
        var policy = Adaptive(time, out _);

        await WarmAsync(policy, time, Fast, 20);
        var before = policy.Measured.AttemptCeiling;

        // Ten calls that time out at the measured ceiling. Were failures sampled, the ceiling would
        // now be climbing on the evidence of the timeouts it produced.
        for (var i = 0; i < 10; i++)
        {
            await HangAsync(policy, time, TimeSpan.FromMilliseconds(400));
        }

        Assert.Equal(before, policy.Measured.AttemptCeiling);
    }

    /// <summary>
    ///     The estimate is per policy <i>instance</i>, so two policies against two dependencies do not
    ///     pool their latency - the same scoping the hedge threshold has, and for the same reason.
    /// </summary>
    [Fact]
    public async Task The_estimate_is_private_to_the_policy_instance()
    {
        var time = new FakeTimeProvider();

        var one = Adaptive(time, out _);
        var other = Adaptive(time, out _);

        await WarmAsync(one, time, Fast, 40);

        Assert.NotNull(one.Measured.AttemptCeiling);
        Assert.Null(other.Measured.AttemptCeiling);
    }

    // ---- The floor ----

    /// <summary>
    ///     A dependency whose p95 is microseconds would otherwise have every attempt cancelled in
    ///     microseconds, so one scheduling hiccup becomes a failed call. The floor is the "do not
    ///     bother" line, and it is the mirror of <see cref="Hedge.MinimumDelay" />.
    /// </summary>
    [Fact]
    public async Task The_floor_keeps_a_very_fast_dependency_from_cancelling_itself()
    {
        var time = new FakeTimeProvider();

        var policy = Adaptive(time, out _) with
        {
            AttemptCeiling = AttemptCeiling.Above() with { Window = Window, Floor = TimeSpan.FromMilliseconds(50) },
        };

        await WarmAsync(policy, time, TimeSpan.FromMicroseconds(300), 40);

        // Three times a p95 of 300 microseconds is about a millisecond. The floor is what the attempt
        // actually gets, and it is exactly the floor rather than anything derived from it.
        Assert.Equal(TimeSpan.FromMilliseconds(50), policy.Measured.AttemptCeiling);

        // Twenty milliseconds is far past three times the p95 and still under the floor, so an attempt
        // that takes that long is untouched.
        var result = await policy.TryRunAsync(_ =>
        {
            time.Advance(TimeSpan.FromMilliseconds(20));
            return Task.FromResult(1);
        });

        Assert.True(result.IsSuccess);
    }

    // ---- Composition ----

    /// <summary>
    ///     The deadline still wins when it is the tighter of the two, and the stop reason still says so.
    ///     This is the case the fourth term in the ceiling arithmetic exists to keep honest: with a
    ///     measured ceiling in play, "the effective timeout is not AttemptTimeout" no longer means "the
    ///     deadline supplied it".
    /// </summary>
    [Fact]
    public async Task The_deadline_still_bounds_the_attempt_and_is_still_reported_as_the_reason()
    {
        var time = new FakeTimeProvider();

        // Three attempts, so the deadline check in Decide is reachable at all - a single-attempt policy
        // reports AttemptsExhausted whatever stopped it.
        var policy = Adaptive(time, out _) with { Attempts = 3, Deadline = TimeSpan.FromMilliseconds(150) };

        await WarmAsync(policy, time, Fast, 40);

        // The measured ceiling is about 300 ms and the deadline is 150 ms, so the deadline supplies the
        // ceiling - and when it fires, the call has to stop rather than spend its two remaining
        // attempts on a budget that is gone. That is the fact `deadlineCeiling` carries, and getting it
        // wrong is what a measured ceiling would have broken: `effective != AttemptTimeout` is true
        // here for two different reasons and only one of them is the deadline.
        var result = await HangAsync(policy, time, TimeSpan.FromMilliseconds(200));

        Assert.False(result.IsSuccess);
        Assert.Equal(StopReason.DeadlineExceeded, result.Reason);
        Assert.Single(result.Attempts);
    }

    /// <summary>
    ///     When hedging is configured, the ceiling is measured from at least the hedge's own quantile.
    ///     A ceiling read from a lower quantile would otherwise cancel the first leg at the moment the
    ///     second was due to start, and the caller would have bought a feature that never fires.
    /// </summary>
    /// <remarks>
    ///     Only reachable when <c>AttemptCeiling.Quantile</c> is below <c>Hedge.Quantile</c> - and only then
    ///     against a distribution with a tail, because both estimates are read from the same traffic and
    ///     a dependency with no spread has one latency at every quantile. Hence the bimodal warm-up:
    ///     without it, <c>Multiple x p95</c> already clears the hedge threshold and this test asserts
    ///     nothing.
    /// </remarks>
    [Fact]
    public async Task The_ceiling_is_measured_from_at_least_the_hedge_quantile()
    {
        var time = new FakeTimeProvider();

        var policy = Adaptive(time, out _) with
        {
            Attempts = 2,
            Hedge = Hedge.At() with { Window = Window, MinimumDelay = TimeSpan.Zero },

            // Read from the median, which is the whole point: the p50 of this dependency is 10 ms and
            // its p95 is 500 ms, so twice the median is 20 ms and a hedge at the p95 could never arm.
            AttemptCeiling = AttemptCeiling.Above(2) with { Window = Window, Quantile = 0.5, Floor = TimeSpan.FromTicks(1) },
        };

        await BimodalWarmAsync(policy, time);

        // Twice the median would be about 20 ms, and that is what an unfloored ceiling would be. What
        // the attempt actually gets is twice the *hedge's* quantile, which is two orders of magnitude
        // larger - so the first leg lives long enough for the hedge to arm.
        var measured = policy.Measured.AttemptCeiling!.Value;

        Assert.InRange(measured, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.1));
    }

    /// <summary>
    ///     The same configuration end to end: the hedge does fire, which is only possible because the
    ///     ceiling was floored above the threshold it arms at.
    /// </summary>
    [Fact]
    public async Task A_hedge_still_fires_under_a_ceiling_measured_from_a_lower_quantile()
    {
        var time = new FakeTimeProvider();

        var policy = Adaptive(time, out var events) with
        {
            Attempts = 2,
            Hedge = Hedge.At() with { Window = Window, MinimumDelay = TimeSpan.Zero },
            AttemptCeiling = AttemptCeiling.Above(2) with { Window = Window, Quantile = 0.5, Floor = TimeSpan.FromTicks(1) },
        };

        await BimodalWarmAsync(policy, time);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        // The first leg parks; a hedge is the only thing that can answer this call.
        var call = policy.TryRunAsync(async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                return 1;
            }

            return 2;
        }).AsTask();

        // Pumped in steps rather than advanced once, because the hedge timer is armed after the first
        // leg's body has started - so a single advance can land before there is a timer to fire.
        for (var i = 0; i < 20 && !call.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(50));

            // A real yield: the loop's continuation runs on the thread pool, which the fake clock
            // cannot advance.
            await Task.Delay(1);
        }

        gate.TrySetResult();

        var result = await call;

        Assert.True(result.IsSuccess);
        Assert.True(events.Contains(CallEventKind.HedgeStarted));

        // The hedge answered, and it could only have started if the first leg outlived the p95 the
        // hedge arms at - which an unfloored ceiling measured from the p50 would not have allowed.
        Assert.Equal(2, result.Value);
    }

    // ---- Configuration ----

    [Fact]
    public void A_multiple_of_one_or_less_is_refused()
    {
        var problems = Problems(TestPolicy.Instant with { AttemptCeiling = AttemptCeiling.Above(1) });

        Assert.Contains(problems, p => p.Contains("AttemptCeiling.Multiple", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(0.999)]
    public void A_quantile_outside_the_tail_is_refused(double quantile)
    {
        var problems = Problems(TestPolicy.Instant with { AttemptCeiling = AttemptCeiling.Above() with { Quantile = quantile } });

        Assert.Contains(problems, p => p.Contains("AttemptCeiling.Quantile", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A floor at or above the configured ceiling makes the measured term unreachable. Refused
    ///     rather than ignored, because silently doing nothing is how a caller ends up believing a
    ///     ceiling is being measured when it is not.
    /// </summary>
    [Fact]
    public void A_floor_at_or_above_the_configured_ceiling_is_refused()
    {
        var policy = TestPolicy.Instant with
        {
            AttemptTimeout = TimeSpan.FromSeconds(1),
            AttemptCeiling = AttemptCeiling.Above() with { Floor = TimeSpan.FromSeconds(1) },
        };

        Assert.Contains(Problems(policy), p => p.Contains("AttemptCeiling.Floor", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A measured ceiling is a bound, so a policy that has one is not passthrough even when every
    ///     constant says otherwise.
    /// </summary>
    /// <remarks>
    ///     <c>Adaptive</c> has to be turned back on alongside it, because <see cref="Resilience.None" />
    ///     says "measure nothing" and this policy is asking for a measurement. That is the same ceremony
    ///     the preset already demands of every other bound - deriving one from passthrough means turning
    ///     it back on by name - and the alternative was a preset that said "no bounds" while quietly
    ///     accepting one.
    /// </remarks>
    [Fact]
    public async Task A_policy_with_a_measured_ceiling_is_not_passthrough()
    {
        var time = new FakeTimeProvider();

        var policy = Resilience.None.WithClock(time) with
        {
            Adaptive = true,
            AttemptCeiling = AttemptCeiling.Above() with { Window = Window },
        };

        await WarmAsync(policy, time, Fast, 40);

        // Passthrough hands back the callback's own task and would never have recorded a sample.
        Assert.NotNull(policy.Measured.AttemptCeiling);
    }

    /// <summary>Value equality is over the effective configuration, so a named default equals an omitted one.</summary>
    [Fact]
    public void Naming_a_default_equals_leaving_it_alone()
    {
        Assert.Equal(AttemptCeiling.Above(), AttemptCeiling.Above() with { Quantile = 0.95 });
        Assert.Equal(AttemptCeiling.Above().GetHashCode(), (AttemptCeiling.Above() with { MinimumSamples = 20 }).GetHashCode());
        Assert.NotEqual(AttemptCeiling.Above(), AttemptCeiling.Above(4));
    }

    /// <summary>
    ///     <c>default(AttemptCeiling)</c> compiles, because <c>policy with { AttemptCeiling = default }</c>
    ///     does. Every property but the multiple has to read as its default rather than as zero.
    /// </summary>
    [Fact]
    public void The_default_instance_reads_as_the_defaults()
    {
        var unconstructed = default(AttemptCeiling);

        Assert.Equal(0.95, unconstructed.Quantile);
        Assert.Equal(TimeSpan.FromMinutes(5), unconstructed.Window);
        Assert.Equal(20, unconstructed.MinimumSamples);
        Assert.Equal(TimeSpan.FromMilliseconds(50), unconstructed.Floor);

        // And the multiple is the one thing it cannot supply, so it is the one thing Validate refuses.
        Assert.Contains(
            Problems(TestPolicy.Instant with { AttemptCeiling = unconstructed }),
            p => p.Contains("AttemptCeiling.Multiple", StringComparison.Ordinal));
    }

    [Fact]
    public void It_prints_its_effective_configuration()
    {
        var text = AttemptCeiling.Above().ToString();

        Assert.Contains("3x p95", text, StringComparison.Ordinal);
        Assert.Contains("300s", text, StringComparison.Ordinal);
        Assert.Contains("min 20 samples", text, StringComparison.Ordinal);
        Assert.Contains("floor 50ms", text, StringComparison.Ordinal);
    }

    // ---- Harness ----

    private static Resilience Adaptive(FakeTimeProvider time, out EventRecorder events, Func<Resilience, Resilience>? configure = null)
    {
        var recorder = new EventRecorder();
        events = recorder;

        var policy = TestPolicy.WithClock(time) with
        {
            Name = "api",
            Attempts = 1,
            AttemptTimeout = Configured,
            AttemptCeiling = AttemptCeiling.Above() with { Window = Window },
            OnEvent = recorder.Record,
        };

        return configure is null ? policy : configure(policy);
    }

    private static IReadOnlyList<string> Problems(Resilience policy) =>
        Assert.Throws<ResilienceConfigurationException>(policy.Validate).Problems;

    /// <summary>
    ///     Records <paramref name="times" /> samples of <paramref name="duration" /> into the policy's
    ///     latency estimate.
    /// </summary>
    /// <remarks>
    ///     The callback advances the clock and completes synchronously, so the executor never suspends
    ///     and no ceiling can fire however long the "attempt" claims to have taken. That is what makes
    ///     this a way to shape the estimate rather than a way to exercise the timeout.
    /// </remarks>
    private static async Task WarmAsync(Resilience policy, FakeTimeProvider time, TimeSpan duration, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await policy.RunAsync(_ =>
            {
                time.Advance(duration);
                return Task.FromResult(1);
            });
        }
    }

    /// <summary>
    ///     Shapes an estimate with a real tail: a p50 of 10 ms and a p95 of 500 ms, so the quantiles a
    ///     ceiling and a hedge read are two orders of magnitude apart.
    /// </summary>
    private static async Task BimodalWarmAsync(Resilience policy, FakeTimeProvider time)
    {
        for (var i = 0; i < 10; i++)
        {
            await WarmAsync(policy, time, TimeSpan.FromMilliseconds(10), 9);
            await WarmAsync(policy, time, TimeSpan.FromMilliseconds(500), 1);
        }
    }

    /// <summary>
    ///     Runs one call whose attempt parks, and moves the clock from outside so the ceiling's timer
    ///     can fire.
    /// </summary>
    /// <remarks>
    ///     The gate is released after the advance rather than left set, so a ceiling that did <i>not</i>
    ///     fire lets the attempt finish normally and the caller's assertion fails - instead of the test
    ///     run hanging on a parked task, which is what a removed clamp would otherwise look like.
    ///     Cancellation lands synchronously inside <c>Advance</c>, so releasing the gate afterwards
    ///     cannot rescue an attempt the ceiling already cancelled.
    /// </remarks>
    private static async Task<CallResult<int>> HangAsync(Resilience policy, FakeTimeProvider time, TimeSpan advance)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var call = policy.TryRunAsync(async ct =>
        {
            started.TrySetResult();
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);

            return 1;
        });

        await started.Task;

        // The attempt is parked and its ceiling is armed. Both are on the fake clock, so one advance is
        // what fires them.
        time.Advance(advance);

        // A real yield, because the cancelled attempt's continuation runs on the thread pool and the
        // fake clock cannot advance it there.
        await Task.Delay(1);

        gate.TrySetResult();

        return await call;
    }
}
