using System.Net;
using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     Criticality propagation: the ambient level itself, the two consumers inside the executor that
///     act on it, and the header that carries it to the next hop.
///     <para>
///         The claim being tested is narrow on purpose. The library does not shed, does not reorder,
///         and does not decide what a level means; it declines to spend a depleted retry budget on
///         work nobody is waiting for, and it never hedges that work. Everything else about a
///         <see cref="Criticality.Sheddable" /> call behaves exactly as it does without the feature.
///     </para>
/// </summary>
public sealed class CriticalityTests
{
    /// <summary>Long enough that no test rolls a slice and loses the samples it just recorded.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(10);

    // ---- The ambient level ----

    [Fact]
    public void Nothing_said_is_critical_rather_than_sheddable()
    {
        // The guardrail. A default that sheds is a default that loses requests during the first
        // incident after an upgrade.
        Assert.Equal(Criticality.Critical, AmbientCriticality.Current);
    }

    [Fact]
    public void A_scope_restores_the_one_it_replaced()
    {
        using (AmbientCriticality.Begin(Criticality.Sheddable))
        {
            Assert.Equal(Criticality.Sheddable, AmbientCriticality.Current);

            using (AmbientCriticality.Begin(Criticality.CriticalPlus))
            {
                Assert.Equal(Criticality.CriticalPlus, AmbientCriticality.Current);
            }

            Assert.Equal(Criticality.Sheddable, AmbientCriticality.Current);
        }

        Assert.Equal(Criticality.Critical, AmbientCriticality.Current);
    }

