using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>The non-throwing entry point, and the attempt history it always carries.</summary>
public sealed class CallResultTests
{
    [Fact]
    public async Task A_success_carries_the_value_and_the_history()
    {
        var result = await TestPolicy.Instant.TryRunAsync(ct => Task.FromResult(42));

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Equal(StopReason.Succeeded, result.StopReason);
        Assert.Single(result.Attempts);
        Assert.Equal(VerdictKind.Ok, result.Attempts[0].Verdict.Kind);
        Assert.True(result.TryGetValue(out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task The_history_survives_a_success_that_took_three_attempts()
    {
        var calls = 0;

        var result = await TestPolicy.Instant.TryRunAsync(ct =>
        {
            return ++calls < 3
                ? Task.FromException<int>(new IOException("nope"))
                : Task.FromResult(1);
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Attempts.Count);
        Assert.Equal([VerdictKind.Transient, VerdictKind.Transient, VerdictKind.Ok], result.Attempts.Select(a => a.Verdict.Kind));
        Assert.IsType<IOException>(result.Attempts[0].Exception);
        Assert.IsType<IOException>(result.Attempts[1].Exception);
        Assert.Null(result.Attempts[2].Exception);
        Assert.Equal([1, 2, 3], result.Attempts.Select(a => a.Number));
    }

    [Fact]
    public async Task A_failure_reports_the_reason_and_does_not_throw()
    {
        var result = await (TestPolicy.Instant with { Attempts = 2 }).TryRunAsync(ct => Task.FromException<int>(new IOException()));

        Assert.False(result.IsSuccess);
        Assert.False(result.HasValue);
        Assert.Equal(StopReason.AttemptsExhausted, result.StopReason);
        Assert.IsType<IOException>(result.Exception);
        Assert.Equal(2, result.Attempts.Count);
        Assert.False(result.TryGetValue(out _));
    }

    [Fact]
    public async Task A_permanent_failure_says_so()
    {
        var result = await TestPolicy.Instant.TryRunAsync(ct => Task.FromException<int>(new InvalidOperationException()));

        Assert.Equal(StopReason.Permanent, result.StopReason);
        Assert.Single(result.Attempts);
    }

    [Fact]
    public async Task A_failing_result_is_still_reported_as_a_value()
    {
        var policy = TestPolicy.Instant with
        {
            Attempts = 2,
            Classify = Classifier.Default.OnResult<int>(static v => v == 503 ? Verdict.Transient : Verdict.Ok),
        };

        var result = await policy.TryRunAsync(ct => Task.FromResult(503));

        Assert.False(result.IsSuccess);
        Assert.True(result.HasValue);
        Assert.Equal(503, result.Value);
        Assert.Null(result.Exception);
        Assert.Equal(StopReason.AttemptsExhausted, result.StopReason);
    }

    [Fact]
    public async Task ValueOrThrow_rethrows_the_original()
    {
        var thrown = new IOException();
        var result = await (TestPolicy.Instant with { Attempts = 1 }).TryRunAsync(ct => Task.FromException<int>(thrown));

        var caught = Assert.Throws<IOException>(() => result.ValueOrThrow());
        Assert.Same(thrown, caught);
    }

    [Fact]
    public async Task The_void_form_reports_the_same_things()
    {
        var result = await (TestPolicy.Instant with { Attempts = 2 }).TryRunAsync(ct => Task.FromException(new IOException()));

        Assert.False(result.IsSuccess);
        Assert.Equal(StopReason.AttemptsExhausted, result.StopReason);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Throws<IOException>(result.ThrowIfFailed);
    }

    [Fact]
    public async Task The_stateful_forms_report_the_same_things()
    {
        var typed = await TestPolicy.Instant.TryRunAsync(static (state, ct) => Task.FromResult(state + 1), 41);
        Assert.Equal(42, typed.Value);

        var untyped = await TestPolicy.Instant.TryRunAsync(static (state, ct) => Task.CompletedTask, "state");
        Assert.True(untyped.IsSuccess);
    }

    [Fact]
    public async Task The_log_records_the_delay_that_preceded_each_attempt()
    {
        var time = new FakeTimeProvider();

        var policy = Resilience.Default with
        {
            Time = time,
            Attempts = 2,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Backoff = Backoff.Constant(TimeSpan.FromSeconds(2)) with { Jitter = Jitter.None },
        };

        var call = policy.TryRunAsync(ct => Task.FromException<int>(new IOException()));

        // The first attempt runs synchronously; the second waits on the virtual clock.
        while (time.GetUtcNow() == default || !call.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();

            if (call.IsCompleted)
                break;
        }

        var result = await call;

        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal(TimeSpan.Zero, result.Attempts[0].DelayBefore);
        Assert.True(result.Attempts[1].DelayBefore >= TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task The_log_prints_something_a_human_can_read()
    {
        var result = await (TestPolicy.Instant with { Attempts = 2 }).TryRunAsync(ct => Task.FromException<int>(new IOException()));

        var text = result.Attempts.ToString();

        Assert.Contains("2 attempts over", text, StringComparison.Ordinal);
        Assert.Contains("Transient IOException", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_log_spills_past_the_inline_buffer_rather_than_truncating()
    {
        var result = await (TestPolicy.Instant with { Attempts = 9 }).TryRunAsync(ct => Task.FromException<int>(new IOException()));

        Assert.Equal(9, result.Attempts.Count);
        Assert.Equal(Enumerable.Range(1, 9), result.Attempts.Select(a => a.Number));
        Assert.All(result.Attempts, a => Assert.IsType<IOException>(a.Exception));
    }

    /// <summary>
    ///     A value an earlier attempt returned survives a later attempt that throws. This is what
    ///     <see cref="CallResult{T}.Value" /> promises - an answer the policy judged a failure is still
    ///     an answer, and for an <c>HttpResponseMessage</c> it is one the caller has to dispose.
    ///     <para>
    ///         Characterisation rather than decoration: the executor threads the attempt's result
    ///         through the invoker, and doing that with an <c>out</c> parameter instead of a
    ///         <c>ref</c> one clears this slot at the start of every attempt. Nothing else notices.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task A_value_from_an_earlier_attempt_outlives_a_later_one_that_threw()
    {
        var calls = 0;

        var policy = TestPolicy.Instant with
        {
            Attempts = 2,
            Classify = Classifier.Default.OnResult<int>(status => status == 503 ? Verdict.Transient : Verdict.Ok),
        };

        var result = await policy.TryRunAsync(ct =>
        {
            calls++;
            return calls == 1 ? Task.FromResult(503) : Task.FromException<int>(new TimeoutException());
        });

        Assert.False(result.IsSuccess);
        Assert.False(result.HasValue);
        Assert.Equal(503, result.Value);
    }

    /// <summary>The same, through a <see cref="ValueTask" />-returning callback.</summary>
    [Fact]
    public async Task A_value_from_an_earlier_ValueTask_attempt_outlives_a_later_one_that_threw()
    {
        var calls = 0;

        var policy = TestPolicy.Instant with
        {
            Attempts = 2,
            Classify = Classifier.Default.OnResult<int>(status => status == 503 ? Verdict.Transient : Verdict.Ok),
        };

        var result = await policy.TryRunAsync(ValueTask<int> (ct) =>
        {
            calls++;
            return calls == 1 ? new ValueTask<int>(503) : ValueTask.FromException<int>(new TimeoutException());
        });

        Assert.False(result.IsSuccess);
        Assert.False(result.HasValue);
        Assert.Equal(503, result.Value);
    }

    [Fact]
    public async Task The_log_records_how_much_of_the_deadline_was_left()
    {
        var policy = Resilience.Default with
        {
            Attempts = 1,
            Deadline = TimeSpan.FromSeconds(30),
            Backoff = Backoff.None,
        };

        var result = await policy.TryRunAsync(ct => Task.FromResult(1));

        Assert.InRange(result.Attempts[0].Remaining, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(30));
    }
}
