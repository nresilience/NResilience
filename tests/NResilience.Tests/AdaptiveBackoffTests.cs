using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The adaptive backoff base: <c>Backoff.MeasuredBase</c>, which expresses the first retry delay as a
///     multiple of what a normal call to this dependency takes instead of a constant an operator has to
///     guess.
///     <para>
///         Two properties are what make it defensible, and both are asserted here. The measurement moves
///         only the <i>transient</i> base, because a rate limiter's refill interval is not visible in how
///         fast it said no. And it is clamped to <c>Spread</c> either side of the configured base, because
///         unlike the attempt ceiling this estimate is not tighten-only: a base that collapses is a retry
///         storm, and one that grows spends the caller's deadline on waiting rather than on attempts.
///     </para>
/// </summary>
public sealed class AdaptiveBackoffTests
{
    /// <summary>Long enough that no test rolls a slice and loses the samples it just recorded.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>The configured base every test clamps around. The shipped default.</summary>
    private static readonly TimeSpan Configured = TimeSpan.FromMilliseconds(100);

    /// <summary>What a normal call to the dependency under test takes.</summary>
    private static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(200);

    // ---- The cold-start rule ----

    /// <summary>
    ///     A cold process does not guess a delay. Below <c>MinimumSamples</c> there is no measured base
    ///     and the retry waits the configured constant, exactly as it would without a measured base
    ///     configured.
    /// </summary>
    [Fact]
    public async Task No_base_is_measured_before_the_estimate_has_enough_samples()
    {
        var time = new FakeTimeProvider();
        var policy = Adaptive(time, out _);

        await WarmAsync(policy, time, Normal, 19);

        Assert.Null(policy.Measured.BackoffBase);
    }

    /// <summary>One more sample crosses the minimum, and the base appears without anyone naming a millisecond.</summary>
    [Fact]
    public async Task The_base_appears_once_the_estimate_has_enough_samples()
    {
        var time = new FakeTimeProvider();
        var policy = Adaptive(time, out _);

        await WarmAsync(policy, time, Normal, 20);

        var measured = policy.Measured.BackoffBase;

        Assert.NotNull(measured);

        // One times a median that is 200 ms, give or take the estimator's bucket width - which rounds
        // up, never down.
        Assert.InRange(measured.Value, Normal, TimeSpan.FromMilliseconds(230));
    }

    /// <summary>A policy that asked for no measurement reports none, and its curve is the constant it was written as.</summary>
    [Fact]
    public async Task A_policy_with_no_measured_base_reports_none()
    {
        var time = new FakeTimeProvider();
        var policy = Adaptive(time, out _) with { Backoff = Backoff.Exponential(Configured) };

        await WarmAsync(policy, time, Normal, 40);

        Assert.Null(policy.Measured.BackoffBase);
    }

    // ---- The feature ----

    /// <summary>
    ///     The point of the whole thing. Against a dependency whose normal call is 200 ms, the shipped
    ///     100 ms base is not backoff - the retry lands while the first attempt's work is very likely
    ///     still queued. The measured base waits for the dependency instead, and the factor, the jitter
    ///     and the cap are all applied on top of it unchanged.
    /// </summary>
    [Fact]
    public void The_measured_base_replaces_the_transient_constant_and_the_curve_is_otherwise_untouched()
    {
        var backoff = Backoff.Measured(1.0, Configured) with { Jitter = Jitter.None };

        Assert.Equal(Normal, Delay(backoff, Verdict.Transient, 2, Normal));

        // The growth factor still compounds from the measured base rather than from the constant.
        Assert.Equal(TimeSpan.FromMilliseconds(400), Delay(backoff, Verdict.Transient, 3, Normal));
        Assert.Equal(TimeSpan.FromMilliseconds(800), Delay(backoff, Verdict.Transient, 4, Normal));

        // And the cap is still the cap.
        Assert.Equal(TimeSpan.FromSeconds(30), Delay(backoff, Verdict.Transient, 30, Normal));
    }

