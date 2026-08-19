using Microsoft.Extensions.Time.Testing;
using NResilience.Internal;

namespace NResilience.Tests;

/// <summary>
/// The retry budget: the token bucket that bounds retries as a fraction of traffic, and the scoping
/// rules that decide whose traffic it is a fraction of.
/// </summary>
public sealed class BudgetTests
{
    private static Resilience Instant(FakeTimeProvider time) => Resilience.Default with
    {
        Backoff = Backoff.None,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
        Deadline = Timeout.InfiniteTimeSpan,
        Time = time,
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

    // ---- The bucket ----

    [Fact]
    public void None_never_refuses()
    {
        for (int i = 0; i < 1_000; i++)
        {
            Assert.True(RetryBudget.None.TrySpend());
        }

        Assert.Equal(0, RetryBudget.None.Utilisation);
        Assert.True(RetryBudget.None.IsNone);
    }

    [Fact]
    public void A_fresh_budget_starts_full_so_a_cold_start_is_not_throttled()
    {
        var time = new FakeTimeProvider();

        // minimumPerSecond 2 banks 10 seconds of the floor rate, so 20 retries are available at once.
        RetryBudget budget = RetryBudget.Of(minimumPerSecond: 2, time: time);

        Assert.Equal(0, budget.Utilisation);

        for (int i = 0; i < 20; i++)
        {
            Assert.True(budget.TrySpend(), $"retry {i + 1} of the burst was refused");
        }

        Assert.False(budget.TrySpend());
        Assert.Equal(1, budget.Utilisation);
    }

    [Fact]
    public void The_floor_rate_refills_it_over_time()
    {
        var time = new FakeTimeProvider();
        RetryBudget budget = RetryBudget.Of(minimumPerSecond: 2, time: time);

        while (budget.TrySpend())
        {
        }

        time.Advance(TimeSpan.FromSeconds(1));

        // The floor exists so a low-traffic client, whose successes are too sparse to fund anything,
        // can still retry at all.
        Assert.True(budget.TrySpend());
        Assert.True(budget.TrySpend());
        Assert.False(budget.TrySpend());
    }

    [Fact]
    public void The_floor_rate_does_not_bank_beyond_the_burst_ceiling()
    {
        var time = new FakeTimeProvider();
        RetryBudget budget = RetryBudget.Of(minimumPerSecond: 2, time: time);

        while (budget.TrySpend())
        {
        }

        time.Advance(TimeSpan.FromHours(1));

        int spent = 0;
        while (budget.TrySpend())
        {
            spent++;
        }

        Assert.Equal(20, spent);
    }

    [Fact]
    public void Successes_fund_retries_at_the_configured_fraction()
    {
        var time = new FakeTimeProvider();

        // No floor, so traffic is the only source of tokens, and a capacity of exactly one retry.
        RetryBudget budget = RetryBudget.Of(fraction: 0.25, minimumPerSecond: 0, time: time);

        Assert.True(budget.TrySpend());
        Assert.False(budget.TrySpend());

        budget.Deposit();
        budget.Deposit();
        budget.Deposit();
        Assert.False(budget.TrySpend());

        // Four successes at a quarter each, so one retry. This is the whole mechanism: with every
        // client independently holding to its fraction, fleet-wide amplification is bounded without
        // any coordination protocol.
        budget.Deposit();
        Assert.True(budget.TrySpend());
    }

    [Fact]
    public void Utilisation_reports_how_much_is_spent()
    {
        var time = new FakeTimeProvider();
        RetryBudget budget = RetryBudget.Of(minimumPerSecond: 1, time: time);

        Assert.Equal(0, budget.Utilisation);

        for (int i = 0; i < 5; i++)
        {
            Assert.True(budget.TrySpend());
        }

        Assert.Equal(0.5, budget.Utilisation);
    }

    [Fact]
    public void A_refusal_hints_when_a_token_will_be_available()
    {
        var time = new FakeTimeProvider();
        RetryBudget budget = RetryBudget.Of(minimumPerSecond: 2, time: time);

        while (budget.TrySpend())
        {
        }

        Assert.Equal(TimeSpan.FromMilliseconds(500), budget.RetryAfterHint());
    }

    [Fact]
    public void A_budget_with_no_floor_has_no_honest_hint_to_give()
    {
        var time = new FakeTimeProvider();
        RetryBudget budget = RetryBudget.Of(minimumPerSecond: 0, time: time);

        Assert.True(budget.TrySpend());
        Assert.False(budget.TrySpend());

        // Only traffic can refill it, and the budget has no idea when the next call is coming.
        Assert.Null(budget.RetryAfterHint());
    }

    // ---- Configuration ----

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void A_fraction_outside_the_unit_interval_is_rejected(double fraction)
    {
        var problem = Assert.Throws<ResilienceConfigurationException>(() => RetryBudget.Of(fraction));

        Assert.Contains("RetryBudget.None", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_floor_is_rejected()
    {
        Assert.Throws<ResilienceConfigurationException>(() => RetryBudget.Of(minimumPerSecond: -1));
    }

    // ---- Scope ----

    [Fact]
    public void Shared_returns_one_budget_per_name()
    {
        RetryBudget payments = RetryBudget.Shared("budget-tests-payments");
        RetryBudget again = RetryBudget.Shared("budget-tests-payments");
        RetryBudget search = RetryBudget.Shared("budget-tests-search");

        Assert.Same(payments, again);
        Assert.NotSame(payments, search);
        Assert.Equal("budget-tests-payments", payments.Name);
    }

    [Fact]
    public void An_automatic_budget_is_private_to_one_policy_instance()
    {
        Resilience payments = Resilience.Default with { Name = "budget-scope-payments" };
        Resilience search = Resilience.Default with { Name = "budget-scope-search" };

        Assert.Same(ExecutionState.BudgetFor(payments), ExecutionState.BudgetFor(payments));

        // A single global budget would let a storm against payments throttle retries to search,
        // which is the blast-radius inversion a resilience library exists to prevent.
        Assert.NotSame(ExecutionState.BudgetFor(payments), ExecutionState.BudgetFor(search));
    }

    [Fact]
    public void A_policy_that_cannot_retry_gets_no_automatic_budget()
    {
        Assert.Null(ExecutionState.BudgetFor(Resilience.Default with { Attempts = 1 }));
    }

    [Fact]
    public void None_turns_the_budget_off_and_an_instance_opts_into_sharing()
    {
        RetryBudget shared = RetryBudget.Of();

        Assert.Null(ExecutionState.BudgetFor(Resilience.Default with { Budget = RetryBudget.None }));
        Assert.Same(shared, ExecutionState.BudgetFor(Resilience.Default with { Budget = shared }));
    }

    [Fact]
    public async Task Executing_a_policy_does_not_change_what_it_compares_or_hashes_as()
    {
        Resilience left = Resilience.Default with { Name = "budget-equality", Deadline = Timeout.InfiniteTimeSpan };
        Resilience right = Resilience.Default with { Name = "budget-equality", Deadline = Timeout.InfiniteTimeSpan };

        int hash = left.GetHashCode();
        Assert.Equal(left, right);

        // Creates the automatic budget, which is exactly why it lives in a ConditionalWeakTable and
        // not in a field: a record's synthesized equality compares every instance field.
        await left.RunAsync(_ => Task.FromResult(1));

        Assert.Equal(left, right);
        Assert.Equal(hash, left.GetHashCode());
    }

    [Fact]
    public void Resilience_None_stays_a_passthrough()
    {
        Assert.Same(RetryBudget.None, Resilience.None.Budget);
    }

    // ---- Executor integration ----

    [Fact]
    public async Task An_exhausted_budget_refuses_the_retry_rather_than_the_call()
    {
        var time = new FakeTimeProvider();
        Resilience policy = Instant(time) with
        {
            Attempts = 3,
            Budget = RetryBudget.Of(fraction: 0.25, minimumPerSecond: 0, time: time),
        };

        int calls = 0;
        Task<int> Failing(CancellationToken _)
        {
            calls++;
            return Task.FromException<int>(new IOException("down"));
        }

        // The one token in the bucket funds one retry, so the first operation makes two attempts.
        CallResult<int> first = await RunAsync(policy, Failing, time);
        Assert.Equal(2, calls);
        Assert.Equal(StopReason.BudgetExhausted, first.StopReason);

        // The next operation still gets its first attempt — a budget throttles retries, never the
        // call the caller actually asked for.
        CallResult<int> second = await RunAsync(policy, Failing, time);
        Assert.Equal(3, calls);
        Assert.Equal(StopReason.BudgetExhausted, second.StopReason);
        Assert.Single(second.Attempts);
    }

    [Fact]
    public async Task A_budget_refusal_reports_itself_with_the_earlier_failure_as_its_cause()
    {
        var time = new FakeTimeProvider();
        Resilience policy = Instant(time) with
        {
            Attempts = 3,
            Budget = RetryBudget.Of(fraction: 0.25, minimumPerSecond: 2, time: time),
        };

        RetryBudget budget = policy.Budget!;
        while (budget.TrySpend())
        {
        }

        CallResult<int> result = await RunAsync(policy, _ => Task.FromException<int>(new IOException("down")), time);

        Assert.Equal(StopReason.BudgetExhausted, result.StopReason);

        var rejected = Assert.IsType<CallRejectedException>(result.Exception);
        Assert.IsType<IOException>(rejected.InnerException);
        Assert.NotNull(rejected.RetryAfter);
    }

    [Fact]
    public async Task The_budget_bounds_amplification_under_sustained_failure()
    {
        var time = new FakeTimeProvider();
        Resilience policy = Instant(time) with
        {
            Attempts = 4,
            Budget = RetryBudget.Of(fraction: 0.25, minimumPerSecond: 0, time: time),
        };

        int attempts = 0;
        Task<int> Failing(CancellationToken _)
        {
            attempts++;
            return Task.FromException<int>(new IOException("down"));
        }

        for (int operation = 0; operation < 20; operation++)
        {
            await RunAsync(policy, Failing, time);
        }

        // Twenty operations at four attempts each is eighty attempts, which is the storm a per-call
        // attempt limit cannot prevent — every caller independently believing it is being
        // reasonable. The budget funds one retry in total, so it is twenty-one.
        Assert.Equal(21, attempts);
    }

    [Fact]
    public async Task Successful_traffic_keeps_funding_retries()
    {
        var time = new FakeTimeProvider();
        Resilience policy = Instant(time) with
        {
            Attempts = 2,
            Budget = RetryBudget.Of(fraction: 0.25, minimumPerSecond: 0, time: time),
        };

        int attempts = 0;

        Task<int> Fail(CancellationToken _)
        {
            attempts++;
            return Task.FromException<int>(new IOException("down"));
        }

        Task<int> Succeed(CancellationToken _)
        {
            attempts++;
            return Task.FromResult(1);
        }

        // The one token in a fresh bucket funds one retry.
        Assert.False((await RunAsync(policy, Fail, time)).IsSuccess);
        Assert.Equal(2, attempts);

        // Empty now, so the next failing operation gets its call and no retry.
        attempts = 0;
        Assert.False((await RunAsync(policy, Fail, time)).IsSuccess);
        Assert.Equal(1, attempts);

        for (int i = 0; i < 4; i++)
        {
            Assert.True((await RunAsync(policy, Succeed, time)).IsSuccess);
        }

        // Four successes at a quarter of a token each, so retries are funded again. A budget
        // recovers from traffic rather than from a timer, which is what makes it a fraction.
        attempts = 0;
        Assert.False((await RunAsync(policy, Fail, time)).IsSuccess);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Two_policies_sharing_one_budget_draw_on_the_same_tokens()
    {
        var time = new FakeTimeProvider();
        RetryBudget shared = RetryBudget.Of(fraction: 0.25, minimumPerSecond: 0, time: time);

        Resilience payments = Instant(time) with { Attempts = 2, Budget = shared };
        Resilience search = Instant(time) with { Attempts = 2, Budget = shared };

        int paymentAttempts = 0;
        int searchAttempts = 0;

        await RunAsync(payments, _ =>
        {
            paymentAttempts++;
            return Task.FromException<int>(new IOException("down"));
        }, time);

        await RunAsync(search, _ =>
        {
            searchAttempts++;
            return Task.FromException<int>(new IOException("down"));
        }, time);

        // Payments spent the only token, so search got its call but no retry. That is opt-in, and it
        // is why the default is a budget private to one policy instance.
        Assert.Equal(2, paymentAttempts);
        Assert.Equal(1, searchAttempts);
    }

    [Fact]
    public async Task A_permanent_failure_is_never_charged_to_the_budget()
    {
        var time = new FakeTimeProvider();
        RetryBudget budget = RetryBudget.Of(fraction: 0.25, minimumPerSecond: 0, time: time);
        Resilience policy = Instant(time) with { Attempts = 3, Budget = budget };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await policy.RunAsync(_ => Task.FromException<int>(new InvalidOperationException("bad request"))));

        // The retry never happened, so nothing was spent on it.
        Assert.Equal(0, budget.Utilisation);
    }

    [Fact]
    public async Task Caller_cancellation_is_never_charged_to_the_budget()
    {
        var time = new FakeTimeProvider();
        RetryBudget budget = RetryBudget.Of(fraction: 0.25, minimumPerSecond: 0, time: time);
        Resilience policy = Instant(time) with { Attempts = 3, Budget = budget };

        using var caller = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await policy.RunAsync(
                async ct =>
                {
                    await caller.CancelAsync();
                    ct.ThrowIfCancellationRequested();
                    return 1;
                },
                caller.Token));

        Assert.Equal(0, budget.Utilisation);
    }
}
