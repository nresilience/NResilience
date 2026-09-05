using System.Net;
using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     Hedging: the third execution loop, and the one whose whole justification is that the threshold it
///     races against is a live quantile rather than a constant. Most of these tests are about the five
///     gates a hedge has to pass, because the gates are the argument.
/// </summary>
/// <remarks>
///     <para>
///         Two shapes of call are used throughout, and the difference matters. A <see cref="WarmAsync" />
///         call advances the fake clock <i>inside</i> a synchronously-completing callback, so the attempt
///         has a duration the latency estimate can learn from and the loop never suspends - which means
///         no hedge can fire. A <see cref="RaceAsync" /> call blocks its first attempt on a gate and moves
///         the clock from outside, which is the only way a hedge ever fires under a fake clock.
///     </para>
///     <para>
///         The latency estimate is private to a policy <i>instance</i>, the same way the automatic retry
///         budget is, so every test warms up and probes the same policy value. Rebuilding it with
///         <c>with</c> would start over from a cold estimate - which is a real footgun and has a test of
///         its own below.
///     </para>
/// </remarks>
public sealed class HedgeTests
{
    /// <summary>Long enough that no test rolls a slice and loses the samples it just recorded.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(500);

    // ---- The gates ----

    /// <summary>
    ///     Gate 4. A cold process does not guess a threshold; the first twenty calls behave exactly as
    ///     they would without hedging configured.
    /// </summary>
    [Fact]
    public async Task No_hedge_fires_before_the_estimate_has_enough_samples()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out var events);

        await WarmAsync(policy, time, Fast, 19);

        var race = await RaceAsync(policy, time);

        Assert.False(events.Contains(CallEventKind.HedgeStarted));
        Assert.Equal(1, race.Calls);
    }

    /// <summary>
    ///     The feature. An attempt slower than the observed p95 gets a copy, and the copy answering is
    ///     what the caller sees.
    /// </summary>
    [Fact]
    public async Task A_call_slower_than_the_quantile_is_hedged_and_the_copy_can_win()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out var events);

        await WarmAsync(policy, time, Fast, 40);

        var race = await RaceAsync(policy, time);

        Assert.True(race.Result.IsSuccess);

        // 2 is what the second attempt returns, so the copy is what answered.
        Assert.Equal(2, race.Result.Value);
        Assert.Equal(2, race.Calls);

        Assert.Single(events.OfKind(CallEventKind.HedgeStarted));
        Assert.Single(events.OfKind(CallEventKind.HedgeWon));
        Assert.Single(events.OfKind(CallEventKind.HedgeDiscarded));
    }

    /// <summary>
    ///     Gate 3. A dependency that is failing does not need a second copy of every slow request, and a
    ///     half-open breaker's attempts are probes - a probe that is raced is not a probe.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_isolated_or_open_breaker_fires_no_hedges(bool isolated)
    {
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings
            { ConsecutiveFailures = 1, BreakDuration = TimeSpan.FromMinutes(10), MaximumBreakDuration = TimeSpan.FromMinutes(10), Time = time });

        var policy = Hedging(time, out var events, p => p with { Breaker = breaker });

        await WarmAsync(policy, time, Fast, 40);

        if (isolated)
            breaker.Isolate();
        else
        {
            // One transient failure trips it. Through a single-attempt policy on purpose: a retrying one
            // would meet its own now-open breaker on the second round and serve the guarded pause, which
            // a fake clock has to be pumped through and which is not what this test is about.
            await (policy with { Attempts = 1, Hedge = null }).TryRunAsync(_ => Task.FromException<int>(new IOException()));
        }

        Assert.NotEqual(BreakerState.Closed, breaker.State);

        var race = await RaceAsync(policy, time);

        Assert.False(events.Contains(CallEventKind.HedgeStarted));
        Assert.False(race.Result.IsSuccess);
        Assert.Equal(StopReason.DependencyUnavailable, race.Result.StopReason);
    }

    /// <summary>
    ///     Gate 5. Hedges and retries draw on one bucket, because both are amplification and the
    ///     aggregate is what the budget exists to bound. A policy already retrying at its limit stops
    ///     hedging, which is the correct precedence.
    /// </summary>
    [Fact]
    public async Task An_exhausted_budget_funds_no_hedges()
    {
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Of(0.1, 0, time);
        var policy = Hedging(time, out var events, p => p with { Budget = budget });

        await WarmAsync(policy, time, Fast, 40);

        // Deposits fund withdrawals at a tenth of successful traffic, so drain what the warm-up funded.
        // TrySpend is internal and this project can see it, which is exactly what it is for.
        while (budget.TrySpend())
        {
        }

        var race = await RaceAsync(policy, time);

        Assert.False(events.Contains(CallEventKind.HedgeStarted));
        Assert.Equal(1, race.Calls);
    }

    /// <summary>
    ///     The floor. A dependency whose p95 is a few hundred microseconds would otherwise have every
    ///     call hedged, which spends the extra traffic on calls nobody would call slow.
    /// </summary>
    [Fact]
    public async Task A_dependency_faster_than_the_floor_is_not_hedged_below_it()
    {
        var time = new FakeTimeProvider();

        var policy = Hedging(time, out var events, p => p with
        {
            Hedge = Hedge.At() with { Window = Window, MinimumDelay = TimeSpan.FromSeconds(5) },
        });

        await WarmAsync(policy, time, Fast, 40);

        var race = await RaceAsync(policy, time);

        // The estimate says 10 ms, the floor says 5 s, and the pump below only moves 200 ms.
        Assert.False(events.Contains(CallEventKind.HedgeStarted));
        Assert.Equal(1, race.Calls);
    }

    // ---- The safety argument ----

    /// <summary>
    ///     The test that answers the original objection, and the reason the threshold is a quantile
    ///     rather than a constant.
    ///     <para>
    ///         The same 500 ms call is hedged when 500 ms is the tail of the distribution and not hedged
    ///         once 500 ms <i>is</i> the distribution. A constant threshold cannot tell those apart, so it
    ///         hedges every call during a brownout and doubles the load on a service that is already in
    ///         trouble. This one stops hedging on its own, without an operator touching anything.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task A_brownout_stops_the_hedging_it_would_otherwise_cause()
    {
        var time = new FakeTimeProvider();

        // A window short enough that the estimate is re-read during the test. It is deliberately not
        // re-read per call: the answer is memoized per slice - a quarter of the window - because
        // rescanning the histogram on every attempt would cost every call to answer a question whose
        // answer moves on the scale of seconds.
        var policy = Hedging(time, out var events, p => p with
        {
            Attempts = 2,
            Hedge = Hedge.At() with { Window = TimeSpan.FromSeconds(40) },
        });

        // A dependency with a tail: 95% at 10 ms, 5% at 500 ms. The p95 sits in the body, so a 500 ms
        // call is genuinely unusual and worth a copy.
        await WarmAsync(policy, time, Fast, 95);
        await WarmAsync(policy, time, Slow, 5);

        var before = await RaceAsync(policy, time);

        Assert.Single(events.OfKind(CallEventKind.HedgeStarted));
        Assert.Equal(2, before.Calls);

        events.Clear();

        // The brownout: every call now takes what only the tail used to. The p95 moves with it.
        await WarmAsync(policy, time, Slow, 100);

        var during = await RaceAsync(policy, time);

        Assert.False(events.Contains(CallEventKind.HedgeStarted));
        Assert.Equal(1, during.Calls);
    }

    // ---- Discarded legs are not evidence ----

    /// <summary>
    ///     The loser of a race was cancelled by us. Treating it as a failure would corrupt every
    ///     downstream signal at once, so it is not classified, not counted against the breaker, and not
    ///     charged to the budget.
    /// </summary>
    [Fact]
    public async Task A_discarded_leg_is_not_counted_against_the_breaker()
    {
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 1, Time = time });
        var policy = Hedging(time, out _, p => p with { Breaker = breaker });

        await WarmAsync(policy, time, Fast, 40);

        var race = await RaceAsync(policy, time);

        Assert.True(race.Result.IsSuccess);

        // One leg was cancelled mid-flight. A breaker that tripped on one failure is still closed, so
        // nothing reported that cancellation as one.
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    /// <summary>
    ///     A discarded leg gives back the probe slot it took, and only the slot it took. Only the first
    ///     leg of a round goes through the breaker's admission; a hedge never does, because hedges fire
    ///     only while the breaker is closed and a closed breaker hands out no probe slots. So the
    ///     clean-up after a discarded hedge must not release one - the slot it would return belongs to
    ///     whichever call is probing by then.
    /// </summary>
    /// <remarks>
    ///     The window is narrow and entirely real: a hedge discarded while the breaker was closed, whose
    ///     callback ignores its cancellation token, finishing after the breaker has opened, served its
    ///     break, and admitted somebody else's probe. What it costs is an extra call through a half-open
    ///     breaker - the one thing <see cref="BreakerSettings.HalfOpenProbes" /> exists to bound.
    /// </remarks>
    [Fact]
    public async Task A_discarded_hedge_does_not_release_a_probe_slot_it_never_took()
    {
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings
        {
            ConsecutiveFailures = 1,
            BreakDuration = TimeSpan.FromSeconds(1),
            BreakJitter = Jitter.None,
            HalfOpenProbes = 1,

            // Two, so a successful probe does not close the breaker and end the half-open state this
            // test is measuring.
            ProbeSuccesses = 2,
            Time = time,
        });

        var policy = Hedging(time, out _, p => p with { Breaker = breaker });
        var single = policy with { Attempts = 1, Hedge = null };

        await WarmAsync(policy, time, Fast, 40);

        // A hedged race whose losing leg ignores its token and is still running afterwards. Its
        // clean-up is parked on that leg, and is what will eventually try to release a slot.
        var stranded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loser = new Closeable();
        var calls = 0;

        var race = policy.TryRunAsync(async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await stranded.Task.WaitAsync(CancellationToken.None);
                return loser;
            }

            return new Closeable();
        }).AsTask();

        await PumpAsync(time, race);
        Assert.True((await race).IsSuccess);
        Assert.Equal(2, calls);

        // Trip the breaker and wait out the break, so the next admission becomes the probe.
        await RunAsync(single, _ => throw new IOException("down"), time);
        Assert.Equal(BreakerState.Open, breaker.State);
        time.Advance(TimeSpan.FromSeconds(2));

        // Take the single probe slot and hold it. The callback signals rather than the test polling
        // BreakerState: an open breaker whose break has elapsed already *reports* HalfOpen, so polling
        // that would race ahead of the admission that actually consumes the slot.
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var probe = single.TryRunAsync(async _ =>
        {
            admitted.SetResult();
            await probing.Task;
            return 1;
        }).AsTask();

        await admitted.Task;
        Assert.Equal(BreakerState.HalfOpen, breaker.State);

        // The slot is taken, so nothing else gets through.
        Assert.Equal(StopReason.DependencyUnavailable, (await RunAsync(single, _ => Task.FromResult(1), time)).StopReason);

        // Now let the stranded leg finish. Its clean-up runs, disposes what it produced, and - before
        // the fix - handed back a probe slot it never held.
        stranded.TrySetResult();

        for (var i = 0; i < 200 && !loser.Closed; i++)
        {
            await Task.Delay(1);
        }

        Assert.True(loser.Closed, "the discarded leg's clean-up never ran, so the test proved nothing");
        await Task.Delay(20);

        // The probe is still the only call in flight, so the breaker still refuses everything else.
        Assert.Equal(StopReason.DependencyUnavailable, (await RunAsync(single, _ => Task.FromResult(1), time)).StopReason);

        probing.TrySetResult();
        await probe;
    }

    /// <summary>
    ///     A hedge you cannot see is a hedge you cannot tune, so the discarded leg is in the log - and
    ///     in the log as nothing else, because nothing classified it.
    /// </summary>
    [Fact]
    public async Task The_log_shows_both_legs_and_which_one_was_thrown_away()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out _);

        await WarmAsync(policy, time, Fast, 40);

        var race = await RaceAsync(policy, time);
        var attempts = race.Result.Attempts;

        Assert.Equal(2, attempts.Count);

        // The winner is recorded first, because it is what stopped the race.
        Assert.True(attempts[0].IsHedged);
        Assert.False(attempts[0].IsDiscarded);
        Assert.Equal(VerdictKind.Ok, attempts[0].Verdict.Kind);

        Assert.False(attempts[1].IsHedged);
        Assert.True(attempts[1].IsDiscarded);

        // The hedge started after the leg it copied, and the log says by how much.
        Assert.True(attempts[0].StartOffset > attempts[1].StartOffset);
        Assert.Contains("discarded", attempts.ToString(), StringComparison.Ordinal);
        Assert.Contains("hedge Ok", attempts.ToString(), StringComparison.Ordinal);

        // The second entry started before the first one finished, so the log says when rather than
        // pretending there was a backoff between them.
        Assert.Contains(", at ", attempts.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    ///     An attempt's event reports that attempt. Legs overlap, so the loop is holding the last value
    ///     <i>any</i> leg produced while a later one comes back - and a leg that threw must not be
    ///     reported carrying its sibling's answer.
    /// </summary>
    /// <remarks>
    ///     The invariant is that no <see cref="CallEventKind.Attempt" /> event ever carries an
    ///     <see cref="CallEvent.Exception" /> and a <see cref="CallEvent.Result" /> at the same time.
    ///     The sequential loops get it for free, because they clear both before every attempt; the
    ///     hedged loop has to pass the leg's own outcome rather than the accumulated one.
    /// </remarks>
    [Fact]
    public async Task An_attempt_event_reports_its_own_leg_and_not_a_siblings_answer()
    {
        var time = new FakeTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        // A zero is an answer the policy refuses, so the hedge produces a *value* and a failure at
        // once - which is what puts a result in the loop's hands while the other leg is still running.
        var policy = Hedging(time, out var events, p => p with
        {
            // Two, so the round cannot arm a third leg while the first one is still blocked.
            Attempts = 2,
            Classifier = Classifier.RetryEverything.OnResult<int>(static v => v == 0 ? Verdict.Transient : Verdict.Ok),
        });

        await WarmAsync(policy, time, Fast, 40);
        events.Clear();

        var call = policy.TryRunAsync(async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                // The first leg: still running when the hedge answers, and it throws rather than
                // returning, so it has no value of its own.
                await gate.Task.WaitAsync(CancellationToken.None);
                throw new IOException("the slow leg failed");
            }

            return 0;
        }).AsTask();

        await PumpAsync(time, call);
        gate.TrySetResult();

        var result = await call;

        Assert.Equal(2, calls);

        var attempts = events.OfKind(CallEventKind.Attempt);
        Assert.Equal(2, attempts.Count);

        // The hedge answered first, so it is recorded first, and it is the one with a result.
        Assert.Null(attempts[0].Exception);
        Assert.Equal(0, attempts[0].Result);

        // The leg that threw. Before the fix this carried the hedge's zero.
        Assert.IsType<IOException>(attempts[1].Exception);
        Assert.Null(attempts[1].Result);

        Assert.All(attempts, e => Assert.False(e.Exception is not null && e.Result is not null, $"{e} carries both an exception and a result"));

        // The accumulated value is still what the caller is handed: a failed answer is an answer.
        Assert.False(result.IsSuccess);
        Assert.True(result.HasValue);
        Assert.Equal(0, result.Value);
    }

    /// <summary>
    ///     Hedging asks for answers nobody requested, so it disposes the ones it throws away. Without
    ///     this, a hedged <c>HttpResponseMessage</c> would leak a socket per race.
    /// </summary>
    [Fact]
    public async Task The_loser_of_a_race_has_its_value_disposed()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out _);
        var loser = new Closeable();

        await WarmAsync(policy, time, Fast, 40);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var call = policy.TryRunAsync(async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                // Cancelled by the hedge winning, and it answers anyway - the interesting case, because
                // a leg that produces nothing has nothing to leak.
                await gate.Task.WaitAsync(CancellationToken.None);
                return loser;
            }

            return new Closeable();
        }).AsTask();

        await PumpAsync(time, call);

        gate.TrySetResult();

        var result = await call;
        Assert.True(result.IsSuccess);

        // The clean-up is deliberately not awaited by the call, so give it the moment it needs.
        for (var i = 0; i < 100 && !loser.Closed; i++)
        {
            await Task.Delay(1);
        }

        Assert.True(loser.Closed);
    }

    /// <summary>
    ///     The same rule one round later: a value from a round the policy went on to retry is
    ///     unreachable, so it is disposed rather than dropped on the floor.
    /// </summary>
    [Fact]
    public async Task A_value_a_later_round_supersedes_is_disposed()
    {
        var time = new FakeTimeProvider();

        var policy = Hedging(time, out _, p => p with
        {
            Classifier = Classifier.Default.OnResult<Closeable>(c => c.Ok ? Verdict.Ok : Verdict.Transient),
        });

        var first = new Closeable { Ok = false };
        var calls = 0;

        var result = await policy.TryRunAsync(_ =>
        {
            time.Advance(Fast);
            return Task.FromResult(++calls == 1 ? first : new Closeable { Ok = true });
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, calls);
        Assert.True(first.Closed);
    }

    // ---- Hedges are attempts ----

    /// <summary>
    ///     <see cref="Resilience.Attempts" /> stays the total number of calls that reach the dependency,
    ///     whether they run one after another or at the same time. This is the one number, and it differs
    ///     from Polly on purpose.
    /// </summary>
    [Fact]
    public async Task A_hedge_spends_an_attempt_rather_than_adding_one()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out var events, p => p with { Attempts = 2 });

        await WarmAsync(policy, time, Fast, 40);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var call = policy.TryRunAsync(async ct =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task.WaitAsync(ct);

            throw new IOException();
        }).AsTask();

        await PumpAsync(time, call);

        gate.TrySetResult();

        var result = await call;

        // Two attempts, both in flight at once, and no third: the hedge used the retry.
        Assert.Equal(2, calls);
        Assert.Equal(StopReason.AttemptsExhausted, result.StopReason);
        Assert.Single(events.OfKind(CallEventKind.HedgeStarted));
    }

    /// <summary>
    ///     <see cref="NResilience.Hedge.MaximumConcurrent" /> bounds how many attempts overlap, and it is a
    ///     separate question from how many there are in total.
    /// </summary>
    [Fact]
    public async Task MaxConcurrent_bounds_how_many_legs_overlap()
    {
        var time = new FakeTimeProvider();

        var policy = Hedging(time, out var events, p => p with
        {
            Attempts = 6,
            Hedge = Hedge.At(0.95, 3) with { Window = Window },
        });

        await WarmAsync(policy, time, Fast, 40);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var call = policy.TryRunAsync(async ct =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task.WaitAsync(ct);

            return 1;
        }).AsTask();

        await PumpAsync(time, call, 20);

        gate.TrySetResult();
        await call;

        // Three in flight, so two hedges - not five, however long the pump ran.
        Assert.Equal(3, calls);
        Assert.Equal(2, events.CountOf(CallEventKind.HedgeStarted));
    }

    // ---- Ordinary behaviour is unchanged ----

    [Fact]
    public async Task A_call_that_comes_back_in_time_is_not_hedged()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out var events);

        await WarmAsync(policy, time, Fast, 40);

        var value = await policy.RunAsync(_ =>
        {
            time.Advance(Fast);
            return Task.FromResult(7);
        });

        Assert.Equal(7, value);
        Assert.False(events.Contains(CallEventKind.HedgeStarted));
    }

    /// <summary>A hedged policy that never hedges still retries, backs off and classifies the same way.</summary>
    [Fact]
    public async Task Retry_still_works_when_nothing_is_hedged()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out _);
        var calls = 0;

        var value = await policy.RunAsync(_ => ++calls < 3 ? Task.FromException<int>(new IOException()) : Task.FromResult(9));

        Assert.Equal(9, value);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task The_caller_cancelling_cancels_every_leg()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out var events);

        await WarmAsync(policy, time, Fast, 40);

        using var caller = new CancellationTokenSource();
        var started = 0;
        var cancelled = 0;

        var call = policy.TryRunAsync(async ct =>
        {
            Interlocked.Increment(ref started);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }

            return 1;
        }, caller.Token).AsTask();

        // Wait for the hedge, so there are two legs to cancel rather than one.
        for (var i = 0; i < 100 && !events.Contains(CallEventKind.HedgeStarted); i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));
            await Task.Delay(1);
        }

        Assert.Equal(2, started);

        await caller.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => call);

        for (var i = 0; i < 100 && cancelled < 2; i++)
        {
            await Task.Delay(1);
        }

        Assert.Equal(2, cancelled);
    }

    /// <summary>
    ///     The estimate is private to a policy instance, exactly like the automatic retry budget, so a
    ///     policy rebuilt per call never learns anything and never hedges. Worth pinning: it is the one
    ///     way to configure hedging and get nothing.
    /// </summary>
    [Fact]
    public async Task A_policy_rebuilt_per_call_never_warms_up()
    {
        var time = new FakeTimeProvider();
        var events = new EventRecorder();

        for (var i = 0; i < 40; i++)
        {
            // A fresh instance every time, which is a fresh estimate every time.
            var fresh = TestPolicy.On(time) with { Hedge = Hedge.At() with { Window = Window }, OnEvent = events.Record };

            await fresh.RunAsync(_ =>
            {
                time.Advance(Fast);
                return Task.FromResult(1);
            });
        }

        Assert.False(events.Contains(CallEventKind.HedgeStarted));
    }

    // ---- The reading ----

    /// <summary>
    ///     The threshold is the one measured term an operator has to watch, because it is the latency at
    ///     which the library starts duplicating load. It reads null while the estimate is too cold to fire
    ///     on, which is the same gate the loop applies.
    /// </summary>
    [Fact]
    public async Task The_hedge_threshold_is_null_until_the_estimate_can_fire()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out _);

        Assert.Null(policy.Measured.HedgeThreshold);

        await WarmAsync(policy, time, Fast, 19);
        Assert.Null(policy.Measured.HedgeThreshold);

        await WarmAsync(policy, time, Fast, 1);
        Assert.NotNull(policy.Measured.HedgeThreshold);
    }

    /// <summary>
    ///     And it is the same number the loop arms on: the quantile, floored by <c>MinimumDelay</c>. Read
    ///     against the delay <c>HedgeStarted</c> carries, which is the threshold that actually fired.
    /// </summary>
    [Fact]
    public async Task The_hedge_threshold_is_what_the_loop_arms_on()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out var events, p => p with
        {
            Hedge = Hedge.At() with { Window = Window, MinimumDelay = TimeSpan.Zero },
        });

        await WarmAsync(policy, time, Fast, 40);

        var threshold = policy.Measured.HedgeThreshold;
        Assert.NotNull(threshold);

        var race = await RaceAsync(policy, time);

        Assert.True(race.Result.IsSuccess);
        Assert.Equal(threshold, events.Events.Single(e => e.Kind == CallEventKind.HedgeStarted).Delay);
    }

    /// <summary>
    ///     A dependency whose quantile sits below <c>MinimumDelay</c> arms at the floor, and the reading
    ///     says so rather than reporting the raw quantile the loop would never use.
    /// </summary>
    [Fact]
    public async Task The_hedge_threshold_reports_the_floor_when_the_quantile_is_below_it()
    {
        var time = new FakeTimeProvider();
        var floor = TimeSpan.FromSeconds(5);
        var policy = Hedging(time, out _, p => p with
        {
            Hedge = Hedge.At() with { Window = Window, MinimumDelay = floor },
        });

        await WarmAsync(policy, time, Fast, 40);

        Assert.Equal(floor, policy.Measured.HedgeThreshold);
    }

    /// <summary>A policy that does not hedge measures no threshold, and reads one without validating.</summary>
    [Fact]
    public void A_policy_without_a_hedge_measures_no_threshold()
    {
        Assert.Null(Resilience.Default.Measured.HedgeThreshold);
        Assert.Null(Resilience.None.Measured.HedgeThreshold);
    }

    // ---- Configuration ----

    [Fact]
    public void Hedging_is_off_in_every_preset()
    {
        Assert.Null(Resilience.None.Hedge);
        Assert.Null(Resilience.Default.Hedge);
        Assert.Null(Resilience.Http.Hedge);
        Assert.Null(TestPolicy.Instant.Hedge);
    }

    [Fact]
    public void At_supplies_a_complete_configuration()
    {
        var hedge = Hedge.At();

        Assert.Equal(0.95, hedge.Quantile);
        Assert.Equal(2, hedge.MaximumConcurrent);
        Assert.Equal(20, hedge.MinimumSamples);
        Assert.Equal(TimeSpan.FromMilliseconds(10), hedge.MinimumDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), hedge.Window);
    }

    /// <summary>
    ///     A struct's default instance is the one thing a constructor cannot reach, so the defaults are
    ///     supplied on read - and naming one explicitly has to produce an equal value, or
    ///     <c>Resilience</c>'s own equality would start reporting identically-behaving policies as
    ///     different.
    /// </summary>
    [Fact]
    public void Naming_a_default_explicitly_changes_nothing()
    {
        var left = Hedge.At(0.99);
        var right = new Hedge { Quantile = 0.99, MaximumConcurrent = 2, MinimumSamples = 20, MinimumDelay = TimeSpan.FromMilliseconds(10) };

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.Equal(Resilience.Default with { Hedge = left }, Resilience.Default with { Hedge = right });
    }

    [Fact]
    public void An_explicit_zero_stays_zero()
    {
        var hedge = Hedge.At() with { MinimumDelay = TimeSpan.Zero };

        Assert.Equal(TimeSpan.Zero, hedge.MinimumDelay);
        Assert.NotEqual(Hedge.At(), hedge);
    }

    [Theory]
    [InlineData(0.4, 2, "Quantile")]
    [InlineData(1.0, 2, "Quantile")]
    [InlineData(0.95, 1, "MaximumConcurrent")]
    public void A_hedge_that_cannot_work_is_refused(double quantile, int maximumConcurrent, string named)
    {
        var policy = Resilience.Default with { Hedge = new Hedge { Quantile = quantile, MaximumConcurrent = maximumConcurrent } };

        var problems = Assert.Throws<ResilienceConfigurationException>(policy.Validate).Problems;
        Assert.Contains(problems, problem => problem.Contains(named, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Rejected rather than ignored: silently doing nothing is how a caller ends up believing a
    ///     dependency's tail is being managed when it is not.
    /// </summary>
    [Fact]
    public void A_hedge_with_one_attempt_is_refused()
    {
        var policy = Resilience.Default with { Attempts = 1, Hedge = Hedge.At() };

        var problems = Assert.Throws<ResilienceConfigurationException>(policy.Validate).Problems;
        Assert.Contains(problems, problem => problem.Contains("Attempts", StringComparison.Ordinal));
    }

    [Fact]
    public void A_default_constructed_hedge_is_refused()
    {
        var policy = Resilience.Default with { Hedge = default(Hedge) };

        Assert.Throws<ResilienceConfigurationException>(policy.Validate);
    }

    [Fact]
    public void ToString_names_the_quantile_the_way_an_operator_reads_it() =>
        Assert.StartsWith("p95", Hedge.At().ToString(), StringComparison.Ordinal);

    // ---- HTTP ----

    /// <summary>
    ///     The HTTP handler needs no hedging code of its own: the repeatability gate it already uses for
    ///     retry is the gate a hedge needs, and the per-host policy it already derives is what gives each
    ///     host its own latency estimate. This is that, end to end, plus the disposal that keeps a race
    ///     from leaking a socket per call.
    /// </summary>
    [Fact]
    public async Task A_slow_get_is_hedged_and_the_response_that_loses_is_disposed()
    {
        var time = new FakeTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racing = false;
        var calls = 0;
        var loser = new TrackedContent();

        var transport = new Transport(async (_, _) =>
        {
            if (!racing)
            {
                time.Advance(Fast);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (Interlocked.Increment(ref calls) == 1)
            {
                // Answers anyway, after being cancelled: the interesting case, because a leg that
                // produces nothing has no socket to leak.
                await gate.Task.WaitAsync(CancellationToken.None);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = loser };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = HedgingClient(transport, time);

        for (var i = 0; i < 40; i++)
        {
            (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();
        }

        racing = true;

        var send = client.GetAsync(new Uri("https://api.test/thing"));
        await PumpAsync(time, send);

        gate.TrySetResult();

        using var response = await send;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);

        for (var i = 0; i < 100 && !loser.Closed; i++)
        {
            await Task.Delay(1);
        }

        Assert.True(loser.Closed);
    }

    /// <summary>
    ///     Gate 2, and it costs the handler nothing: a hedge is a concurrent retry, so a request that may
    ///     not be repeated may not be hedged either. A POST is slow here and stays a single call.
    /// </summary>
    [Fact]
    public async Task A_post_without_an_idempotency_declaration_is_never_hedged()
    {
        var time = new FakeTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racing = false;
        var calls = 0;

        var transport = new Transport(async (_, _) =>
        {
            if (!racing)
            {
                time.Advance(Fast);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            Interlocked.Increment(ref calls);
            await gate.Task.WaitAsync(CancellationToken.None);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var client = HedgingClient(transport, time);

        // Warm the estimate on the same host, so nothing but repeatability is holding the hedge back.
        for (var i = 0; i < 40; i++)
        {
            (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();
        }

        racing = true;

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.test/thing"));
        var send = client.SendAsync(request);

        await PumpAsync(time, send);

        gate.TrySetResult();
        (await send).Dispose();

        Assert.Equal(1, calls);
    }

    // ---- Helpers ----

    /// <summary>
    ///     A hedging policy on a test clock, with a listener attached to the same instance the tests warm
    ///     up - because the estimate belongs to the instance.
    /// </summary>
    private static Resilience Hedging(FakeTimeProvider time, out EventRecorder events, Func<Resilience, Resilience>? configure = null)
    {
        var recorder = new EventRecorder();
        events = recorder;

        var policy = TestPolicy.On(time) with
        {
            Name = "api",
            Hedge = Hedge.At() with { Window = Window },
            OnEvent = recorder.Record,
        };

        return configure is null ? policy : configure(policy);
    }

    /// <summary>
    ///     Records <paramref name="times" /> samples of <paramref name="duration" /> into the policy's
    ///     latency estimate.
    /// </summary>
    /// <remarks>
    ///     The callback advances the clock and completes synchronously, so the executor never suspends
    ///     and no hedge can fire however long the "attempt" claims to have taken. That is what makes this
    ///     a way to shape the estimate rather than a way to exercise the race.
    /// </remarks>
    private static async Task WarmAsync(Resilience policy, FakeTimeProvider time, TimeSpan duration, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await policy.RunAsync(_ =>
            {
                time.Advance(duration);
                return Task.FromResult(1);
            });
        }
    }

    /// <summary>
    ///     Runs one call whose first attempt blocks until it is cancelled or the pump gives up, and moves
    ///     the clock from outside so that an armed hedge timer can fire.
    /// </summary>
    /// <returns>The outcome and how many attempts the callback saw.</returns>
    private static async Task<Race> RaceAsync(Resilience policy, FakeTimeProvider time)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var call = policy.TryRunAsync(async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await gate.Task.WaitAsync(ct);
                return 1;
            }

            return 2;
        }).AsTask();

        await PumpAsync(time, call);

        // Whatever happened, let the blocked attempt go rather than leaving it parked for the rest of
        // the test run.
        gate.TrySetResult();

        return new Race(await call, calls);
    }

    /// <summary>
    ///     Runs one call and moves the fake clock until it lands. A guarded rejection is not instant, so
    ///     a test that simply awaited one would hang.
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

    /// <summary>
    ///     Moves the fake clock in small steps until the call completes, or until it has moved 200 ms.
    ///     <para>
    ///         The 200 ms ceiling is load-bearing rather than arbitrary: it is above every threshold a
    ///         test expects a hedge to fire at and below every threshold a test expects to hold one back,
    ///         so "the pump ran out" and "no hedge was due" are the same outcome.
    ///     </para>
    /// </summary>
    private static async Task PumpAsync(FakeTimeProvider time, Task call, int steps = 10)
    {
        for (var i = 0; i < steps && !call.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));

            // A real yield, because the loop's continuation runs on the thread pool and the fake clock
            // cannot advance it there.
            await Task.Delay(1);
        }
    }

    /// <summary>An <see cref="HttpClient" /> whose handler hedges, on the test clock.</summary>
    private static HttpClient HedgingClient(HttpMessageHandler transport, FakeTimeProvider time)
    {
        var policy = TestPolicy.InstantHttp.UseClock(time) with { Hedge = Hedge.At() with { Window = Window } };

        return new HttpClient(new ResilienceHandler(transport, policy));
    }

    private sealed class Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    /// <summary>Response content that reports having been disposed, which is how a leaked socket shows up here.</summary>
    private sealed class TrackedContent : HttpContent
    {
        public bool Closed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    private sealed record Race(CallResult<int> Result, int Calls);

    /// <summary>A value that reports having been disposed, for the two disposal tests.</summary>
    private sealed class Closeable : IDisposable
    {
        public bool Ok { get; init; } = true;

        public bool Closed { get; private set; }

        public void Dispose() => Closed = true;
    }
}
