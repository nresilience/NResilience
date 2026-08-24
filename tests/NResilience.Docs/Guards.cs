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

        Assert.Same(breaker, paymentsWrites.Breaker);
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public void A_breaker_can_trip_on_brownouts()
    {
        // <snippet:breaker-slow-calls>
        // The most common real degradation is not errors, it is a dependency answering 200s at
        // 30x normal latency. A breaker that only counts errors stays closed through the whole
        // incident, because the responses are not failing - they are just slow.
        var breaker = new Breaker(new BreakerSettings
        {
            ConsecutiveFailures = 5,                             // the default trip condition
            SlowCallThreshold = TimeSpan.FromSeconds(2),         // anything slower counts against
            SlowCallRatio = 0.5,                                 // half the window being slow trips it
            MinimumCalls = 20,                                   // below this, a ratio means nothing
            Window = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(15),            // doubles per consecutive open
            MaxBreakDuration = TimeSpan.FromMinutes(2),
            ProbeSuccesses = 2,                                  // two good probes to close, not one
        })
        {
            Name = "search",
        };
        // </snippet:breaker-slow-calls>

        Assert.Equal(TimeSpan.FromSeconds(2), breaker.Settings.SlowCallThreshold);
    }

    [Fact]
    public void A_breaker_can_be_read_and_driven_by_an_operator()
    {
        var breaker = new Breaker { Name = "payments" };

        // <snippet:breaker-admin>
        var state = breaker.State;         // Closed, Open, HalfOpen or Isolated
        var since = breaker.OpenedAt;   // null while it is closed

        breaker.Isolate();                          // force it open and keep it there
        breaker.Reset();                            // close it and forget the history
        // </snippet:breaker-admin>

        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Null(since);
        Assert.Equal(BreakerState.Closed, state);
    }

    [Fact]
    public async Task An_open_breaker_rejects_the_call()
    {
        var time = new FakeTimeProvider();
        var breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 1, Time = time }) { Name = "payments" };
        var api = Resilience.Default with { Time = time, Attempts = 1, Breaker = breaker, Backoff = Backoff.None };
        var calls = Sequence.For<int>(time).Throws(new IOException(), 2).Returns(1);

        await api.TryRunAsync(attempt => calls.NextAsync(attempt));
        var rejected = api.TryRunAsync(attempt => calls.NextAsync(attempt)).AsTask();
        time.Advance(TimeSpan.FromMilliseconds(100));
        var result = await rejected;

        // <snippet:breaker-rejection>
        // A refused call reports itself rather than the dependency's last exception, and it says
        // which guard refused it. RetryAfter is there so a caller that schedules its own polling
        // does not have to guess.
        if (result.Exception is CallRejectedException rejection)
        {
            Console.WriteLine(rejection.Reason);      // DependencyUnavailable, or BudgetExhausted
            Console.WriteLine(rejection.RetryAfter);  // when to come back, when there is an answer
        }
        // </snippet:breaker-rejection>

        Assert.Equal(StopReason.DependencyUnavailable, result.StopReason);
        Assert.Equal(BreakerState.Open, breaker.State);
    }

    [Fact]
    public void A_budget_can_be_shared_by_name()
    {
        // <snippet:budget-shared>
        // Retries compose multiplicatively: three layers each retrying three times is 27 attempts
        // at the bottom. A budget bounds retries as a fraction of traffic - 10% here - so the
        // aggregate is bounded whether or not anybody coordinates.
        var budget = RetryBudget.Shared("payments", fraction: 0.1, minimumPerSecond: 3);

        var charge = Resilience.Http with { Budget = budget };
        var refund = Resilience.Http with { Budget = budget };
        // </snippet:budget-shared>

        Assert.Same(budget, charge.Budget);
        Assert.Same(budget, refund.Budget);
        Assert.Equal("payments", budget.Name);
    }

    [Fact]
    public void A_budget_is_on_by_default_and_can_be_turned_off()
    {
        // A snippet is not a call path. The reader holds this policy in a static readonly field, which
        // is what NRES005 asks for; here it lives in a test method so that the docs gate can run it.
        #pragma warning disable NRES005
        // <snippet:budget-off>
        // Null - the default - is an automatic budget private to this policy instance, so storm
        // protection needs no configuration. None is the opt-out, and the only correct
        // use is a dependency you know is not shared.
        var unbudgeted = Resilience.Default with { Budget = RetryBudget.None };

        // Or tune it, privately to whoever holds the instance.
        var generous = Resilience.Default with { Budget = RetryBudget.Of(fraction: 0.2, minimumPerSecond: 10) };
        // </snippet:budget-off>
        #pragma warning restore NRES005

        Assert.NotNull(unbudgeted.Budget);
        Assert.Equal(0, generous.Budget!.Utilization);
    }

    [Fact]
    public void A_budget_reports_how_much_of_it_is_spent()
    {
        var budget = RetryBudget.Of();

        // <snippet:budget-utilization>
        // For a dashboard: a budget sitting near 1 is a client whose retries are being refused,
        // which is a symptom to alert on rather than a steady state.
        var spent = budget.Utilization;   // 0 to 1
        // </snippet:budget-utilization>

        Assert.Equal(0, spent);
    }
}
