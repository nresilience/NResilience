using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NResilience.Extensions;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The health check: what it can see, what it calls a problem, and what it refuses to call an
///     outage.
/// </summary>
public sealed class HealthCheckTests
{
    [Fact]
    public async Task A_registered_policys_breaker_is_reported()
    {
        var breaker = new Breaker();
        using var provider = Provider(services => services
            .AddResilience("api", Resilience.Default with { Breaker = breaker })
            .AddHealthChecks().AddResilience());

        var report = await Check(provider);

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Equal("Closed", report.Data["breaker:api"]);
    }

    /// <summary>
    ///     An adaptive breaker knows what this dependency normally costs, and that is the number a
    ///     dashboard wants next to the state. It appears only once the baseline can answer, which is the
    ///     same condition under which the slow-call trip is armed.
    /// </summary>
    [Fact]
    public async Task An_adaptive_breaker_reports_the_latency_it_measured()
    {
        var breaker = new Breaker(new BreakerSettings { SlowCalls = SlowCalls.Above(3) });
        using var provider = Provider(services => services
            .AddResilience("api", Resilience.Default with { Breaker = breaker })
            .AddHealthChecks().AddResilience());

        Assert.DoesNotContain("breaker:api:normal", (await Check(provider)).Data.Keys);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(breaker.TryEnter(out _, out _));
            breaker.Record(VerdictKind.Ok, TimeSpan.FromMilliseconds(40));
        }

        var report = await Check(provider);

