using Microsoft.Extensions.Time.Testing;

namespace NResilience.Tests;

/// <summary>
///     Tests for the attempt loop: including the number of runs, stop conditions,
///     and outcome handling.
/// </summary>
public sealed class RetryTests
{
    /// <summary>A policy that retries without sleeping to avoid clock coordination in tests.</summary>
    private static Resilience Instant => Resilience.Default with
    {
        Backoff = Backoff.None,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
        Deadline = Timeout.InfiniteTimeSpan,
    };

    [Fact]
    public async Task Attempts_is_the_total_including_the_first()
    {
        var calls = 0;

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await (Instant with { Attempts = 3 }).RunAsync(ct =>
            {
                calls++;
                throw new TimeoutException();
            }));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task One_attempt_means_no_retry()
    {
        var calls = 0;

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await (Instant with { Attempts = 1 }).RunAsync(ct =>
            {
                calls++;
                throw new TimeoutException();
            }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_transient_failure_that_then_succeeds_returns_the_value()
    {
        var calls = 0;

        var value = await Instant.RunAsync(ct =>
        {
            if (++calls < 3)
                throw new IOException();

            return Task.FromResult(42);
        });

        Assert.Equal(42, value);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_permanent_failure_is_not_retried()
    {
        var calls = 0;

        // Classifier.Default does not recognize InvalidOperationException, so it is Permanent.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Instant.RunAsync(ct =>
            {
                calls++;
                throw new InvalidOperationException();
            }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task The_callback_is_re_invoked_rather_than_the_task_re_awaited()
    {
        var tasks = new List<Task<int>>();

        var value = await Instant.RunAsync(ct =>
        {
            var task = tasks.Count < 2
                ? Task.FromException<int>(new IOException())
                : Task.FromResult(7);

            tasks.Add(task);
            return task;
        });

        Assert.Equal(7, value);
        Assert.Equal(3, tasks.Count);
        Assert.Equal(3, tasks.Distinct().Count());
    }

    [Fact]
    public async Task The_original_exception_is_rethrown_unchanged()
    {
        var thrown = new IOException("the far end hung up");

        var caught = await Assert.ThrowsAsync<IOException>(async () =>
            await Instant.RunAsync(ct => throw thrown));

        Assert.Same(thrown, caught);
        Assert.Equal("the far end hung up", caught.Message);
    }

    [Fact]
    public async Task The_attempt_history_is_attached_to_the_rethrown_exception()
    {
        var caught = await Assert.ThrowsAsync<IOException>(async () =>
            await (Instant with { Attempts = 2 }).RunAsync(ct => throw new IOException()));

        var log = AttemptLog.Of(caught);
        Assert.NotNull(log);
        Assert.Equal(2, log.Count);
        Assert.All(log, a => Assert.Equal(VerdictKind.Transient, a.Verdict.Kind));
    }

    [Fact]
    public async Task A_result_the_classifier_calls_a_failure_is_retried_and_then_returned()
    {
        var calls = 0;

        var policy = Instant with
        {
            Classify = Classifier.Default.OnResult<int>(static code => code == 503 ? Verdict.Transient : Verdict.Ok),
        };

        // An answer the policy judged a failure is still an answer: the caller gets the 503 back
        // rather than an exception, which is what makes the HTTP story work.
        var value = await policy.RunAsync(ct =>
        {
            calls++;
            return Task.FromResult(503);
        });

        Assert.Equal(503, value);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_classifier_cannot_turn_an_exception_into_a_success()
    {
        var calls = 0;
        var policy = Instant with { Classify = Classifier.Default.On<IOException>(Verdict.Ok) };

        await Assert.ThrowsAsync<IOException>(async () =>
            await policy.RunAsync(ct =>
            {
                calls++;
                throw new IOException();
            }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Retries_stop_when_the_deadline_has_no_room_left()
    {
        var time = new FakeTimeProvider();
        var calls = 0;

        var policy = Resilience.Default with
        {
            Time = time,
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = TimeSpan.FromSeconds(1),
            Attempts = 5,
        };

        var caught = await Assert.ThrowsAsync<DeadlineExceededException>(async () =>
            await policy.RunAsync(ct =>
            {
                calls++;
                time.Advance(TimeSpan.FromSeconds(2));
                throw new IOException();
            }));

        Assert.Equal(1, calls);
        Assert.IsType<IOException>(caught.InnerException);
        Assert.Single(caught.Attempts);
    }

    [Fact]
    public async Task The_void_overload_runs_the_loop_and_returns_nothing()
    {
        var calls = 0;

        await Instant.RunAsync(ct =>
        {
            if (++calls < 2)
                throw new IOException();

            return Task.CompletedTask;
        });

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task The_state_overload_threads_state_without_a_closure()
    {
        var value = await Instant.RunAsync(
            static (state, ct) => Task.FromResult(state * 2),
            21);

        Assert.Equal(42, value);
    }

    [Fact]
    public async Task BeforeAttempt_runs_before_every_attempt_including_the_first()
    {
        var seen = new List<int>();

        var policy = Instant with
        {
            Attempts = 3,
            BeforeAttempt = next =>
            {
                seen.Add(next.Number);
                return Task.CompletedTask;
            },
        };

        await Assert.ThrowsAsync<IOException>(async () => await policy.RunAsync(ct => throw new IOException()));

        Assert.Equal([1, 2, 3], seen);
    }

    [Fact]
    public async Task Passthrough_returns_the_callback_task_itself()
    {
        var thrown = new InvalidOperationException();

        var caught = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Resilience.None.RunAsync(ct => Task.FromException<int>(thrown)));

        Assert.Same(thrown, caught);
        Assert.Null(AttemptLog.Of(caught));
    }
}
