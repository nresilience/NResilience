using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The retry the executor declines to start: one with less time left on the deadline than a call to
///     this dependency has ever taken.
///     <para>
///         The loop already refuses a retry when the backoff delay alone would outlast the deadline.
///         This is the other half of the same question. With 6 ms left and a dependency whose median
///         call is 400 ms, the attempt cannot finish - so starting it sends a real request to a
///         dependency that is probably already struggling, and hands the caller an
///         <c>AttemptTimeoutException</c> where the <c>DeadlineExceededException</c> it was reaching
///         anyway was both truer and available immediately.
///     </para>
///     <para>
///         It can only refuse a <b>retry</b>, it only fires when something is already measuring the
///         dependency, and it changes <i>when</i> the caller learns the deadline is spent rather than
///         <i>what</i> they learn. Only the attempt count differs.
///     </para>
/// </summary>
public sealed class DoomedAttemptTests
{
    /// <summary>What a healthy call to this dependency costs, in every test here.</summary>
    private static readonly TimeSpan Normal = TimeSpan.FromMilliseconds(400);

    /// <summary>Enough successes to put <c>Breaker.NormalLatency</c> above <c>SlowCalls.MinimumSamples</c>.</summary>
    private const int WarmCalls = 25;

    // ---- The feature ----

    /// <summary>
    ///     The point of the whole thing. The first attempt burns all but 50 ms of a 500 ms deadline, and
    ///     the second is not started, because no call to this dependency has ever finished in 50 ms.
    /// </summary>
    [Fact]
    public async Task A_retry_with_less_time_left_than_a_call_needs_is_not_started()
    {
        var time = new FakeTimeProvider();
        var policy = Measured(time);

        await WarmAsync(policy, time);

        // The estimator reports a bucket's upper bound, so the baseline is Normal or a little above it,
        // never below.
        Assert.InRange(policy.Breaker!.NormalLatency!.Value, Normal, Normal * 1.15);

        var result = await FailAsync(policy, time, spend: TimeSpan.FromMilliseconds(450));

        Assert.False(result.IsSuccess);
        Assert.Equal(StopReason.DeadlineExceeded, result.StopReason);
        Assert.Single(result.Attempts);
    }

    /// <summary>
    ///     The same policy with nothing measuring the dependency: the breaker's slow-call trip has been
    ///     turned off, so there is no baseline, and the loop behaves exactly as it did before this
    ///     feature existed. The doomed attempt runs, and the caller learns the same thing 50 ms later.
    /// </summary>
    [Fact]
    public async Task Without_an_estimate_the_doomed_attempt_still_runs()
    {
        var time = new FakeTimeProvider();
        var policy = Measured(time, new BreakerSettings { SlowCalls = null, Time = time });

        await WarmAsync(policy, time);

        Assert.Null(policy.Breaker!.NormalLatency);

        var result = await FailAsync(policy, time, spend: TimeSpan.FromMilliseconds(450));

        Assert.False(result.IsSuccess);

        // The same stop reason. What differs is that a second request reached a dependency nobody was
        // going to wait for.
        Assert.Equal(StopReason.DeadlineExceeded, result.StopReason);
        Assert.Equal(2, result.Attempts.Count);
    }

    /// <summary>
    ///     A cold estimate is no estimate. Below <c>SlowCalls.MinimumSamples</c> the baseline is null and
    ///     the behaviour is today's, byte for byte - which is what makes this feature invisible to a
    ///     process that has just started.
    /// </summary>
    [Fact]
    public async Task A_cold_estimate_refuses_nothing()
    {
        var time = new FakeTimeProvider();
        var policy = Measured(time);

        await WarmAsync(policy, time, calls: 5);

        Assert.Null(policy.Breaker!.NormalLatency);

        var result = await FailAsync(policy, time, spend: TimeSpan.FromMilliseconds(450));

        Assert.Equal(2, result.Attempts.Count);
    }

    /// <summary>
    ///     The first attempt of every call runs whatever the estimate says, because the first attempt is
    ///     the one the caller asked for. Here the deadline is a tenth of what a healthy call costs and
    ///     the attempt is still made.
    /// </summary>
    [Fact]
    public async Task The_first_attempt_always_runs()
    {
        var time = new FakeTimeProvider();
        var policy = Measured(time);

        await WarmAsync(policy, time);

        var attempts = 0;

        var result = await (policy with { Deadline = TimeSpan.FromMilliseconds(40) }).TryRunAsync(_ =>
        {
            attempts++;
            time.Advance(TimeSpan.FromMilliseconds(40));

            throw new IOException();
        });

        Assert.Equal(1, attempts);
        Assert.False(result.IsSuccess);
    }

