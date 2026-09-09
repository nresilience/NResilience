using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.IntegrationTests;

/// <summary>
///     Drain-aware shutdown: the latch, what the executor does about it, and the host subscription that
///     sets it.
///     <para>
///         These live here rather than in the behavioural suite because <see cref="Draining" /> is
///         process-wide by design - a process is either shutting down or it is not - and a latch one
///         test sets is a latch every test running beside it observes. This assembly disables collection
///         parallelism (see <c>xunit.runner.json</c>) so that the window is not shared; the behavioural
///         suite runs 2,500 tests in parallel and cannot make that promise.
///     </para>
/// </summary>
public sealed class DrainingTests : IDisposable
{
    public void Dispose() => Draining.Reset();

    /// <summary>
    ///     A process that has not begun draining reports nothing. The default matters more than it
    ///     looks: every other test in the repository depends on it.
    /// </summary>
    [Fact]
    public void A_process_that_is_not_draining_reports_nothing()
    {
        Assert.False(Draining.IsDraining);
        Assert.Null(Draining.Remaining);
    }

    /// <summary>
    ///     <see cref="Draining.Begin()" /> latches, and without a grace period there is no remaining
    ///     time to report - draining stops retries and leaves deadlines alone.
    /// </summary>
    [Fact]
    public void Begin_latches_and_reports_no_grace_period()
    {
        Draining.Begin();

        Assert.True(Draining.IsDraining);
        Assert.Null(Draining.Remaining);
    }

