using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
                Classify = Classifier.Http.On<MyTransportException>(verdict: Verdict.Transient),
                Breaker = shared,
            });

        // </snippet:di-configure-callback>

        using var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<IResiliencePolicies>()[name: "api"];

        Assert.Same(expected: shared, actual: api.Breaker);
        Assert.Equal(expected: VerdictKind.Transient, actual: api.Classify.ClassifyException(exception: new MyTransportException()).Kind);
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
}
