using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NResilience.Extensions;
using NResilience.Http;

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
        services.AddHttpClient("reports").AddResilience(Resilience.Http with { Attempts = 5 });
        services.AddHttpClient("payments").AddResilience("api", o => o.RetryUnsafeMethods = false);
        // </snippet:di-http-client>

        services.AddResilience("api", Resilience.Http);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IHttpClientFactory>().CreateClient("reports"));
    }

    [Fact]
    public void Policies_are_registered_by_name()
    {
        var services = new ServiceCollection();

        // <snippet:di-register-named>
        // Say what a dependency is worth once, in one place.
        services.AddResilience("api", Resilience.Http with { Deadline = TimeSpan.FromSeconds(10) });

        // Or in code, without a policy value.
        services.AddResilience("reports", o =>
        {
            o.Preset = "Http";
            o.Attempts = 5;
            o.Deadline = TimeSpan.FromMinutes(5);
        });
        // </snippet:di-register-named>

        using ServiceProvider provider = services.BuildServiceProvider();
        IResiliencePolicies policies = provider.GetRequiredService<IResiliencePolicies>();

        Assert.Equal(TimeSpan.FromSeconds(10), policies["api"].Deadline);
        Assert.Equal(5, policies["reports"].Attempts);
    }

    [Fact]
    public void A_section_registers_one_policy_per_child()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.resilience.json")
            .Build();
        var services = new ServiceCollection();

        // <snippet:di-register-section>
        services.AddResilience(configuration.GetSection("Resilience"));
        // </snippet:di-register-section>

        using ServiceProvider provider = services.BuildServiceProvider();
        IResiliencePolicies policies = provider.GetRequiredService<IResiliencePolicies>();

        Assert.Equal(["api", "reports"], policies.Names.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(TimeSpan.FromSeconds(10), policies["api"].Deadline);
        Assert.Equal(TimeSpan.FromSeconds(15), policies["api"].Breaker!.Settings.BreakDuration);
        Assert.Equal(5, policies["reports"].Attempts);
    }

    [Fact]
    public void The_configure_callback_holds_what_json_cannot()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.resilience.json")
            .Build();
        var services = new ServiceCollection();
        var shared = new Breaker { Name = "payments" };

        // <snippet:di-configure-callback>
        // Runs last, after the section and after the live objects are re-attached. A classifier is
        // a lambda and JSON cannot hold one, so this is where one goes - along with a hook, or a
        // breaker you mean to share with something else.
        services.AddResilience(
            "api",
            configuration.GetSection("Resilience:api"),
            policy => policy with
            {
                Classify = Classifier.Http.On<MyTransportException>(Verdict.Transient),
                Breaker = shared,
            });
        // </snippet:di-configure-callback>

        using ServiceProvider provider = services.BuildServiceProvider();
        Resilience api = provider.GetRequiredService<IResiliencePolicies>()["api"];

        Assert.Same(shared, api.Breaker);
        Assert.Equal(VerdictKind.Transient, api.Classify.ClassifyException(new MyTransportException()).Kind);
    }

    [Fact]
    public async Task Inject_the_roster_and_resolve_per_call()
    {
        var services = new ServiceCollection();
        services.AddResilience("api", Resilience.Default with { Attempts = 1 });
        using ServiceProvider provider = services.BuildServiceProvider();

        var client = new Orders(provider.GetRequiredService<IResiliencePolicies>());

        Assert.Equal("ok", await client.ReadAsync(TestContext.Current.CancellationToken));
    }

    // <snippet:di-inject>
    public sealed class Orders(IResiliencePolicies policies)
    {
        // Resolve on every call rather than into a readonly field: a policy captured at
        // construction time is a snapshot, and a configuration reload will never reach it.
        // The indexer is a dictionary lookup.
        public Task<string> ReadAsync(CancellationToken cancellationToken) =>
            policies["api"].RunAsync(attempt => FetchAsync(attempt), cancellationToken).AsTask();

        private static Task<string> FetchAsync(CancellationToken cancellationToken) => Task.FromResult("ok");
    }
    // </snippet:di-inject>

    internal sealed class MyTransportException : Exception;

    internal sealed class OrdersClient(HttpClient client)
    {
        internal HttpClient Client { get; } = client;
    }
}
