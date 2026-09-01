using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Time.Testing;
using NResilience.Extensions;

namespace NResilience.Tests;

/// <summary>
///     The adaptive concurrency limit: a permit count discovered from latency rather than one an
///     operator divides out by hand.
///     <para>
///         The claim the feature makes is narrow and testable. Under a dependency that is keeping up,
///         the limit probes upward and stops at the ceiling. Under one that is queueing, it backs off
///         geometrically and stops at the floor. And when the dependency's capacity changes, the limit
///         follows it - which is the thing <c>Limit.Concurrency(50)</c> cannot do, and the whole reason
///         this exists.
///     </para>
///     <para>
///         Every test drives the loop through the leases themselves, on a fake clock, because the lease
///         <i>is</i> the measurement: how long a permit is held is the round-trip time the control loop
///         reads. There is nothing else to inject.
///     </para>
/// </summary>
public sealed class AdaptiveLimitTests
{
    /// <summary>
    ///     A latency comfortably inside one histogram bucket, so the baseline the estimator reports is
    ///     stable across runs and the arithmetic in these tests is exact.
    /// </summary>
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(10);

    /// <summary>Well past twice the baseline, so a round containing only these is unambiguously congested.</summary>
    private static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(100);

    // ---- Configuration ----

    [Fact]
    public void The_defaults_describe_a_usable_range()
    {
        var options = new AdaptiveLimitOptions();

        options.Validate();

        Assert.Equal(20, options.Initial);
        Assert.Equal(4, options.Minimum);
        Assert.Equal(200, options.Maximum);
        Assert.Equal(2.0, options.Threshold);
        Assert.Equal(0.9, options.DecreaseFactor);
    }

    /// <summary>
    ///     Every problem at once, in the shape the rest of the library reports configuration errors.
    /// </summary>
    [Fact]
    public void A_range_the_loop_could_not_move_within_is_refused()
    {
        var error = Assert.Throws<ResilienceConfigurationException>(() => new AdaptiveLimitOptions
        {
            Minimum = 0,
            Maximum = 5,
            Initial = 50,
            Threshold = 1,
            DecreaseFactor = 1,
        }.Validate());

        Assert.Equal(4, error.Problems.Count);
        Assert.Contains(error.Problems, p => p.StartsWith("Minimum", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.StartsWith("Initial", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.StartsWith("Threshold", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.StartsWith("DecreaseFactor", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A threshold of 1 or less makes every round look congested, so the limit would walk to its
    ///     floor and stay there whatever the dependency was doing.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(double.NaN)]
    public void A_threshold_that_calls_everything_congested_is_refused(double threshold) =>
        Assert.Throws<ResilienceConfigurationException>(() => new AdaptiveLimitOptions { Threshold = threshold }.Validate());

    /// <summary>At 1 the limit never shrinks, which is the one thing the loop is for.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    public void A_decrease_factor_that_never_decreases_is_refused(double factor) =>
        Assert.Throws<ResilienceConfigurationException>(() => new AdaptiveLimitOptions { DecreaseFactor = factor }.Validate());

    // ---- Admission ----

    [Fact]
    public void The_limiter_starts_at_the_limit_it_was_given()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 5, Minimum = 4, Maximum = 10 }, time: time);

        Assert.Equal(5, limiter.CurrentLimit);
        Assert.Null(limiter.Baseline);
        Assert.Equal(0, limiter.InFlight);
    }

    [Fact]
    public void The_limit_is_how_many_permits_are_held_at_once()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 3, Minimum = 1 }, time: time);

        var held = Take(limiter, 3);

        Assert.Equal(3, limiter.InFlight);

        using (var refused = limiter.AttemptAcquire())
            Assert.False(refused.IsAcquired);

        held[0].Dispose();

        using var admitted = limiter.AttemptAcquire();
        Assert.True(admitted.IsAcquired);

        Release(held.Skip(1));
    }

