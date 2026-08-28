using Microsoft.Extensions.Time.Testing;

namespace NResilience.Tests;

/// <summary>
///     <c>PolicyScope&lt;TKey&gt;</c>: the per-host scoping the HTTP handler has always done, keyed by
///     anything else. Two properties are what it is for - a key gets guards of its own, and the set of
///     keys stays bounded - and the second one is the part a hand-rolled dictionary forgets.
/// </summary>
public sealed class PolicyScopeTests
{
    private static Resilience Template(FakeTimeProvider time) => Resilience.Default with { Breaker = new Breaker(), Time = time };

    // ---- One key, one set of guards ----

    [Fact]
    public void A_key_gets_the_same_policy_every_time_it_is_asked_for()
    {
        var scope = new PolicyScope<string>(Resilience.Default);

        Assert.Same(scope.For("alpha"), scope.For("alpha"));
        Assert.NotSame(scope.For("alpha"), scope.For("beta"));
    }

    [Fact]
    public void Each_key_gets_a_breaker_of_its_own_so_one_tenant_cannot_trip_another()
    {
        var time = new FakeTimeProvider();
        var scope = new PolicyScope<string>(Template(time));

        var alpha = scope.For("alpha");
        var beta = scope.For("beta");

        Assert.NotSame(alpha.Breaker, beta.Breaker);

        alpha.Breaker!.Isolate();

        Assert.Equal(BreakerState.Isolated, alpha.Breaker.State);
        Assert.Equal(BreakerState.Closed, beta.Breaker!.State);
    }

    [Fact]
    public void The_template_breaker_is_a_prototype_and_is_never_the_one_executed_against()
    {
        var prototype = new Breaker(new BreakerSettings { ConsecutiveFailures = 2 });
        var scope = new PolicyScope<string>(Resilience.Default with { Breaker = prototype });

        var alpha = scope.For("alpha");

        Assert.NotSame(prototype, alpha.Breaker);
        Assert.Equal(expected: 2, alpha.Breaker!.Settings.ConsecutiveFailures);
    }

    [Fact]
    public void A_per_key_breaker_takes_the_policy_clock_unless_the_settings_named_one()
    {
        var policyTime = new FakeTimeProvider();
        var settingsTime = new FakeTimeProvider();

        var inherited = new PolicyScope<string>(Resilience.Default with { Breaker = new Breaker(), Time = policyTime });
        var named = new PolicyScope<string>(Resilience.Default with { Breaker = new Breaker(new BreakerSettings { Time = settingsTime }), Time = policyTime });

        Assert.Same(policyTime, inherited.For("alpha").Breaker!.Settings.Time);
        Assert.Same(settingsTime, named.For("alpha").Breaker!.Settings.Time);
    }

    [Fact]
    public void A_template_with_no_breaker_gets_no_breaker_per_key()
    {
        var scope = new PolicyScope<string>(Resilience.Default);

        Assert.Null(scope.For("alpha").Breaker);
        Assert.Empty(scope.Breakers());
    }

    [Fact]
    public void An_automatic_budget_becomes_one_budget_per_key()
    {
        var scope = new PolicyScope<string>(Resilience.Default);

        var alpha = scope.For("alpha");
        var beta = scope.For("beta");

        Assert.NotNull(alpha.Budget);
        Assert.NotSame(alpha.Budget, beta.Budget);
    }

    [Fact]
    public void A_shared_budget_is_a_deliberate_decision_and_is_left_alone()
    {
        var shared = RetryBudget.Shared("tenants");
        var scope = new PolicyScope<string>(Resilience.Default with { Budget = shared });

        Assert.Same(shared, scope.For("alpha").Budget);
        Assert.Same(shared, scope.For("beta").Budget);
    }

    [Fact]
    public void A_hedging_policy_gets_a_latency_estimate_per_key()
    {
        var scope = new PolicyScope<string>(Resilience.Default with { Hedge = Hedge.At(0.95) });

        // The estimate is keyed by policy instance, so distinct policies is the property that matters:
        // a slow tenant cannot lower the hedge threshold for a fast one.
        Assert.NotSame(scope.For("alpha"), scope.For("beta"));
    }

