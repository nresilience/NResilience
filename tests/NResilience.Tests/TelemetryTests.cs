using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace NResilience.Tests;

/// <summary>
/// Tests for telemetry: including <see cref="CallEvent"/> and <see cref="Resilience.OnEvent"/>.
/// <para>
/// These tests verify that events accurately describe the outcome, rather than just checking 
/// that they fire. A telemetry surface that emits events in the wrong order or reports a 
/// <c>Retrying</c> event for a retry that never ran is misleading and therefore worse than 
/// having no telemetry.
/// </para>
/// </summary>
public sealed class TelemetryTests
{
    /// <summary>Collects call events in order. This recorder is thread-safe for listeners.</summary>
    private sealed class Recorder
    {
        private readonly ConcurrentQueue<CallEvent> _events = new();

        public Action<CallEvent> Listener => _events.Enqueue;

        public IReadOnlyList<CallEvent> Events => [.. _events];

        public IReadOnlyList<CallEventKind> Kinds => [.. _events.Select(e => e.Kind)];

        public CallEvent Single(CallEventKind kind) => _events.Single(e => e.Kind == kind);

        public int Count(CallEventKind kind) => _events.Count(e => e.Kind == kind);
    }

    private static Resilience Instant(Recorder recorder) => Resilience.Default with
    {
        Backoff = Backoff.None,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
        Deadline = Timeout.InfiniteTimeSpan,
        OnEvent = recorder.Listener,
    };