    /// <summary>
    ///     Zero is the default because this library is already good at waiting: a refusal becomes a
    ///     retry on the throttled curve, which is visible, bounded by the deadline, and charged to
    ///     nobody's attempt timeout.
    /// </summary>
    [Fact]
    public async Task Queueing_is_off_by_default()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 1, Minimum = 1 }, time: time);

        var held = limiter.AttemptAcquire();
        var queued = limiter.AcquireAsync().AsTask();

        Assert.True(queued.IsCompleted);
        Assert.False((await queued).IsAcquired);

        held.Dispose();
    }

    [Fact]
    public async Task A_queued_caller_is_admitted_when_a_slot_frees()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 1, Minimum = 1 }, 2, time: time);

        var held = limiter.AttemptAcquire();
        var queued = limiter.AcquireAsync().AsTask();

        Assert.False(queued.IsCompleted);
        Assert.Equal(1, limiter.GetStatistics().CurrentQueuedCount);

        held.Dispose();

        using var lease = await queued;
        Assert.True(lease.IsAcquired);
        Assert.Equal(0, limiter.GetStatistics().CurrentQueuedCount);
    }

    /// <summary>The queue is a bound, not a waiting room: past it the answer is no, immediately.</summary>
    [Fact]
    public async Task A_full_queue_refuses_rather_than_grows()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 1, Minimum = 1 }, 1, time: time);

        var held = limiter.AttemptAcquire();
        var queued = limiter.AcquireAsync().AsTask();
        var refused = limiter.AcquireAsync().AsTask();

        Assert.False(queued.IsCompleted);
        Assert.True(refused.IsCompleted);
        Assert.False((await refused).IsAcquired);

        held.Dispose();
        (await queued).Dispose();
    }

    /// <summary>
    ///     A caller whose attempt timed out while queueing has to give its reservation back, or the
    ///     queue fills with callers nobody is waiting for and the limiter refuses everyone forever.
    /// </summary>
    [Fact]
    public async Task A_queued_caller_that_gives_up_releases_its_reservation()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 1, Minimum = 1 }, 1, time: time);

        var held = limiter.AttemptAcquire();
        using var abandoning = new CancellationTokenSource();
        var queued = limiter.AcquireAsync(1, abandoning.Token).AsTask();

        Assert.Equal(1, limiter.GetStatistics().CurrentQueuedCount);

        await abandoning.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        Assert.Equal(0, limiter.GetStatistics().CurrentQueuedCount);

        // The slot the abandoned caller had reserved is available to the next one.
        var next = limiter.AcquireAsync().AsTask();
        held.Dispose();

        using var lease = await next;
        Assert.True(lease.IsAcquired);
    }

    /// <summary>
    ///     Head-of-line, so a caller that arrives while somebody is queued does not walk past them.
    /// </summary>
    [Fact]
    public async Task A_caller_that_arrives_while_someone_is_queued_does_not_overtake_them()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 2, Minimum = 1 }, 1, time: time);

        var held = Take(limiter, 2);
        var queued = limiter.AcquireAsync().AsTask();

        held[0].Dispose();

        // The freed slot went to the queued caller, so the walk-up finds nothing.
        Assert.True(queued.IsCompleted);

        using var walkUp = limiter.AttemptAcquire();
        Assert.False(walkUp.IsAcquired);

        (await queued).Dispose();
        held[1].Dispose();
    }

    /// <summary>The limiter can never grant it, so asking is a mistake rather than a refusal.</summary>
    [Fact]
    public void Asking_for_more_permits_than_the_ceiling_is_an_argument_error()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 5, Minimum = 1, Maximum = 10 }, time: time);

        Assert.Throws<ArgumentOutOfRangeException>(() => limiter.AttemptAcquire(11));
    }

    // ---- The control loop ----

    /// <summary>
    ///     A cold process does not guess. Below the sample minimum there is no baseline, so there is
    ///     nothing to call congested and the limit stays where it was configured.
    /// </summary>
    [Fact]
    public void A_cold_limiter_holds_at_its_initial_limit()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 20, Minimum = 4 }, time: time);

        // Nineteen calls, every one of them ruinously slow. One short of an opinion.
        Serial(limiter, time, Slow, 19);

        Assert.Null(limiter.Baseline);
        Assert.Equal(20, limiter.CurrentLimit);
    }

    /// <summary>
    ///     The headline behavior. Once the baseline knows what fast looks like, a round whose
    ///     <i>fastest</i> call is twice that is a queue somewhere downstream, and the limit backs off
    ///     geometrically - 0.9 per round, four rounds, 20 to 13.
    /// </summary>
    [Fact]
    public void Queueing_shrinks_the_limit()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 20, Minimum = 4 }, time: time);

        Serial(limiter, time, Fast, 20);
        Assert.Equal(20, limiter.CurrentLimit);
        Assert.NotNull(limiter.Baseline);

        Serial(limiter, time, Slow, 80);

        Assert.Equal(13, limiter.CurrentLimit);
    }

    /// <summary>
    ///     One slow call is a tail, not a queue. The round is judged by its fastest call precisely so
    ///     that the limiter reacts to the dependency being saturated rather than to its p99.
    /// </summary>
    [Fact]
    public void A_single_slow_call_in_a_healthy_round_is_not_queueing()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 20, Minimum = 4 }, time: time);

        Serial(limiter, time, Fast, 20);

        for (var round = 0; round < 5; round++)
        {
            Serial(limiter, time, Slow, 1);
            Serial(limiter, time, Fast, 19);
        }

        Assert.Equal(20, limiter.CurrentLimit);
    }

    /// <summary>
    ///     Additive increase, one permit per round, and only while the limit is what is actually
    ///     constraining the caller.
    /// </summary>
    [Fact]
    public void A_saturated_limit_over_a_healthy_dependency_grows()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 20, Minimum = 4, Maximum = 200 }, time: time);

        Serial(limiter, time, Fast, 20);
        Assert.Equal(20, limiter.CurrentLimit);

        // Two rounds' worth of samples, taken sixteen at a time - saturated, and fast.
        Concurrent(limiter, time, Fast, 16);
        Concurrent(limiter, time, Fast, 16);

        Assert.Equal(21, limiter.CurrentLimit);
    }

    /// <summary>
    ///     Growing while the limit is not the bound discovers nothing, and it would ratchet an idle
    ///     limiter up to its ceiling so that the first burst after a quiet period met no limit at all.
    /// </summary>
    [Fact]
    public void An_unsaturated_limit_does_not_grow_however_long_it_waits()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 20, Minimum = 4, Maximum = 200 }, time: time);

        Serial(limiter, time, Fast, 20);

        for (var round = 0; round < 20; round++)
            Concurrent(limiter, time, Fast, 2);

        Assert.Equal(20, limiter.CurrentLimit);
    }

    /// <summary>
    ///     The floor is a liveness guarantee rather than a tuning knob. A dependency that is slow for a
    ///     reason unrelated to this caller's concurrency drives the loop down every round, and without a
    ///     floor the limiter would converge on refusing everything and never sample the recovery.
    /// </summary>
    [Fact]
    public void The_limit_never_goes_below_the_floor()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 8, Minimum = 4 }, time: time);

        Serial(limiter, time, Fast, 20);
        Serial(limiter, time, Slow, 400);

        Assert.Equal(4, limiter.CurrentLimit);

        using var lease = limiter.AttemptAcquire(4);
        Assert.True(lease.IsAcquired);
    }

    /// <summary>
    ///     The ceiling is what bounds the damage when the baseline is wrong, so it has to hold however
    ///     many healthy rounds the loop sees.
    /// </summary>
    [Fact]
    public void The_limit_never_goes_above_the_ceiling()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 5, Minimum = 4, Maximum = 6 }, time: time);

        Serial(limiter, time, Fast, 20);

        for (var round = 0; round < 20; round++)
            Concurrent(limiter, time, Fast, 6);

        Assert.Equal(6, limiter.CurrentLimit);
    }

    /// <summary>
    ///     The claim the feature is for. A dependency with a fixed capacity is served by a limit the
    ///     process discovers, and when that capacity halves the limit follows it down without anybody
    ///     redeploying a number.
    ///     <para>
    ///         The equilibrium is around <c>Threshold x capacity</c> rather than capacity itself, and
    ///         that is what the threshold means: two times the baseline latency is the queue depth this
    ///         configuration is willing to tolerate. What matters is that it converges there instead of
    ///         at the ceiling, and that it moves when the capacity does.
    ///     </para>
    /// </summary>
    [Fact]
    public void The_limit_follows_a_dependency_whose_capacity_changes()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 200, Minimum = 4, Maximum = 200 }, time: time);

        // Warm the baseline on an unloaded dependency, exactly as a process warms up before its
        // traffic arrives. Without this the limiter learns the queued latency as normal - the one
        // failure mode the ceiling exists to bound, and it is documented as such.
        Serial(limiter, time, Fast, 20);

        Drive(limiter, time, 10, rounds: 40);
        var discovered = limiter.CurrentLimit;

        Assert.InRange(discovered, 12, 28);

        // Half the pods went away.
        Drive(limiter, time, 5, rounds: 40);

        Assert.InRange(limiter.CurrentLimit, 6, 14);
        Assert.True(limiter.CurrentLimit < discovered);
    }

    // ---- Lifetime and observability ----

    [Fact]
    public async Task Statistics_report_the_discovered_limit_and_what_is_waiting_on_it()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 2, Minimum = 1 }, 1, time: time);

        var held = Take(limiter, 2);
        var queued = limiter.AcquireAsync().AsTask();
        var refused = limiter.AttemptAcquire();

        var stats = limiter.GetStatistics();

        Assert.Equal(0, stats.CurrentAvailablePermits);
        Assert.Equal(1, stats.CurrentQueuedCount);
        Assert.Equal(2, stats.TotalSuccessfulLeases);
        Assert.Equal(1, stats.TotalFailedLeases);

        refused.Dispose();
        Release(held);
        (await queued).Dispose();
    }

    [Fact]
    public void An_idle_limiter_reports_how_long_it_has_been_idle()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 2, Minimum = 1 }, time: time);

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(3), limiter.IdleDuration);

        var held = limiter.AttemptAcquire();
        Assert.Null(limiter.IdleDuration);

        held.Dispose();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), limiter.IdleDuration);
    }

    /// <summary>
    ///     A refusal rather than a cancellation: nothing was cancelled, the limiter went away, and a
    ///     refusal is the answer the caller already knows how to turn into a retry.
    /// </summary>
    [Fact]
    public async Task Disposing_the_limiter_answers_everyone_still_queued()
    {
        var time = new FakeTimeProvider();
        var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 1, Minimum = 1 }, 2, time: time);

        var held = limiter.AttemptAcquire();
        var queued = limiter.AcquireAsync().AsTask();

        limiter.Dispose();

        Assert.False((await queued).IsAcquired);
        Assert.Throws<ObjectDisposedException>(() => limiter.AttemptAcquire());

        held.Dispose();
    }

    /// <summary>Disposing a lease twice must not hand the same slot back twice.</summary>
    [Fact]
    public void Disposing_a_lease_twice_returns_one_slot()
    {
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 2, Minimum = 1 }, time: time);

        var held = limiter.AttemptAcquire();
        held.Dispose();
        held.Dispose();

        Assert.Equal(0, limiter.InFlight);
    }

    /// <summary>
    ///     The discovered limit is the number this feature exists to produce, and it is invisible to
    ///     every other instrument. Recorded when it moves, because a limit that is not moving is one the
    ///     previous sample already reported.
    /// </summary>
    [Fact]
    public void The_discovered_limit_is_reported_when_it_moves()
    {
        using var recording = new LimitRecording();
        var time = new FakeTimeProvider();
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Initial = 20, Minimum = 4 }, name: "payments", time: time);

        Serial(limiter, time, Fast, 20);
        Assert.Empty(recording.Values);

        Serial(limiter, time, Slow, 40);

        Assert.Equal([18, 16], recording.Values);
        Assert.All(recording.Names, name => Assert.Equal("payments", name));
    }

    // ---- Configuration binding ----

    [Fact]
    public void Adaptive_is_a_fourth_kind_and_only_one_may_be_set()
    {
        var error = Assert.Throws<ResilienceConfigurationException>(() =>
            new RateLimitOptions { Concurrency = 20, Adaptive = new AdaptiveLimitOptions() }.Validate());

        Assert.Contains(error.Problems, p => p.Contains("four different guards", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The presence of the section is what turns it on, so an empty one is a complete
    ///     configuration - every property inside has a working default.
    /// </summary>
    [Fact]
    public void An_empty_adaptive_section_is_a_complete_configuration()
    {
        using var limiter = new RateLimitOptions { Adaptive = new AdaptiveLimitOptions() }.ToLimiter();

        Assert.Equal(20, Assert.IsType<AdaptiveLimiter>(limiter).CurrentLimit);
    }

    /// <summary>A section that is wrong in two places says so once.</summary>
    [Fact]
    public void A_bad_adaptive_section_reports_its_problems_with_the_outer_ones()
    {
        var error = Assert.Throws<ResilienceConfigurationException>(() =>
            new RateLimitOptions { QueueLimit = -1, Adaptive = new AdaptiveLimitOptions { Minimum = 0 } }.Validate());

        Assert.Contains(error.Problems, p => p.Contains("QueueLimit", StringComparison.Ordinal));
        Assert.Contains(error.Problems, p => p.Contains("Minimum", StringComparison.Ordinal));
    }

    // ---- Driving the loop ----

    private static List<RateLimitLease> Take(AdaptiveLimiter limiter, int count)
    {
        var leases = new List<RateLimitLease>(count);

        for (var i = 0; i < count; i++)
        {
            var lease = limiter.AttemptAcquire();
            Assert.True(lease.IsAcquired);
            leases.Add(lease);
        }

        return leases;
    }

    private static void Release(IEnumerable<RateLimitLease> leases)
    {
        foreach (var lease in leases)
            lease.Dispose();
    }

    /// <summary>One call at a time, each taking <paramref name="duration" />. Never saturates the limit.</summary>
    private static void Serial(AdaptiveLimiter limiter, FakeTimeProvider time, TimeSpan duration, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using var lease = limiter.AttemptAcquire();
            Assert.True(lease.IsAcquired);
            time.Advance(duration);
        }
    }

    /// <summary><paramref name="concurrency" /> calls overlapping, all taking <paramref name="duration" />.</summary>
    private static void Concurrent(AdaptiveLimiter limiter, FakeTimeProvider time, TimeSpan duration, int concurrency)
    {
        var leases = new List<RateLimitLease>(concurrency);

        for (var i = 0; i < concurrency; i++)
        {
            var lease = limiter.AttemptAcquire();

            if (lease.IsAcquired)
                leases.Add(lease);
            else
                lease.Dispose();
        }

        time.Advance(duration);
        Release(leases);
    }

    /// <summary>
    ///     A dependency that serves <paramref name="capacity" /> calls at a time and queues the rest:
    ///     round-trip time is the base latency multiplied by how far over capacity the caller is. That is
    ///     the whole model, and it is the one the control loop claims to be able to read.
    /// </summary>
    private static void Drive(AdaptiveLimiter limiter, FakeTimeProvider time, int capacity, int rounds)
    {
        for (var round = 0; round < rounds; round++)
        {
            var leases = new List<RateLimitLease>();

            while (true)
            {
                var lease = limiter.AttemptAcquire();

                if (!lease.IsAcquired)
                {
                    lease.Dispose();
                    break;
                }

                leases.Add(lease);
            }

            var load = Math.Max(1.0, (double)leases.Count / capacity);
            time.Advance(Fast * load);
            Release(leases);
        }
    }

    /// <summary>Records <c>nresilience.limiter.limit</c>, which the shared metrics recorder does not see: it is the only <c>int</c> instrument.</summary>
    private sealed class LimitRecording : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(int Value, string? Name)> _measurements = [];

        public LimitRecording()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, ResilienceTelemetry.Meter) && instrument.Name == "nresilience.limiter.limit")
                    listener.EnableMeasurementEvents(instrument);
            };

            _listener.SetMeasurementEventCallback<int>((_, value, tags, _) =>
            {
                lock (_measurements)
                {
                    _measurements.Add((value, Tag(tags)));
                }
            });

            _listener.Start();
        }

        public IReadOnlyList<int> Values
        {
            get
            {
                lock (_measurements)
                {
                    return [.. _measurements.Select(m => m.Value)];
                }
            }
        }

        public IReadOnlyList<string?> Names
        {
            get
            {
                lock (_measurements)
                {
                    return [.. _measurements.Select(m => m.Name)];
                }
            }
        }

        public void Dispose() => _listener.Dispose();

        private static string? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
                if (tag.Key == "nresilience.limiter")
                    return tag.Value as string;

            return null;
        }
    }
}
