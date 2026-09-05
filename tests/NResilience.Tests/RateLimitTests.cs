using System.Threading.RateLimiting;
using Microsoft.Extensions.Time.Testing;
using NResilience.Extensions;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     Tests for local admission control: including how the executor handles a <see cref="RateLimitedException" />
///     and the guards that behave differently for it.
///     <para>
///         A refusal the process imposes on itself is considered throttling. This involves the long backoff
///         curve and honoring the limiter's hint verbatim, and it is not evidence about the dependency.
///         The breaker already ignores anything that is not <see cref="VerdictKind.Transient" />; the
///         retry budget did not, and these tests verify that behavior.
///     </para>
/// </summary>
public sealed class RateLimitTests
{
    /// <summary>
    ///     Runs a call that may serve a guarded rejection and moves the fake clock until it lands.
    ///     This follows the pattern in <c>BudgetTests</c> because a rejection is not instant.
    /// </summary>
    private static async Task<CallResult<int>> RunAsync(Resilience policy, Func<CancellationToken, Task<int>> work, FakeTimeProvider time)
    {
        var call = policy.TryRunAsync(work).AsTask();

        while (!call.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1);
        }

        return await call;
    }

    // ---- The verdict ----

    [Fact]
    public void Refused_is_throttling_that_knows_where_it_came_from()
    {
        var refused = Verdict.Refused(TimeSpan.FromSeconds(2));

        Assert.Equal(VerdictKind.Throttled, refused.Kind);
        Assert.True(refused.SelfImposed);
        Assert.Equal(TimeSpan.FromSeconds(2), refused.RetryAfter);
    }

    [Fact]
    public void Server_throttling_and_self_throttling_are_not_the_same_verdict()
    {
        Assert.NotEqual(Verdict.Throttled(), Verdict.Refused());
        Assert.NotEqual(Verdict.Throttled(TimeSpan.FromSeconds(1)), Verdict.Refused(TimeSpan.FromSeconds(1)));
        Assert.Equal(Verdict.Refused(), Verdict.Refused());
    }

    [Fact]
    public void The_default_verdict_is_not_self_imposed()
    {
        // The polarity matters: false is the conservative answer, so a default-constructed verdict
        // cannot claim exemption from the retry budget.
        Assert.False(default(Verdict).SelfImposed);
        Assert.False(Verdict.Ok.SelfImposed);
        Assert.False(Verdict.Throttled().SelfImposed);
    }

    [Fact]
    public void A_self_imposed_verdict_says_so_when_printed()
    {
        Assert.Equal("Throttled (self-imposed)", Verdict.Refused().ToString());
        Assert.Equal("Throttled (self-imposed, retry after 0.5s)", Verdict.Refused(TimeSpan.FromMilliseconds(500)).ToString());
        Assert.Equal("Throttled (retry after 0.5s)", Verdict.Throttled(TimeSpan.FromMilliseconds(500)).ToString());
    }

    // ---- The budget ----

    [Fact]
    public async Task Self_imposed_refusal_does_not_spend_retry_budget()
    {
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Of(minimumPerSecond: 1, time: time);
        var policy = TestPolicy.On(time) with { Attempts = 4, Budget = budget };

        var result = await RunAsync(
            policy,
            _ => throw new RateLimitedException(limiter: "api"),
            time);

        // Four attempts, three retries, and not one of them charged: the dependency was never
        // called, so there was nothing to fund.
        Assert.False(result.IsSuccess);
        Assert.Equal(4, result.Attempts.Count);
        Assert.Equal(StopReason.AttemptsExhausted, result.Reason);
        Assert.Equal(0, budget.Utilization);
    }

    [Fact]
    public async Task Server_throttling_still_spends_retry_budget()
    {
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Of(minimumPerSecond: 1, time: time);

        var policy = TestPolicy.On(time) with
        {
            Attempts = 4,
            Budget = budget,
            Classifier = Classifier.Default.On<InvalidOperationException>(Verdict.Throttled()),
        };

        var result = await RunAsync(
            policy,
            _ => throw new InvalidOperationException("429 from the server"),
            time);

        Assert.False(result.IsSuccess);
        Assert.True(budget.Utilization > 0, "a server's pushback is still charged to the budget");
    }

    [Fact]
    public async Task A_budget_already_exhausted_still_does_not_stop_a_self_imposed_retry()
    {
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Of(minimumPerSecond: 1, time: time);

        // Drain it, so any charge at all would refuse the retry.
        while (budget.TrySpend())
        {
        }

        var policy = TestPolicy.On(time) with { Attempts = 3, Budget = budget };

        var result = await RunAsync(policy, _ => throw new RateLimitedException(limiter: "api"), time);

        Assert.Equal(3, result.Attempts.Count);
        Assert.Equal(StopReason.AttemptsExhausted, result.Reason);
    }

    // ---- The breaker ----

    [Fact]
    public async Task Self_imposed_refusal_does_not_open_the_breaker()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 2, Time = time });
        var policy = TestPolicy.On(time) with { Attempts = 6, Breaker = breaker };

        var result = await RunAsync(policy, _ => throw new RateLimitedException(limiter: "api"), time);

        Assert.Equal(6, result.Attempts.Count);
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public async Task Self_imposed_refusal_releases_a_half_open_probe_slot()
    {
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings
        {
            ConsecutiveFailures = 1,
            BreakDuration = TimeSpan.FromSeconds(1),
            HalfOpenProbes = 1,
            Time = time,
        });

        var single = TestPolicy.On(time) with { Attempts = 1, Breaker = breaker };

        // Trip it, then wait out the break so the next call becomes a probe.
        await RunAsync(single, _ => throw new IOException("down"), time);
        Assert.Equal(BreakerState.Open, breaker.State);
        time.Advance(TimeSpan.FromSeconds(2));

        // The probe is refused by local admission control rather than by the dependency. The slot
        // has to come back, or the breaker never probes again and wedges half-open forever.
        var refused = await RunAsync(single, _ => throw new RateLimitedException(limiter: "api"), time);
        Assert.False(refused.IsSuccess);

        var probe = await RunAsync(single, _ => Task.FromResult(1), time);
        Assert.True(probe.IsSuccess, "the refused probe did not release its slot");
    }

    // ---- The backoff ----

    [Fact]
    public async Task A_limiter_hint_is_honored_in_preference_to_the_backoff_curve()
    {
        var time = new FakeTimeProvider();
        var delays = new List<TimeSpan>();

        var policy = Resilience.Default with
        {
            Attempts = 2,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Time = time,
            Backoff = Backoff.Exponential(throttledBase: TimeSpan.FromMinutes(5)) with { Jitter = Jitter.None },
            OnEvent = e =>
            {
                if (e.Kind == CallEventKind.Retrying && e.Delay is { } delay)
                    delays.Add(delay);
            },
        };

        var result = await RunAsync(
            policy,
            _ => throw new RateLimitedException(TimeSpan.FromSeconds(3), "api"),
            time);

        Assert.False(result.IsSuccess);
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.Single(delays));
    }

    // ---- The classifier ----

    [Fact]
    public async Task A_classifier_never_sees_a_rate_limited_exception()
    {
        var time = new FakeTimeProvider();
        var seen = new List<Type>();

        var policy = TestPolicy.On(time) with
        {
            Attempts = 2,
            Classifier = Classifier.RetryEverything.On<Exception>(ex =>
            {
                seen.Add(ex.GetType());
                return Verdict.Transient;
            }),
        };

        var result = await RunAsync(policy, _ => throw new RateLimitedException(limiter: "api"), time);

        Assert.Empty(seen);
        Assert.True(result.Attempts[0].Verdict.SelfImposed);
    }

    // ---- The attempt log ----

    [Fact]
    public async Task Self_imposed_survives_into_the_attempt_log_inline_and_spilled()
    {
        var time = new FakeTimeProvider();

        // Six attempts, so four land in the inline buffer and two in the spill array. The flag
        // rides in the packed verdict byte and has to survive both paths.
        var policy = TestPolicy.On(time) with { Attempts = 6, Budget = RetryBudget.None };

        var result = await RunAsync(policy, _ => throw new RateLimitedException(limiter: "api"), time);

        Assert.Equal(6, result.Attempts.Count);

        Assert.All(result.Attempts, attempt =>
        {
            Assert.Equal(VerdictKind.Throttled, attempt.Verdict.Kind);
            Assert.True(attempt.Verdict.SelfImposed);
        });
    }

    [Fact]
    public async Task Server_throttling_is_distinguishable_from_self_throttling_in_the_log()
    {
        var time = new FakeTimeProvider();
        var calls = 0;

        var policy = TestPolicy.On(time) with
        {
            Attempts = 3,
            Budget = RetryBudget.None,
            Classifier = Classifier.Default.On<InvalidOperationException>(Verdict.Throttled()),
        };

        var result = await RunAsync(
            policy,
            _ => calls++ == 0
                ? throw new InvalidOperationException("429")
                : throw new RateLimitedException(limiter: "api"),
            time);

        Assert.Equal(3, result.Attempts.Count);
        Assert.False(result.Attempts[0].Verdict.SelfImposed);
        Assert.True(result.Attempts[1].Verdict.SelfImposed);
    }

    // ---- The exception the caller ends on ----

    [Fact]
    public async Task The_refusal_is_what_surfaces_when_the_attempts_run_out()
    {
        var time = new FakeTimeProvider();
        var policy = TestPolicy.On(time) with { Attempts = 2, Budget = RetryBudget.None };

        var call = policy.RunAsync(_ => Task.FromException<int>(new RateLimitedException(TimeSpan.FromSeconds(4), "payments"))).AsTask();

        while (!call.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1);
        }

        var error = await Assert.ThrowsAsync<RateLimitedException>(() => call);

        Assert.Equal("payments", error.Limiter);
        Assert.Equal(TimeSpan.FromSeconds(4), error.RetryAfter);
        Assert.Contains("payments", error.Message, StringComparison.Ordinal);
    }

    // ---- The adapter ----

    [Fact]
    public async Task A_limiter_with_no_permits_left_throws_rather_than_returning_a_dead_lease()
    {
        using var limiter = Limit.Concurrency(1);

        using var held = await limiter.AcquireOrThrowAsync("bulk");

        var error = await Assert.ThrowsAsync<RateLimitedException>(async () => await limiter.AcquireOrThrowAsync("bulk"));

        Assert.Equal("bulk", error.Limiter);
    }

    [Fact]
    public async Task A_released_permit_is_available_again()
    {
        using var limiter = Limit.Concurrency(1);

        using (await limiter.AcquireOrThrowAsync())
        {
        }

        using var second = await limiter.AcquireOrThrowAsync();
        Assert.True(second.IsAcquired);
    }

    [Fact]
    public async Task A_limiter_inside_the_callback_is_acquired_once_per_attempt()
    {
        var time = new FakeTimeProvider();

        // Two permits for three attempts: the third is refused, and the refusal is a retry rather
        // than a failure of the dependency.
        using var limiter = Limit.Concurrency(2);
        var held = new List<RateLimitLease>();

        var policy = TestPolicy.On(time) with { Attempts = 3, Budget = RetryBudget.None };

        var result = await RunAsync(
            policy,
            async ct =>
            {
                // Deliberately not disposed, so each attempt consumes a permit.
                held.Add(await limiter.AcquireOrThrowAsync("api", ct));
                throw new IOException("transient");
            },
            time);

        Assert.Equal(2, held.Count);
        Assert.Equal(3, result.Attempts.Count);
        Assert.False(result.Attempts[1].Verdict.SelfImposed);
        Assert.True(result.Attempts[2].Verdict.SelfImposed);

        foreach (var lease in held)
        {
            lease.Dispose();
        }
    }

    // ---- The options ----

    [Fact]
    public void Options_describing_no_limiter_are_refused()
    {
        var error = Assert.Throws<ResilienceConfigurationException>(() => new RateLimitOptions().Validate());

        Assert.Contains("Set one of", Assert.Single(error.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void Options_describing_two_limiters_are_refused_rather_than_resolved()
    {
        var error = Assert.Throws<ResilienceConfigurationException>(() => new RateLimitOptions { PermitsPerSecond = 10, Concurrency = 2 }.Validate());

        Assert.Contains("four different guards", Assert.Single(error.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_window_needs_both_halves()
    {
        var error = Assert.Throws<ResilienceConfigurationException>(() => new RateLimitOptions { Permits = 100 }.Validate());

        Assert.Contains("must be set together", Assert.Single(error.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_problem_is_listed_at_once()
    {
        var error = Assert.Throws<ResilienceConfigurationException>(() => new RateLimitOptions { PermitsPerSecond = 0, QueueLimit = -1 }.Validate());

        Assert.Equal(2, error.Problems.Count);
    }

    [Fact]
    public void Queueing_is_off_by_default()
    {
        Assert.Equal(0, new RateLimitOptions().QueueLimit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_limiter_must_allow_at_least_one_call(int permits)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Limit.PerSecond(permits));
        Assert.Throws<ArgumentOutOfRangeException>(() => Limit.Concurrency(permits));
        Assert.Throws<ArgumentOutOfRangeException>(() => Limit.PerWindow(permits, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_window_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Limit.PerWindow(10, TimeSpan.Zero));
    }

    [Fact]
    public void Each_shape_of_options_builds_the_limiter_it_describes()
    {
        using (new RateLimitOptions { PermitsPerSecond = 10 }.ToLimiter())
        {
        }

        using (new RateLimitOptions { Permits = 10, Window = TimeSpan.FromMinutes(1) }.ToLimiter())
        {
        }

        using (new RateLimitOptions { Concurrency = 4 }.ToLimiter())
        {
        }
    }
}
