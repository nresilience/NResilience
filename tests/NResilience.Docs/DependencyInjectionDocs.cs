using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NResilience.Extensions;

namespace NResilience.Docs;

/// <summary>Registration, configuration and injection.</summary>
public sealed class DependencyInjectionDocs
{
    [Fact]
    public void One_line_on_a_client()
    {
        var services = new ServiceCollection();

        // <snippet:di-http-client>
        // The one line most people need. The handler is added, the transport timeout stops
        // competing with the deadline, and the client is instrumented.
        services.AddHttpClient<OrdersClient>().AddResilience();

        // Or with a policy of your own, or a registered one by name.
        services.AddHttpClient(name: "reports").AddResilience(policy: Resilience.Http with { Attempts = 5 });
        services.AddHttpClient(name: "payments").AddResilience(policyName: "api", o => o.RetryUnsafeMethods = false);

        // </snippet:di-http-client>

        services.AddResilience(name: "api", policy: Resilience.Http);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(@object: provider.GetRequiredService<IHttpClientFactory>().CreateClient(name: "reports"));
    }

    [Fact]
    public void Policies_are_registered_by_name()
    {
        var services = new ServiceCollection();

        // <snippet:di-register-named>
        // Say what a dependency is worth once, in one place.
        services.AddResilience(name: "api", policy: Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10) });

        // Or in code, without a policy value.
        services.AddResilience(name: "reports", o =>
        {
            o.Preset = "Http";
            o.Attempts = 5;
            o.Deadline = TimeSpan.FromMinutes(value: 5);
        });

        // </snippet:di-register-named>

        using var provider = services.BuildServiceProvider();
        var policies = provider.GetRequiredService<IResiliencePolicies>();

        Assert.Equal(expected: TimeSpan.FromSeconds(value: 10), actual: policies[name: "api"].Deadline);
        Assert.Equal(expected: 5, actual: policies[name: "reports"].Attempts);
    }

    [Fact]
    public void A_section_registers_one_policy_per_child()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path: "appsettings.resilience.json")
            .Build();

        var services = new ServiceCollection();

        // <snippet:di-register-section>
        services.AddResilience(section: configuration.GetSection(key: "Resilience"));

        // </snippet:di-register-section>

        using var provider = services.BuildServiceProvider();
        var policies = provider.GetRequiredService<IResiliencePolicies>();

        Assert.Equal(expected: ["api", "reports"], actual: policies.Names.OrderBy(n => n, comparer: StringComparer.Ordinal));
        Assert.Equal(expected: TimeSpan.FromSeconds(value: 10), actual: policies[name: "api"].Deadline);
        Assert.Equal(expected: TimeSpan.FromSeconds(value: 15), actual: policies[name: "api"].Breaker!.Settings.BreakDuration);
        Assert.Equal(expected: 5, actual: policies[name: "reports"].Attempts);
    }

    /// <summary>
    ///     The reason every section has an <c>Enabled</c>: configuration providers merge and never
    ///     remove a key, so an environment file that wants no breaker has to be able to say so.
    /// </summary>
    [Fact]
    public void A_later_layer_turns_the_breaker_off()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path: "appsettings.resilience.json")
            .AddJsonFile(path: "appsettings.resilience.production.json")
            .Build();

        var services = new ServiceCollection();
        services.AddResilience(section: configuration.GetSection(key: "Resilience"));

        using var provider = services.BuildServiceProvider();
        var policies = provider.GetRequiredService<IResiliencePolicies>();

        Assert.Null(policies[name: "api"].Breaker);

        // Everything else the base file said still stands.
        Assert.Equal(expected: TimeSpan.FromSeconds(value: 10), actual: policies[name: "api"].Deadline);
    }

    /// <summary>
    ///     One key turns off every measured term in the policy and in the breaker the section builds.
    ///     In code the policy's switch stops at the breaker, because a breaker may be shared; a section
    ///     builds one for this policy alone, so there is no second holder to surprise.
    /// </summary>
    [Fact]
    public void One_adaptive_key_makes_a_configured_policy_deterministic()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path: "appsettings.resilience.deterministic.json")
            .Build();

        var services = new ServiceCollection();
        services.AddResilience(section: configuration.GetSection(key: "Resilience"));

        using var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<IResiliencePolicies>()[name: "api"];

        Assert.False(api.Adaptive);
        Assert.Null(api.AttemptCeiling);
        Assert.Null(api.Breaker!.Settings.SlowCalls);
        Assert.Null(api.Breaker.Settings.Failures);

        // What is left is what the section wrote.
        Assert.Equal(expected: TimeSpan.FromSeconds(value: 3), actual: api.AttemptTimeout);
        Assert.Equal(expected: 5, actual: api.Breaker.Settings.ConsecutiveFailures);
    }

    [Fact]
    public void The_configure_callback_holds_what_json_cannot()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path: "appsettings.resilience.json")
            .Build();

        var services = new ServiceCollection();
        var shared = new Breaker { Name = "payments" };

        // <snippet:di-configure-callback>
        // Runs last, after the section and after the live objects are re-attached. A classifier is
        // a lambda and JSON cannot hold one, so this is where one goes - along with a hook, or a
        // breaker you mean to share with something else.
        services.AddResilience(
            name: "api",
            section: configuration.GetSection(key: "Resilience:api"),
            policy => policy with
            {
                Classifier = Classifier.Http.On<MyTransportException>(verdict: Verdict.Transient),
                Breaker = shared,
            });

        // </snippet:di-configure-callback>

        using var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<IResiliencePolicies>()[name: "api"];

        Assert.Same(expected: shared, actual: api.Breaker);
        Assert.Equal(expected: VerdictKind.Transient, actual: api.Classifier.ClassifyException(exception: new MyTransportException()).Kind);
    }

    [Fact]
    public async Task Inject_the_roster_and_resolve_per_call()
    {
        var services = new ServiceCollection();
        services.AddResilience(name: "api", policy: Resilience.Default with { Attempts = 1 });
        using var provider = services.BuildServiceProvider();

        var client = new Orders(policies: provider.GetRequiredService<IResiliencePolicies>());

        Assert.Equal(expected: "ok", actual: await client.ReadAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    // <snippet:di-inject>
    public sealed class Orders(IResiliencePolicies policies)
    {
        // Resolve on every call rather than into a readonly field: a policy captured at
        // construction time is a snapshot, and a configuration reload will never reach it.
        // The indexer is a dictionary lookup.
        public Task<string> ReadAsync(CancellationToken cancellationToken) =>
            policies[name: "api"].RunAsync(attempt => FetchAsync(cancellationToken: attempt), cancellationToken: cancellationToken).AsTask();

        private static Task<string> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(result: "ok");
    }

    // </snippet:di-inject>

    internal sealed class MyTransportException : Exception;

    internal sealed class OrdersClient(HttpClient client)
    {
        internal HttpClient Client { get; } = client;
    }
    [Fact]
    public async Task Every_breaker_and_budget_can_be_put_on_the_health_endpoint()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // <snippet:di-health-checks>
        services.AddResilience(name: "api", policy: Resilience.Http with { Breaker = Api });
        services.AddHttpClient(name: "orders").AddResilience();

        // One line. Every breaker behind a registered policy, every per-host breaker held by a
        // client registered with AddResilience(), and every retry budget's utilization.
        services.AddHealthChecks().AddResilience();

        // </snippet:di-health-checks>

        using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: HealthStatus.Healthy, actual: report.Status);
        Assert.Equal(expected: "Closed", actual: report.Entries["resilience"].Data["breaker:api"]);
    }

    [Fact]
    public void The_health_check_thresholds_and_statuses_are_configurable()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // <snippet:di-health-checks-configured>
        // An open breaker reports Degraded by default: the dependency is down and this process is
        // shedding load correctly, so reporting Unhealthy invites an orchestrator to restart a pod
        // that is working. Override it when the process genuinely cannot serve without that
        // dependency.
        services.AddHealthChecks().AddResilience(configure: o =>
        {
            o.BreakerOpenStatus = HealthStatus.Unhealthy;
            o.BudgetThreshold = 0.75;
            o.Watch(name: "payments", breaker: Payments);
        });

        // </snippet:di-health-checks-configured>

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(@object: provider.GetRequiredService<HealthCheckService>());
    }

    /// <summary>
    ///     Breakers live in static fields, because a breaker created per call has its state discarded
    ///     per call and can never open. NRES005 says so at build time - including in this project,
    ///     which is analyzed exactly as a consumer's would be.
    /// </summary>
    private static readonly Breaker Api = new() { Name = "api" };

    /// <summary>A breaker held the way a hand-built policy holds one, which DI cannot find on its own.</summary>
    private static readonly Breaker Payments = new() { Name = "payments" };
}
