using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The non-throwing streaming contract: the await ends at the first element, the outcomes a
///     failed <c>RunAsync</c> would have thrown at the first pull arrive as a failed
///     <see cref="CallResult{T}" /> instead, and everything after the first element is still the
///     consumer's.
/// </summary>
public sealed class StreamingTryRunTests
{
    [Fact]
    public async Task A_started_stream_is_a_success_carrying_the_whole_enumeration()
    {
        var streams = ScriptedStream.For<int>()
            .Throws(new IOException())
            .Yields(1, 2, 3);

        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };

        var result = await policy.TryRunAsync(streams.Next);

        Assert.True(result.IsSuccess);
        Assert.True(result.ReturnedValue);
        Assert.Equal(StopReason.Succeeded, result.Reason);
        Assert.Null(result.Exception);

        // The first element is already in hand when the await returns, and it is the first element
        // the consumer sees - the value re-yields it rather than pulling a second one.
        Assert.Equal([1, 2, 3], await CollectAsync(result.Value!));
        Assert.Equal(2, streams.CallCount);
        Assert.Equal(0, streams.LiveEnumerators);
    }

    [Fact]
    public async Task The_attempt_log_is_materialized_on_success()
    {
        var streams = ScriptedStream.For<int>()
            .Throws(new IOException())
            .Yields(7);

        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };

        var result = await policy.TryRunAsync(streams.Next);

        // The buffered contract: a caller who asked for a result object gets the history on
        // success too, so "it succeeded on the second attempt" is assertable.
        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal(VerdictKind.Transient, result.Attempts[0].Verdict.Kind);
        Assert.Equal(VerdictKind.Ok, result.Attempts[1].Verdict.Kind);

        await DrainAsync(result.Value!);
    }

    [Fact]
    public async Task The_retry_loop_is_over_before_the_await_returns()
    {
        var streams = ScriptedStream.For<int>()
            .Throws(new IOException())
            .Throws(new IOException())
            .Yields(1);

        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };

        var result = await policy.TryRunAsync(streams.Next);

        // Nothing is left for the consumer's enumeration to do about failure: every attempt, every
        // backoff and every guard ran during the await.
        Assert.Equal(3, streams.CallCount);
        Assert.True(result.IsSuccess);

        await DrainAsync(result.Value!);
    }

    [Fact]
    public async Task An_empty_source_is_a_success_with_no_elements()
    {
        var streams = ScriptedStream.For<int>().YieldsNothing();

        var result = await TestPolicy.Instant.TryRunAsync(streams.Next);

        Assert.True(result.IsSuccess);
        Assert.Empty(await CollectAsync(result.Value!));
        Assert.Equal(1, streams.CallCount);
        Assert.Equal(0, streams.LiveEnumerators);
    }

    [Fact]
    public async Task A_first_element_the_classifier_refuses_is_a_failure_and_is_never_yielded()
    {
        var streams = ScriptedStream.For<int>().Yields(-1);

        var policy = TestPolicy.Instant with
        {
            Classifier = Classifier.Default.OnResult<int>(static v => v < 0 ? Verdict.Permanent : Verdict.Ok),
        };

        var result = await policy.TryRunAsync(streams.Next);

        Assert.False(result.IsSuccess);
        Assert.False(result.ReturnedValue);
        Assert.Null(result.Value);
        Assert.Equal(StopReason.Permanent, result.Reason);
        Assert.IsType<CallRejectedException>(result.Exception);
        Assert.Single(result.Attempts);

        // The refused element is not somewhere waiting to be enumerated, and nothing the policy
        // started is left live.
        Assert.Equal(0, streams.LiveEnumerators);
        Assert.Equal(1, streams.DisposedEnumerators);
    }

    [Fact]
    public async Task Exhausted_attempts_report_the_original_exception()
    {
        var fault = new IOException("the dependency is down");
        var streams = ScriptedStream.For<int>()
            .Throws(fault)
            .Throws(fault)
            .Throws(fault);

        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };

        var result = await policy.TryRunAsync(streams.Next);

        Assert.False(result.IsSuccess);
        Assert.Same(fault, result.Exception);
        Assert.Equal(StopReason.AttemptsExhausted, result.Reason);
        Assert.Equal(3, result.Attempts.Count);

        // The same log the throwing form attaches to the exception it throws.
        Assert.Equal(3, AttemptLog.Of(result.Exception!)!.Count);
    }

    [Fact]
    public async Task A_deadline_reports_the_deadline_exception()
    {
        var time = new FakeTimeProvider();

        var streams = ScriptedStream.For<int>(time)
            .Throws(new IOException())
            .YieldsAfter(TimeSpan.FromSeconds(30), 1);

        var policy = TestPolicy.On(time) with
        {
            Classifier = Classifier.RetryEverything,
            Deadline = TimeSpan.FromSeconds(1),
        };

        var pending = policy.TryRunAsync(streams.Next).AsTask();
        time.Advance(TimeSpan.FromSeconds(2));

        var result = await pending;

        Assert.False(result.IsSuccess);
        Assert.Equal(StopReason.DeadlineExceeded, result.Reason);
        Assert.IsType<DeadlineExceededException>(result.Exception);
    }

    [Fact]
    public async Task A_tripped_breaker_reports_the_rejection_without_starting_a_source()
    {
        var breaker = new Breaker();
        breaker.Isolate();

        var streams = ScriptedStream.For<int>().Yields(1);

        var policy = TestPolicy.Instant with { Breaker = breaker };

        var result = await policy.TryRunAsync(streams.Next);

        Assert.False(result.IsSuccess);
        Assert.Equal(StopReason.DependencyUnavailable, result.Reason);
        Assert.IsType<CallRejectedException>(result.Exception);
        Assert.Equal(0, streams.CallCount);
    }

    [Fact]
    public async Task A_post_start_fault_still_belongs_to_the_consumer()
    {
        var midStream = new InvalidOperationException("the dependency broke mid-stream");
        var streams = ScriptedStream.For<int>().FaultsAfter(midStream, 1, 2);

        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };

        var result = await policy.TryRunAsync(streams.Next);

        // The result was decided before element three existed, so the fault is not folded into it:
        // it throws from the enumeration, exactly as it does under RunAsync.
        Assert.True(result.IsSuccess);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(result.Value!));

        Assert.Same(midStream, thrown);
        Assert.Equal(1, streams.CallCount);
    }

    [Fact]
    public async Task The_value_is_enumerable_once()
    {
        var streams = ScriptedStream.For<int>().Yields(1, 2);

        var result = await TestPolicy.Instant.TryRunAsync(streams.Next);

        Assert.Equal([1, 2], await CollectAsync(result.Value!));

        // The elements behind it are a live enumerator, not a source that can be re-run.
        Assert.Throws<InvalidOperationException>(() => result.Value!.GetAsyncEnumerator());
    }

    [Fact]
    public async Task Disposing_the_value_releases_the_stream_the_caller_decided_not_to_read()
    {
        var streams = ScriptedStream.For<int>().Yields(1, 2, 3);

        var result = await TestPolicy.Instant.TryRunAsync(streams.Next);

        Assert.Equal(1, streams.LiveEnumerators);

        await ((IAsyncDisposable)result.Value!).DisposeAsync();

        Assert.Equal(0, streams.LiveEnumerators);
        Assert.Equal(1, streams.DisposedEnumerators);
    }

    [Fact]
    public async Task A_failed_result_rethrows_on_demand()
    {
        var fault = new IOException();
        var streams = ScriptedStream.For<int>().Throws(fault).Throws(fault).Throws(fault);

        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };

        var result = await policy.TryRunAsync(streams.Next);

        Assert.Same(fault, Assert.Throws<IOException>(result.ThrowIfFailed));
        Assert.Same(fault, Assert.Throws<IOException>(() => result.ValueOrThrow()));
        Assert.False(result.TryGetValue(out _));
    }

    [Fact]
    public async Task The_stateful_overload_threads_state_to_every_attempt()
    {
        var streams = ScriptedStream.For<int>()
            .Throws(new IOException())
            .Yields(5);

        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };

        var result = await policy.TryRunAsync(static (source, ct) => source.Next(ct), streams);

        Assert.Equal([5], await CollectAsync(result.Value!));
        Assert.Equal(2, streams.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_rather_than_becoming_an_outcome()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var streams = ScriptedStream.For<int>().Yields(1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await TestPolicy.Instant.TryRunAsync(streams.Next, cancellation.Token));

        Assert.Equal(0, streams.CallCount);
    }

    [Fact]
    public void Hedge_is_refused_eagerly_at_the_call()
    {
        var hedged = Resilience.Default with { Hedge = Hedge.At() };

        // Synchronously, not on the returned task: a configuration error belongs at the call site.
        Assert.Throws<ResilienceConfigurationException>(() => hedged.TryRunAsync<int>(static ct => Empty()));
        Assert.Throws<ResilienceConfigurationException>(() => hedged.TryRunAsync(static (int _, CancellationToken _) => Empty(), 0));
    }

    [Fact]
    public void A_null_source_throws_eagerly_at_the_call()
    {
        Func<CancellationToken, IAsyncEnumerable<int>>? stateless = null;
        Func<int, CancellationToken, IAsyncEnumerable<int>>? stateful = null;

        Assert.Throws<ArgumentNullException>(() => TestPolicy.Instant.TryRunAsync(stateless!));
        Assert.Throws<ArgumentNullException>(() => TestPolicy.Instant.TryRunAsync(stateful!, 0));
    }

    [Fact]
    public async Task A_passthrough_policy_still_reports_an_outcome()
    {
        var streams = ScriptedStream.For<int>().Yields(1, 2);

        // Resilience.None imposes nothing, and the throwing form hands back the source's own
        // enumerable for it. This form cannot: the caller asked for a result object.
        var result = await Resilience.None.TryRunAsync(streams.Next);

        Assert.True(result.IsSuccess);
        Assert.Equal([1, 2], await CollectAsync(result.Value!));
        Assert.Single(result.Attempts);
    }

    private static async IAsyncEnumerable<int> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async Task<List<int>> CollectAsync(IAsyncEnumerable<int> stream)
    {
        var items = new List<int>();

        await foreach (var item in stream)
        {
            items.Add(item);
        }

        return items;
    }

    private static async Task DrainAsync(IAsyncEnumerable<int> stream)
    {
        await foreach (var _ in stream)
        {
        }
    }
}