    [Fact]
    public void An_undeclared_level_is_refused_rather_than_published()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AmbientCriticality.Begin((Criticality)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => AmbientCriticality.Format((Criticality)9));
    }

    [Theory]
    [InlineData("Sheddable", Criticality.Sheddable)]
    [InlineData("sheddable", Criticality.Sheddable)]
    [InlineData("SheddablePlus", Criticality.SheddablePlus)]
    [InlineData("SHEDDABLEPLUS", Criticality.SheddablePlus)]
    [InlineData("Critical", Criticality.Critical)]
    public void A_header_naming_a_level_reads_as_that_level(string value, Criticality expected)
    {
        Assert.True(AmbientCriticality.TryParse(value, out var criticality));
        Assert.Equal(expected, criticality);
    }

    [Fact]
    public void CriticalPlus_cannot_arrive_from_the_wire()
    {
        // A caller that could escalate itself would escalate itself, and then the level means nothing.
        // The value parses - it is a name this library writes - and clamps.
        Assert.True(AmbientCriticality.TryParse("CriticalPlus", out var criticality));
        Assert.Equal(Criticality.Critical, criticality);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("urgent")]
    [InlineData("Sheddable ")]
    public void Anything_else_is_no_level_at_all(string? value)
    {
        Assert.False(AmbientCriticality.TryParse(value, out var criticality));

        // And the out value is still the safe answer, so a caller that ignores the bool is not
        // handed a shed.
        Assert.Equal(Criticality.Critical, criticality);
    }

    [Theory]
    [InlineData(Criticality.Sheddable)]
    [InlineData(Criticality.SheddablePlus)]
    [InlineData(Criticality.Critical)]
    public void What_is_written_is_what_is_read_back(Criticality criticality)
    {
        Assert.True(AmbientCriticality.TryParse(AmbientCriticality.Format(criticality), out var parsed));
        Assert.Equal(criticality, parsed);
    }

    // ---- The retry budget ----

    [Fact]
    public void A_sheddable_retry_leaves_half_the_bucket_behind()
    {
        var time = new FakeTimeProvider();

        // minimumPerSecond 2 banks ten seconds of the floor rate, so the bucket holds 20 tokens.
        var budget = RetryBudget.Of(minimumPerSecond: 2, time: time);

        // Ten of them are spendable by sheddable work; the eleventh would break into the half held
        // for work someone is waiting for.
        for (var i = 0; i < 10; i++)
        {
            Assert.True(budget.TrySpend(sheddable: true), $"sheddable retry {i + 1} was refused");
        }

        Assert.False(budget.TrySpend(sheddable: true));

        // And the half that was held back is exactly that - held, not lost. Critical work spends it.
        Assert.True(budget.TrySpend());
    }

    [Fact]
    public async Task A_sheddable_call_stops_retrying_once_the_bucket_is_half_spent()
    {
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Of(minimumPerSecond: 2, time: time);
        var policy = Budgeted(time, budget);

        // Spend the top half, so the next sheddable withdrawal is the one that would break into the
        // reserve. Nothing about the budget's own arithmetic has changed for anyone else.
        for (var i = 0; i < 10; i++)
        {
            Assert.True(budget.TrySpend());
        }

        using var scope = AmbientCriticality.Begin(Criticality.Sheddable);

        var attempts = 0;
        var result = await RunAsync(policy, _ => Fail(ref attempts), time);

        Assert.Equal(StopReason.BudgetExhausted, result.Reason);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task A_critical_call_spends_the_half_a_sheddable_one_would_not()
    {
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Of(minimumPerSecond: 2, time: time);
        var policy = Budgeted(time, budget);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(budget.TrySpend());
        }

        // Same bucket, same depletion, no scope: the level the library assumes is Critical and the
        // retry is funded.
        var attempts = 0;
        var result = await RunAsync(policy, _ => Fail(ref attempts), time);

        Assert.Equal(StopReason.AttemptsExhausted, result.Reason);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task A_sheddable_call_retries_freely_while_the_bucket_is_full()
    {
        var time = new FakeTimeProvider();
        var policy = Budgeted(time, RetryBudget.Of(minimumPerSecond: 2, time: time));

        using var scope = AmbientCriticality.Begin(Criticality.Sheddable);

        // Nothing is refused while the dependency is healthy. The feature holds capacity back at the
        // point it becomes scarce, which is the only point at which holding it back means anything.
        var attempts = 0;
        var result = await RunAsync(policy, _ => Fail(ref attempts), time);

        Assert.Equal(StopReason.AttemptsExhausted, result.Reason);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task SheddablePlus_is_not_sheddable()
    {
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Of(minimumPerSecond: 2, time: time);
        var policy = Budgeted(time, budget);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(budget.TrySpend());
        }

        // Only the bottom level is held back. SheddablePlus degrades a feature rather than the
        // request, so something is still waiting for it.
        using var scope = AmbientCriticality.Begin(Criticality.SheddablePlus);

        var attempts = 0;
        var result = await RunAsync(policy, _ => Fail(ref attempts), time);

        Assert.Equal(StopReason.AttemptsExhausted, result.Reason);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task A_policy_that_did_not_opt_in_never_reads_the_level()
    {
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Of(minimumPerSecond: 2, time: time);
        var policy = Budgeted(time, budget) with { UseAmbientCriticality = false };

        for (var i = 0; i < 10; i++)
        {
            Assert.True(budget.TrySpend());
        }

        using var scope = AmbientCriticality.Begin(Criticality.Sheddable);

        var attempts = 0;
        var result = await RunAsync(policy, _ => Fail(ref attempts), time);

        Assert.Equal(StopReason.AttemptsExhausted, result.Reason);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task A_refused_sheddable_retry_is_the_ordinary_budget_rejection()
    {
        var time = new FakeTimeProvider();
        var budget = RetryBudget.Of(minimumPerSecond: 2, time: time);
        var events = new EventRecorder();
        var policy = Budgeted(time, budget) with { OnEvent = events.Record };

        for (var i = 0; i < 10; i++)
        {
            Assert.True(budget.TrySpend());
        }

        using var scope = AmbientCriticality.Begin(Criticality.Sheddable);

        var attempts = 0;
        await RunAsync(policy, _ => Fail(ref attempts), time);

        // No new event kind and no new stop reason: a refusal is a refusal, and criticality changed
        // when it happens rather than what it is.
        Assert.Equal(1, events.CountOf(CallEventKind.RejectedByBudget));
    }

    // ---- Hedging ----

    [Fact]
    public async Task Sheddable_work_is_never_hedged()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out var events);

        await WarmAsync(policy, time, Fast, 40);

        using var scope = AmbientCriticality.Begin(Criticality.Sheddable);

        var race = await RaceAsync(policy, time);

        // No hedge started, and no HedgeSuppressed either: a suppression is a judgment about whether
        // hedging is working, and this is a bound on the call, like an open breaker.
        Assert.Equal(0, events.CountOf(CallEventKind.HedgeStarted));
        Assert.Equal(0, events.CountOf(CallEventKind.HedgeSuppressed));
        Assert.Equal(1, race.Calls);
    }

    [Fact]
    public async Task The_same_policy_hedges_the_same_call_when_it_matters()
    {
        var time = new FakeTimeProvider();
        var policy = Hedging(time, out var events);

        await WarmAsync(policy, time, Fast, 40);

        var race = await RaceAsync(policy, time);

        Assert.True(events.CountOf(CallEventKind.HedgeStarted) > 0);
        Assert.Equal(2, race.Calls);
    }

    // ---- Configuration ----

    [Fact]
    public void A_criticality_with_nothing_to_gate_is_refused()
    {
        var policy = Resilience.Default with { Attempts = 1, UseAmbientCriticality = true };

        var caught = Assert.Throws<ResilienceConfigurationException>(policy.Validate);
        Assert.Contains("nothing for it to gate", caught.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_budget_of_none_and_no_hedge_is_refused_too()
    {
        // Three attempts, but no bucket to hold anything back from and no hedge to withhold.
        var policy = Resilience.Default with { Budget = RetryBudget.None, UseAmbientCriticality = true };

        Assert.Throws<ResilienceConfigurationException>(policy.Validate);
    }

    [Fact]
    public void A_hedging_policy_needs_no_budget_for_it_to_mean_something()
    {
        var policy = Resilience.Default with
        {
            Budget = RetryBudget.None,
            Hedge = Hedge.At(),
            UseAmbientCriticality = true,
        };

        policy.Validate();
    }

    // ---- The outbound header ----

    [Fact]
    public async Task Nothing_goes_on_the_wire_unless_asked()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var client = new HttpClient(new HttpResilienceHandler(transport, TestPolicy.InstantHttp));
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.DoesNotContain(AmbientCriticality.Header, transport.Requests[0].Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task The_level_this_call_is_running_at_goes_on_every_attempt()
    {
        var transport = new ScriptedHttpHandler()
            .Responds(HttpStatusCode.ServiceUnavailable)
            .Responds(HttpStatusCode.OK);

        using var scope = AmbientCriticality.Begin(Criticality.Sheddable);
        using var client = Client(transport);
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Sheddable", Header(transport, 0));
        Assert.Equal("Sheddable", Header(transport, 1));
    }

    [Fact]
    public async Task An_unlabeled_call_says_so_explicitly()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var client = Client(transport);
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        // Writing the default rather than omitting it means the header is visible in a trace, and the
        // two spellings mean the same thing to a reader that clamps unknown values at Critical.
        Assert.Equal("Critical", Header(transport, 0));
    }

    [Fact]
    public async Task The_top_level_is_sent_and_the_peer_is_the_one_that_clamps_it()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var scope = AmbientCriticality.Begin(Criticality.CriticalPlus);
        using var client = Client(transport);
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Equal("CriticalPlus", Header(transport, 0));
    }

    [Fact]
    public async Task The_header_name_is_the_callers_to_choose()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        var options = new HttpResilienceOptions { PropagateCriticality = true, CriticalityHeader = "X-Importance" };

        using var client = new HttpClient(new HttpResilienceHandler(transport, TestPolicy.InstantHttp, options));
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Equal("Critical", Header(transport, 0, "X-Importance"));
        Assert.DoesNotContain(AmbientCriticality.Header, transport.Requests[0].Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task Ours_replaces_whatever_the_caller_wrote()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var scope = AmbientCriticality.Begin(Criticality.Sheddable);
        using var client = Client(transport);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));
        request.Headers.TryAddWithoutValidation(AmbientCriticality.Header, "CriticalPlus");

        using var response = await client.SendAsync(request);

        Assert.Equal("Sheddable", Header(transport, 0));
    }

    [Fact]
    public void An_empty_header_name_is_refused_at_construction()
    {
        var options = new HttpResilienceOptions { PropagateCriticality = true, CriticalityHeader = " " };

        var caught = Assert.Throws<ResilienceConfigurationException>(options.Validate);
        Assert.Contains("CriticalityHeader", caught.Message, StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private static HttpClient Client(HttpMessageHandler transport) =>
        new(new HttpResilienceHandler(transport, TestPolicy.InstantHttp, new HttpResilienceOptions { PropagateCriticality = true }));

    private static string? Header(ScriptedHttpHandler transport, int attempt, string name = AmbientCriticality.Header) =>
        transport.Requests[attempt].Headers.TryGetValues(name, out var values) ? values.Single() : null;

    /// <summary>A retrying policy on the given bucket, reading the ambient level.</summary>
    private static Resilience Budgeted(FakeTimeProvider time, RetryBudget budget) =>
        TestPolicy.WithClock(time) with { Budget = budget, Classifier = Classifier.Http, UseAmbientCriticality = true };

    private static Task<int> Fail(ref int attempts)
    {
        attempts++;
        return Task.FromException<int>(new HttpRequestException("transient"));
    }

    /// <summary>
    ///     Runs one call and moves the fake clock until it lands. A guarded rejection is not instant,
    ///     so a test that simply awaited one would hang.
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

    private static Resilience Hedging(FakeTimeProvider time, out EventRecorder events)
    {
        var recorder = new EventRecorder();
        events = recorder;

        return TestPolicy.WithClock(time) with
        {
            Name = "api",
            Hedge = Hedge.At() with { Window = Window },
            UseAmbientCriticality = true,
            OnEvent = recorder.Record,
        };
    }

    /// <summary>Records <paramref name="times" /> samples of <paramref name="duration" /> into the policy's latency estimate.</summary>
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
    ///     Runs one call whose first attempt blocks until it is cancelled or the pump gives up, and
    ///     moves the clock from outside so that an armed hedge timer can fire.
    /// </summary>
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

        for (var i = 0; i < 10 && !call.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));

            // A real yield, because the loop's continuation runs on the thread pool and the fake clock
            // cannot advance it there.
            await Task.Delay(1);
        }

        gate.TrySetResult();

        return new Race(await call, calls);
    }

    private sealed record Race(CallResult<int> Result, int Calls);
}