    [Fact]
    public void The_key_names_the_policy_and_the_breaker_for_whoever_reads_a_log_line()
    {
        var anonymous = new PolicyScope<string>(Resilience.Default with { Breaker = new Breaker() });
        var named = new PolicyScope<string>(Resilience.Http with { Breaker = new Breaker() });

        Assert.Equal("alpha", anonymous.For("alpha").Name);
        Assert.Equal("alpha", anonymous.For("alpha").Breaker!.Name);
        Assert.Equal("http:alpha", named.For("alpha").Name);
    }

    // ---- Shaping ----

    [Fact]
    public void Shaping_a_key_does_not_cost_it_the_guards_the_scope_exists_to_give_it()
    {
        var scope = new PolicyScope<string>(
            Resilience.Default with { Breaker = new Breaker() },
            key => Resilience.Default with { Breaker = new Breaker(), Attempts = key == "batch" ? 5 : 2 });

        var batch = scope.For("batch");
        var interactive = scope.For("interactive");

        Assert.Equal(expected: 5, batch.Attempts);
        Assert.Equal(expected: 2, interactive.Attempts);
        Assert.NotNull(batch.Breaker);
        Assert.NotSame(batch.Breaker, interactive.Breaker);
        Assert.NotSame(batch.Budget, interactive.Budget);
    }

    [Fact]
    public void Shaping_runs_once_per_key_rather_than_once_per_lookup()
    {
        var shaped = 0;

        var scope = new PolicyScope<string>(Resilience.Default, _ =>
        {
            Interlocked.Increment(ref shaped);
            return Resilience.Default;
        });

        for (var i = 0; i < 10; i++)
            scope.For("alpha");

        Assert.Equal(expected: 1, shaped);
    }

    // ---- The views ----

    [Fact]
    public void The_breakers_and_budgets_are_reported_by_key_for_a_health_endpoint()
    {
        var scope = new PolicyScope<string>(Resilience.Default with { Breaker = new Breaker() });

        scope.For("alpha");
        scope.For("beta");

        var breakers = scope.Breakers();
        var budgets = scope.Budgets();

        Assert.Equal(["alpha", "beta"], breakers.Keys.Order());
        Assert.Equal(["alpha", "beta"], budgets.Keys.Order());
        Assert.Same(scope.For("alpha").Breaker, breakers["alpha"]);
        Assert.Same(scope.For("beta").Budget, budgets["beta"]);
    }

    [Fact]
    public void A_key_that_has_never_been_asked_for_has_no_guards_to_report()
    {
        var scope = new PolicyScope<string>(Resilience.Default with { Breaker = new Breaker() });

        Assert.Equal(expected: 0, scope.Count);
        Assert.Empty(scope.Breakers());

        scope.For("alpha");

        Assert.Equal(expected: 1, scope.Count);
    }

    [Fact]
    public void The_template_and_the_cap_are_reported_as_handed_in()
    {
        var template = Resilience.Default with { Attempts = 2 };
        var scope = new PolicyScope<string>(template, maxKeys: 8);

        Assert.Same(template, scope.Template);
        Assert.Equal(expected: 8, scope.MaxKeys);
        Assert.Equal(expected: 1024, new PolicyScope<string>(template).MaxKeys);
    }

    // ---- Keys ----

    [Fact]
    public void Keys_are_compared_with_the_comparer_the_scope_was_given()
    {
        var ordinal = new PolicyScope<string>(Resilience.Default);
        var insensitive = new PolicyScope<string>(Resilience.Default, comparer: StringComparer.OrdinalIgnoreCase);

        Assert.NotSame(ordinal.For("Alpha"), ordinal.For("alpha"));
        Assert.Same(insensitive.For("Alpha"), insensitive.For("alpha"));
    }

    [Fact]
    public void Any_key_type_works_and_a_value_key_is_not_boxed_into_a_different_one()
    {
        var scope = new PolicyScope<int>(Resilience.Default);

        Assert.Same(scope.For(7), scope.For(7));
        Assert.NotSame(scope.For(7), scope.For(8));
    }

    // ---- Bound ----

