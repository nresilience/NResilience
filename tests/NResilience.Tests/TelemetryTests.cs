using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     Tests for telemetry: including <see cref="CallEvent" /> and <see cref="Resilience.OnEvent" />.
///     <para>
///         These tests verify that events accurately describe the outcome, rather than just checking
///         that they fire. A telemetry surface that emits events in the wrong order or reports a
///         <c>Retrying</c> event for a retry that never ran is misleading and therefore worse than
///         having no telemetry.
///     </para>
/// </summary>
public sealed class TelemetryTests
{
    /// <summary>
    ///     Runs a call that may serve a guarded rejection, moving the fake clock until it lands. A
    ///     rejection is deliberately not instant, so a test that simply awaited it would hang.
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

    // ---- Pay-for-play ----

    [Fact]
    public void No_listener_is_the_default()
    {
        Assert.Null(Resilience.Default.OnEvent);
        Assert.Null(Resilience.Http.OnEvent);
        Assert.Null(Resilience.None.OnEvent);
    }

    /// <summary>
    ///     A listener is the only feature that takes a policy out of passthrough. Handing back
    ///     the callback's own task is cheaper and would raise no events, but a listener that
    ///     never fires is a worse surprise than a policy that stops being free once
    ///     explicitly instrumented.
    /// </summary>
    [Fact]
    public async Task A_listener_on_the_passthrough_preset_still_hears_about_the_call()
    {
        var recorder = new EventRecorder();
        var policy = Resilience.None with { OnEvent = recorder.Record };

        Assert.Equal(1, await policy.RunAsync(static ct => Task.FromResult(1)));
        Assert.Equal([CallEventKind.Attempt, CallEventKind.Succeeded], recorder.Kinds);
    }

    /// <summary>The listener is a plain field, so attaching one must not disturb record equality.</summary>
    [Fact]
    public void Two_policies_sharing_a_listener_are_still_equal()
    {
        Action<CallEvent> listener = static _ => { };

        Assert.Equal(Resilience.Default with { OnEvent = listener }, Resilience.Default with { OnEvent = listener });
        Assert.NotEqual(Resilience.Default with { OnEvent = listener }, Resilience.Default);
    }

    // ---- The happy path ----

    [Fact]
    public async Task A_call_that_succeeds_first_time_raises_the_attempt_and_the_success()
    {
        var recorder = new EventRecorder();

        await (Resilience.Default with { Name = "api", OnEvent = recorder.Record })
            .RunAsync(static ct => Task.FromResult(7));

        Assert.Equal([CallEventKind.Attempt, CallEventKind.Succeeded], recorder.Kinds);

        var attempt = recorder.Single(CallEventKind.Attempt);
        Assert.Equal("api", attempt.PolicyName);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal(VerdictKind.Ok, attempt.Verdict.Kind);
        Assert.Equal(7, attempt.Result);
        Assert.Null(attempt.Exception);
        Assert.Null(attempt.Delay);
    }

    /// <summary>
    ///     Implements the design example: "log every retry with the status code that caused it".
    ///     Because a cross-cutting listener is not generic over <c>T</c>, the value is boxed.
    /// </summary>
    [Fact]
    public async Task The_result_that_was_classified_a_failure_is_on_the_event()
    {
        var recorder = new EventRecorder();

        var policy = TestPolicy.Instant with
        {
            OnEvent = recorder.Record,
            Attempts = 2,
            Classify = Classifier.Default.OnResult<int>(static status => status == 503 ? Verdict.Transient : Verdict.Ok),
        };

        await policy.TryRunAsync(static ct => Task.FromResult(503));

        Assert.Equal(2, recorder.CountOf(CallEventKind.Attempt));
        Assert.All(recorder.Events.Where(e => e.Kind == CallEventKind.Attempt), e => Assert.Equal(503, e.Result));
        Assert.Equal(503, recorder.Single(CallEventKind.Retrying).Result);
    }

    /// <summary>
    ///     A void call has no result to report. The internal no-result placeholder must not
    ///     be visible to the listener.
    /// </summary>
    [Fact]
    public async Task A_void_call_reports_no_result_rather_than_a_placeholder()
    {
        var recorder = new EventRecorder();

        await (Resilience.Default with { OnEvent = recorder.Record }).RunAsync(static ct => Task.CompletedTask);

        Assert.All(recorder.Events, e => Assert.Null(e.Result));
    }

    // ---- Retry ----

