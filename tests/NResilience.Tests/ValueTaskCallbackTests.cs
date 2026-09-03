using System.Threading.Tasks.Sources;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     Tests for <see cref="ValueTask" />-returning callback overloads.
///     <para>
///         These tests specifically verify that callbacks returning a pooled
///         <see cref="IValueTaskSource" /> survive retries. Because a <see cref="ValueTask" />
///         can be awaited only once, a loop that incorrectly retains a <see cref="ValueTask" />
///         from a previous attempt would fail. These tests use a source that recycles its
///         token, mimicking the behavior of <c>Socket</c> and <c>Channel</c>.
///     </para>
/// </summary>
public sealed class ValueTaskCallbackTests
{
    [Fact]
    public async Task A_pooled_source_is_re_invoked_rather_than_re_awaited()
    {
        var source = new PooledSource(2);

        var value = await TestPolicy.Instant.RunAsync(static (s, ct) => s.ReadAsync(ct), source);

        Assert.Equal(PooledSource.Value, value);
        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task A_synchronously_completing_callback_returns_its_value()
    {
        var source = new PooledSource(0);

        Assert.Equal(PooledSource.Value, await TestPolicy.Instant.RunAsync(static (s, ct) => s.ReadAsync(ct), source));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task An_asynchronously_completing_callback_returns_its_value()
    {
        var source = new PooledSource(0, true);

        Assert.Equal(PooledSource.Value, await TestPolicy.Instant.RunAsync(static (s, ct) => s.ReadAsync(ct), source));
    }

    [Fact]
    public async Task A_faulted_callback_is_classified_retried_and_finally_rethrown()
    {
        var source = new PooledSource(int.MaxValue);

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await TestPolicy.Instant.RunAsync(static (s, ct) => s.ReadAsync(ct), source));

        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task A_callback_that_throws_before_returning_a_task_is_classified()
    {
        var calls = 0;

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await TestPolicy.Instant.RunAsync(ValueTask<int> (ct) =>
            {
                calls++;
                throw new TimeoutException();
            }));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task A_permanent_verdict_stops_the_loop()
    {
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await TestPolicy.Instant.RunAsync(ValueTask<int> (ct) =>
            {
                calls++;
                return ValueTask.FromException<int>(new InvalidOperationException());
            }));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TryRunAsync_reports_the_attempt_log()
    {
        var source = new PooledSource(1);

        var result = await TestPolicy.Instant.TryRunAsync(static (s, ct) => s.ReadAsync(ct), source);

        Assert.True(result.IsSuccess);
        Assert.Equal(PooledSource.Value, result.Value);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal(VerdictKind.Transient, result.Attempts[0].Verdict.Kind);
        Assert.Equal(VerdictKind.Ok, result.Attempts[1].Verdict.Kind);
    }

    [Fact]
    public async Task TryRunAsync_reports_a_failure_rather_than_throwing()
    {
        var result = await TestPolicy.Instant.TryRunAsync(ValueTask<int> (ct) => ValueTask.FromException<int>(new TimeoutException()));

        Assert.False(result.IsSuccess);
        Assert.Equal(StopReason.AttemptsExhausted, result.StopReason);
        Assert.IsType<TimeoutException>(result.Exception);
    }

    [Fact]
    public async Task The_void_form_runs_the_callback()
    {
        var calls = 0;

        await TestPolicy.Instant.RunAsync(ValueTask (ct) =>
        {
            calls++;
            return default;
        });

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task The_void_form_with_state_retries()
    {
        var calls = 0;

        var result = await TestPolicy.Instant.TryRunAsync(
            static (counter, ct) =>
            {
                counter.Bump();
                return counter.Count < 2 ? ValueTask.FromException(new TimeoutException()) : default;
            },
            new Ref(() => calls++));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task The_attempt_token_is_cancelled_by_the_attempt_timeout()
    {
        var policy = TestPolicy.Instant with { Attempts = 1, AttemptTimeout = TimeSpan.FromMilliseconds(50) };

        await Assert.ThrowsAsync<AttemptTimeoutException>(async () =>
            await policy.RunAsync(async ValueTask<int> (ct) =>
            {
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
                return 1;
            }));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await TestPolicy.Instant.RunAsync(static (s, ct) => s.ReadAsync(ct), new PooledSource(0), cts.Token));
    }

    [Fact]
    public async Task Passthrough_hands_back_the_callback_own_task()
    {
        var source = new PooledSource(0);

        Assert.Equal(PooledSource.Value, await Resilience.None.RunAsync(static (s, ct) => s.ReadAsync(ct), source));
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Admit_gates_a_ValueTask_callback_the_same_way()
    {
        var admitted = 0;
        var calls = 0;

        var policy = TestPolicy.Instant with
        {
            Attempts = 2,
            Admit = _ => Task.FromResult(++admitted == 1 ? Verdict.Refused() : Verdict.Ok),
        };

        var result = await policy.TryRunAsync(ValueTask<int> (ct) =>
        {
            calls++;
            return new ValueTask<int>(9);
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(9, result.Value);
        Assert.Equal(2, admitted);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_null_callback_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await TestPolicy.Instant.RunAsync((Func<CancellationToken, ValueTask<int>>)null!));
    }

    private sealed class Ref(Action bump)
    {
        public int Count { get; private set; }

        public void Bump()
        {
            Count++;
            bump();
        }
    }

    /// <summary>
    ///     An <see cref="IValueTaskSource{TResult}" /> that recycles a single core across calls,
    ///     simulating BCL pooled sources. Awaiting the same token twice throws, which allows these
    ///     tests to verify that the executor does not re-await <see cref="ValueTask" /> objects.
    /// </summary>
    private sealed class PooledSource(int failuresPerCall, bool suspend = false) : IValueTaskSource<int>
    {
        public const int Value = 42;

        private ManualResetValueTaskSourceCore<int> _core;

        public int Calls { get; private set; }

        public int GetResult(short token) => _core.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _core.OnCompleted(continuation, state, token, flags);

        public ValueTask<int> ReadAsync(CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            _core.Reset();

            if (suspend)
            {
                var pending = new ValueTask<int>(this, _core.Version);

                _ = Task.Run(async () =>
                {
                    await Task.Yield();
                    _core.SetResult(Value);
                }, CancellationToken.None);

                return pending;
            }

            if (Calls <= failuresPerCall)
                _core.SetException(new TimeoutException("transient"));
            else
                _core.SetResult(Value);

            return new ValueTask<int>(this, _core.Version);
        }
    }
}
