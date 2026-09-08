using NResilience.Internal;

namespace NResilience.IntegrationTests;

/// <summary>
///     The collection that owns the thread pool while it runs.
///     <para>
///         The one test in it fills every pool thread on purpose, which is the opposite of what the
///         Kestrel-hosted and loopback-socket suites beside it need. <c>DisableParallelization</c> keeps
///         those two facts from being true at the same time.
///     </para>
/// </summary>
[CollectionDefinition("thread pool", DisableParallelization = true)]
public sealed class ThreadPoolCollection;

/// <summary>
///     <c>Resilience.Saturation</c> against a thread pool that is really starved.
///     <para>
///         The behavioural suite drives saturation through a seam, because starving the test host's
///         pool to assert a decision is a way to write a flaky test rather than a thorough one. That
///         leaves one thing unproven, and it is the thing the whole feature rests on: that the number
///         the seam substitutes is the number the real probe measures. This is where that is shown, once.
///     </para>
/// </summary>
[Collection("thread pool")]
public sealed class ThreadPoolSaturationTests
{
    /// <summary>
    ///     A probe queued behind blocked work reports the queue, and it does so while the sample is
    ///     still outstanding - which is the case the design turns on. A starved pool cannot run the
    ///     probe that measures the starvation, so an overdue sample is itself the reading; anything
    ///     that waited for the callback would report the microsecond it measured before the incident.
    /// </summary>
    /// <remarks>
    ///     Every number here is relative to what this machine measured a moment earlier, because the
    ///     claim is "the probe noticed" rather than "the probe measured 412 ms" - and a wall-clock
    ///     figure asserted on a test host running a suite in parallel is a flaky test with extra steps.
    /// </remarks>
    [Fact]
    public void The_probe_measures_a_pool_that_is_really_starved()
    {
        var idle = Warm();

        // Sized against the threads the pool actually has, not against the core count. A warm test
        // host has accumulated far more threads than it has cores, and a backlog smaller than that
        // starts running immediately and queues nothing - which is a test that measures whether the
        // machine was busy.
        var blockers = ThreadPool.ThreadCount + (Environment.ProcessorCount * 2);
        using var release = new ManualResetEventSlim(initialState: false);

        for (var i = 0; i < blockers; i++)
        {
            // A hard ceiling on each blocker as well as the explicit release below: every thread that
            // could set the event is a thread this loop may have just blocked, so the blockers have to
            // be able to end without one.
            ThreadPool.UnsafeQueueUserWorkItem(
                static state => state.Wait(TimeSpan.FromSeconds(5)),
                release,
                preferLocal: false);
        }

        // Five times normal is the multiple Saturation.Above() defaults to, so this is the comparison
        // the executor makes rather than one invented for the test.
        var target = idle * 5;
        var starved = TimeSpan.Zero;
        PoolProbe.Reading reading = default;

        try
        {
            // Thread.Sleep rather than Task.Delay: a continuation would be queued behind the very
            // backlog this test just created, and the loop would not resume until it had drained.
            for (var i = 0; i < 20 && starved <= target; i++)
            {
                reading = PoolProbe.Read(minimumSamples: 1);

                if (reading.Delay > starved)
                    starved = reading.Delay;

                Thread.Sleep(millisecondsTimeout: 25);
            }
        }
        finally
        {
            release.Set();
        }

        Assert.True(
            starved > target,
            $"A pool with {blockers} blocked work items measured a queue delay of {starved.TotalMilliseconds:0.000} ms, "
            + $"against {idle.TotalMilliseconds:0.000} ms while idle - under the 5x that Saturation.Above() compares against. "
            + "The probe is not measuring the queue it is queued into.");

        // And the decision the executor makes, made on that reading rather than on a substituted one.
        // The floor is what a real configuration sets to ignore an idle pool's microseconds; here it is
        // derived from what idle actually measured, so the assertion is about the multiple.
        var settings = Saturation.Above() with { Floor = idle * 2 };

        Assert.True(
            settings.IsSaturated(new PoolProbe.Reading(starved, idle)),
            $"A queue delay of {starved.TotalMilliseconds:0.000} ms against a normal of {idle.TotalMilliseconds:0.000} ms "
            + "did not read as saturated, so the reading and the decision disagree.");
    }

    /// <summary>
    ///     Warms the process-wide baseline on an idle pool and reports what idle looks like.
    /// </summary>
    /// <returns>The median queue delay of a pool with nothing to do, and never zero.</returns>
    /// <remarks>
    ///     A read is what paces the probes - at most one every <c>PoolProbe.Interval</c> - so this loop
    ///     is what makes the baseline exist at all. It runs before the pool is loaded, so the median it
    ///     leaves behind is a median of a healthy process.
    /// </remarks>
    private static TimeSpan Warm()
    {
        TimeSpan? normal = null;

        for (var i = 0; i < 24 && normal is null; i++)
        {
            normal = PoolProbe.Read(minimumSamples: 8).Normal;
            Thread.Sleep(millisecondsTimeout: 130);
        }

        Assert.NotNull(normal);

        // A healthy pool can measure below the estimator's smallest bucket, and a baseline of zero
        // would make every multiple of it trivially satisfied. One microsecond is the floor the
        // histogram itself resolves to.
        return normal.Value > TimeSpan.FromMicroseconds(1) ? normal.Value : TimeSpan.FromMicroseconds(1);
    }
}
