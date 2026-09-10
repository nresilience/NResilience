using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The fourth model: this process's own thread pool, so a simulation can exercise
///     <c>Resilience.Saturation</c> - the one term in the library that measures the caller rather than
///     the dependency, and therefore the one a simulation with a real idle pool underneath it could
///     never reach.
///     <para>
///         What is under test is the model, not the decision. The comparison, the onset flag and the
///         executor's contamination check are the shipping ones, and <c>SaturationTests</c> pins those
///         directly. These tests pin that the model reproduces the probe's two blind spots - a cold
///         baseline and a rolling one - because a model that missed either would make the feature look
///         better than it is.
///     </para>
/// </summary>
public sealed class SimulationPoolTests
{
    private static readonly Dependency Fast =
        Dependency.Healthy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200));

    /// <summary>A pool that queues for microseconds, which is what a healthy one does.</summary>
    private static readonly Pool Healthy = Pool.Healthy(TimeSpan.FromMicroseconds(80));

    /// <summary>
    ///     A policy that measures something for saturation to protect - <c>Saturation</c> on a policy
    ///     that measures nothing is refused at validation, and rightly.
    /// </summary>
    private static readonly Resilience Measuring = TestPolicy.Instant with
    {
        Attempts = 3,
        Deadline = TimeSpan.FromSeconds(10),
        AttemptCeiling = AttemptCeiling.Above(3),
        Saturation = Saturation.Above(5),
        Name = "api",
    };

    private static Simulation Run(Resilience policy, Pool? pool = null, int seconds = 60)
    {
        var simulation = Simulate.Policy(policy)
            .Against(Fast)
            .Under(Load.Constant(200))
            .For(TimeSpan.FromSeconds(seconds));

        return pool is null ? simulation : simulation.WithPool(pool);
    }

    // ---- The model reaches the shipping decision ----

    /// <summary>
    ///     The point of the whole thing: a stall in the modeled pool reaches the executor's own
    ///     contamination check and is reported as an episode.
    /// </summary>
    [Fact]
    public void A_stall_in_the_modeled_pool_is_detected()
    {
        var stalling = Healthy.Stall(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10));

        var report = Run(Measuring, stalling).Run(seed: 42);

        Assert.True(report.CountOf(CallEventKind.SaturationDetected) > 0);
    }

    /// <summary>
    ///     The onset only. The flag that makes the event proportional to incidents rather than to
    ///     traffic is shipping code, and a simulation running thousands of calls through one stall is
    ///     the cheapest way to see that it holds.
    /// </summary>
    [Fact]
    public void One_stall_is_reported_once_however_many_calls_pass_through_it()
    {
        var stalling = Healthy.Stall(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10));

        var report = Run(Measuring, stalling).Run(seed: 42);

        Assert.True(report.Calls > 1_000);
        Assert.Equal(1, report.CountOf(CallEventKind.SaturationDetected));
    }

    /// <summary>An unstalled pool is not an incident, however long the run.</summary>
    [Fact]
    public void A_healthy_pool_is_never_saturated()
    {
        var report = Run(Measuring, Healthy).Run(seed: 42);

        Assert.Equal(0, report.CountOf(CallEventKind.SaturationDetected));
    }

    /// <summary>
    ///     A pool that always queues deeply is not saturated either, because the comparison is against
    ///     the process's own normal. The shipping behaviour, and the model's honest limit: a process
    ///     that is permanently starved has no episode to detect.
    /// </summary>
    [Fact]
    public void A_pool_that_is_always_deep_has_no_episode_to_detect()
    {
        var report = Run(Measuring, Pool.Healthy(TimeSpan.FromMilliseconds(400))).Run(seed: 42);

        Assert.Equal(0, report.CountOf(CallEventKind.SaturationDetected));
    }

    // ---- The two blind spots the probe actually has ----

    /// <summary>
    ///     A cold baseline is never saturated. Probes are queued four times a second, so the default
    ///     twenty samples is five seconds during which no stall registers however deep it is - and a
    ///     simulation that reported one would be describing a process this library does not produce.
    /// </summary>
    [Fact]
    public void A_stall_before_the_baseline_is_warm_is_not_detected()
    {
        var early = Healthy.Stall(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(3));

        var report = Run(Measuring, early, seconds: 20).Run(seed: 42);

        Assert.Equal(0, report.CountOf(CallEventKind.SaturationDetected));
    }

    /// <summary>
    ///     The baseline is a rolling median over the window, so a stall that comes to cover more than
    ///     half of it becomes the median and stops being an anomaly. Detection goes quiet while the
    ///     queue is still deep - so a policy waiting for a second event to tell it the incident ended
    ///     would wait forever, and a listener counting episodes is counting onsets rather than minutes.
    /// </summary>
    [Fact]
    public void A_stall_stops_being_detected_once_the_baseline_has_learned_it()
    {
        var sustained = Healthy.Stall(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(50));
        var settings = Saturation.Above(5);

        // Ten seconds in, the window is mostly still the healthy pool and the stall is an anomaly.
        var early = sustained.At(TimeSpan.FromSeconds(15), minimumSamples: 20);

        Assert.True(settings.IsSaturated(early));

        // Thirty-five seconds in, the window is mostly the stall. The queue is exactly as deep as it
        // was - the reading has not improved, the yardstick has moved.
        var late = sustained.At(TimeSpan.FromSeconds(45), minimumSamples: 20);

        Assert.Equal(early.Delay, late.Delay);
        Assert.False(settings.IsSaturated(late));
    }

    /// <summary>Two separated stalls are two episodes, which is what makes a count of onsets a count of incidents.</summary>
    [Fact]
    public void Two_stalls_are_two_episodes()
    {
        var twice = Healthy
            .Stall(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5))
            .Stall(TimeSpan.FromSeconds(40), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(5));

        var report = Run(Measuring, twice, seconds: 120).Run(seed: 42);

        Assert.Equal(2, report.CountOf(CallEventKind.SaturationDetected));
    }

    // ---- The delay is inside the duration the policy measures ----

    /// <summary>
    ///     The mechanism, not the label. A work item waits for a thread before it runs, and the
    ///     executor's stopwatch is already going when it does - so the queue delay is inside every
    ///     duration every estimator in the library learns from. That misattribution is the whole of what
    ///     <c>Saturation</c> exists to correct, and a model that reported the delay to the probe without
    ///     also spending it would leave the feature with nothing to protect.
    /// </summary>
    [Fact]
    public void The_modeled_queue_delay_is_spent_inside_the_call()
    {
        var quiet = Run(Measuring, Healthy).Run(seed: 42);
        var deep = Run(Measuring, Pool.Healthy(TimeSpan.FromMilliseconds(200))).Run(seed: 42);

        // Neither run is saturated - a pool that is always deep has no episode - so what is left is the
        // 200 ms every attempt spends waiting for a thread.
        Assert.Equal(0, deep.CountOf(CallEventKind.SaturationDetected));

        var added = deep.Latency(0.5) - quiet.Latency(0.5);

        Assert.InRange(added, TimeSpan.FromMilliseconds(195), TimeSpan.FromMilliseconds(205));
    }

    // ---- Determinism ----

    /// <summary>The guarantee the whole simulator rests on, extended to the fourth model.</summary>
    [Fact]
    public void The_same_seed_and_the_same_pool_produce_a_byte_identical_report()
    {
        var stalling = Healthy.Stall(TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10));

        Assert.Equal(
            Run(Measuring, stalling).Run(seed: 42).ToString(),
            Run(Measuring, stalling).Run(seed: 42).ToString());
    }

    /// <summary>
    ///     A run that names no pool does not read the real one. Left to the process-wide probe, a test
    ///     host loaded enough to clear the 20 ms floor would put a machine-dependent term in a report
    ///     that claims to be reproducible to the last byte.
    /// </summary>
    [Fact]
    public void A_simulation_with_no_pool_is_never_saturated_whatever_the_host_is_doing()
    {
        var report = Run(Measuring).Run(seed: 42);

        Assert.Equal(0, report.CountOf(CallEventKind.SaturationDetected));
    }

    // ---- The mistakes ----

    /// <summary>
    ///     A pool is a property of the process, not of the policy: every attempt waits in it whether or
    ///     not the policy is configured to notice. Which is what makes the comparison worth running -
    ///     the same pool and the same seed against a policy that can tell a stalled pool from a slow
    ///     dependency and one that cannot. Refusing a pool here would leave only "stalled" against "not
    ///     stalled", which measures the stall rather than the setting.
    /// </summary>
    [Fact]
    public void A_pool_applies_to_a_policy_that_does_not_measure_one()
    {
        var deep = Pool.Healthy(TimeSpan.FromMilliseconds(200));
        var unaware = Measuring with { Saturation = null };

        var aware = Run(Measuring, deep).Run(seed: 42);
        var oblivious = Run(unaware, deep).Run(seed: 42);

        // The delay is spent either way. Only the noticing is configurable.
        Assert.InRange(
            (oblivious.Latency(0.5) - aware.Latency(0.5)).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(5));
    }

    /// <summary>A losing pool is refused where the dependency and the load are, and says everything at once.</summary>
    [Fact]
    public void A_losing_pool_is_refused()
    {
        var pool = Pool.Healthy(TimeSpan.Zero).Stall(-TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.Zero);

        var error = Assert.Throws<ResilienceConfigurationException>(pool.Validate);

        Assert.Equal(4, error.Problems.Count);
    }

    /// <summary>A null pool is a null argument, not a pool that does nothing.</summary>
    [Fact]
    public void A_null_pool_is_refused_at_the_call() =>
        Assert.Throws<ArgumentNullException>(() => Simulate.Policy(Measuring).WithPool(null!));
}