    /// <summary>
    ///     A retry with room for a call still happens. The estimate refuses attempts that cannot finish,
    ///     not attempts that merely might not - so 500 ms left against a 400 ms baseline is a retry.
    /// </summary>
    [Fact]
    public async Task A_retry_with_room_for_a_call_is_still_started()
    {
        var time = new FakeTimeProvider();
        var policy = Measured(time) with { Deadline = TimeSpan.FromMilliseconds(1000) };

        await WarmAsync(policy, time);

        var result = await FailAsync(policy, time, spend: TimeSpan.FromMilliseconds(450), deadline: TimeSpan.FromMilliseconds(1000));

        Assert.Equal(2, result.Attempts.Count);
    }

    /// <summary>
    ///     An unbounded call has no deadline to run out of, so there is nothing for the estimate to
    ///     compare against and every attempt the policy allows is made.
    /// </summary>
    [Fact]
    public async Task An_unbounded_call_is_untouched()
    {
        var time = new FakeTimeProvider();
        var policy = Measured(time);

        await WarmAsync(policy, time);

        var result = await FailAsync(policy, time, spend: TimeSpan.FromHours(1), deadline: Timeout.InfiniteTimeSpan);

        Assert.Equal(3, result.Attempts.Count);
    }

    /// <summary>
    ///     The backoff and the estimate are added, not chosen between: a delay that fits and an attempt
    ///     that fits are still a retry that does not.
    /// </summary>
    [Fact]
    public async Task The_backoff_and_the_estimate_are_weighed_together()
    {
        var time = new FakeTimeProvider();

        // 900 ms left after the first attempt: more than the 400 ms baseline on its own, and more than
        // the 600 ms backoff on its own. Together they overrun it.
        var backoff = Backoff.Constant(TimeSpan.FromMilliseconds(600)) with { Jitter = Jitter.None };
        var policy = Measured(time) with { Backoff = backoff };

        await WarmAsync(policy, time);

        // Given a deadline rather than awaited outright: a retry the estimate failed to refuse would
        // park on a 600 ms backoff nobody is going to advance, and a hung run says less than a failure.
        var call = FailAsync(policy, time, spend: TimeSpan.FromMilliseconds(600), deadline: TimeSpan.FromMilliseconds(1500));
        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StopReason.DeadlineExceeded, result.StopReason);
        Assert.Single(result.Attempts);
    }

    // ---- Harness ----

    /// <summary>
    ///     A policy whose breaker measures the dependency, on one clock throughout. Backoff is off so
    ///     that the estimate is the only thing standing between a failure and the next attempt.
    /// </summary>
    private static Resilience Measured(FakeTimeProvider time, BreakerSettings? settings = null) =>
        TestPolicy.On(time) with
        {
            Name = "api",
            Attempts = 3,
            Deadline = TimeSpan.FromMilliseconds(500),
            Breaker = new Breaker(settings ?? new BreakerSettings { Time = time }),

            // The measured per-attempt ceiling is a different feature reading a different quantile, and
            // an attempt that is cancelled early would change the arithmetic these tests are about.
            Timeouts = null,
        };

    /// <summary>
    ///     Records successful calls of <see cref="Normal" /> into the breaker's baseline. The callback
    ///     advances the clock and completes synchronously, so nothing can time out however long the
    ///     "call" claims to have taken.
    /// </summary>
    private static async Task WarmAsync(Resilience policy, FakeTimeProvider time, int calls = WarmCalls)
    {
        var warm = policy with { Deadline = Timeout.InfiniteTimeSpan };

        for (var i = 0; i < calls; i++)
        {
            await warm.RunAsync(_ =>
            {
                time.Advance(Normal);
                return Task.FromResult(1);
            });
        }
    }

    /// <summary>
    ///     One call whose every attempt spends <paramref name="spend" /> of the deadline and then fails
    ///     transiently.
    /// </summary>
    private static async Task<CallResult<int>> FailAsync(
        Resilience policy,
        FakeTimeProvider time,
        TimeSpan spend,
        TimeSpan? deadline = null)
    {
        var bounded = policy with { Deadline = deadline ?? policy.Deadline };

        return await bounded.TryRunAsync<int>(_ =>
        {
            time.Advance(spend);

            throw new IOException();
        });
    }
}