    /// <summary>
    /// Runs a call that may serve a guarded rejection, moving the fake clock until it lands. A
    /// rejection is deliberately not instant, so a test that simply awaited it would hang.
    /// </summary>
    private static async Task<CallResult<int>> RunAsync(Resilience policy, Func<CancellationToken, Task<int>> work, FakeTimeProvider time)
    {
        Task<CallResult<int>> call = policy.TryRunAsync(work).AsTask();

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
    /// A listener is the only feature that takes a policy out of passthrough. Handing back 
    /// the callback's own task is cheaper and would raise no events, but a listener that 
    /// never fires is a worse surprise than a policy that stops being free once 
    /// explicitly instrumented.
    /// </summary>
    [Fact]
    public async Task A_listener_on_the_passthrough_preset_still_hears_about_the_call()
    {
        var recorder = new Recorder();
        Resilience policy = Resilience.None with { OnEvent = recorder.Listener };

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
        var recorder = new Recorder();

        await (Resilience.Default with { Name = "api", OnEvent = recorder.Listener })
            .RunAsync(static ct => Task.FromResult(7));

        Assert.Equal([CallEventKind.Attempt, CallEventKind.Succeeded], recorder.Kinds);

        CallEvent attempt = recorder.Single(CallEventKind.Attempt);
        Assert.Equal("api", attempt.PolicyName);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal(VerdictKind.Ok, attempt.Verdict.Kind);
        Assert.Equal(7, attempt.Result);
        Assert.Null(attempt.Exception);
        Assert.Null(attempt.Delay);
    }

    /// <summary>
    /// Implements the design example: "log every retry with the status code that caused it". 
    /// Because a cross-cutting listener is not generic over <c>T</c>, the value is boxed.
    /// </summary>
    [Fact]
    public async Task The_result_that_was_classified_a_failure_is_on_the_event()
    {
        var recorder = new Recorder();
        Resilience policy = Instant(recorder) with
        {
            Attempts = 2,
            Classify = Classifier.Default.OnResult<int>(static status => status == 503 ? Verdict.Transient : Verdict.Ok),
        };

        await policy.TryRunAsync(static ct => Task.FromResult(503));

        Assert.Equal(2, recorder.Count(CallEventKind.Attempt));
        Assert.All(recorder.Events.Where(e => e.Kind == CallEventKind.Attempt), e => Assert.Equal(503, e.Result));
        Assert.Equal(503, recorder.Single(CallEventKind.Retrying).Result);
    }

    /// <summary>
    /// A void call has no result to report. The internal no-result placeholder must not 
    /// be visible to the listener.
    /// </summary>
    [Fact]
    public async Task A_void_call_reports_no_result_rather_than_a_placeholder()
    {
        var recorder = new Recorder();

        await (Resilience.Default with { OnEvent = recorder.Listener }).RunAsync(static ct => Task.CompletedTask);

        Assert.All(recorder.Events, e => Assert.Null(e.Result));
    }

    // ---- Retry ----

    [Fact]
    public async Task A_retried_call_raises_one_attempt_and_one_retrying_per_retry()
    {
        var recorder = new Recorder();
        int calls = 0;

        int value = await (Instant(recorder) with { Attempts = 3 }).RunAsync(ct =>
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
    /// <c>Retrying</c> is raised before the backoff is served and identifies the attempt 
    /// that is about to run. This allows a listener to report, for example, "retrying 
    /// attempt 2 in 500 ms".
    /// </summary>
    [Fact]
    public async Task Retrying_carries_the_backoff_and_the_number_of_the_attempt_it_precedes()
    {
        var recorder = new Recorder();
        var time = new FakeTimeProvider();

        Resilience policy = Resilience.Default with
        {
            Attempts = 2,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Backoff = Backoff.Constant(TimeSpan.FromSeconds(1)) with { Jitter = Jitter.None },
            Time = time,
            OnEvent = recorder.Listener,
        };

        CallResult<int> result = await RunAsync(policy, static ct => Task.FromException<int>(new IOException("flaky")), time);

        Assert.False(result.IsSuccess);

        CallEvent retrying = recorder.Single(CallEventKind.Retrying);
        Assert.Equal(2, retrying.AttemptNumber);
        Assert.Equal(TimeSpan.FromSeconds(1), retrying.Delay);
        Assert.IsType<IOException>(retrying.Exception);
    }

    /// <summary>
    /// Running out of attempts is a terminal state. This event ensures that every call 
    /// ends with exactly one terminal event. A listener counting logical operations 
    /// that skips this event would only count successful calls, which would invalidate 
    /// the retry fraction metric.
    /// </summary>
    [Fact]
    public async Task Exhausting_the_attempts_is_a_terminal_event()
    {
        var recorder = new Recorder();

        await (Instant(recorder) with { Attempts = 2 })
            .TryRunAsync(static ct => Task.FromException<int>(new IOException("flaky")));

        Assert.Equal(
            [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Exhausted],
            recorder.Kinds);

        CallEvent exhausted = recorder.Single(CallEventKind.Exhausted);
        Assert.Equal(StopReason.AttemptsExhausted, exhausted.Reason);
        Assert.Equal(2, exhausted.AttemptNumber);
        Assert.IsType<IOException>(exhausted.Exception);
    }

    /// <summary>
    /// Every call ends with exactly one terminal event, regardless of the path taken. 
    /// This invariant allows a stateless listener to count logical operations.
    /// </summary>
    [Theory]
    [InlineData(0, CallEventKind.Succeeded, StopReason.Succeeded)]
    [InlineData(1, CallEventKind.NotRetried, StopReason.Permanent)]
    [InlineData(2, CallEventKind.Exhausted, StopReason.AttemptsExhausted)]
    public async Task Every_call_ends_with_exactly_one_terminal_event(int shape, CallEventKind kind, StopReason reason)
    {
        var recorder = new Recorder();

        Func<CancellationToken, Task<int>> work = shape switch
        {
            0 => static ct => Task.FromResult(1),
            1 => static ct => Task.FromException<int>(new InvalidOperationException("unrecognized")),
            _ => static ct => Task.FromException<int>(new IOException("flaky")),
        };

        await (Instant(recorder) with { Attempts = 2 }).TryRunAsync(work);

        CallEventKind[] terminals =
        [
            CallEventKind.Succeeded,
            CallEventKind.NotRetried,
            CallEventKind.Rejected,
            CallEventKind.DeadlineExceeded,
            CallEventKind.Exhausted,
        ];

        CallEvent terminal = Assert.Single(recorder.Events, e => terminals.Contains(e.Kind));
        Assert.Equal(kind, terminal.Kind);
        Assert.Equal(reason, terminal.Reason);
        Assert.Equal(terminal, recorder.Events[^1]);
    }

    /// <summary>
    /// A <see cref="CallEventKind.Rejected"/> event can cover two types of refusals: 
    /// "the dependency is down" or "we are retrying too hard". These require opposite 
    /// responses, and the reason field distinguishes them.
    /// </summary>
    [Fact]
    public async Task A_rejection_says_which_guard_refused_the_call()
    {
        var recorder = new Recorder();
        var breaker = new Breaker();
        breaker.Isolate();

        await (Instant(recorder) with { Breaker = breaker })
            .TryRunAsync(static ct => Task.FromResult(1));

        Assert.Equal(StopReason.DependencyUnavailable, recorder.Single(CallEventKind.Rejected).Reason);
    }

    /// <summary>Non-terminal events do not carry a stop reason because the operation has not stopped.</summary>
    [Fact]
    public async Task A_non_terminal_event_carries_no_reason()
    {
        var recorder = new Recorder();

        await (Instant(recorder) with { Attempts = 2 })
            .TryRunAsync(static ct => Task.FromException<int>(new IOException("flaky")));

        Assert.All(
            recorder.Events.Where(e => e.Kind is CallEventKind.Attempt or CallEventKind.Retrying),
            e => Assert.Null(e.Reason));
    }

    // ---- Not retried ----

    /// <summary>
    /// An unrecognized exception type raises a <see cref="CallEventKind.NotRetried"/> event 
    /// that names the type, making the failure visible rather than mysterious.
    /// </summary>
    [Fact]
    public async Task An_unrecognized_exception_type_raises_NotRetried_naming_the_type()
    {
        var recorder = new Recorder();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Instant(recorder) with { Attempts = 3 })
                .RunAsync(static ct => Task.FromException<int>(new InvalidOperationException("a bug"))));

        Assert.Equal([CallEventKind.Attempt, CallEventKind.NotRetried], recorder.Kinds);

        CallEvent notRetried = recorder.Single(CallEventKind.NotRetried);
        Assert.IsType<InvalidOperationException>(notRetried.Exception);
        Assert.Equal(VerdictKind.Permanent, notRetried.Verdict.Kind);
        Assert.Equal(1, notRetried.AttemptNumber);
    }

    // ---- Deadline ----

    [Fact]
    public async Task A_deadline_that_stops_the_call_raises_DeadlineExceeded()
    {
        var recorder = new Recorder();
        var time = new FakeTimeProvider();

        Resilience policy = Resilience.Default with
        {
            Attempts = 5,
            Deadline = TimeSpan.FromSeconds(1),
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Backoff = Backoff.Constant(TimeSpan.FromSeconds(10)) with { Jitter = Jitter.None },
            Time = time,
            OnEvent = recorder.Listener,
        };

        CallResult<int> result = await RunAsync(policy, static ct => Task.FromException<int>(new IOException("flaky")), time);

        Assert.Equal(StopReason.DeadlineExceeded, result.StopReason);
        Assert.Equal(1, recorder.Count(CallEventKind.DeadlineExceeded));

        // The backoff would outlast the deadline, so the call stops rather than sleeping through
        // it - and must not announce a retry it is not going to make.
        Assert.Equal(0, recorder.Count(CallEventKind.Retrying));
    }

    // ---- Rejection ----

    [Fact]
    public async Task An_open_breaker_raises_BreakerOpened_and_then_Rejected()
    {
        var recorder = new Recorder();
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 2, Time = time });
        Resilience policy = Resilience.Default with
        {
            Attempts = 2,
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Breaker = breaker,
            Budget = RetryBudget.None,
            Time = time,
            OnEvent = recorder.Listener,
        };

