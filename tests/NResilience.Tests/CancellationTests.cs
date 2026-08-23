using Microsoft.Extensions.Time.Testing;

namespace NResilience.Tests;

/// <summary>
/// The cancellation contract: the caller's token always wins, an attempt timeout never reaches
/// the classifier, and the two are never confused for one another.
/// </summary>
public sealed class CancellationTests
{
    [Fact]
    public async Task Caller_cancellation_before_the_first_attempt_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        int calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Resilience.Default.RunAsync(
                ct =>
                {
                    calls++;
                    return Task.FromResult(1);
                },
                cts.Token));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Caller_cancellation_is_never_retried()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Resilience.Default.RunAsync(
                ct =>
                {
                    calls++;
                    cts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(1);
                },
                cts.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_out_of_TryRunAsync()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // The one thing the non-throwing entry point still throws.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Resilience.Default.TryRunAsync(ct => Task.FromResult(1), cts.Token));
    }

    [Fact]
    public async Task Cancellation_during_a_backoff_delay_aborts_the_operation()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;

        Resilience policy = Resilience.Default with
        {
            Backoff = Backoff.Constant(TimeSpan.FromSeconds(30)),
            Deadline = Timeout.InfiniteTimeSpan,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
        };

        ValueTask call = policy.RunAsync(
            ct =>
            {
                calls++;
                return Task.FromException(new IOException());
            },
            cts.Token);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task An_attempt_timeout_is_transient_and_is_retried()
    {
        var time = new FakeTimeProvider();
        int calls = 0;

        Resilience policy = Resilience.Default with
        {
            Time = time,
            Attempts = 2,
            AttemptTimeout = TimeSpan.FromSeconds(5),
            Deadline = Timeout.InfiniteTimeSpan,
            Backoff = Backoff.None,
        };

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask<int> call = policy.RunAsync(async ct =>
        {
            if (++calls == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return 9;
        });

        await started.Task;

        // CancelAfter honors an injected provider, so virtual time cancels the attempt.
        time.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal(9, await call);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task An_attempt_timeout_on_the_last_attempt_surfaces_as_AttemptTimeoutException()
    {
        var time = new FakeTimeProvider();

        Resilience policy = Resilience.Default with
        {
            Time = time,
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromSeconds(5),
            Deadline = Timeout.InfiniteTimeSpan,
            Backoff = Backoff.None,
        };

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask<int> call = policy.RunAsync(async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });

        await started.Task;
        time.Advance(TimeSpan.FromSeconds(6));

        AttemptTimeoutException caught = await Assert.ThrowsAsync<AttemptTimeoutException>(async () => await call);
        Assert.Equal(TimeSpan.FromSeconds(5), caught.Timeout);
        Assert.Single(caught.Attempts);
        Assert.Equal(VerdictKind.Transient, caught.Attempts[0].Verdict.Kind);
    }

    [Fact]
    public async Task The_attempt_timeout_is_clamped_to_the_time_left_on_the_deadline()
    {
        var time = new FakeTimeProvider();

        Resilience policy = Resilience.Default with
        {
            Time = time,
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromMinutes(10),
            Deadline = TimeSpan.FromSeconds(5),
            Backoff = Backoff.None,
        };

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask<int> call = policy.RunAsync(async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 1;
        });

        await started.Task;

        // The configured ceiling is ten minutes; the effective one is the five seconds left.
        time.Advance(TimeSpan.FromSeconds(6));

        AttemptTimeoutException caught = await Assert.ThrowsAsync<AttemptTimeoutException>(async () => await call);
        Assert.Equal(TimeSpan.FromSeconds(5), caught.Timeout);
    }

    [Fact]
    public async Task A_cancellable_caller_token_still_reports_our_own_timeout_as_a_timeout()
    {
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        Resilience policy = Resilience.Default with
        {
            Time = time,
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromSeconds(5),
            Deadline = Timeout.InfiniteTimeSpan,
            Backoff = Backoff.None,
        };

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask<int> call = policy.RunAsync(
            async ct =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return 1;
            },
            cts.Token);

        await started.Task;
        time.Advance(TimeSpan.FromSeconds(6));

        await Assert.ThrowsAsync<AttemptTimeoutException>(async () => await call);
    }

    [Fact]
    public async Task Cancelling_while_an_attempt_is_succeeding_does_not_discard_the_result()
    {
        using var cts = new CancellationTokenSource();

        // The post-attempt cancellation check stops the loop starting another attempt. An attempt
        // that already succeeded has been waited for either way, so its result is returned.
        int value = await Resilience.Default.RunAsync(
            ct =>
            {
                cts.Cancel();
                return Task.FromResult(11);
            },
            cts.Token);

        Assert.Equal(11, value);
    }

    [Fact]
    public async Task Cancelling_while_an_attempt_is_failing_aborts_instead_of_retrying()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;

        Resilience policy = Resilience.Default with { Backoff = Backoff.None, Attempts = 3 };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await policy.RunAsync(
                ct =>
                {
                    calls++;
                    cts.Cancel();
                    return Task.FromException<int>(new IOException());
                },
                cts.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task The_callback_receives_a_token_even_when_the_caller_passed_none()
    {
        var time = new FakeTimeProvider();
        Resilience policy = Resilience.Default with { Time = time, Attempts = 1 };

        bool cancellable = await policy.RunAsync(ct => Task.FromResult(ct.CanBeCanceled));

        Assert.True(cancellable);
    }
}