    /// <summary>
    ///     A grace period decays against its own clock, and reaches zero rather than going negative.
    /// </summary>
    [Fact]
    public void A_grace_period_decays_and_stops_at_zero()
    {
        var time = new FakeTimeProvider();
        Draining.Begin(TimeSpan.FromSeconds(30), time);

        Assert.Equal(TimeSpan.FromSeconds(30), Draining.Remaining);

        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(TimeSpan.FromSeconds(10), Draining.Remaining);

        time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.Zero, Draining.Remaining);
    }

    /// <summary>
    ///     The first call wins. A second signal must not be able to hand a shutting-down process a
    ///     longer grace period than the first one did.
    /// </summary>
    [Fact]
    public void The_first_Begin_wins()
    {
        var time = new FakeTimeProvider();
        Draining.Begin(TimeSpan.FromSeconds(5), time);
        Draining.Begin(TimeSpan.FromSeconds(300), time);

        Assert.Equal(TimeSpan.FromSeconds(5), Draining.Remaining);
    }

    /// <summary>A negative grace period is a mistake, not a request to drain instantly.</summary>
    [Fact]
    public void A_negative_grace_period_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Draining.Begin(TimeSpan.FromSeconds(-1)));
        Assert.False(Draining.IsDraining);
    }

    /// <summary>
    ///     A call that fails while the process is draining stops with the failure it has. The contract:
    ///     one attempt rather than three, the terminal event is <see cref="CallEventKind.Draining" />,
    ///     and the reason is <see cref="StopReason.Draining" />.
    /// </summary>
    [Fact]
    public async Task A_draining_process_does_not_retry()
    {
        var events = new EventRecorder();
        var attempts = 0;

        var policy = Resilience.Default with
        {
            Attempts = 3,
            Backoff = Backoff.None,
            OnEvent = events.Record,
        };

        Draining.Begin();

        var result = await policy.TryRunAsync(_ =>
        {
            attempts++;
            throw new TimeoutException("down");
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(1, attempts);
        Assert.Equal(StopReason.Draining, result.Reason);
        Assert.DoesNotContain(CallEventKind.Retrying, events.Kinds);
        Assert.Equal(CallEventKind.Draining, events.Kinds[^1]);
        Assert.True(events.Events[^1].IsTerminal);
    }

    /// <summary>
    ///     The failure reported is the dependency's, not one the library invented. No guard turned this
    ///     call away - the process simply stopped asking for another attempt - so a
    ///     <c>catch</c> on the dependency's own exception keeps working through a rollout.
    /// </summary>
    [Fact]
    public async Task A_drained_call_reports_the_failure_it_had()
    {
        var policy = Resilience.Default with { Attempts = 3, Backoff = Backoff.None };

        Draining.Begin();

        var thrown = await Assert.ThrowsAsync<TimeoutException>(
            async () => await policy.RunAsync(Task<int> (_) => throw new TimeoutException("down")));

        Assert.Equal("down", thrown.Message);
        Assert.Equal(StopReason.Draining, AttemptLog.ReasonOf(thrown));
    }

    /// <summary>
    ///     A call that starts while the process is draining is bounded by what is left of the grace
    ///     period, not by its own deadline. The policy asks for five minutes; the process has 200ms
    ///     left, so the call ends on the deadline and the caller waits milliseconds rather than minutes.
    /// </summary>
    [Fact]
    public async Task A_deadline_is_clamped_to_the_grace_period()
    {
        var policy = Resilience.Default with
        {
            Attempts = 3,
            Backoff = Backoff.None,
            Deadline = TimeSpan.FromMinutes(5),
            AttemptTimeout = Timeout.InfiniteTimeSpan,
        };

        Draining.Begin(TimeSpan.FromMilliseconds(200));

        var started = DateTimeOffset.UtcNow;

        var result = await policy.TryRunAsync(async token =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5), token);
            return 1;
        });

        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.False(result.IsSuccess);
        Assert.Equal(StopReason.DeadlineExceeded, result.Reason);
        Assert.True(elapsed < TimeSpan.FromSeconds(30), $"the call took {elapsed.TotalSeconds:0.#}s, so the grace period did not bound it");
    }

    /// <summary>
    ///     Nothing is hedged while the process is draining. A hedge is a second copy of a request whose
    ///     answer this process will not be here to read, so the threshold arms no timer at all.
    /// </summary>
    /// <remarks>
    ///     Real time and a wide margin, the way <see cref="HedgedCallTests" /> works: a 50ms floor
    ///     against a one-second stall, so a loaded runner changes nothing about the outcome.
    /// </remarks>
    [Fact]
    public async Task A_draining_process_does_not_hedge()
    {
        var events = new EventRecorder();
        var started = 0;

        var policy = Resilience.Default with
        {
            Attempts = 3,
            Backoff = Backoff.None,
            Hedge = Hedge.At(0.95) with { MinimumSamples = 5, MinimumDelay = TimeSpan.FromMilliseconds(50) },
            OnEvent = events.Record,
        };

        // Instant successes, so the estimate has an opinion and nothing here arms anything.
        for (var i = 0; i < 10; i++)
            await policy.RunAsync(_ => Task.FromResult(0));

        Draining.Begin();

        await policy.RunAsync(async token =>
        {
            Interlocked.Increment(ref started);
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            return 0;
        });

        Assert.Equal(1, started);
        Assert.Empty(events.OfKind(CallEventKind.HedgeStarted));
    }

    /// <summary>
    ///     A host stopping latches the process as draining. This is the whole feature from the outside:
    ///     nothing but <c>AddResilience</c> and a host that stops.
    /// </summary>
    [Fact]
    public async Task Stopping_the_host_begins_draining()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddResilience("api", Resilience.Http);

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.False(Draining.IsDraining);

        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(Draining.IsDraining);
        Assert.NotNull(Draining.Remaining);
    }

    /// <summary>
    ///     A process that must run its outbound calls to their own bounds during shutdown turns the
    ///     subscription off, and stopping the host changes nothing.
    /// </summary>
    [Fact]
    public async Task DrainOnShutdown_false_leaves_the_latch_alone()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddResilience("api", Resilience.Http);
        builder.Services.AddResilienceDraining(o => o.DrainOnShutdown = false);

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(Draining.IsDraining);
    }

    /// <summary>
    ///     The grace period the host announces is the one deadlines are clamped to, and it is
    ///     configurable for an application that needs room after the calls stop.
    /// </summary>
    [Fact]
    public async Task The_configured_grace_period_is_the_one_announced()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddResilience("api", Resilience.Http);
        builder.Services.AddResilienceDraining(o => o.Grace = TimeSpan.FromSeconds(5));

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(Draining.Remaining);
        Assert.InRange(Draining.Remaining.Value, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    ///     A negative grace period fails at startup rather than at shutdown, which is the only time
    ///     anyone can do anything about it.
    /// </summary>
    [Fact]
    public async Task A_negative_configured_grace_period_fails_at_startup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddResilience("api", Resilience.Http);
        builder.Services.AddResilienceDraining(o => o.Grace = TimeSpan.FromSeconds(-1));

        using var host = builder.Build();

        await Assert.ThrowsAsync<ResilienceConfigurationException>(
            async () => await host.StartAsync(TestContext.Current.CancellationToken));
    }
}