        // Two failing attempts trip it; the operation still reports its own failure.
        CallResult<int> first = await RunAsync(policy, static ct => Task.FromException<int>(new IOException("down")), time);
        Assert.Equal(StopReason.AttemptsExhausted, first.StopReason);
        Assert.Equal(1, recorder.Count(CallEventKind.BreakerOpened));
        Assert.Equal(BreakerState.Open, breaker.State);

        CallResult<int> second = await RunAsync(policy, static ct => Task.FromResult(1), time);

        Assert.Equal(StopReason.DependencyUnavailable, second.StopReason);

        CallEvent rejected = recorder.Single(CallEventKind.Rejected);
        Assert.Equal(1, rejected.AttemptNumber);
        Assert.Equal(TimeSpan.FromMilliseconds(100), rejected.Delay);
    }

    /// <summary>
    /// The transition a call causes is reported to the policy that made the call. The breaker
    /// itself has no listener, because a breaker is shared and a listener is per-policy.
    /// </summary>
    [Fact]
    public async Task The_probe_that_reopens_a_breaker_raises_HalfOpened_then_Opened()
    {
        var recorder = new Recorder();
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings
        {
            ConsecutiveFailures = 1,
            BreakDuration = TimeSpan.FromSeconds(5),
            Time = time,
        });

        Resilience policy = Resilience.Default with
        {
            Attempts = 1,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Breaker = breaker,
            Time = time,
            OnEvent = recorder.Listener,
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
        var recorder = new Recorder();
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings
        {
            ConsecutiveFailures = 1,
            ProbeSuccesses = 1,
            BreakDuration = TimeSpan.FromSeconds(5),
            Time = time,
        });

        Resilience policy = Resilience.Default with
        {
            Attempts = 1,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Breaker = breaker,
            Time = time,
            OnEvent = recorder.Listener,
        };

        await RunAsync(policy, static ct => Task.FromException<int>(new IOException("down")), time);
        time.Advance(TimeSpan.FromSeconds(6));
        await RunAsync(policy, static ct => Task.FromResult(1), time);

        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Equal(1, recorder.Count(CallEventKind.BreakerHalfOpened));
        Assert.Equal(1, recorder.Count(CallEventKind.BreakerClosed));
    }

    /// <summary>Isolate and Reset are administrative: there is no call to attribute them to.</summary>
    [Fact]
    public async Task Isolating_a_breaker_raises_nothing_until_a_call_meets_it()
    {
        var recorder = new Recorder();
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings { Time = time });
        breaker.Isolate();

        Assert.Empty(recorder.Events);

        Resilience policy = Resilience.Default with
        {
            Attempts = 1,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Breaker = breaker,
            Time = time,
            OnEvent = recorder.Listener,
        };

        CallResult<int> result = await RunAsync(policy, static ct => Task.FromResult(1), time);

        Assert.Equal(StopReason.DependencyUnavailable, result.StopReason);
        Assert.Equal([CallEventKind.Rejected], recorder.Kinds);
    }

    [Fact]
    public async Task An_exhausted_budget_raises_Rejected()
    {
        var recorder = new Recorder();
        var time = new FakeTimeProvider();

        Resilience policy = Resilience.Default with
        {
            Attempts = 2,
            Backoff = Backoff.None,
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            Deadline = Timeout.InfiniteTimeSpan,
            Budget = RetryBudget.Of(fraction: 0.1, minimumPerSecond: 1, time: time),
            Time = time,
            OnEvent = recorder.Listener,
        };

        StopReason last = StopReason.Succeeded;
        for (int i = 0; i < 40 && last != StopReason.BudgetExhausted; i++)
        {
            last = (await RunAsync(policy, static ct => Task.FromException<int>(new IOException("flaky")), time)).StopReason;
        }

        Assert.Equal(StopReason.BudgetExhausted, last);

        CallEvent rejected = recorder.Events.Last(e => e.Kind == CallEventKind.Rejected);
        Assert.Equal(1, rejected.AttemptNumber);
        Assert.Equal(TimeSpan.FromMilliseconds(100), rejected.Delay);
        Assert.IsType<IOException>(rejected.Exception);
    }

    // ---- Orphaned work ----

    /// <summary>
    /// A callback that ignores its token continues running after the attempt timeout expires. 
    /// Because the executor is blocked on that task, the event is reported when the work 
    /// finally returns.
    /// </summary>
    [Fact]
    public async Task Work_that_outlives_its_attempt_timeout_raises_OrphanedWork()
    {
        var recorder = new Recorder();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Resilience policy = Resilience.Default with
        {
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromMilliseconds(20),
            Deadline = Timeout.InfiniteTimeSpan,
            OnEvent = recorder.Listener,
        };

        // The callback never looks at its token, which is the whole point.
        Task<CallResult<int>> call = policy.TryRunAsync(_ => release.Task).AsTask();

        await Task.Delay(TimeSpan.FromMilliseconds(1_100));
        release.SetResult(3);

        CallResult<int> result = await call;

        Assert.True(result.IsSuccess);
        Assert.Equal(1, recorder.Count(CallEventKind.OrphanedWork));
        Assert.Equal(1, recorder.Single(CallEventKind.OrphanedWork).AttemptNumber);
    }

    [Fact]
    public async Task An_attempt_that_honors_its_timeout_is_not_reported_as_orphaned()
    {
        var recorder = new Recorder();

        Resilience policy = Resilience.Default with
        {
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromMilliseconds(20),
            Deadline = Timeout.InfiniteTimeSpan,
            OnEvent = recorder.Listener,
        };

        CallResult<int> result = await policy.TryRunAsync(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 1;
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(0, recorder.Count(CallEventKind.OrphanedWork));
        Assert.IsType<AttemptTimeoutException>(recorder.Single(CallEventKind.Attempt).Exception);
    }

    // ---- The listener itself ----

    /// <summary>
    /// Telemetry that can fail the operation it observes is worse than no telemetry. 
    /// Therefore, a listener that throws is swallowed to avoid affecting the call's outcome.
    /// </summary>
    [Fact]
    public async Task A_listener_that_throws_does_not_fail_the_call()
    {
        int seen = 0;

        Resilience policy = Resilience.Default with
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
    /// Caller cancellation is neither a failure nor a retry and produces no terminal event. 
    /// This prevents cancellation from appearing as a policy-driven decision.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_raises_no_terminal_event()
    {
        var recorder = new Recorder();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await (Resilience.Default with { OnEvent = recorder.Listener })
                .RunAsync(static ct => Task.FromResult(1), cts.Token));

        Assert.Empty(recorder.Events);
    }

    /// <summary>Every event carries the policy name, allowing a single listener to distinguish between multiple policies.</summary>
    [Fact]
    public async Task Every_event_carries_the_policy_name()
    {
        var recorder = new Recorder();

        await (Instant(recorder) with { Name = "payments", Attempts = 2 })
            .TryRunAsync(static ct => Task.FromException<int>(new IOException("flaky")));

        Assert.NotEmpty(recorder.Events);
        Assert.All(recorder.Events, e => Assert.Equal("payments", e.PolicyName));
    }

    [Fact]
    public async Task An_events_text_form_says_what_happened()
    {
        var recorder = new Recorder();

        await (Instant(recorder) with { Name = "api" }).RunAsync(static ct => Task.FromResult(1));

        string text = recorder.Single(CallEventKind.Attempt).ToString();

        Assert.Contains("[api]", text, StringComparison.Ordinal);
        Assert.Contains("Attempt #1", text, StringComparison.Ordinal);
        Assert.Contains("Ok", text, StringComparison.Ordinal);
    }
}
