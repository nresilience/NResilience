using NResilience.Http;
using NResilience.Http.Internal;

namespace NResilience.Tests;

/// <summary>
///     Tests for the bounded host registry: the second-chance sweep that keeps a client talking to an
///     unbounded set of hosts from growing a breaker and a budget for every one of them.
/// </summary>
public sealed class HostRegistryTests
{
    /// <summary>
    ///     The registry sweeps only when a host is added past the cap, and a sweep clears every
    ///     <c>Used</c> flag before it removes anything. Filling to <paramref name="max" /> and adding one
    ///     more therefore leaves the registry full and cold, which is the state the eviction cases start
    ///     from.
    /// </summary>
    private static HostRegistry FullAndCold(int max, out string[] hosts)
    {
        var registry = new HostRegistry(Resilience.Http, new HttpResilienceOptions { MaxHosts = max });

        hosts = Enumerable.Range(start: 0, max + 1).Select(i => $"host{i}.example").ToArray();

        foreach (var host in hosts)
            registry.For(host);

        return registry;
    }

    [Fact]
    public void Under_the_cap_nothing_is_dropped_and_a_host_keeps_its_scope()
    {
        var registry = new HostRegistry(Resilience.Http, new HttpResilienceOptions { MaxHosts = 16 });

        for (var i = 0; i < 16; i++)
            registry.For($"host{i}.example");

        Assert.Equal(expected: 16, registry.Scopes.Count());
        Assert.Same(registry.For("host0.example"), registry.For("host0.example"));
    }

    [Fact]
    public void A_host_seen_since_the_last_sweep_survives_the_next_one()
    {
        var registry = FullAndCold(max: 8, out var hosts);
        var cold = hosts[0];
        var warm = hosts[1..];

        // Everything but `cold` is touched, so the next sweep has exactly one eviction candidate.
        foreach (var host in warm)
            registry.For(host);

        registry.For("late.example");

        var remaining = registry.Scopes.Select(s => s.Host).ToArray();

        Assert.DoesNotContain(cold, remaining);
        Assert.All(warm, host => Assert.Contains(host, remaining));
        Assert.Contains("late.example", remaining);
    }

    [Fact]
    public void A_dropped_host_that_returns_starts_again_with_a_closed_breaker()
    {
        var registry = FullAndCold(max: 8, out var hosts);
        var cold = hosts[0];

        var before = registry.For(cold);
        before.Breaker!.Isolate();
        Assert.Equal(BreakerState.Isolated, before.Breaker.State);

        // `For` above warmed `cold`, so it takes two sweeps to reach it: the first spends its second
        // chance, the second finds it cold.
        registry.For("late1.example");

        foreach (var host in hosts[1..])
            registry.For(host);

        registry.For("late2.example");

        Assert.DoesNotContain(cold, registry.Scopes.Select(s => s.Host));

        var after = registry.For(cold);

        Assert.NotSame(before, after);
        Assert.Equal(BreakerState.Closed, after.Breaker!.State);
    }

    [Fact]
    public async Task Concurrent_lookups_over_more_hosts_than_the_cap_stay_bounded()
    {
        const int max = 32;

        var registry = new HostRegistry(Resilience.Http, new HttpResilienceOptions { MaxHosts = max });

        var workers = Enumerable.Range(start: 0, count: 8).Select(worker => Task.Run(() =>
        {
            for (var round = 0; round < 200; round++)
            {
                var scope = registry.For($"host{(round * 7 + worker) % 400}.example");

                Assert.NotNull(scope);
            }
        }));

        await Task.WhenAll(workers);

        // A sweep runs on one thread at a time and lets the others through, so the cap bounds growth
        // rather than pinning the count. Four times the cap is generous and still finite.
        Assert.InRange(registry.Scopes.Count(), low: 1, max * 4);
    }

    [Fact]
    public void A_null_cap_never_sweeps()
    {
        var registry = new HostRegistry(Resilience.Http, new HttpResilienceOptions { MaxHosts = null });

        for (var i = 0; i < 2048; i++)
            registry.For($"host{i}.example");

        Assert.Equal(expected: 2048, registry.Scopes.Count());
    }

    [Fact]
    public void The_default_cap_is_1024() => Assert.Equal(expected: 1024, new HttpResilienceOptions().MaxHosts);
}