    /// <summary>
    ///     A scope sweeps only when a key is added past the cap, and a sweep clears every recency flag
    ///     before it removes anything. Filling to <paramref name="maxKeys" /> and adding one more
    ///     therefore leaves the scope full and cold, which is the state the eviction cases start from.
    /// </summary>
    private static PolicyScope<string> FullAndCold(int maxKeys, out string[] keys)
    {
        var scope = new PolicyScope<string>(Resilience.Default with { Breaker = new Breaker() }, maxKeys: maxKeys);

        keys = Enumerable.Range(start: 0, maxKeys + 1).Select(i => $"key{i}").ToArray();

        foreach (var key in keys)
            scope.For(key);

        return scope;
    }

    [Fact]
    public void Under_the_cap_nothing_is_dropped()
    {
        var scope = new PolicyScope<string>(Resilience.Default, maxKeys: 16);

        for (var i = 0; i < 16; i++)
            scope.For($"key{i}");

        Assert.Equal(expected: 16, scope.Count);
    }

    [Fact]
    public void A_key_seen_since_the_last_sweep_survives_the_next_one()
    {
        var scope = FullAndCold(maxKeys: 8, out var keys);
        var cold = keys[0];
        var warm = keys[1..];

        // Everything but `cold` is touched, so the next sweep has exactly one eviction candidate.
        foreach (var key in warm)
            scope.For(key);

        scope.For("late");

        var kept = scope.Breakers().Keys;

        Assert.DoesNotContain(cold, kept);
        Assert.All(warm, key => Assert.Contains(key, kept));
        Assert.Contains("late", kept);
    }

    [Fact]
    public void A_dropped_key_that_returns_starts_again_with_a_closed_breaker()
    {
        var scope = new PolicyScope<string>(Resilience.Default with { Breaker = new Breaker() }, maxKeys: 8);

        var before = scope.For("first");
        before.Breaker!.Isolate();

        // Two hundred keys through a scope that keeps eight: whatever the sweep order, the key nobody
        // has asked for since the first line is gone.
        for (var i = 0; i < 200; i++)
            scope.For($"key{i}");

        Assert.DoesNotContain("first", scope.Breakers().Keys);

        var after = scope.For("first");

        Assert.NotSame(before, after);
        Assert.Equal(BreakerState.Closed, after.Breaker!.State);
    }

    [Fact]
    public async Task Concurrent_lookups_over_more_keys_than_the_cap_stay_bounded()
    {
        const int max = 32;

        var scope = new PolicyScope<string>(Resilience.Default, maxKeys: max);

        var workers = Enumerable.Range(start: 0, count: 8).Select(worker => Task.Run(() =>
        {
            for (var round = 0; round < 200; round++)
                Assert.NotNull(scope.For($"key{(round * 7 + worker) % 400}"));
        }));

        await Task.WhenAll(workers);

        // A sweep runs on one thread at a time and lets the others through, so the cap bounds growth
        // rather than pinning the count. Four times the cap is generous and still finite.
        Assert.InRange(scope.Count, low: 1, high: max * 4);
    }

    // ---- Refusals ----

    [Fact]
    public void An_unbounded_scope_is_not_on_offer_because_it_is_the_leak_the_type_prevents()
    {
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => new PolicyScope<string>(Resilience.Default, maxKeys: 0));

        Assert.Equal("maxKeys", refused.ParamName);
    }

    [Fact]
    public void A_template_that_cannot_execute_is_refused_where_the_scope_is_written()
    {
        var refused = Assert.Throws<ResilienceConfigurationException>(() => new PolicyScope<string>(Resilience.Default with { Attempts = 0 }));

        Assert.Contains("Attempts", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_null_template_or_key_is_refused_rather_than_keyed_by_nothing()
    {
        Assert.Throws<ArgumentNullException>(() => new PolicyScope<string>(null!));
        Assert.Throws<ArgumentNullException>(() => new PolicyScope<string>(Resilience.Default).For(null!));
    }

    // ---- It is a policy like any other ----

    [Fact]
    public async Task A_scoped_policy_runs_and_charges_its_own_key()
    {
        var scope = new PolicyScope<string>(Resilience.Default with { Breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 1 }) });

        var result = await scope.For("alpha").TryRunAsync(_ => Task.FromException<int>(new TimeoutException("down")), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BreakerState.Open, scope.Breakers()["alpha"].State);
        Assert.Equal(BreakerState.Closed, scope.For("beta").Breaker!.State);
    }
}