        Assert.InRange((double)report.Data["breaker:api:normal"], 40, 45);
    }

    /// <summary>
    ///     The default that matters most. An open breaker means a dependency is down and this process is
    ///     shedding load correctly; reporting Unhealthy for that gets a working pod restarted.
    /// </summary>
    [Fact]
    public async Task An_open_breaker_is_degraded_rather_than_unhealthy()
    {
        var breaker = Tripped();
        using var provider = Provider(services => services
            .AddResilience("api", Resilience.Default with { Breaker = breaker })
            .AddHealthChecks().AddResilience());

        var report = await Check(provider);

        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Contains("Open since", (string)report.Data["breaker:api"], StringComparison.Ordinal);
        Assert.Contains("1 of 1 breaker(s) open", report.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_reported_status_is_configurable_in_both_directions()
    {
        using var provider = Provider(services => services
            .AddResilience("api", Resilience.Default with { Breaker = Tripped() })
            .AddHealthChecks().AddResilience(configure: o => o.BreakerOpenStatus = HealthStatus.Unhealthy));

        Assert.Equal(HealthStatus.Unhealthy, (await Check(provider)).Status);

        using var lenient = Provider(services => services
            .AddResilience("api", Resilience.Default with { Breaker = Tripped() })
            .AddHealthChecks().AddResilience(configure: o => o.BreakerOpenStatus = HealthStatus.Healthy));

        Assert.Equal(HealthStatus.Healthy, (await Check(lenient)).Status);
    }

    [Fact]
    public async Task An_isolated_breaker_counts_as_open()
    {
        var breaker = new Breaker();
        breaker.Isolate();

        using var provider = Provider(services => services
            .AddResilience("api", Resilience.Default with { Breaker = breaker })
            .AddHealthChecks().AddResilience());

        var report = await Check(provider);

        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Contains("Isolated", (string)report.Data["breaker:api"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retry_budgets_utilization_is_reported()
    {
        var budget = RetryBudget.Of(fraction: 0.1, minimumPerSecond: 1);
        using var provider = Provider(services => services
            .AddResilience("api", Resilience.Default with { Budget = budget })
            .AddHealthChecks().AddResilience());

        var report = await Check(provider);

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.InRange((double)report.Data["budget:api"], 0, 0.001);
    }

    [Fact]
    public async Task An_exhausted_retry_budget_is_degraded()
    {
        var budget = RetryBudget.Of(fraction: 0.1, minimumPerSecond: 1);

        // Drain it. The bucket banks ten seconds of the floor rate, so this empties it.
        while (budget.TrySpend())
        {
        }

        using var provider = Provider(services => services
            .AddResilience("api", Resilience.Default with { Budget = budget })
            .AddHealthChecks().AddResilience());

        var report = await Check(provider);

        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Contains("retry budget(s) exhausted", report.Description, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Neither marker is a bucket, so a row for one would be a zero that means "not applicable"
    ///     sitting next to zeros that mean "nothing spent".
    /// </summary>
    [Fact]
    public async Task The_None_and_Automatic_markers_are_not_reported_as_budgets()
    {
        using var provider = Provider(services => services
            .AddResilience("off", Resilience.Default with { Budget = RetryBudget.None })
            .AddResilience("auto", Resilience.Default with { Budget = RetryBudget.Automatic })
            .AddHealthChecks().AddResilience());

        var report = await Check(provider);

        Assert.DoesNotContain("budget:off", report.Data.Keys);

        // The automatic marker is materialized into a real bucket by the registration, because a
        // per-instance budget would be thrown away on every reload. So it is reported, and the
        // marker itself never is.
        Assert.Contains("budget:auto", report.Data.Keys);
    }

    [Fact]
    public async Task A_clients_per_host_breakers_are_reported()
    {
        var transport = new ScriptedHttpHandler().Respond(HttpStatusCode.OK);

        using var provider = Provider(services =>
        {
            services.AddHttpClient("api")
                .AddResilience(policy: TestPolicy.InstantHttp)
                .ConfigurePrimaryHttpMessageHandler(() => transport);

            services.AddHealthChecks().AddResilience();
        });

        // A host gets a scope on first use, so the client has to have been used.
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        var report = await Check(provider);

        Assert.Contains("breaker:api:api.test", report.Data.Keys);
        Assert.Equal("Closed", report.Data["breaker:api:api.test"]);
    }

    [Fact]
    public async Task Http_clients_can_be_excluded()
    {
        var transport = new ScriptedHttpHandler().Respond(HttpStatusCode.OK);

        using var provider = Provider(services =>
        {
            services.AddHttpClient("api")
                .AddResilience(policy: TestPolicy.InstantHttp)
                .ConfigurePrimaryHttpMessageHandler(() => transport);

            services.AddHealthChecks().AddResilience(configure: o => o.IncludeHttpClients = false);
        });

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.DoesNotContain("breaker:api:api.test", (await Check(provider)).Data.Keys);
    }

    [Fact]
    public async Task A_breaker_from_a_static_field_can_be_watched_explicitly()
    {
        var breaker = Tripped();
        using var provider = Provider(services => services
            .AddHealthChecks().AddResilience(configure: o => o.Watch("payments", breaker)));

        var report = await Check(provider);

        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Contains("Open since", (string)report.Data["breaker:payments"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_process_with_nothing_to_report_says_so_rather_than_claiming_health()
    {
        using var provider = Provider(services => services.AddHealthChecks().AddResilience());

        var report = await Check(provider);

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Contains("No breakers or retry budgets are registered", report.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_threshold_that_cannot_work_is_refused_at_registration()
    {
        var services = new ServiceCollection();

        Assert.Throws<ResilienceConfigurationException>(() =>
            services.AddHealthChecks().AddResilience(configure: o => o.BudgetThreshold = 1.5));
    }

    [Fact]
    public async Task The_check_registers_under_a_name_of_your_choosing()
    {
        using var provider = Provider(services => services.AddHealthChecks().AddResilience(name: "nresilience"));

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Contains("nresilience", report.Entries.Keys);
    }

    private static ServiceProvider Provider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();

        // The platform's own HealthCheckService takes an ILogger, so a container that can run health
        // checks at all has logging in it. Every host does; a bare ServiceCollection does not.
        services.AddLogging();

        configure(services);
        return services.BuildServiceProvider();
    }

    private static async Task<HealthReportEntry> Check(IServiceProvider provider)
    {
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        return report.Entries[ResilienceHealthChecksBuilderExtensions.DefaultName];
    }

    /// <summary>A breaker in the open state, by feeding it the failures its settings ask for.</summary>
    /// <summary>
    ///     A recovering breaker is still refusing some of what it is offered, so the answer to "is this
    ///     process serving every call?" is the same as it is for an open one.
    /// </summary>
    [Fact]
    public async Task A_recovering_breaker_is_degraded_too()
    {
        var time = new FakeTimeProvider();

        var breaker = new Breaker(new BreakerSettings
        {
            Time = time,
            ConsecutiveFailures = 1,
            ProbeSuccesses = 1,
            BreakDuration = TimeSpan.FromSeconds(10),
            BreakJitter = Jitter.None,
            Recovery = Recovery.Over(0.25),
        });

        breaker.TryEnter(out _, out _);
        breaker.Record(VerdictKind.Transient, TimeSpan.Zero);

        time.Advance(TimeSpan.FromSeconds(10));
        breaker.TryEnter(out _, out _);
        breaker.Record(VerdictKind.Ok, TimeSpan.Zero);

        Assert.Equal(BreakerState.Recovering, breaker.State);

        using var provider = Provider(services => services
            .AddResilience("api", Resilience.Default with { Breaker = breaker })
            .AddHealthChecks().AddResilience());

        var report = await Check(provider);

        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Contains("Recovering since", (string)report.Data["breaker:api"], StringComparison.Ordinal);
        Assert.Contains("1 of 1 breaker(s) open, recovering or isolated", report.Description, StringComparison.Ordinal);
    }

    private static Breaker Tripped()
    {
        var breaker = new Breaker(new BreakerSettings { ConsecutiveFailures = 1 });

        breaker.TryEnter(out _, out _);
        breaker.Record(VerdictKind.Transient, TimeSpan.Zero);

        return breaker;
    }
}