    [Fact]
    public async Task A_retried_call_raises_one_attempt_and_one_retrying_per_retry()
    {
        var recorder = new EventRecorder();
        var calls = 0;

        var value = await (TestPolicy.Instant with { OnEvent = recorder.Record, Attempts = 3 }).RunAsync(ct =>
            ++calls < 3 ? Task.FromException<int>(new IOException("flaky")) : Task.FromResult(9));

        Assert.Equal(9, value);

        Assert.Equal(
            [
                CallEventKind.Attempt, CallEventKind.Retrying,
                CallEventKind.Attempt, CallEventKind.Retrying,
                CallEventKind.Attempt, CallEventKind.Succeeded,
            ],
            recorder.Kinds);
    }

    /// <summary>
    ///     <c>Retrying</c> is raised before the backoff is served and identifies the attempt
    ///     that is about to run. This allows a listener to report, for example, "retrying
    ///     attempt 2 in 500 ms".
    /// </summary>
    [Fact]
    public async Task Retrying_carries_the_backoff_and_the_number_of_the_attempt_it_precedes()
    {
        var recorder = new EventRecorder();
        var time = new FakeTimeProvider();

        var policy = Resilience.Default with
        {
            Attempts = 2,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Backoff = Backoff.Constant(TimeSpan.FromSeconds(1)) with { Jitter = Jitter.None },
            Time = time,
            OnEvent = recorder.Record,
        };

        var result = await RunAsync(policy, static ct => Task.FromException<int>(new IOException("flaky")), time);

        Assert.False(result.IsSuccess);

        var retrying = recorder.Single(CallEventKind.Retrying);
        Assert.Equal(2, retrying.AttemptNumber);
        Assert.Equal(TimeSpan.FromSeconds(1), retrying.Delay);
        Assert.IsType<IOException>(retrying.Exception);
    }

    /// <summary>
    ///     Running out of attempts is a terminal state. This event ensures that every call
    ///     ends with exactly one terminal event. A listener counting logical operations
    ///     that skips this event would only count successful calls, which would invalidate
    ///     the retry fraction metric.
    /// </summary>
    [Fact]
    public async Task Exhausting_the_attempts_is_a_terminal_event()
    {
        var recorder = new EventRecorder();

        await (TestPolicy.Instant with { OnEvent = recorder.Record, Attempts = 2 })
            .TryRunAsync(static ct => Task.FromException<int>(new IOException("flaky")));

        Assert.Equal(
            [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Exhausted],
            recorder.Kinds);

        var exhausted = recorder.Single(CallEventKind.Exhausted);
        Assert.Equal(StopReason.AttemptsExhausted, exhausted.Reason);
        Assert.Equal(2, exhausted.AttemptNumber);
        Assert.IsType<IOException>(exhausted.Exception);
    }

    /// <summary>
    ///     Every call ends with exactly one terminal event, regardless of the path taken.
    ///     This invariant allows a stateless listener to count logical operations.
    /// </summary>
    [Theory]
    [InlineData(0, CallEventKind.Succeeded, StopReason.Succeeded)]
    [InlineData(1, CallEventKind.NotRetried, StopReason.Permanent)]
    [InlineData(2, CallEventKind.Exhausted, StopReason.AttemptsExhausted)]
    public async Task Every_call_ends_with_exactly_one_terminal_event(int shape, CallEventKind kind, StopReason reason)
    {
        var recorder = new EventRecorder();

        Func<CancellationToken, Task<int>> work = shape switch
        {
            0 => static ct => Task.FromResult(1),
            1 => static ct => Task.FromException<int>(new InvalidOperationException("unrecognized")),
            _ => static ct => Task.FromException<int>(new IOException("flaky")),
        };

        await (TestPolicy.Instant with { OnEvent = recorder.Record, Attempts = 2 }).TryRunAsync(work);

        var terminal = Assert.Single(recorder.Events, e => e.IsTerminal);
        Assert.Equal(kind, terminal.Kind);
        Assert.Equal(reason, terminal.Reason);
        Assert.Equal(terminal, recorder.Events[^1]);
    }