    /// <summary>
    ///     The portability claim, stated as a test: one configuration, two dependencies three orders of
    ///     magnitude apart, a sensible first delay for both. No constant can do this.
    /// </summary>
    [Fact]
    public async Task One_multiple_is_correct_for_dependencies_orders_of_magnitude_apart()
    {
        var time = new FakeTimeProvider();

        // The clamp is opened up on purpose: this test is about the multiple porting, and the default
        // band around 100 ms is what both bases would otherwise come from.
        var wide = MeasuredBase.Times() with { Window = Window, Spread = 1000 };

        var quick = Adaptive(time, out _) with { Backoff = Backoff.Measured(1, Configured) with { MeasuredBase = wide } };
        var slow = Adaptive(time, out _) with { Backoff = Backoff.Measured(1, Configured) with { MeasuredBase = wide } };

        await WarmAsync(quick, time, TimeSpan.FromMilliseconds(2), 40);
        await WarmAsync(slow, time, TimeSpan.FromSeconds(2), 40);

        Assert.InRange(quick.Measured.BackoffBase!.Value, TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(2.3));
        Assert.InRange(slow.Measured.BackoffBase!.Value, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.3));
    }

    /// <summary>
    ///     End to end, through the executor: the retry the caller actually served waited the measured
    ///     base rather than the configured one, and the listener was told the base had moved.
    /// </summary>
    [Fact]
    public async Task A_retry_waits_the_measured_base_and_the_move_is_reported_once()
    {
        var time = new FakeTimeProvider();
        var policy = Adaptive(time, out var events) with { Attempts = 2 };

        await WarmAsync(policy, time, Normal, 40);

        var calls = 0;

        var call = policy.TryRunAsync(_ =>
        {
            if (++calls == 1)
                throw new IOException();

            return Task.FromResult(1);
        }).AsTask();

        await PumpAsync(time, call);

        var result = await call;

        Assert.True(result.IsSuccess);
        Assert.Equal(2, calls);

        // The delay the retry was told to wait is the measured base, not the 100 ms constant. Jitter is
        // off, so this is the curve itself rather than a draw from it.
        var retrying = Assert.Single(events.Events, e => e.Kind == CallEventKind.Retrying);
        Assert.InRange(retrying.Delay!.Value, Normal, TimeSpan.FromMilliseconds(230));

        // And the base itself is reported where a dashboard can read it, once, because it moved once.
        Assert.Equal(1, events.CountOf(CallEventKind.BackoffBaseAdapted));
        Assert.Equal(policy.Measured.BackoffBase, events.Events.Single(e => e.Kind == CallEventKind.BackoffBaseAdapted).Delay);
    }

    // ---- The clamp, which is the safety argument ----

    /// <summary>
    ///     A dependency slow enough that the measurement exceeds the band gets the top of the band. The
    ///     configured constant stays the anchor, so a brownout cannot spend an unbounded share of the
    ///     caller's deadline on waiting.
    /// </summary>
    [Fact]
    public async Task A_measured_base_above_the_band_is_clamped_to_it()
    {
        var time = new FakeTimeProvider();
        var policy = Adaptive(time, out _);

        await WarmAsync(policy, time, TimeSpan.FromSeconds(60), 40);

        // Ten times the configured 100 ms, exactly, whatever the dependency did.
        Assert.Equal(TimeSpan.FromSeconds(1), policy.Measured.BackoffBase);
    }

    /// <summary>
    ///     And the other end, which is the more dangerous one: a very fast dependency would otherwise
    ///     produce a base of microseconds, and a retry curve that starts there is a hot loop against a
    ///     dependency that has just failed.
    /// </summary>
    [Fact]
    public async Task A_measured_base_below_the_band_is_clamped_to_it()
    {
        var time = new FakeTimeProvider();
        var policy = Adaptive(time, out _);

        await WarmAsync(policy, time, TimeSpan.FromMicroseconds(100), 40);

        // A tenth of the configured 100 ms, exactly.
        Assert.Equal(TimeSpan.FromMilliseconds(10), policy.Measured.BackoffBase);
    }

    // ---- What is deliberately not measured ----

    /// <summary>
    ///     Throttling keeps its constant. A rate limiter that answers in two milliseconds is telling you
    ///     about its token bucket, not about how long to wait, and deriving the wait from the latency
    ///     would send a hostile retry rate at the one dependency that explicitly asked for less.
    /// </summary>
    [Fact]
    public void A_throttled_retry_still_waits_the_configured_throttled_base()
    {
        var backoff = Backoff.Measured(1.0, Configured) with { Jitter = Jitter.None };

        Assert.Equal(TimeSpan.FromSeconds(1), Delay(backoff, Verdict.Throttled(), 2, Normal));
    }

    /// <summary>Server pushback is strictly better information than any client-side estimate, and still wins.</summary>
    [Fact]
    public void Server_pushback_still_beats_the_measured_base()
    {
        var backoff = Backoff.Measured(1.0, Configured) with { Jitter = Jitter.None };

        Assert.Equal(TimeSpan.FromSeconds(4), Delay(backoff, Verdict.Throttled(TimeSpan.FromSeconds(4)), 2, Normal));
    }

    /// <summary>
    ///     A caller holding a bare <c>Backoff</c> value gets the configured curve. The estimate is
    ///     private to the policy instance that owns it, so there is nothing else this overload could
    ///     honestly answer - and it is the same answer the executor gives while the estimate is cold.
    /// </summary>
    [Fact]
    public void Computing_without_a_baseline_gives_the_configured_curve()
    {
        var backoff = Backoff.Measured(1.0, Configured) with { Jitter = Jitter.None };

        Assert.Equal(Configured, backoff.Compute(new NextAttempt(2, Verdict.Transient, null, Timeout.InfiniteTimeSpan, default)));
    }

    // ---- Self-correction ----

    /// <summary>
    ///     Only successful attempts are sampled, and here that is what keeps backoff from collapsing: a
    ///     dependency failing fast has a very short latency distribution, and a base measured from it
    ///     would turn the retry curve into a tight loop at the moment the dependency could least afford
    ///     one.
    /// </summary>
    [Fact]
    public async Task Failed_attempts_do_not_feed_the_estimate()
    {
        var time = new FakeTimeProvider();
        var policy = Adaptive(time, out _);

        await WarmAsync(policy, time, Normal, 20);
        var before = policy.Measured.BackoffBase;

        // Sixty fast failures against twenty slow successes. Were failures sampled, the median would
        // now be a microsecond and the base would be pinned to the bottom of the band.
        for (var i = 0; i < 60; i++)
        {
            // The explicit return type is what tells a never-returning lambda apart from a
            // streaming source: TryRunAsync has both a Task<T> and an IAsyncEnumerable<T> overload.
            await policy.TryRunAsync(Task<int> (_) =>
            {
                time.Advance(TimeSpan.FromMicroseconds(1));
                throw new IOException();
            });
        }

        Assert.Equal(before, policy.Measured.BackoffBase);
    }

    /// <summary>
    ///     The estimate is per policy <i>instance</i>, so two policies against two dependencies do not
    ///     pool their latency - the same scoping every other measured term has, and for the same reason.
    /// </summary>
    [Fact]
    public async Task The_estimate_is_private_to_the_policy_instance()
    {
        var time = new FakeTimeProvider();

        var one = Adaptive(time, out _);
        var other = Adaptive(time, out _);

        await WarmAsync(one, time, Normal, 40);

        Assert.NotNull(one.Measured.BackoffBase);
        Assert.Null(other.Measured.BackoffBase);
    }

    // ---- Configuration ----

    [Fact]
    public void A_multiple_of_zero_or_less_is_refused()
    {
        var policy = TestPolicy.Instant with { Backoff = Backoff.Measured(0) };

        Assert.Contains(Problems(policy), p => p.Contains("MeasuredBase.Multiple", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.9)]
    [InlineData(0)]
    public void A_quantile_outside_the_body_is_refused(double quantile)
    {
        var policy = TestPolicy.Instant with { Backoff = Backoff.Measured() with { MeasuredBase = MeasuredBase.Times() with { Quantile = quantile } } };

        Assert.Contains(Problems(policy), p => p.Contains("MeasuredBase.Quantile", StringComparison.Ordinal));
    }

    /// <summary>A spread of 1 pins the measured base to the constant it was supposed to replace.</summary>
    [Fact]
    public void A_spread_of_one_or_less_is_refused()
    {
        var policy = TestPolicy.Instant with { Backoff = Backoff.Measured() with { MeasuredBase = MeasuredBase.Times() with { Spread = 1 } } };

        Assert.Contains(Problems(policy), p => p.Contains("MeasuredBase.Spread", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A measured base belongs to a curve that has a transient base to anchor it. A constant curve's
    ///     single delay is also its cap and a custom curve computes everything itself, so measuring into
    ///     either would be a value the caller cannot predict from what they wrote.
    /// </summary>
    [Fact]
    public void A_measured_base_on_a_curve_that_is_not_exponential_is_refused()
    {
        var constant = TestPolicy.Instant with { Backoff = Backoff.Constant(TimeSpan.FromSeconds(1)) with { MeasuredBase = MeasuredBase.Times() } };
        var custom = TestPolicy.Instant with { Backoff = Backoff.Custom(_ => TimeSpan.Zero) with { MeasuredBase = MeasuredBase.Times() } };

        Assert.Contains(Problems(constant), p => p.Contains("Backoff.MeasuredBase", StringComparison.Ordinal));
        Assert.Contains(Problems(custom), p => p.Contains("Backoff.MeasuredBase", StringComparison.Ordinal));
    }

    /// <summary>
    ///     <c>Adaptive = false</c> suppresses the defaults the library would supply; it does not silently
    ///     override what the caller wrote. A policy that says both has contradicted itself.
    /// </summary>
    [Fact]
    public void Turning_measurement_off_and_configuring_a_measured_base_is_refused()
    {
        var policy = TestPolicy.Instant with { Adaptive = false, Backoff = Backoff.Measured() };

        Assert.Contains(Problems(policy), p => p.Contains("Backoff.MeasuredBase is set", StringComparison.Ordinal));
    }

    /// <summary>Value equality is over the effective configuration, so a named default equals an omitted one.</summary>
    [Fact]
    public void Naming_a_default_equals_leaving_it_alone()
    {
        Assert.Equal(MeasuredBase.Times(), MeasuredBase.Times() with { Quantile = 0.5 });
        Assert.Equal(MeasuredBase.Times().GetHashCode(), (MeasuredBase.Times() with { MinimumSamples = 20 }).GetHashCode());
        Assert.NotEqual(MeasuredBase.Times(), MeasuredBase.Times(2));
    }

    /// <summary>
    ///     <c>default(MeasuredBase)</c> compiles, because <c>backoff with { MeasuredBase = default }</c> does.
    ///     Every property but the multiple has to read as its default rather than as zero.
    /// </summary>
    [Fact]
    public void The_default_instance_reads_as_the_defaults()
    {
        var unconstructed = default(MeasuredBase);

        Assert.Equal(0.5, unconstructed.Quantile);
        Assert.Equal(TimeSpan.FromMinutes(5), unconstructed.Window);
        Assert.Equal(20, unconstructed.MinimumSamples);
        Assert.Equal(10.0, unconstructed.Spread);

        // And the multiple is the one thing it cannot supply, so it is the one thing Validate refuses.
        Assert.Contains(
            Problems(TestPolicy.Instant with { Backoff = Backoff.Exponential() with { MeasuredBase = unconstructed } }),
            p => p.Contains("MeasuredBase.Multiple", StringComparison.Ordinal));
    }

    /// <summary>
    ///     <c>default(Backoff)</c> normalizes to the shipped exponential curve, and a measured base
    ///     written onto it has to survive that - <c>policy with { Backoff = default }</c> compiles.
    /// </summary>
    [Fact]
    public void An_unconstructed_curve_keeps_a_measured_base()
    {
        var backoff = default(Backoff) with { MeasuredBase = MeasuredBase.Times(), Jitter = Jitter.None };

        Assert.Equal(Normal, Delay(backoff, Verdict.Transient, 2, Normal));
    }

    [Fact]
    public void It_prints_its_effective_configuration()
    {
        var text = MeasuredBase.Times().ToString();

        Assert.Contains("1x p50", text, StringComparison.Ordinal);
        Assert.Contains("300s", text, StringComparison.Ordinal);
        Assert.Contains("min 20 samples", text, StringComparison.Ordinal);
        Assert.Contains("within 10x", text, StringComparison.Ordinal);
    }

    // ---- Harness ----

    private static Resilience Adaptive(FakeTimeProvider time, out EventRecorder events)
    {
        var recorder = new EventRecorder();
        events = recorder;

        return TestPolicy.WithClock(time) with
        {
            Name = "api",
            Attempts = 1,
            Backoff = Backoff.Measured(1, Configured) with
            {
                Jitter = Jitter.None,
                MeasuredBase = MeasuredBase.Times() with { Window = Window },
            },
            OnEvent = recorder.Record,
        };
    }

    private static IReadOnlyList<string> Problems(Resilience policy) =>
        Assert.Throws<ResilienceConfigurationException>(policy.Validate).Problems;

    private static TimeSpan Delay(Backoff backoff, Verdict previous, int attemptNumber, TimeSpan normal) =>
        backoff.Compute(new NextAttempt(attemptNumber, previous, null, Timeout.InfiniteTimeSpan, default), normal);

    /// <summary>
    ///     Records <paramref name="times" /> samples of <paramref name="duration" /> into the policy's
    ///     latency estimate. The callback advances the clock and completes synchronously, so the
    ///     executor never suspends.
    /// </summary>
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

    /// <summary>Advances the fake clock in steps until the call completes, yielding for real in between.</summary>
    private static async Task PumpAsync(FakeTimeProvider time, Task call, int steps = 40)
    {
        for (var i = 0; i < steps && !call.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));

            // A real yield, because the loop's continuation runs on the thread pool and the fake clock
            // cannot advance it there.
            await Task.Delay(1);
        }
    }
}
