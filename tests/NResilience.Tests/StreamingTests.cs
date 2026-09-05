using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using NResilience.Internal;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The streaming contract: retry until the first element is yielded and never after, the first
///     element is the one verdict point, post-start faults belong to the consumer, and the surviving
///     attempt's enumerator and tokens outlive the loop that produced them.
/// </summary>
public sealed class StreamingTests
{
    [Fact]
    public async Task A_transient_fault_before_the_first_element_is_retried()
    {
        var streams = ScriptedStream.For<int>()
            .Throws(new IOException())
            .Yields(1, 2, 3);

        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };
        var received = await CollectAsync(policy.RunAsync(streams.Next));

        Assert.Equal([1, 2, 3], received);
        Assert.Equal(2, streams.Starts);

        // The abandoned attempt's enumerator is disposed, and so is the surviving one once the
        // consumer finishes - so nothing the policy started is left live.
        Assert.Equal(2, streams.DisposedEnumerators);
        Assert.Equal(0, streams.LiveEnumerators);
    }

    [Fact]
    public async Task A_permanent_first_element_is_never_yielded()
    {
        var streams = ScriptedStream.For<int>().Yields(-1);
        var events = new List<CallEvent>();
        var received = new List<int>();

        var policy = TestPolicy.Instant with
        {
            Classifier = Classifier.Default.OnResult<int>(static v => v < 0 ? Verdict.Permanent : Verdict.Ok),
            OnEvent = e => events.Add(e),
        };

        var rejected = await Assert.ThrowsAsync<CallRejectedException>(() =>
            CollectIntoAsync(policy.RunAsync(streams.Next), received));

        // The consumer never receives an element the classifier refused. An element does not
        // self-describe its failure the way a response with a status code does, so the verdict,
        // the reason and the log travel on the exception instead.
        Assert.Empty(received);
        Assert.Equal(StopReason.Permanent, rejected.Reason);
        Assert.Single(rejected.Attempts);
        Assert.Equal(1, streams.Starts);
        Assert.Equal([CallEventKind.Attempt, CallEventKind.NotRetried], events.Select(e => e.Kind));

        // A permanent verdict stops after one attempt, so the message must not claim the attempts
        // ran out - that is the other reason that reaches the same constructor.
        Assert.Contains("was not retried", rejected.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("every attempt", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retryable_verdict_on_the_first_element_starts_a_fresh_source()
    {
        var streams = ScriptedStream.For<int>()
            .Yields(-1)
            .Yields(5, 6);

        var policy = TestPolicy.Instant with
        {
            Classifier = Classifier.Default.OnResult<int>(static v => v < 0 ? Verdict.Transient : Verdict.Ok),
        };

        var received = await CollectAsync(policy.RunAsync(streams.Next));

        Assert.Equal([5, 6], received);
        Assert.Equal(2, streams.Starts);
        Assert.Equal(2, streams.DisposedEnumerators);
    }

    [Fact]
    public async Task Elements_after_the_first_are_not_classified()
    {
        var streams = ScriptedStream.For<int>().Yields(1, -2, -3);
        var judged = 0;

        var policy = TestPolicy.Instant with
        {
            Classifier = Classifier.Default.OnResult<int>(v =>
            {
                judged++;
                return v < 0 ? Verdict.Permanent : Verdict.Ok;
            }),
        };

        var received = await CollectAsync(policy.RunAsync(streams.Next));

        // The classifier ran once - on the first element. The middle elements pass through
        // untouched; had they been judged, the Permanent rule would have ended the stream at 1.
        Assert.Equal([1, -2, -3], received);
        Assert.Equal(1, judged);
    }

    [Fact]
    public async Task A_post_start_fault_is_the_consumers_unclassified()
    {
        var streams = ScriptedStream.For<int>().FaultsAfter(new InvalidOperationException("mid-stream"), 1, 2);
        var events = new List<CallEvent>();

        var policy = TestPolicy.Instant with
        {
            Classifier = Classifier.RetryEverything,
            OnEvent = e => events.Add(e),
        };

        var received = new List<int>();

        var fault = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CollectIntoAsync(policy.RunAsync(streams.Next), received));

        Assert.Equal("mid-stream", fault.Message);
        Assert.Equal([1, 2], received);

        // The fault raised nothing: the attempt and the success are the whole story.
        Assert.Equal([CallEventKind.Attempt, CallEventKind.Succeeded], events.Select(e => e.Kind));

        // The consumer's enumeration still ran the epilogue, so the surviving enumerator is
        // disposed despite the fault.
        Assert.Equal(1, streams.DisposedEnumerators);
        Assert.Equal(0, streams.LiveEnumerators);
    }

    [Fact]
    public async Task An_empty_source_is_a_success()
    {
        var streams = ScriptedStream.For<int>().Empty();
        var events = new List<CallEvent>();

        var policy = TestPolicy.Instant with { OnEvent = e => events.Add(e) };

        var received = await CollectAsync(policy.RunAsync(streams.Next));

        Assert.Empty(received);
        Assert.Equal([CallEventKind.Attempt, CallEventKind.Succeeded], events.Select(e => e.Kind));
    }

    [Fact]
    public async Task Abandoned_attempts_enumerators_are_disposed()
    {
        var streams = ScriptedStream.For<int>()
            .Throws(new IOException())
            .Yields(1);

        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };
        var received = await CollectAsync(policy.RunAsync(streams.Next));

        Assert.Equal([1], received);
        Assert.Equal(2, streams.Starts);
        Assert.Equal(2, streams.DisposedEnumerators);
        Assert.Equal(0, streams.LiveEnumerators);
    }

    [Fact]
    public async Task Caller_cancellation_before_the_first_attempt_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var streams = ScriptedStream.For<int>().Yields(1);
        var starts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CollectAsync(Resilience.Default.RunAsync(
                ct =>
                {
                    starts++;
                    return streams.Next(ct);
                },
                cts.Token)));

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Cancellation_during_a_backoff_aborts_the_operation()
    {
        using var cts = new CancellationTokenSource();

        var streams = ScriptedStream.For<int>()
            .Throws(new IOException())
            .Yields(1);

        var policy = TestPolicy.Instant with
        {
            Classifier = Classifier.RetryEverything,
            Backoff = Backoff.Constant(TimeSpan.FromSeconds(30)),
        };

        var drained = DrainAsync(policy.RunAsync(streams.Next, cts.Token));

        await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drained);
        Assert.Equal(1, streams.Starts);
    }

    [Fact]
    public async Task A_deadline_expiring_between_attempts_stops_the_stream()
    {
        var time = new FakeTimeProvider();

        var streams = ScriptedStream.For<int>(time)
            .Throws(new IOException())
            .Yields(1);

        var policy = TestPolicy.On(time) with
        {
            Classifier = Classifier.RetryEverything,
            Deadline = TimeSpan.FromSeconds(1),
            Backoff = Backoff.Constant(TimeSpan.FromSeconds(30)),
        };

        // The backoff would outlast the deadline, so the deadline stops the operation rather than
        // sleeping through it - and the failure surfaces from the consumer's first MoveNextAsync.
        var failure = await Assert.ThrowsAsync<DeadlineExceededException>(() => DrainAsync(policy.RunAsync(streams.Next)));

        Assert.Single(failure.Attempts);
        Assert.Equal(1, streams.Starts);
    }

    [Fact]
    public async Task An_attempt_ceiling_bounds_time_to_the_first_element()
    {
        var time = new FakeTimeProvider();

        var streams = ScriptedStream.For<int>(time)
            .YieldsAfter(TimeSpan.FromSeconds(30), 0)
            .Yields(1, 2);

        var policy = TestPolicy.On(time) with
        {
            Classifier = Classifier.RetryEverything,
            AttemptTimeout = TimeSpan.FromSeconds(5),
        };

        var collected = CollectAsync(policy.RunAsync(streams.Next));

        // Virtual time fires the attempt ceiling while the first source is still reaching for its
        // first element; the second source answers immediately.
        time.Advance(TimeSpan.FromSeconds(6));

        Assert.Equal([1, 2], await collected);
        Assert.Equal(2, streams.Starts);
    }

    [Fact]
    public async Task An_attempt_timeout_on_the_last_attempt_surfaces_as_AttemptTimeoutException()
    {
        var time = new FakeTimeProvider();
        var streams = ScriptedStream.For<int>(time).YieldsAfter(TimeSpan.FromSeconds(30), 0);

        var policy = TestPolicy.On(time) with
        {
            Classifier = Classifier.RetryEverything,
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromSeconds(5),
        };

        var collected = DrainAsync(policy.RunAsync(streams.Next));
        time.Advance(TimeSpan.FromSeconds(6));

        var failure = await Assert.ThrowsAsync<AttemptTimeoutException>(() => collected);
        Assert.Equal(1, streams.Starts);
        Assert.Single(failure.Attempts);
    }

    /// <summary>
    ///     The disarm race, deterministically: a source that ignores its token yields its first
    ///     element after the ceiling has already fired. The path must drop the element, judge the
    ///     attempt a timeout, and retry - never hand the consumer an element whose attempt was
    ///     already dead.
    /// </summary>
    [Fact]
    public async Task A_first_element_landing_after_the_ceiling_fired_is_dropped_and_retried()
    {
        var time = new FakeTimeProvider();
        var streams = new RudeStream(time);
        var events = new List<CallEvent>();

        var policy = TestPolicy.On(time) with
        {
            Classifier = Classifier.RetryEverything,
            AttemptTimeout = TimeSpan.FromSeconds(5),
            OnEvent = e => events.Add(e),
        };

        var received = await CollectAsync(policy.RunAsync(streams.Next));

        Assert.Equal([99], received);
        Assert.Equal(2, streams.Attempts);

        // The rude source also earned itself an OrphanedWork: it ran past its ceiling by more than
        // the grace the executor allows, because it ignored the token it was handed - which is what
        // that event exists to report, for streams exactly as for calls.
        Assert.Equal(
        [
            CallEventKind.Attempt,
            CallEventKind.OrphanedWork,
            CallEventKind.Retrying,
            CallEventKind.Attempt,
            CallEventKind.Succeeded,
        ], events.Select(e => e.Kind).ToArray());

        Assert.IsType<AttemptTimeoutException>(events[0].Exception);
    }

    [Fact]
    public async Task The_ceiling_is_disarmed_once_the_first_element_is_in_hand()
    {
        var time = new FakeTimeProvider();
        var streams = new TailObservingStream(time);

        var policy = TestPolicy.On(time) with
        {
            Classifier = Classifier.RetryEverything,
            AttemptTimeout = TimeSpan.FromSeconds(5),
        };

        var collected = CollectAsync(policy.RunAsync(streams.Items));

        // The consumer's side of the enumeration crosses the ceiling by a factor of six. A live
        // timer would fire here and cancel the surviving attempt's token, and the next pull would
        // throw; the disarmed one does nothing.
        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal([1, 2], await collected);
    }

    [Fact]
    public async Task Hedge_is_refused_eagerly_and_the_policy_still_runs_calls()
    {
        var policy = Resilience.Default with { Hedge = Hedge.At() };

        // Eagerly: the refusal is at the RunAsync call, not three frames later at the consumer's
        // first MoveNextAsync.
        Assert.Throws<ResilienceConfigurationException>(() => policy.RunAsync(static ct => Empty()));

        Assert.Throws<ResilienceConfigurationException>(() =>
            policy.RunAsync(static (_, ct) => Empty(), 0));

        // The same policy still runs a call.
        Assert.Equal(4, await policy.RunAsync(ct => Task.FromResult(4)));
    }

    [Fact]
    public async Task Passthrough_returns_the_sources_own_enumerable()
    {
        var source = Range(1, 3);
        var passed = Resilience.None.RunAsync(ct => source);

        Assert.Same(source, passed);
        Assert.Equal([1, 2, 3], await CollectAsync(passed));
    }

    [Fact]
    public async Task Admit_composes_and_its_verdict_is_processed_like_a_calls()
    {
        var streams = ScriptedStream.For<int>().Yields(1, 2);
        var admissions = 0;
        var events = new List<CallEvent>();

        var policy = TestPolicy.Instant with
        {
            Classifier = Classifier.RetryEverything,
            Admit = _ =>
            {
                admissions++;
                return Task.FromResult(admissions == 1 ? Verdict.Refused() : Verdict.Ok);
            },
            OnEvent = e => events.Add(e),
        };

        var received = await CollectAsync(policy.RunAsync(streams.Next));

        Assert.Equal([1, 2], received);

        // The refused admission never invoked the source, so one attempt started it.
        Assert.Equal(1, streams.Starts);
        Assert.Equal(2, admissions);

        // The refusal produced the same story a call's would: a throttled attempt, the retry, the
        // admitted attempt, the success.
        Assert.Equal([CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded],
            events.Select(e => e.Kind));
    }

    [Fact]
    public async Task Reenumerating_runs_an_independent_attempt_sequence()
    {
        var streams = ScriptedStream.For<int>()
            .Yields(1, 2)
            .Yields(3, 4);

        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };
        var enumerable = policy.RunAsync(streams.Next);

        Assert.Equal([1, 2], await CollectAsync(enumerable));
        Assert.Equal([3, 4], await CollectAsync(enumerable));
        Assert.Equal(2, streams.Starts);
    }

    /// <summary>
    ///     Lifetime test (a): the surviving attempt's token source outlives the loop, so a consumer
    ///     adding registrations mid-enumeration gets working ones. Disposing the source at attempt
    ///     end - the call path's teardown - would make this throw
    ///     <see cref="ObjectDisposedException" />.
    /// </summary>
    [Fact]
    public async Task The_surviving_tokens_registrations_still_work_mid_enumeration()
    {
        var streams = new TokenCapturingStream();
        var policy = TestPolicy.Instant with { AttemptTimeout = TimeSpan.FromSeconds(10) };

        var registrations = 0;

        await foreach (var _ in policy.RunAsync(streams.Items))
        {
            Assert.True(streams.Token.CanBeCanceled);

            using var registration = streams.Token.Register(static () => { });
            registrations++;
        }

        Assert.Equal(2, registrations);
    }

    /// <summary>
    ///     Lifetime test (b): the surviving attempt's timer is disposed rather than returned to the
    ///     pool, so pooled ceiling traffic on the same thread cannot cancel a live stream. A
    ///     returned-while-linked timer is the one rule whose violation is silent, intermittent, and
    ///     blamed on the wrong call - this is the kill switch for it.
    /// </summary>
    [Fact]
    public async Task Pooled_timer_traffic_after_the_handover_does_not_cancel_a_live_stream()
    {
        var streams = new TokenObservingStream();
        var policy = TestPolicy.Instant with { AttemptTimeout = TimeSpan.FromSeconds(10) };

        var received = new List<int>();

        await foreach (var item in policy.RunAsync(streams.Items))
        {
            received.Add(item);

            // Rent the pooled ceiling source this very thread armed the surviving attempt with, and
            // cancel it outright. If the surviving timer had gone back to the pool, this is it -
            // and the cancellation lands on the linked source the live stream still holds a token
            // from, killing the enumeration at the next pull.
            var rented = CtsPool.Rent(TimeProvider.System);
            rented.Cancel();
            rented.Dispose();
        }

        Assert.Equal([1, 2, 3], received);
    }

    [Fact]
    public async Task A_final_failed_first_element_throws_rather_than_yields()
    {
        var streams = ScriptedStream.For<int>()
            .Yields(-1)
            .Yields(-1)
            .Yields(-1);

        var policy = TestPolicy.Instant with
        {
            Classifier = Classifier.Default.OnResult<int>(static v => v < 0 ? Verdict.Transient : Verdict.Ok),
        };

        var received = new List<int>();

        var rejected = await Assert.ThrowsAsync<CallRejectedException>(() =>
            CollectIntoAsync(policy.RunAsync(streams.Next), received));

        // Attempts exhausted on an element the classifier kept calling retryable. Nothing threw and
        // no guard refused, so the fall-through message says what the attempts kept being - and
        // never reads as a guard refusal, because there was no guard.
        Assert.Empty(received);
        Assert.Equal(StopReason.AttemptsExhausted, rejected.Reason);
        Assert.Null(rejected.InnerException);
        Assert.Equal(3, rejected.Attempts.Count);
        Assert.Contains("every attempt produced a result the policy refused", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(3, streams.Starts);
    }

    [Fact]
    public async Task A_breaker_rejection_never_yields_the_held_element()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(new BreakerSettings { Time = time, ConsecutiveFailures = 1, MinimumCalls = 1 });
        var events = new List<CallEvent>();
        var received = new List<int>();

        var streams = ScriptedStream.For<int>(time).Yields(-1);

        var policy = TestPolicy.On(time) with
        {
            Breaker = breaker,
            Classifier = Classifier.Default.OnResult<int>(static v => v < 0 ? Verdict.Transient : Verdict.Ok),
            OnEvent = e => events.Add(e),
        };

        var collected = CollectIntoAsync(policy.RunAsync(streams.Next), received);

        // Release the guarded rejection pause, which is fake-clock time.
        time.Advance(TimeSpan.FromMilliseconds(150));

        // The retry was refused - and the element from the discarded attempt never reaches the
        // consumer. Handing back a prefix of a stream the policy had already torn down would be
        // indistinguishable from a one-element success, so the refusal throws as it would for a
        // call.
        var rejected = await Assert.ThrowsAsync<CallRejectedException>(() => collected);

        Assert.Empty(received);
        Assert.Equal(StopReason.DependencyUnavailable, rejected.Reason);
        Assert.NotNull(rejected.RetryAfter);
        Assert.Single(rejected.Attempts);
        Assert.Equal(1, streams.Starts);

        Assert.Equal([CallEventKind.Attempt, CallEventKind.BreakerOpened, CallEventKind.Retrying, CallEventKind.RejectedByBreaker],
            events.Select(e => e.Kind));
    }

    /// <summary>
    ///     The deadline variant of the guarded rejection: the budget runs out between attempts while
    ///     a classified element from the previous attempt is still in hand. The element is dropped
    ///     with the attempt it came from - a held value never suppresses the deadline's exception.
    /// </summary>
    [Fact]
    public async Task A_deadline_expiring_between_attempts_never_yields_the_held_element()
    {
        var time = new FakeTimeProvider();
        var received = new List<int>();

        var streams = ScriptedStream.For<int>(time)
            .Yields(-1)
            .Yields(-1);

        var policy = TestPolicy.On(time) with
        {
            Classifier = Classifier.Default.OnResult<int>(static v => v < 0 ? Verdict.Transient : Verdict.Ok),
            Deadline = TimeSpan.FromSeconds(1),
            Backoff = Backoff.Constant(TimeSpan.FromSeconds(30)),
        };

        var collected = CollectIntoAsync(policy.RunAsync(streams.Next), received);

        // The backoff would outlast the deadline, so the deadline stops the operation between
        // attempts - with attempt 1's element still in hand.
        time.Advance(TimeSpan.FromSeconds(2));

        var exceeded = await Assert.ThrowsAsync<DeadlineExceededException>(() => collected);

        Assert.Empty(received);
        Assert.Single(exceeded.Attempts);
        Assert.Equal(1, streams.Starts);
    }

    /// <summary>
    ///     An <see cref="Resilience.Admit" /> hook that refuses every attempt is the other live
    ///     caller of the null-error fall-through in <c>FailureException.Build</c>: nothing threw, no guard
    ///     intervened, the hook kept refusing. The message has to read as that rather than as a
    ///     breaker-style refusal.
    /// </summary>
    [Fact]
    public async Task An_admit_hook_refusing_every_attempt_throws_rather_than_yields()
    {
        var streams = ScriptedStream.For<int>().Yields(1, 2);
        var received = new List<int>();

        var policy = TestPolicy.Instant with
        {
            Classifier = Classifier.RetryEverything,
            Admit = static _ => Task.FromResult(Verdict.Refused()),
            Attempts = 2,
        };

        var rejected = await Assert.ThrowsAsync<CallRejectedException>(() =>
            CollectIntoAsync(policy.RunAsync(streams.Next), received));

        Assert.Empty(received);

        // Both refusals are self-imposed, so neither charges the budget nor counts against the
        // breaker - and the reason records the attempts ran out rather than blaming a guard.
        Assert.Equal(StopReason.AttemptsExhausted, rejected.Reason);
        Assert.Null(rejected.InnerException);
        Assert.Contains("every attempt produced a result the policy refused", rejected.Message, StringComparison.Ordinal);

        // The source was never pulled from: a refused admission skips it entirely.
        Assert.Equal(0, streams.Starts);
    }

    [Fact]
    public async Task Exhausted_attempts_on_a_faulting_source_throw_the_original()
    {
        var fault = new IOException("socket reset");

        var streams = ScriptedStream.For<int>()
            .Throws(fault)
            .Throws(fault)
            .Throws(fault);

        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };

        var caught = await Assert.ThrowsAsync<IOException>(() => DrainAsync(policy.RunAsync(streams.Next)));

        Assert.Same(fault, caught);
        Assert.Equal(3, streams.Starts);
        Assert.Equal(3, AttemptLog.Of(caught)!.Count);
    }

    [Fact]
    public async Task The_stateful_overload_threads_state_to_every_attempt()
    {
        var streams = ScriptedStream.For<int>()
            .Throws(new IOException())
            .Yields(1);

        var policy = TestPolicy.Instant with { Classifier = Classifier.RetryEverything };

        var received = await CollectAsync(policy.RunAsync(
            static (streams, ct) => streams.Next(ct),
            streams));

        Assert.Equal([1], received);
        Assert.Equal(2, streams.Starts);
    }

    [Fact]
    public async Task BeforeAttempt_runs_before_each_pull()
    {
        var streams = ScriptedStream.For<int>()
            .Throws(new IOException())
            .Yields(1);

        var runs = 0;

        var policy = TestPolicy.Instant with
        {
            Classifier = Classifier.RetryEverything,
            BeforeAttempt = _ =>
            {
                runs++;
                return Task.CompletedTask;
            },
        };

        Assert.Equal([1], await CollectAsync(policy.RunAsync(streams.Next)));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task The_retry_budget_refuses_a_stream_retry()
    {
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Of(0.5, 0, time);

        var streams = ScriptedStream.For<int>(time)
            .Throws(new IOException())
            .Throws(new IOException());

        var policy = TestPolicy.On(time) with
        {
            Classifier = Classifier.RetryEverything,
            Budget = budget,
        };

        var collected = DrainAsync(policy.RunAsync(streams.Next));

        // The bucket starts with one token: attempt 1's retry is funded, attempt 2's is refused,
        // and the refusal serves its guarded pause on the fake clock.
        time.Advance(TimeSpan.FromMilliseconds(150));

        var rejected = await Assert.ThrowsAsync<CallRejectedException>(() => collected);

        Assert.Equal(StopReason.BudgetExhausted, rejected.Reason);
        Assert.Equal(2, streams.Starts);
    }

    [Fact]
    public void A_null_source_throws_eagerly_at_the_call()
    {
        // The null is typed, because an untyped null literal is ambiguous between this overload and
        // the Task-returning one - the one place the overload pair needs help, and it is the same
        // help every Func-shaped null needs.
        Func<CancellationToken, IAsyncEnumerable<int>>? stateless = null;
        Func<int, CancellationToken, IAsyncEnumerable<int>>? stateful = null;

        Assert.Throws<ArgumentNullException>(() => TestPolicy.Instant.RunAsync(stateless!));
        Assert.Throws<ArgumentNullException>(() => TestPolicy.Instant.RunAsync(stateful!, 0));
    }

    [Fact]
    public void An_invalid_policy_is_refused_eagerly_at_the_call()
    {
        var policy = Resilience.Default with { Attempts = 0 };

        Assert.Throws<ResilienceConfigurationException>(() => policy.RunAsync(static ct => Empty()));
    }

    private static async IAsyncEnumerable<int> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<int> Range(int start, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await Task.CompletedTask;
            yield return start + i;
        }
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

    private static async Task CollectIntoAsync(IAsyncEnumerable<int> stream, List<int> into)
    {
        await foreach (var item in stream)
        {
            into.Add(item);
        }
    }

    private static async Task DrainAsync(IAsyncEnumerable<int> stream)
    {
        await foreach (var _ in stream)
        {
        }
    }

    /// <summary>
    ///     A rude source: it ignores the token it was given, and its first attempt yields an element
    ///     only after the attempt ceiling has already fired. It is the deterministic form of "the
    ///     timer fired in the window between the pull returning and the disarm landing", seen from
    ///     the source's side.
    /// </summary>
    private sealed class RudeStream(FakeTimeProvider time)
    {
        public int Attempts { get; private set; }

        public IAsyncEnumerable<int> Next(CancellationToken cancellationToken = default) => Attempt(cancellationToken);

        private async IAsyncEnumerable<int> Attempt([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Attempts++;

            if (Attempts == 1)
            {
                // Fire the ceiling before yielding: the element arrives for an attempt that is
                // already dead, and the path has to notice.
                time.Advance(TimeSpan.FromSeconds(10));
                yield return 0;
            }
            else
                yield return 99;
        }
    }

    /// <summary>
    ///     A source whose tail observes its token: the first element is immediate, the second waits
    ///     on the clock. The consumer crosses the attempt ceiling between the two, so a timer left
    ///     armed past the handover cancels the token and the second pull throws.
    /// </summary>
    private sealed class TailObservingStream(FakeTimeProvider time)
    {
        public async IAsyncEnumerable<int> Items([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return 1;

            await Task.Delay(TimeSpan.FromMilliseconds(1), time, cancellationToken);
            yield return 2;
        }
    }

    /// <summary>Records the token its enumeration runs under, and never suspends.</summary>
    private sealed class TokenCapturingStream
    {
        public CancellationToken Token { get; private set; }

        public async IAsyncEnumerable<int> Items([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            yield return 1;
            yield return 2;
        }
    }

    /// <summary>
    ///     A source that observes its token on every pull after the first and never suspends, so
    ///     the pooled-timer test is deterministic: a cancellation that reaches the surviving token
    ///     kills the next pull outright.
    /// </summary>
    private sealed class TokenObservingStream
    {
        public async IAsyncEnumerable<int> Items([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return 1;

            cancellationToken.ThrowIfCancellationRequested();
            yield return 2;

            cancellationToken.ThrowIfCancellationRequested();
            yield return 3;
        }
    }
}
