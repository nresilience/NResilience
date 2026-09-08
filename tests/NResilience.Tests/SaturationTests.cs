using Microsoft.Extensions.Time.Testing;
using NResilience.Internal;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     Local saturation: <c>Resilience.Saturation</c>, which stops a policy learning from a duration
///     that is mostly this process's own thread-pool queue.
///     <para>
///         The defect this closes is not a missing feature. Every estimator in the library measures wall
///         clock around the callback and attributes all of it to the dependency, and six things read the
///         result - so a queue delay of 400 ms relaxes every bound the library has, at the moment a
///         local incident is under way. These tests pin the correction and, just as importantly, pin
///         what it is <b>not</b> allowed to do: refuse anything, move a bound, or add a delay.
///     </para>
/// </summary>
public sealed class SaturationTests
{
    /// <summary>Long enough that no test rolls a slice and loses the samples it just recorded.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan Configured = TimeSpan.FromSeconds(30);

    /// <summary>A healthy pool: microseconds of queueing, and a baseline to match.</summary>
    private static readonly PoolProbe.Reading Healthy =
        new(TimeSpan.FromMicroseconds(50), TimeSpan.FromMicroseconds(40));

    /// <summary>A starved pool: 400 ms of queueing against a 1 ms normal, which is 400x.</summary>
    private static readonly PoolProbe.Reading Starved =
        new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1));

    // ---- The decision ----

    /// <summary>
    ///     Both bars have to be cleared. Five times a 1 µs normal is 5 µs, which is nothing, and a
    ///     policy that stopped measuring every time a GC pause moved one probe would never learn
    ///     anything.
    /// </summary>
    [Fact]
    public void The_floor_is_what_makes_a_relative_multiple_safe()
    {
        var settings = Saturation.Above();

        Assert.False(settings.IsSaturated(new PoolProbe.Reading(TimeSpan.FromMicroseconds(50), TimeSpan.FromMicroseconds(1))));
        Assert.True(settings.IsSaturated(new PoolProbe.Reading(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1))));
    }

    /// <summary>Above the floor and below the multiple is still not saturated.</summary>
    [Fact]
    public void The_multiple_is_what_makes_an_absolute_floor_portable()
    {
        var settings = Saturation.Above();

        // 30 ms of queueing is well over the 20 ms floor, but this process normally queues 25 ms - so
        // 30 ms is what normal looks like here, and a host whose pool is simply busy is not in an
        // incident.
        Assert.False(settings.IsSaturated(new PoolProbe.Reading(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(25))));
        Assert.True(settings.IsSaturated(new PoolProbe.Reading(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(25))));
    }

    /// <summary>
    ///     The cold-start rule, applied to the probe. A process that starts into a deep queue has no
    ///     baseline to compare against, and no estimate means no opinion rather than a guessed one.
    /// </summary>
    [Fact]
    public void A_cold_baseline_is_never_saturated_however_deep_the_queue()
    {
        var settings = Saturation.Above();

        Assert.False(settings.IsSaturated(new PoolProbe.Reading(TimeSpan.FromSeconds(10), Normal: null)));
    }

    /// <summary>An absurd multiple saturates the comparison rather than overflowing it.</summary>
    [Fact]
    public void An_absurd_multiple_is_never_satisfied_rather_than_overflowing()
    {
        var settings = Saturation.Above(double.MaxValue);

        Assert.False(settings.IsSaturated(new PoolProbe.Reading(TimeSpan.FromDays(1), TimeSpan.FromTicks(1))));
    }

    // ---- What it changes: nothing is recorded ----

    /// <summary>
    ///     The half of the design that fixes the defect. A saturated process records nothing, so the
    ///     attempt ceiling stays cold however many calls succeed - and does not learn a ceiling that is
    ///     mostly queue delay.
    /// </summary>
    [Fact]
    public async Task A_saturated_process_stops_feeding_the_attempt_ceiling()
    {
        var time = new FakeTimeProvider();
        var policy = Aware(time, out _);

        ExecutionState.OverrideProbe(policy, Starved);
        await WarmAsync(policy, time, Fast, 40);

        Assert.Null(policy.Measured.AttemptCeiling);
    }

    /// <summary>And resumes the moment the queue drains, from the samples that arrive after it.</summary>
    [Fact]
    public async Task The_estimate_resumes_the_moment_the_queue_drains()
    {
        var time = new FakeTimeProvider();
        var policy = Aware(time, out _);

        ExecutionState.OverrideProbe(policy, Starved);
        await WarmAsync(policy, time, TimeSpan.FromSeconds(5), 40);

        Assert.Null(policy.Measured.AttemptCeiling);

        ExecutionState.OverrideProbe(policy, Healthy);
        await WarmAsync(policy, time, Fast, 20);

        var measured = policy.Measured.AttemptCeiling;

        Assert.NotNull(measured);

        // Three times the 100 ms samples taken while healthy, give or take the estimator's bucket
        // width. The 5-second samples taken while saturated left no trace: had even one landed, a p95
        // over 60 samples would be reading them.
        Assert.InRange(measured.Value, TimeSpan.FromMilliseconds(280), TimeSpan.FromMilliseconds(360));
    }

    /// <summary>
    ///     The same for the measured backoff base, which is the reader whose contamination is the most
    ///     directly harmful: a base lengthened by the local queue delays the retry that would have
    ///     succeeded.
    /// </summary>
    [Fact]
    public async Task A_saturated_process_stops_feeding_the_measured_backoff_base()
    {
        var time = new FakeTimeProvider();

        var policy = Aware(time, out _, p => p with
        {
            Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(50)) with
            {
                MeasuredBase = MeasuredBase.Times() with { Window = Window },
            },
        });

        ExecutionState.OverrideProbe(policy, Starved);
        await WarmAsync(policy, time, Fast, 40);

        Assert.Null(policy.Measured.BackoffBase);
    }

    /// <summary>
    ///     And for the hedge threshold, where the wrong answer is the most expensive: a threshold that
    ///     learned the queue delay arms a second copy of the work during exactly the incident where the
    ///     process cannot afford one.
    /// </summary>
    [Fact]
    public async Task A_saturated_process_stops_feeding_the_hedge_threshold()
    {
        var time = new FakeTimeProvider();

        var policy = Aware(time, out _, p => p with
        {
            Attempts = 3,
            Hedge = Hedge.At() with { Window = Window },
        });

        ExecutionState.OverrideProbe(policy, Starved);
        await WarmAsync(policy, time, Fast, 40);

        Assert.Null(policy.Measured.HedgeThreshold);
    }

    /// <summary>
    ///     Tighten-only, stated as a test. A policy that finds itself saturated behaves exactly like one
    ///     that never configured the feature: same attempts, same success, same verdict. The only
    ///     difference is what it learned.
    /// </summary>
    [Fact]
    public async Task Saturation_refuses_nothing_and_changes_no_bound()
    {
        var time = new FakeTimeProvider();
        var policy = Aware(time, out var events);

        ExecutionState.OverrideProbe(policy, Starved);

        var result = await policy.TryRunAsync(_ => Task.FromResult(7));

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.ValueOrThrow());
        Assert.Single(result.Attempts);
        Assert.Equal(1, events.CountOf(CallEventKind.Succeeded));
        Assert.Equal(0, events.CountOf(CallEventKind.RejectedByBudget));
        Assert.Equal(0, events.CountOf(CallEventKind.RejectedByBreaker));
    }

    // ---- The event ----

    /// <summary>
    ///     Once per episode, not once per call. This is read on every recorded attempt, so without the
    ///     episode flag a listener would see an event per call for the duration of the incident - which
    ///     is the shape of flood the rest of the telemetry surface goes out of its way to avoid.
    /// </summary>
    [Fact]
    public async Task The_onset_of_an_episode_is_reported_once()
    {
        var time = new FakeTimeProvider();
        var policy = Aware(time, out var events);

        ExecutionState.OverrideProbe(policy, Starved);
        await WarmAsync(policy, time, Fast, 20);

        Assert.Equal(1, events.CountOf(CallEventKind.SaturationDetected));

        var raised = events.Single(CallEventKind.SaturationDetected);

        Assert.Equal(TimeSpan.FromMilliseconds(400), raised.Delay);
        Assert.Equal(VerdictKind.Ok, raised.Verdict.Kind);
        Assert.False(raised.IsTerminal);
        Assert.False(raised.IsRejection);
    }

    /// <summary>A second episode is a second event, so the count is a count of local incidents.</summary>
    [Fact]
    public async Task A_second_episode_is_reported_again()
    {
        var time = new FakeTimeProvider();
        var policy = Aware(time, out var events);

        ExecutionState.OverrideProbe(policy, Starved);
        await WarmAsync(policy, time, Fast, 5);

        ExecutionState.OverrideProbe(policy, Healthy);
        await WarmAsync(policy, time, Fast, 5);

        ExecutionState.OverrideProbe(policy, Starved);
        await WarmAsync(policy, time, Fast, 5);

        Assert.Equal(2, events.CountOf(CallEventKind.SaturationDetected));
    }

    /// <summary>
    ///     Once per episode on the hedged path too, and this is the test that earns its keep. The hedged
    ///     loop asks the question twice per leg - once before it feeds the latency estimate, once from
    ///     the attempt record - and a single call that claimed the episode as a side effect of answering
    ///     would let the first ask swallow the onset and the second raise nothing, leaving the hedged
    ///     path silent for the whole incident.
    /// </summary>
    [Fact]
    public async Task The_onset_is_reported_once_on_the_hedged_path()
    {
        var time = new FakeTimeProvider();

        var policy = Aware(time, out var events, p => p with
        {
            Attempts = 3,
            Hedge = Hedge.At() with { Window = Window },
        });

        ExecutionState.OverrideProbe(policy, Starved);
        await WarmAsync(policy, time, Fast, 20);

        Assert.Equal(1, events.CountOf(CallEventKind.SaturationDetected));
        Assert.Equal(TimeSpan.FromMilliseconds(400), events.Single(CallEventKind.SaturationDetected).Delay);

        // And the estimate it guards learned nothing, which is the point of asking twice per leg.
        Assert.Null(policy.Measured.HedgeThreshold);
    }

    /// <summary>A healthy pool raises nothing at all, which is what makes the event worth watching.</summary>
    [Fact]
    public async Task A_healthy_pool_raises_nothing()
    {
        var time = new FakeTimeProvider();
        var policy = Aware(time, out var events);

        ExecutionState.OverrideProbe(policy, Healthy);
        await WarmAsync(policy, time, Fast, 20);

        Assert.Equal(0, events.CountOf(CallEventKind.SaturationDetected));
        Assert.NotNull(policy.Measured.AttemptCeiling);
    }

    // ---- The reading ----

    /// <summary>
    ///     <c>Measured.QueueDelay</c> reports the delay whether or not it counts as saturated, because
    ///     the number an operator wants on a dashboard is the one that moves before the threshold fires.
    /// </summary>
    [Fact]
    public void The_reading_is_the_delay_rather_than_the_verdict()
    {
        var policy = Aware(new FakeTimeProvider(), out _);

        ExecutionState.OverrideProbe(policy, Healthy);
        Assert.Equal(TimeSpan.FromMicroseconds(50), policy.Measured.QueueDelay);

        ExecutionState.OverrideProbe(policy, Starved);
        Assert.Equal(TimeSpan.FromMilliseconds(400), policy.Measured.QueueDelay);
    }

    /// <summary>Null while the baseline is cold, like every other reading on the type.</summary>
    [Fact]
    public void The_reading_is_null_while_the_baseline_is_cold()
    {
        var policy = Aware(new FakeTimeProvider(), out _);

        ExecutionState.OverrideProbe(policy, new PoolProbe.Reading(TimeSpan.FromSeconds(1), Normal: null));

        Assert.Null(policy.Measured.QueueDelay);
    }

    /// <summary>And null without the feature configured, without validating anything.</summary>
    [Fact]
    public void The_reading_is_null_without_the_feature_configured() =>
        Assert.Null((Resilience.Default with { Attempts = 0 }).Measured.QueueDelay);

    // ---- The probe ----

    /// <summary>
    ///     The probe answers, and the numbers it answers with are the ones the documentation names: a
    ///     median over a minute.
    /// </summary>
    [Fact]
    public void The_probe_reports_a_median_over_one_minute()
    {
        Assert.Equal(0.5, PoolProbe.Quantile);
        Assert.Equal(TimeSpan.FromMinutes(1), PoolProbe.Window);
        Assert.Equal(TimeSpan.FromMilliseconds(250), PoolProbe.Interval);
    }

    /// <summary>
    ///     A cold read is not saturated and does not throw, which is the state every process is in for
    ///     its first few seconds. It also queues the first probe, so this is what starts the
    ///     measurement at all.
    /// </summary>
    [Fact]
    public void A_read_of_the_real_probe_answers_without_a_baseline()
    {
        var reading = PoolProbe.Read(minimumSamples: 20);

        Assert.True(reading.Delay >= TimeSpan.Zero);
        Assert.False(Saturation.Above().IsSaturated(reading));
    }

    /// <summary>
    ///     The probe does eventually measure the pool it is queued into. Asserted loosely - the point is
    ///     that samples arrive and the baseline warms, not what a healthy queue delay is on a machine
    ///     running a test suite in parallel.
    /// </summary>
    [Fact]
    public async Task The_probe_warms_from_the_real_thread_pool()
    {
        // Four per second at most, so a baseline of two samples is the smallest thing worth waiting
        // for. The read is what paces them, hence the loop.
        PoolProbe.Reading reading = default;

        for (var i = 0; i < 40 && reading.Normal is null; i++)
        {
            reading = PoolProbe.Read(minimumSamples: 2);
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        Assert.NotNull(reading.Normal);
        Assert.True(reading.Normal >= TimeSpan.Zero);
    }

    // ---- Configuration ----

    [Fact]
    public void The_configuration_reads_as_a_sentence() =>
        Assert.Equal(
            "5x normal queue delay (floor 20ms, min 20 samples)",
            (Saturation.Above() with { Floor = TimeSpan.FromMilliseconds(20) }).ToString());

    /// <summary>Naming a default explicitly equals leaving it alone, like every other options value.</summary>
    [Fact]
    public void Value_equality_is_over_the_effective_configuration()
    {
        var implicitDefaults = Saturation.Above();
        var explicitDefaults = Saturation.Above() with { Floor = TimeSpan.FromMilliseconds(20), MinimumSamples = 20 };

        Assert.Equal(implicitDefaults, explicitDefaults);
        Assert.Equal(implicitDefaults.GetHashCode(), explicitDefaults.GetHashCode());
        Assert.NotEqual(implicitDefaults, Saturation.Above(6));
    }

    /// <summary>
    ///     A multiple at or below 1 is not a comparison against normal, it is "always saturated" - and a
    ///     policy that never measures anything is not what anybody configuring this wanted.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_multiple_that_is_not_above_normal_is_refused(double multiple)
    {
        var problems = Problems(Guard(Saturation.Above(multiple)));

        Assert.Contains(problems, problem => problem.Contains("Saturation.Multiple", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_positive_floor_is_refused()
    {
        var problems = Problems(Guard(Saturation.Above() with { Floor = TimeSpan.Zero }));

        Assert.Contains(problems, problem => problem.Contains("Saturation.Floor", StringComparison.Ordinal));
    }

    [Fact]
    public void A_minimum_below_one_sample_is_refused()
    {
        var problems = Problems(Guard(Saturation.Above() with { MinimumSamples = 0 }));

        Assert.Contains(problems, problem => problem.Contains("Saturation.MinimumSamples", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Saturation does nothing but decline to feed a measured term, so a policy with no measured
    ///     term has asked for a guard over nothing. Refused rather than ignored: silently doing nothing
    ///     is how a caller ends up believing their estimates are protected when they are not.
    /// </summary>
    [Fact]
    public void Saturation_on_a_policy_that_measures_nothing_is_refused()
    {
        var policy = TestPolicy.Instant with
        {
            AttemptCeiling = null,
            Saturation = Saturation.Above(),
        };

        Assert.Contains(
            Problems(policy),
            problem => problem.Contains("measures nothing for it to protect", StringComparison.Ordinal));
    }

    /// <summary>
    ///     <c>Adaptive = false</c> and <c>Saturation</c> are two incompatible statements, and the library
    ///     reports both rather than ranking them - the same rule the ceiling, the measured base and the
    ///     hedge already follow.
    /// </summary>
    [Fact]
    public void Adaptive_false_beside_Saturation_is_a_contradiction()
    {
        var policy = TestPolicy.Instant with
        {
            Adaptive = false,
            AttemptCeiling = null,
            Saturation = Saturation.Above(),
        };

        Assert.Contains(
            Problems(policy),
            problem => problem.Contains("Adaptive is false", StringComparison.Ordinal)
                       && problem.Contains("Saturation is set", StringComparison.Ordinal));
    }

    /// <summary>Off in every preset, because it changes what every other measured term learns.</summary>
    [Fact]
    public void No_preset_configures_saturation()
    {
        Assert.Null(Resilience.None.Saturation);
        Assert.Null(Resilience.Default.Saturation);
        Assert.Null(Resilience.Http.Saturation);
    }

    // ---- Explain ----

    /// <summary>
    ///     <c>Explain()</c> prints the reading beside the three terms it guards, and says outright that
    ///     those three pause. It is the only line in the block that changes what the others mean.
    /// </summary>
    [Fact]
    public void The_explanation_names_the_condition_under_which_measurement_pauses()
    {
        var policy = Aware(new FakeTimeProvider(), out _);

        ExecutionState.OverrideProbe(policy, Starved);

        var text = policy.Explain();

        Assert.Contains("pool queue delay", text, StringComparison.Ordinal);
        Assert.Contains("5x p50 of queue delay over 1m, 20 samples minimum", text, StringComparison.Ordinal);
        Assert.Contains("measurement pauses above 20ms and 5x normal", text, StringComparison.Ordinal);
    }

    /// <summary>A policy without it says "not configured", like every other unconfigured term.</summary>
    [Fact]
    public void The_explanation_reports_an_unconfigured_probe_as_such()
    {
        var text = Resilience.Default.Explain();

        Assert.Contains("pool queue delay  -  not configured", text, StringComparison.Ordinal);
        Assert.DoesNotContain("measurement pauses", text, StringComparison.Ordinal);
    }

    // ---- Helpers ----

    /// <summary>
    ///     A policy with a measured ceiling and saturation awareness on, on a test clock, with its
    ///     events recorded.
    /// </summary>
    private static Resilience Aware(FakeTimeProvider time, out EventRecorder events, Func<Resilience, Resilience>? configure = null)
    {
        var recorder = new EventRecorder();
        events = recorder;

        var policy = TestPolicy.WithClock(time) with
        {
            Name = "api",
            Attempts = 1,
            AttemptTimeout = Configured,
            AttemptCeiling = AttemptCeiling.Above() with { Window = Window },
            Saturation = Saturation.Above(),
            OnEvent = recorder.Record,
        };

        return configure is null ? policy : configure(policy);
    }

    /// <summary>
    ///     The settings on a policy that has something for them to guard, so the only problems reported
    ///     are the ones the settings have on their own.
    /// </summary>
    private static Resilience Guard(Saturation settings) => TestPolicy.Instant with { Saturation = settings };

    private static IReadOnlyList<string> Problems(Resilience policy) =>
        Assert.Throws<ResilienceConfigurationException>(policy.Validate).Problems;

    /// <summary>
    ///     Records <paramref name="times" /> samples of <paramref name="duration" /> into the policy's
    ///     estimates - or would, if the probe let it.
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
}