    /// <summary>
    ///     A refusal is one of two kinds: "the dependency is down"
    ///     (<see cref="CallEventKind.RejectedByBreaker" />) or "we are retrying too hard"
    ///     (<see cref="CallEventKind.RejectedByBudget" />). These require opposite responses, so the
    ///     kind states which guard refused and <see cref="CallEvent.Reason" /> agrees with it.
    /// </summary>
    [Fact]
    public async Task A_rejection_says_which_guard_refused_the_call()
    {
        var recorder = new EventRecorder();
        var breaker = new Breaker();
        breaker.Isolate();

        await (TestPolicy.Instant with { OnEvent = recorder.Record, Breaker = breaker })
            .TryRunAsync(static ct => Task.FromResult(1));

        var rejection = recorder.Single(CallEventKind.RejectedByBreaker);
        Assert.Equal(StopReason.DependencyUnavailable, rejection.Reason);
        Assert.True(rejection.IsRejection);
        Assert.True(rejection.IsTerminal);
    }

    /// <summary>
    ///     <see cref="CallEvent.IsRejection" /> is exactly the two refusal kinds, so a listener that
    ///     treats them alike does not pay for the split.
    /// </summary>
    [Theory]
    [InlineData(CallEventKind.RejectedByBreaker, true)]
    [InlineData(CallEventKind.RejectedByBudget, true)]
    [InlineData(CallEventKind.Attempt, false)]
    [InlineData(CallEventKind.Retrying, false)]
    [InlineData(CallEventKind.Succeeded, false)]
    [InlineData(CallEventKind.NotRetried, false)]
    [InlineData(CallEventKind.DeadlineExceeded, false)]
    [InlineData(CallEventKind.OrphanedWork, false)]
    [InlineData(CallEventKind.BreakerOpened, false)]
    [InlineData(CallEventKind.BreakerClosed, false)]
    [InlineData(CallEventKind.BreakerHalfOpened, false)]
    [InlineData(CallEventKind.NestedRetry, false)]
    [InlineData(CallEventKind.Exhausted, false)]
    [InlineData(CallEventKind.HedgeStarted, false)]
    [InlineData(CallEventKind.HedgeWon, false)]
    [InlineData(CallEventKind.HedgeDiscarded, false)]
    [InlineData(CallEventKind.AttemptTimeoutAdapted, false)]
    [InlineData(CallEventKind.BackoffBaseAdapted, false)]
    [InlineData(CallEventKind.HedgeSuppressed, false)]
    public void IsRejection_covers_the_two_refusals(CallEventKind kind, bool expected) =>
        Assert.Equal(expected, CallEvent.Create(kind).IsRejection);

    /// <summary>
    ///     <see cref="CallEvent.IsTerminal" /> is exactly the kinds that end a call - the list the
    ///     "exactly one terminal event per call" rule is stated against.
    /// </summary>
    [Theory]
    [InlineData(CallEventKind.Succeeded, true)]
    [InlineData(CallEventKind.NotRetried, true)]
    [InlineData(CallEventKind.RejectedByBreaker, true)]
    [InlineData(CallEventKind.RejectedByBudget, true)]
    [InlineData(CallEventKind.DeadlineExceeded, true)]
    [InlineData(CallEventKind.Exhausted, true)]
    [InlineData(CallEventKind.Attempt, false)]
    [InlineData(CallEventKind.Retrying, false)]
    [InlineData(CallEventKind.OrphanedWork, false)]
    [InlineData(CallEventKind.BreakerOpened, false)]
    [InlineData(CallEventKind.BreakerClosed, false)]
    [InlineData(CallEventKind.BreakerHalfOpened, false)]
    [InlineData(CallEventKind.NestedRetry, false)]
    [InlineData(CallEventKind.HedgeStarted, false)]
    [InlineData(CallEventKind.HedgeWon, false)]
    [InlineData(CallEventKind.HedgeDiscarded, false)]
    [InlineData(CallEventKind.AttemptTimeoutAdapted, false)]
    [InlineData(CallEventKind.BackoffBaseAdapted, false)]
    [InlineData(CallEventKind.HedgeSuppressed, false)]
    public void IsTerminal_covers_the_kinds_that_end_a_call(CallEventKind kind, bool expected) =>
        Assert.Equal(expected, CallEvent.Create(kind).IsTerminal);

    /// <summary>
    ///     The two theories above name every kind, and this is what keeps them exhaustive: adding a
    ///     kind without deciding whether it is a rejection or terminal fails here.
    /// </summary>
    [Fact]
    public void The_kind_predicates_are_asserted_over_every_kind() =>
        Assert.Equal(19, Enum.GetValues<CallEventKind>().Length);

