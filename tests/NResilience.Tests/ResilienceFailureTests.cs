using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     <see cref="IResilienceFailure" />: the attempt log and the reason, reachable from one
///     <c>catch</c> rather than from a three-arm type switch.
/// </summary>
public sealed class ResilienceFailureTests
{
    [Fact]
    public async Task A_budget_refusal_reports_its_log_and_reason_through_the_interface()
    {
        // No floor, so traffic is the only source of tokens, and no traffic has funded any: the
        // first retry the policy wants is refused.
        var budget = RetryBudget.Of(0.25, 0, new FakeTimeProvider());

        Assert.True(budget.TrySpend());

        var policy = TestPolicy.Instant with { Attempts = 5, Budget = budget };

        var thrown = await Assert.ThrowsAsync<CallRejectedException>(
            () => policy.RunAsync(ct => Task.FromException<int>(new IOException("down")), CancellationToken.None).AsTask());

        var failure = Assert.IsAssignableFrom<IResilienceFailure>(thrown);

        Assert.Equal(StopReason.BudgetExhausted, failure.Reason);
        Assert.NotEmpty(failure.Attempts);
    }

    [Fact]
    public async Task A_deadline_reports_its_log_and_reason_through_the_interface()
    {
        var policy = Resilience.None with
        {
            Attempts = 2,
            Deadline = TimeSpan.FromMilliseconds(1),
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Classifier = Classifier.RetryEverything,
        };

        var thrown = await Assert.ThrowsAsync<DeadlineExceededException>(
            () => policy.RunAsync(
                async ct =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
                    throw new IOException("down");
                },
                CancellationToken.None).AsTask());

        var failure = Assert.IsAssignableFrom<IResilienceFailure>(thrown);

        Assert.Equal(StopReason.DeadlineExceeded, failure.Reason);
        Assert.Same(thrown.Attempts, failure.Attempts);
    }

    [Fact]
    public async Task An_attempt_timeout_that_ends_a_call_reports_the_reason_the_executor_decided()
    {
        var policy = Resilience.None with
        {
            Attempts = 2,
            AttemptTimeout = TimeSpan.FromMilliseconds(20),
            Deadline = Timeout.InfiniteTimeSpan,
            AttemptCeiling = null,
            Backoff = Backoff.None,
        };

        var thrown = await Assert.ThrowsAsync<AttemptTimeoutException>(
            () => policy.RunAsync(ct => Task.Delay(Timeout.InfiniteTimeSpan, ct), CancellationToken.None).AsTask());

        var failure = Assert.IsAssignableFrom<IResilienceFailure>(thrown);

        // Both attempts timed out, so the call ran out of attempts rather than out of deadline.
        Assert.Equal(StopReason.AttemptsExhausted, failure.Reason);
        Assert.Equal(2, failure.Attempts.Count);
    }

    [Fact]
    public void The_two_exceptions_that_are_not_call_failures_stay_out_of_the_interface()
    {
        // RateLimitedException is thrown by consumer code inside an attempt and is classified and
        // retried like any other failure; ResilienceConfigurationException reports a policy that
        // cannot run, which has no call behind it.
        Assert.IsNotAssignableFrom<IResilienceFailure>(new RateLimitedException());
        Assert.IsNotAssignableFrom<IResilienceFailure>(new ResilienceConfigurationException("bad"));
    }

    [Fact]
    public async Task One_catch_reaches_the_log_whichever_of_the_three_ended_the_call()
    {
        var timedOut = await Caught(Resilience.None with
        {
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromMilliseconds(20),
            AttemptCeiling = null,
        });

        var refused = await Caught(TestPolicy.Instant with
        {
            Attempts = 3,
            Breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 1 }),
        });

        foreach (var failure in new[] { timedOut, refused })
        {
            Assert.NotNull(failure);
            Assert.NotEqual(StopReason.Succeeded, failure.Reason);
        }

        return;

        static async Task<IResilienceFailure?> Caught(Resilience policy)
        {
            try
            {
                await policy.RunAsync(
                    async ct =>
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
                        throw new IOException("down");
                    },
                    CancellationToken.None);

                return null;
            }
            catch (Exception e) when (e is IResilienceFailure failure)
            {
                return failure;
            }
        }
    }
}
