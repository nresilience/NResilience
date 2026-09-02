using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The circuit breaker: what trips it, what does not, how long it stays tripped, and what it takes
///     to close it again.
///     <para>
///         The state machine is exercised directly through the admission and recording entry points the
///         executor uses, because that is where the transitions live and driving them through a policy would
///         only add a callback between the assertion and the thing being asserted. The executor's own
///         integration - admission, feeding, and the guarded rejection - is tested end to end at the bottom.
///     </para>
/// </summary>
public sealed class BreakerTests
{
    private static Breaker Build(FakeTimeProvider time, BreakerSettings? settings = null) =>
        new((settings ?? new BreakerSettings()) with { Time = time });

    private static void Sample(Breaker breaker, VerdictKind kind, int count = 1, TimeSpan duration = default)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(breaker.TryEnter(out _), "admission was refused before the test expected it");
            breaker.Record(kind, duration);
        }
    }

    // ---- Tripping ----

    [Fact]
    public void Five_consecutive_failures_open_it()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time);

        Sample(breaker, VerdictKind.Transient, 4);
        Assert.Equal(BreakerState.Closed, breaker.State);

        Sample(breaker, VerdictKind.Transient);

        Assert.Equal(BreakerState.Open, breaker.State);
        Assert.False(breaker.TryEnter(out _));
        Assert.NotNull(breaker.OpenedAt);
    }

    [Fact]
    public void A_success_resets_the_consecutive_counter()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time);

        Sample(breaker, VerdictKind.Transient, 4);
        Sample(breaker, VerdictKind.Ok);
        Sample(breaker, VerdictKind.Transient, 4);

        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void Throttling_is_not_evidence_about_the_dependency()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time);

        // A 429 means the dependency is working correctly and defending itself. Counting it as a
        // failure turns throttling into an outage.
        Sample(breaker, VerdictKind.Throttled, 50);

        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void Permanent_failures_are_not_evidence_about_the_dependency()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time);

        // Overwhelmingly a client-side fact. Five NullReferenceExceptions in your own mapping code
        // must not open a circuit against a dependency that never misbehaved.
        Sample(breaker, VerdictKind.Permanent, 50);

        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void The_failure_ratio_trips_it_alongside_the_consecutive_counter()
    {
        var time = new FakeTimeProvider();

        var breaker = Build(time, new BreakerSettings
        {
            ConsecutiveFailures = 100,
            FailureRatio = 0.5,
            MinimumCalls = 4,
        });

        Sample(breaker, VerdictKind.Ok, 2);
        Sample(breaker, VerdictKind.Transient);
        Assert.Equal(BreakerState.Closed, breaker.State);

        Sample(breaker, VerdictKind.Transient);

        Assert.Equal(BreakerState.Open, breaker.State);
    }

    [Fact]
    public void The_failure_ratio_waits_for_the_minimum_call_count()
    {
        var time = new FakeTimeProvider();

        var breaker = Build(time, new BreakerSettings
        {
            ConsecutiveFailures = 100,
            FailureRatio = 0.5,
            MinimumCalls = 20,
        });

        // A 100% failure ratio over three calls means nothing, and this is the trap in Polly v8's
        // rate-only breaker: its minimum of 100 calls per 30 s is unreachable for the median .NET
        // service, so the breaker can never open at all. Here the consecutive counter covers that
        // case, and the ratio is the one that waits.
        Sample(breaker, VerdictKind.Transient, 3);

        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void Slow_calls_trip_it_even_though_they_succeeded()
    {
        var time = new FakeTimeProvider();

        var breaker = Build(time, new BreakerSettings
        {
            SlowCallThreshold = TimeSpan.FromSeconds(1),
            SlowCallRatio = 0.5,
            MinimumCalls = 4,
        });

        Sample(breaker, VerdictKind.Ok, 2, TimeSpan.FromMilliseconds(10));
        Sample(breaker, VerdictKind.Ok, 1, TimeSpan.FromSeconds(5));
        Assert.Equal(BreakerState.Closed, breaker.State);

        Sample(breaker, VerdictKind.Ok, 1, TimeSpan.FromSeconds(5));

        // Two of four calls were slow, which is the ratio. An error-rate breaker would have sat
        // closed through the entire brownout.
        Assert.Equal(BreakerState.Open, breaker.State);
    }

    [Fact]
    public void The_window_forgets_failures_that_fall_out_of_it()
    {
        var time = new FakeTimeProvider();

        var breaker = Build(time, new BreakerSettings
        {
            ConsecutiveFailures = 100,
            FailureRatio = 0.5,
            MinimumCalls = 4,
            Window = TimeSpan.FromSeconds(30),
        });

        Sample(breaker, VerdictKind.Transient, 3);
        time.Advance(TimeSpan.FromSeconds(31));

        // The three failures are out of the window, so these four successes are the whole sample and
        // the ratio has nothing to trip on.
        Sample(breaker, VerdictKind.Ok, 4);

        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    // ---- Staying tripped, and coming back ----

    [Fact]
    public void An_expired_break_reports_half_open_without_consuming_a_probe()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time, new BreakerSettings
        {
            BreakDuration = TimeSpan.FromSeconds(15),
            BreakJitter = Jitter.None,
        });

        Sample(breaker, VerdictKind.Transient, 5);
        time.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(BreakerState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(1));

        // Reading the state must not take the probe slot a real call needs, or a health endpoint
        // polling every second would starve recovery.
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
        Assert.True(breaker.TryEnter(out _));
    }

    [Fact]
    public void Half_open_is_a_trickle_rather_than_a_surge()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time, new BreakerSettings { BreakDuration = TimeSpan.FromSeconds(1) });

        Sample(breaker, VerdictKind.Transient, 5);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(breaker.TryEnter(out _));

        // One probe at a time by default. The alternative is handing a client fleet's accumulated
        // retries straight back to a dependency that is still broken.
        Assert.False(breaker.TryEnter(out _));
    }

    [Fact]
    public void Closing_takes_two_successful_probes()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time, new BreakerSettings { BreakDuration = TimeSpan.FromSeconds(1) });

        Sample(breaker, VerdictKind.Transient, 5);
        time.Advance(TimeSpan.FromSeconds(1));

        Sample(breaker, VerdictKind.Ok);
        Assert.Equal(BreakerState.HalfOpen, breaker.State);

        Sample(breaker, VerdictKind.Ok);
        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Null(breaker.OpenedAt);
    }

    [Fact]
    public void A_slow_probe_is_not_a_recovery()
    {
        var time = new FakeTimeProvider();

        var breaker = Build(time, new BreakerSettings
        {
            BreakDuration = TimeSpan.FromSeconds(1),
            SlowCallThreshold = TimeSpan.FromSeconds(1),
        });

        Sample(breaker, VerdictKind.Transient, 5);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(breaker.TryEnter(out _));
        breaker.Record(VerdictKind.Ok, TimeSpan.FromSeconds(30));

        // A 200 that took 30 s is not evidence the dependency recovered.
        Assert.Equal(BreakerState.Open, breaker.State);
    }

    [Fact]
    public void The_break_doubles_on_each_consecutive_open()
    {
        var time = new FakeTimeProvider();

        var breaker = Build(time, new BreakerSettings
        {
            BreakDuration = TimeSpan.FromSeconds(10),
            MaxBreakDuration = TimeSpan.FromMinutes(2),
            BreakJitter = Jitter.None,
        });

        Sample(breaker, VerdictKind.Transient, 5);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(breaker.TryEnter(out _));
        breaker.Record(VerdictKind.Transient, TimeSpan.Zero);

        // The second break is 20 s, not 10 s. Its absence is why breakers flap on a fixed cadence
        // forever.
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(BreakerState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    [Fact]
    public void The_growth_is_capped_by_the_maximum()
    {
        var time = new FakeTimeProvider();

        var breaker = Build(time, new BreakerSettings
        {
            BreakDuration = TimeSpan.FromSeconds(10),
            MaxBreakDuration = TimeSpan.FromSeconds(20),
            BreakJitter = Jitter.None,
        });

        Sample(breaker, VerdictKind.Transient, 5);

        for (var open = 0; open < 5; open++)
        {
            time.Advance(TimeSpan.FromSeconds(20));
            Assert.True(breaker.TryEnter(out _));
            breaker.Record(VerdictKind.Transient, TimeSpan.Zero);
        }

        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    [Fact]
    public void A_clean_close_resets_the_accumulated_growth()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time, new BreakerSettings
        {
            BreakDuration = TimeSpan.FromSeconds(10),
            BreakJitter = Jitter.None,
        });

        Sample(breaker, VerdictKind.Transient, 5);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(breaker.TryEnter(out _));
        breaker.Record(VerdictKind.Transient, TimeSpan.Zero);

        // Reopened, so the next break is 20 s. Recover properly, and the growth is forgotten.
        time.Advance(TimeSpan.FromSeconds(20));
        Sample(breaker, VerdictKind.Ok, 2);
        Assert.Equal(BreakerState.Closed, breaker.State);

        Sample(breaker, VerdictKind.Transient, 5);
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    // ---- The break duration's jitter ----

    /// <summary>
    ///     Two hundred pods watching one dependency fail open within a second of each other. Without
    ///     jitter they all serve the same break and all probe in the same second, and a dependency
    ///     halfway through recovering takes a two-hundred-request synchronized pulse - which it often
    ///     fails, re-opening every breaker together with a doubled break. <c>HalfOpenProbes = 1</c> makes
    ///     each pod polite and does nothing at all about the fleet.
    /// </summary>
    [Fact]
    public void The_break_is_jittered_so_a_fleet_that_opened_together_does_not_probe_together()
    {
        var breaks = new HashSet<TimeSpan>();

        for (var pod = 0; pod < 50; pod++)
        {
            var time = new FakeTimeProvider();
            var breaker = Build(time, new BreakerSettings { BreakDuration = TimeSpan.FromSeconds(15) });

            Sample(breaker, VerdictKind.Transient, 5);

            var served = breaker.RetryAfterHint();
            Assert.NotNull(served);

            // Equal jitter rather than full: the break duration has a purpose beyond de-correlation -
            // it is how long the dependency gets left alone - and full jitter would let a pod probe
            // after 200 ms of a 15-second break.
            Assert.InRange(served.Value, TimeSpan.FromSeconds(7.5), TimeSpan.FromSeconds(15));
            breaks.Add(served.Value);
        }

        Assert.True(breaks.Count > 25, $"50 pods drew only {breaks.Count} distinct breaks");
    }

    [Fact]
    public void The_hint_reports_the_break_being_served_rather_than_the_nominal_one()
    {
        var time = new FakeTimeProvider();

        var breaker = Build(time, new BreakerSettings
        {
            BreakDuration = TimeSpan.FromSeconds(15),
            BreakJitter = Jitter.None,
        });

        Sample(breaker, VerdictKind.Transient, 5);

        // Jitter is applied once, at open, so RetryAfterHint stays honest either way - and Jitter.None
        // is the escape hatch for a test that wants the break to expire at exactly BreakDuration.
        Assert.Equal(TimeSpan.FromSeconds(15), breaker.RetryAfterHint());

        time.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(BreakerState.Open, breaker.State);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(BreakerState.HalfOpen, breaker.State);
    }

    [Fact]
    public void The_growth_is_computed_from_the_nominal_break_so_jitter_does_not_compound()
    {
        for (var pod = 0; pod < 20; pod++)
        {
            var time = new FakeTimeProvider();

            var breaker = Build(time, new BreakerSettings
            {
                ConsecutiveFailures = 1,
                BreakDuration = TimeSpan.FromSeconds(10),
                MaxBreakDuration = TimeSpan.FromSeconds(20),
            });

            Sample(breaker, VerdictKind.Transient);
            time.Advance(TimeSpan.FromSeconds(10));

            Assert.True(breaker.TryEnter(out _));
            breaker.Record(VerdictKind.Transient, TimeSpan.Zero);

            // The second break is 20 s nominal, so equal jitter puts it in [10 s, 20 s]. Jittering
            // the grown value rather than growing the jittered one is what keeps the backoff a
            // backoff: a short first break must not shorten every break after it.
            var served = breaker.RetryAfterHint();
            Assert.NotNull(served);
            Assert.InRange(served.Value, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        }
    }

    // ---- Manual control ----

    [Fact]
    public void Isolate_never_self_heals()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time);

        breaker.Isolate();

        Assert.Equal(BreakerState.Isolated, breaker.State);
        Assert.NotNull(breaker.OpenedAt);

        time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(BreakerState.Isolated, breaker.State);
        Assert.False(breaker.TryEnter(out _));
    }

    [Fact]
    public void Reset_closes_it_from_any_state()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time);

        breaker.Isolate();
        breaker.Reset();

        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Null(breaker.OpenedAt);
        Assert.True(breaker.TryEnter(out _));
    }

    // ---- Configuration ----

    [Fact]
    public void Bad_settings_are_rejected_at_construction_listing_every_problem()
    {
        var problem = Assert.Throws<ResilienceConfigurationException>(() => new Breaker(new BreakerSettings
        {
            ConsecutiveFailures = 0,
            FailureRatio = 2,
            BreakDuration = TimeSpan.FromSeconds(30),
            MaxBreakDuration = TimeSpan.FromSeconds(10),
            ProbeSuccesses = 0,
        }));

        Assert.Equal(4, problem.Problems.Count);
    }

    // ---- Scope ----

    [Fact]
    public void With_copies_the_reference_so_scope_is_visible_in_the_code()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time);

        var payments = TestPolicy.On(time) with { Breaker = breaker };
        var derived = payments with { Attempts = 9 };

        Assert.Same(breaker, payments.Breaker);
        Assert.Same(breaker, derived.Breaker);

        // Two policies, one dependency, one breaker - because the code says so at the point the
        // breaker was constructed, rather than as an emergent property of where a pipeline was
        // registered.
        Assert.NotSame(payments.Breaker, Build(time));
    }

    // ---- Executor integration ----

    [Fact]
    public async Task An_isolated_breaker_refuses_the_call_without_running_it()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time);
        breaker.Isolate();

        var ran = false;

        var call = (TestPolicy.On(time) with { Breaker = breaker })
            .TryRunAsync(_ =>
            {
                ran = true;
                return Task.FromResult(1);
            })
            .AsTask();

        time.Advance(TimeSpan.FromMilliseconds(100));
        var result = await call;

        Assert.False(ran);
        Assert.False(result.IsSuccess);
        Assert.Equal(StopReason.DependencyUnavailable, result.StopReason);
        Assert.IsType<CallRejectedException>(result.Exception);
        Assert.Empty(result.Attempts);
    }

    [Fact]
    public async Task A_refusal_waits_before_it_is_reported()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time);
        breaker.Isolate();

        var call = (TestPolicy.On(time) with { Breaker = breaker })
            .TryRunAsync(_ => Task.FromResult(1))
            .AsTask();

        // Guarded rejection, not fail-fast: a refusal returned with no delay turns a caller's
        // polling loop into a CPU spin that generates more load than the call it refused.
        Assert.False(call.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(99));
        Assert.False(call.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(StopReason.DependencyUnavailable, (await call).StopReason);
    }

    [Fact]
    public async Task The_refusal_pause_is_bounded_by_the_deadline()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time);
        breaker.Isolate();

        var policy = TestPolicy.On(time) with { Breaker = breaker, Deadline = TimeSpan.FromMilliseconds(40) };
        var call = policy.TryRunAsync(_ => Task.FromResult(1)).AsTask();

        // A refusal must never make a call overrun the budget its caller set.
        time.Advance(TimeSpan.FromMilliseconds(40));

        Assert.Equal(StopReason.DependencyUnavailable, (await call).StopReason);
    }

    [Fact]
    public async Task A_refusal_carries_a_retry_after_hint()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time, new BreakerSettings
        {
            BreakDuration = TimeSpan.FromSeconds(15),
            BreakJitter = Jitter.None,
        });

        Sample(breaker, VerdictKind.Transient, 5);

        var call = (TestPolicy.On(time) with { Breaker = breaker })
            .TryRunAsync(_ => Task.FromResult(1))
            .AsTask();

        time.Advance(TimeSpan.FromMilliseconds(100));
        var result = await call;

        var rejected = Assert.IsType<CallRejectedException>(result.Exception);

        // 15 s of break, less the 100 ms the guard already served.
        Assert.NotNull(rejected.RetryAfter);
        Assert.Equal(TimeSpan.FromMilliseconds(14_900), rejected.RetryAfter!.Value);
    }

    [Fact]
    public async Task A_refusal_reports_itself_and_keeps_the_earlier_failure_as_its_cause()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time, new BreakerSettings { ConsecutiveFailures = 1 });

        var policy = TestPolicy.On(time) with { Breaker = breaker, Attempts = 3 };

        var call = policy
            .TryRunAsync(_ => Task.FromException<int>(new IOException("down")))
            .AsTask();

        time.Advance(TimeSpan.FromMilliseconds(100));
        var result = await call;

        // The first attempt tripped the breaker, so the second was refused. Reporting the
        // IOException would describe a call that was never made.
        Assert.Equal(StopReason.DependencyUnavailable, result.StopReason);
        Assert.Single(result.Attempts);

        var rejected = Assert.IsType<CallRejectedException>(result.Exception);
        Assert.IsType<IOException>(rejected.InnerException);
    }

    [Fact]
    public async Task The_breaker_samples_attempts_rather_than_operations()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time, new BreakerSettings { ConsecutiveFailures = 3 });

        var policy = TestPolicy.On(time) with
        {
            Breaker = breaker,
            Attempts = 3,
            Budget = RetryBudget.None,
        };

        await Assert.ThrowsAsync<IOException>(async () =>
            await policy.RunAsync(_ => Task.FromException<int>(new IOException("down"))));

        // One operation, three failing attempts, and the breaker is open - so "does the breaker see
        // attempts or whole operations?" has one answer here rather than depending on where a
        // strategy was placed relative to the retry.
        Assert.Equal(BreakerState.Open, breaker.State);
    }

    [Fact]
    public async Task Caller_cancellation_is_never_counted_against_the_breaker()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time, new BreakerSettings { ConsecutiveFailures = 1 });

        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await (TestPolicy.On(time) with { Breaker = breaker }).RunAsync(
                _ => Task.FromResult(1),
                caller.Token));

        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public async Task A_successful_call_passes_through_a_closed_breaker_untouched()
    {
        var time = new FakeTimeProvider();
        var breaker = Build(time);

        var value = await (TestPolicy.On(time) with { Breaker = breaker }).RunAsync(_ => Task.FromResult(42));

        Assert.Equal(42, value);
        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Null(breaker.OpenedAt);
    }

    // ---- Probe-slot release ----

    /// <summary>
    ///     Runs a call that may serve a guarded rejection and moves the fake clock until it lands.
    ///     A rejection is deliberately not instant, so a test that simply awaited it would hang.
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

    [Fact]
    public async Task Caller_cancellation_during_a_half_open_probe_releases_the_slot()
    {
        var time = new FakeTimeProvider();

        var breaker = Build(time, new BreakerSettings
        {
            ConsecutiveFailures = 1,
            BreakDuration = TimeSpan.FromSeconds(1),
            HalfOpenProbes = 1,
            ProbeSuccesses = 1,
            Time = time,
        });

        var single = TestPolicy.On(time) with { Attempts = 1, Breaker = breaker };

        // Trip it, then wait out the break so the next call becomes a probe.
        await RunAsync(single, _ => throw new IOException("down"), time);
        Assert.Equal(BreakerState.Open, breaker.State);
        time.Advance(TimeSpan.FromSeconds(2));

        // The probe starts, then the caller cancels mid-attempt. Without ReleaseProbe the slot
        // is leaked and the breaker wedges in HalfOpen forever.
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await single.RunAsync(async ct =>
            {
                started.SetResult();
                await caller.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return 1;
            }, caller.Token));

        Assert.Equal(BreakerState.HalfOpen, breaker.State);

        // The slot came back: the next call is admitted as a probe and succeeds.
        var probe = await RunAsync(single, _ => Task.FromResult(1), time);
        Assert.True(probe.IsSuccess);
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public async Task A_deadline_exceeded_after_admission_releases_the_probe_slot()
    {
        var time = new FakeTimeProvider();

        var breaker = Build(time, new BreakerSettings
        {
            ConsecutiveFailures = 1,
            BreakDuration = TimeSpan.FromSeconds(1),
            HalfOpenProbes = 1,
            ProbeSuccesses = 1,
            Time = time,
        });

        var single = TestPolicy.On(time) with { Attempts = 1, Breaker = breaker };

        // Trip it, then wait out the break so the next call becomes a probe.
        await RunAsync(single, _ => throw new IOException("down"), time);
        Assert.Equal(BreakerState.Open, breaker.State);
        time.Advance(TimeSpan.FromSeconds(2));

        // The next call is admitted as a probe. The BeforeAttempt hook advances time past the
        // deadline, so the recheck after the hook breaks the loop without recording - the leak path.
        var withDeadline = TestPolicy.On(time) with
        {
            Attempts = 1,
            Breaker = breaker,
            Deadline = TimeSpan.FromSeconds(1),
            BeforeAttempt = _ =>
            {
                time.Advance(TimeSpan.FromSeconds(2));
                return Task.CompletedTask;
            },
        };

        var caught = await Assert.ThrowsAsync<DeadlineExceededException>(async () =>
            await withDeadline.RunAsync(_ => Task.FromResult(1)));

        // The breaker transitioned to HalfOpen when TryEnter admitted, and the slot was released
        // because the deadline stopped the call before Record ran.
        Assert.Equal(BreakerState.HalfOpen, breaker.State);

        // The slot was released, so the next call through a policy with a live deadline can probe.
        var live = TestPolicy.On(time) with { Attempts = 1, Breaker = breaker };
        var probe = await RunAsync(live, _ => Task.FromResult(1), time);
        Assert.True(probe.IsSuccess);
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public async Task A_before_attempt_hook_that_throws_releases_the_probe_slot()
    {
        var time = new FakeTimeProvider();

        var breaker = Build(time, new BreakerSettings
        {
            ConsecutiveFailures = 1,
            BreakDuration = TimeSpan.FromSeconds(1),
            HalfOpenProbes = 1,
            ProbeSuccesses = 1,
            Time = time,
        });

        var single = TestPolicy.On(time) with { Attempts = 1, Breaker = breaker };

        // Trip it, then wait out the break so the next call becomes a probe.
        await RunAsync(single, _ => throw new IOException("down"), time);
        Assert.Equal(BreakerState.Open, breaker.State);
        time.Advance(TimeSpan.FromSeconds(2));

        // The BeforeAttempt hook throws after admission. The probe slot must come back.
        var withHook = single with
        {
            BeforeAttempt = _ => Task.FromException(new InvalidOperationException("hook failed")),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await withHook.RunAsync(_ => Task.FromResult(1)));

        Assert.Equal(BreakerState.HalfOpen, breaker.State);

        // The next call is admitted as a probe and closes the breaker.
        var probe = await RunAsync(single, _ => Task.FromResult(1), time);
        Assert.True(probe.IsSuccess);
        Assert.Equal(BreakerState.Closed, breaker.State);
    }
}
