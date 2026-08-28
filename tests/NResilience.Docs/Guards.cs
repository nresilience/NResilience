using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>The circuit breaker and the retry budget.</summary>
public sealed class Guards
{
    [Fact]
    public void A_breaker_is_an_object_you_hold()
    {
        // <snippet:breaker-construct>
        // Breaker scope is a variable with a name and a lifetime. `with` copies the reference,
        // so every policy derived from `payments` shares this breaker.
        var breaker = new Breaker { Name = "payments" };

        var payments = Resilience.Http with { Breaker = breaker };
        var paymentsWrites = payments with { Attempts = 1 };

        // </snippet:breaker-construct>

        Assert.Same(expected: breaker, actual: paymentsWrites.Breaker);
        Assert.Equal(expected: BreakerState.Closed, actual: breaker.State);
    }

    [Fact]
    public void A_breaker_can_trip_on_brownouts()
    {
        // <snippet:breaker-slow-calls>
        // The most common real degradation is not errors, it is a dependency answering 200s at
        // 30x normal latency. A breaker that only counts errors stays closed through the whole
        // incident, because the responses are not failing - they are just slow.
        var breaker = new Breaker(settings: new BreakerSettings
        {
            ConsecutiveFailures = 5, // the default trip condition
            SlowCallThreshold = TimeSpan.FromSeconds(value: 2), // anything slower counts against
            SlowCallRatio = 0.5, // half the window being slow trips it
            MinimumCalls = 20, // below this, a ratio means nothing
            Window = TimeSpan.FromSeconds(value: 30),
            BreakDuration = TimeSpan.FromSeconds(value: 15), // doubles per consecutive open
            MaxBreakDuration = TimeSpan.FromMinutes(value: 2),
            ProbeSuccesses = 2, // two good probes to close, not one
        })
        {
            Name = "search",
        };

        // </snippet:breaker-slow-calls>

        Assert.Equal(expected: TimeSpan.FromSeconds(value: 2), actual: breaker.Settings.SlowCallThreshold);
    }

    [Fact]
    public void A_breaker_can_learn_what_slow_means()
    {
        // <snippet:breaker-adaptive-slow-calls>
        // "3x slower than usual" ports to any dependency. "800 ms" does not: it is a number you
        // have to guess per dependency, before that dependency has ever run in production, and
        // re-guess every time its latency changes. The breaker measures normal itself, from the
        // successful attempts it already samples.
        var breaker = new Breaker(settings: new BreakerSettings
        {
            SlowCalls = SlowCalls.Above(multiple: 3), // slow = 3x the recent median
            SlowCallRatio = 0.5, // half the window being slow trips it
            MinimumCalls = 20,
        })
        {
            Name = "search",
        };

        // What the dependency normally costs, as this breaker measures it. Worth graphing; null
        // until 20 successful calls have landed, and the trip is not armed until then either.
        var normal = breaker.NormalLatency;

        // </snippet:breaker-adaptive-slow-calls>

        Assert.Null(normal);
        Assert.Equal(expected: 3, actual: breaker.Settings.SlowCalls!.Value.Multiple);
    }

    [Fact]
    public void A_breaker_can_be_read_and_driven_by_an_operator()
    {
        var breaker = new Breaker { Name = "payments" };

        // <snippet:breaker-admin>
        var state = breaker.State; // Closed, Open, HalfOpen or Isolated
        var since = breaker.OpenedAt; // null while it is closed

        breaker.Isolate(); // force it open and keep it there
        breaker.Reset(); // close it and forget the history

        // </snippet:breaker-admin>

        Assert.Equal(expected: BreakerState.Closed, actual: breaker.State);
        Assert.Null(value: since);
        Assert.Equal(expected: BreakerState.Closed, actual: state);
    }

    [Fact]
    public async Task An_open_breaker_rejects_the_call()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(settings: new BreakerSettings { ConsecutiveFailures = 1, Time = time }) { Name = "payments" };
        var api = Resilience.Default with { Time = time, Attempts = 1, Breaker = breaker, Backoff = Backoff.None };
        var calls = Sequence.For<int>(time: time).Throws(exception: new IOException(), count: 2).Returns(result: 1);

        await api.TryRunAsync(attempt => calls.NextAsync(cancellationToken: attempt));
        var rejected = api.TryRunAsync(attempt => calls.NextAsync(cancellationToken: attempt)).AsTask();
        time.Advance(delta: TimeSpan.FromMilliseconds(value: 100));
        var result = await rejected;

        // <snippet:breaker-rejection>
        // A refused call reports itself rather than the dependency's last exception, and it says
        // which guard refused it. RetryAfter is there so a caller that schedules its own polling
        // does not have to guess.
        if (result.Exception is CallRejectedException rejection)
        {
            Console.WriteLine(value: rejection.Reason); // DependencyUnavailable, or BudgetExhausted
            Console.WriteLine(value: rejection.RetryAfter); // when to come back, when there is an answer
        }

        // </snippet:breaker-rejection>

        Assert.Equal(expected: StopReason.DependencyUnavailable, actual: result.StopReason);
        Assert.Equal(expected: BreakerState.Open, actual: breaker.State);
    }

    [Fact]
    public void A_budget_can_be_shared_by_name()
    {
        // <snippet:budget-shared>
        // Retries compose multiplicatively: three layers each retrying three times is 27 attempts
        // at the bottom. A budget bounds retries as a fraction of traffic - 10% here - so the
        // aggregate is bounded whether or not anybody coordinates.
        var budget = RetryBudget.Shared(name: "payments", fraction: 0.1, minimumPerSecond: 3);

        var charge = Resilience.Http with { Budget = budget };
        var refund = Resilience.Http with { Budget = budget };

        // </snippet:budget-shared>

        Assert.Same(expected: budget, actual: charge.Budget);
        Assert.Same(expected: budget, actual: refund.Budget);
        Assert.Equal(expected: "payments", actual: budget.Name);
    }

    [Fact]
    public void A_budget_is_on_by_default_and_can_be_turned_off()
    {
        // A snippet is not a call path. The reader holds this policy in a static readonly field, which
        // is what NRES005 asks for; here it lives in a test method so that the docs gate can run it.
#pragma warning disable NRES005

        // <snippet:budget-off>
        // Presets use `RetryBudget.Automatic` to provide a private budget by default.
        // `RetryBudget.None` disables the budget, which is appropriate for dependencies
        // that are not shared. `null` also disables the budget.
        var unbudgeted = Resilience.Default with { Budget = RetryBudget.None };

        // Or tune it, privately to whoever holds the instance.
        var generous = Resilience.Default with { Budget = RetryBudget.Of(fraction: 0.2, minimumPerSecond: 10) };

        // </snippet:budget-off>
#pragma warning restore NRES005

        Assert.Same(expected: RetryBudget.Automatic, actual: Resilience.Default.Budget);
        Assert.Same(expected: RetryBudget.None, actual: unbudgeted.Budget);
        Assert.Equal(expected: 0, actual: generous.Budget!.Utilization);
    }

    [Fact]
    public void A_budget_reports_how_much_of_it_is_spent()
    {
        var budget = RetryBudget.Of();

        // <snippet:budget-utilization>
        // For a dashboard: a budget sitting near 1 is a client whose retries are being refused,
        // which is a symptom to alert on rather than a steady state.
        var spent = budget.Utilization; // 0 to 1

        // </snippet:budget-utilization>

        Assert.Equal(expected: 0, actual: spent);
    }
}