    /// <summary>
    ///     <see cref="CallEvent.Create" /> exists so a listener can be tested without the executor, and
    ///     it is only worth anything if what it builds is indistinguishable from what the executor
    ///     raises. This pins that field for field.
    /// </summary>
    [Fact]
    public async Task Create_builds_the_event_the_executor_raises()
    {
        var recorder = new EventRecorder();

        await (TestPolicy.Instant with { OnEvent = recorder.Record, Name = "api" }).TryRunAsync(static ct => Task.FromResult(7));

        var raised = recorder.Single(CallEventKind.Succeeded);

        var built = CallEvent.Create(
            CallEventKind.Succeeded,
            raised.PolicyName,
            raised.AttemptNumber,
            raised.Verdict,
            raised.Duration,
            raised.Delay,
            raised.Exception,
            raised.Result,
            raised.Reason);

        Assert.Equal(raised.Kind, built.Kind);
        Assert.Equal("api", built.PolicyName);
        Assert.Equal(raised.AttemptNumber, built.AttemptNumber);
        Assert.Equal(raised.Verdict, built.Verdict);
        Assert.Equal(raised.Duration, built.Duration);
        Assert.Equal(raised.Delay, built.Delay);
        Assert.Same(raised.Exception, built.Exception);
        Assert.Equal(7, built.Result);
        Assert.Equal(StopReason.Succeeded, built.Reason);
        Assert.Equal(raised.ToString(), built.ToString());
    }

    /// <summary>
    ///     Everything but the kind is defaulted, so a listener test names only the fields it asserts
    ///     on. The defaults are the "nothing has happened yet" shape.
    /// </summary>
    [Fact]
    public void Create_defaults_every_field_but_the_kind()
    {
        var built = CallEvent.Create(CallEventKind.NestedRetry);

        Assert.Equal(CallEventKind.NestedRetry, built.Kind);
        Assert.Null(built.PolicyName);
        Assert.Equal(1, built.AttemptNumber);
        Assert.Equal(Verdict.Ok, built.Verdict);
        Assert.Equal(TimeSpan.Zero, built.Duration);
        Assert.Null(built.Delay);
        Assert.Null(built.Exception);
        Assert.Null(built.Result);
        Assert.Null(built.Reason);
    }

    /// <summary>Non-terminal events do not carry a stop reason because the operation has not stopped.</summary>
    [Fact]
    public async Task A_non_terminal_event_carries_no_reason()
    {
        var recorder = new EventRecorder();

        await (TestPolicy.Instant with { OnEvent = recorder.Record, Attempts = 2 })
            .TryRunAsync(static ct => Task.FromException<int>(new IOException("flaky")));

        Assert.All(
            recorder.Events.Where(e => e.Kind is CallEventKind.Attempt or CallEventKind.Retrying),
            e => Assert.Null(e.Reason));
    }

    // ---- Not retried ----

    /// <summary>
    ///     An unrecognized exception type raises a <see cref="CallEventKind.NotRetried" /> event
    ///     that names the type, making the failure visible rather than mysterious.
    /// </summary>
    [Fact]
    public async Task An_unrecognized_exception_type_raises_NotRetried_naming_the_type()
    {
        var recorder = new EventRecorder();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (TestPolicy.Instant with { OnEvent = recorder.Record, Attempts = 3 })
                .RunAsync(static ct => Task.FromException<int>(new InvalidOperationException("a bug"))));

        Assert.Equal([CallEventKind.Attempt, CallEventKind.NotRetried], recorder.Kinds);

        var notRetried = recorder.Single(CallEventKind.NotRetried);
        Assert.IsType<InvalidOperationException>(notRetried.Exception);
        Assert.Equal(VerdictKind.Permanent, notRetried.Verdict.Kind);
        Assert.Equal(1, notRetried.AttemptNumber);
    }

    // ---- Deadline ----

    [Fact]
    public async Task A_deadline_that_stops_the_call_raises_DeadlineExceeded()
    {
        var recorder = new EventRecorder();
        var time = new FakeTimeProvider();

        var policy = Resilience.Default with
        {
            Attempts = 5,
            Deadline = TimeSpan.FromSeconds(1),
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Backoff = Backoff.Constant(TimeSpan.FromSeconds(10)) with { Jitter = Jitter.None },
            Time = time,
            OnEvent = recorder.Record,
        };

        var result = await RunAsync(policy, static ct => Task.FromException<int>(new IOException("flaky")), time);

        Assert.Equal(StopReason.DeadlineExceeded, result.StopReason);
        Assert.Equal(1, recorder.CountOf(CallEventKind.DeadlineExceeded));

        // The backoff would outlast the deadline, so the call stops rather than sleeping through
        // it - and must not announce a retry it is not going to make.
        Assert.Equal(0, recorder.CountOf(CallEventKind.Retrying));
    }

    // ---- Rejection ----

    [Fact]
    public async Task An_open_breaker_raises_BreakerOpened_and_then_RejectedByBreaker()
    {
        var recorder = new EventRecorder();
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 2, Time = time });

        var policy = Resilience.Default with
        {
            Attempts = 2,
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Breaker = breaker,
            Budget = RetryBudget.None,
            Time = time,
            OnEvent = recorder.Record,
        };

        // Two failing attempts trip it; the operation still reports its own failure.
        var first = await RunAsync(policy, static ct => Task.FromException<int>(new IOException("down")), time);
        Assert.Equal(StopReason.AttemptsExhausted, first.StopReason);
        Assert.Equal(1, recorder.CountOf(CallEventKind.BreakerOpened));
        Assert.Equal(BreakerState.Open, breaker.State);

        var second = await RunAsync(policy, static ct => Task.FromResult(1), time);

        Assert.Equal(StopReason.DependencyUnavailable, second.StopReason);

        var rejected = recorder.Single(CallEventKind.RejectedByBreaker);
        Assert.Equal(1, rejected.AttemptNumber);
        Assert.Equal(TimeSpan.FromMilliseconds(100), rejected.Delay);
    }

    /// <summary>
    ///     The transition a call causes is reported to the policy that made the call. The breaker
    ///     itself has no listener, because a breaker is shared and a listener is per-policy.
    /// </summary>
    [Fact]
    public async Task The_probe_that_reopens_a_breaker_raises_HalfOpened_then_Opened()
    {
        var recorder = new EventRecorder();
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings
        {
            ConsecutiveFailures = 1,
            BreakDuration = TimeSpan.FromSeconds(5),
            Time = time,
        });

        var policy = Resilience.Default with
        {
            Attempts = 1,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Breaker = breaker,
            Time = time,
            OnEvent = recorder.Record,
        };

        await RunAsync(policy, static ct => Task.FromException<int>(new IOException("down")), time);
        Assert.Equal(BreakerState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(6));

        await RunAsync(policy, static ct => Task.FromException<int>(new IOException("still down")), time);

        Assert.Equal(
            [
                CallEventKind.Attempt, CallEventKind.BreakerOpened, CallEventKind.Exhausted,
                CallEventKind.BreakerHalfOpened, CallEventKind.Attempt, CallEventKind.BreakerOpened,
                CallEventKind.Exhausted,
            ],
            recorder.Kinds);
    }

    [Fact]
    public async Task A_probe_that_recovers_raises_BreakerClosed()
    {
        var recorder = new EventRecorder();
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings
        {
            ConsecutiveFailures = 1,
            ProbeSuccesses = 1,
            BreakDuration = TimeSpan.FromSeconds(5),
            Time = time,
        });

        var policy = Resilience.Default with
        {
            Attempts = 1,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Breaker = breaker,
            Time = time,
            OnEvent = recorder.Record,
        };

        await RunAsync(policy, static ct => Task.FromException<int>(new IOException("down")), time);
        time.Advance(TimeSpan.FromSeconds(6));
        await RunAsync(policy, static ct => Task.FromResult(1), time);

        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Equal(1, recorder.CountOf(CallEventKind.BreakerHalfOpened));
        Assert.Equal(1, recorder.CountOf(CallEventKind.BreakerClosed));
    }

    /// <summary>Isolate and Reset are administrative: there is no call to attribute them to.</summary>
    [Fact]
    public async Task Isolating_a_breaker_raises_nothing_until_a_call_meets_it()
    {
        var recorder = new EventRecorder();
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings { Time = time });
        breaker.Isolate();

        Assert.Empty(recorder.Events);

        var policy = Resilience.Default with
        {
            Attempts = 1,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Breaker = breaker,
            Time = time,
            OnEvent = recorder.Record,
        };

        var result = await RunAsync(policy, static ct => Task.FromResult(1), time);

        Assert.Equal(StopReason.DependencyUnavailable, result.StopReason);
        Assert.Equal([CallEventKind.RejectedByBreaker], recorder.Kinds);
    }

    [Fact]
    public async Task An_exhausted_budget_raises_RejectedByBudget()
    {
        var recorder = new EventRecorder();
        var time = new FakeTimeProvider();

        var policy = Resilience.Default with
        {
            Attempts = 2,
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Budget = RetryBudget.Of(0.1, 1, time),
            Time = time,
            OnEvent = recorder.Record,
        };

        var last = StopReason.Succeeded;

        for (var i = 0; i < 40 && last != StopReason.BudgetExhausted; i++)
        {
            last = (await RunAsync(policy, static ct => Task.FromException<int>(new IOException("flaky")), time)).StopReason;
        }

        Assert.Equal(StopReason.BudgetExhausted, last);

        var rejected = recorder.Events.Last(e => e.Kind == CallEventKind.RejectedByBudget);
        Assert.Equal(1, rejected.AttemptNumber);
        Assert.Equal(TimeSpan.FromMilliseconds(100), rejected.Delay);
        Assert.IsType<IOException>(rejected.Exception);
    }

    // ---- Orphaned work ----

    /// <summary>
    ///     A callback that ignores its token continues running after the attempt timeout expires.
    ///     Because the executor is blocked on that task, the event is reported when the work
    ///     finally returns.
    /// </summary>
    [Fact]
    public async Task Work_that_outlives_its_attempt_timeout_raises_OrphanedWork()
    {
        var recorder = new EventRecorder();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var policy = Resilience.Default with
        {
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromMilliseconds(20),
            Deadline = Timeout.InfiniteTimeSpan,
            OnEvent = recorder.Record,
        };

        // The callback never looks at its token, which is the whole point.
        var call = policy.TryRunAsync(_ => release.Task).AsTask();

        await Task.Delay(TimeSpan.FromMilliseconds(1_100));
        release.SetResult(3);

        var result = await call;

        Assert.True(result.IsSuccess);
        Assert.Equal(1, recorder.CountOf(CallEventKind.OrphanedWork));
        Assert.Equal(1, recorder.Single(CallEventKind.OrphanedWork).AttemptNumber);
    }

    [Fact]
    public async Task An_attempt_that_honors_its_timeout_is_not_reported_as_orphaned()
    {
        var recorder = new EventRecorder();

        var policy = Resilience.Default with
        {
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromMilliseconds(20),
            Deadline = Timeout.InfiniteTimeSpan,
            OnEvent = recorder.Record,
        };

        var result = await policy.TryRunAsync(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 1;
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(0, recorder.CountOf(CallEventKind.OrphanedWork));
        Assert.IsType<AttemptTimeoutException>(recorder.Single(CallEventKind.Attempt).Exception);
    }

    // ---- The listener itself ----

    /// <summary>
    ///     Telemetry that can fail the operation it observes is worse than no telemetry.
    ///     Therefore, a listener that throws is swallowed to avoid affecting the call's outcome.
    /// </summary>
    [Fact]
    public async Task A_listener_that_throws_does_not_fail_the_call()
    {
        var seen = 0;

        var policy = Resilience.Default with
        {
            OnEvent = _ =>
            {
                seen++;
                throw new InvalidOperationException("bad listener");
            },
        };

        Assert.Equal(4, await policy.RunAsync(static ct => Task.FromResult(4)));
        Assert.Equal(2, seen);
    }

    /// <summary>
    ///     Caller cancellation is neither a failure nor a retry and produces no terminal event.
    ///     This prevents cancellation from appearing as a policy-driven decision.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_raises_no_terminal_event()
    {
        var recorder = new EventRecorder();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await (Resilience.Default with { OnEvent = recorder.Record })
                .RunAsync(static ct => Task.FromResult(1), cts.Token));

        Assert.Empty(recorder.Events);
    }

    /// <summary>Every event carries the policy name, allowing a single listener to distinguish between multiple policies.</summary>
    [Fact]
    public async Task Every_event_carries_the_policy_name()
    {
        var recorder = new EventRecorder();

        await (TestPolicy.Instant with { OnEvent = recorder.Record, Name = "payments", Attempts = 2 })
            .TryRunAsync(static ct => Task.FromException<int>(new IOException("flaky")));

        Assert.NotEmpty(recorder.Events);
        Assert.All(recorder.Events, e => Assert.Equal("payments", e.PolicyName));
    }

    [Fact]
    public async Task An_events_text_form_says_what_happened()
    {
        var recorder = new EventRecorder();

        await (TestPolicy.Instant with { OnEvent = recorder.Record, Name = "api" }).RunAsync(static ct => Task.FromResult(1));

        var text = recorder.Single(CallEventKind.Attempt).ToString();

        Assert.Contains("[api]", text, StringComparison.Ordinal);
        Assert.Contains("Attempt #1", text, StringComparison.Ordinal);
        Assert.Contains("Ok", text, StringComparison.Ordinal);
    }
}
