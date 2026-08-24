using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NResilience.Extensions;
using NResilience.Http;

namespace NResilience.Docs;

/// <summary>The complete examples the guides are built around.</summary>
public sealed class Guides
{
    private sealed record Order(string Id, string Status);

    [Fact]
    public async Task Retry_an_http_call_end_to_end()
    {
        var transport = new Doubles.ScriptedTransport(
            () => Doubles.Status(HttpStatusCode.ServiceUnavailable),
            () => Doubles.Json(new Order("A-1", "shipped")));

        var order = await ReadOrderAsync(
            ResilienceHttp.CreateClient(Resilience.Http with { Backoff = Backoff.None }, innerHandler: transport),
            "A-1",
            TestContext.Current.CancellationToken);

        Assert.Equal("shipped", order?.Status);
        Assert.Equal(2, transport.Requests.Count);
    }

    // <snippet:guide-retry-an-http-call>
    private static async Task<Order?> ReadOrderAsync(HttpClient client, string id, CancellationToken cancellationToken)
    {
        // Resilience.Http knows that a 503 is transient, a 429 is throttling and a 404 is an
        // answer. Three attempts, a 30 s deadline and a 10 s attempt ceiling are the defaults.
        var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(10) };

        var result = await api.TryRunAsync(
            attempt => client.GetFromJsonAsync<Order>(new Uri($"https://api.example.com/orders/{id}"), attempt),
            cancellationToken);

        if (result.TryGetValue(out var order))
        {
            return order;
        }

        // The failure, and everything that led to it, without an exception.
        Console.WriteLine($"{result.StopReason}: {result.Attempts}");
        return null;
    }
    // </snippet:guide-retry-an-http-call>

    [Fact]
    public async Task Protect_one_dependency_without_touching_the_others()
    {
        var dependencies = new Dependencies();
        Assert.Equal(BreakerState.Closed, dependencies.Payments.State);

        var result = await dependencies.Charge.TryRunAsync(
            attempt => Task.FromResult("charged"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    // <snippet:guide-protect-a-dependency>
    public sealed class Dependencies
    {
        // One breaker per dependency, held where its lifetime is obvious. A storm against payments
        // must not trip calls to search, and here that is a property of the code.
        public Breaker Payments { get; } = new(new BreakerSettings
        {
            ConsecutiveFailures = 5,
            SlowCallThreshold = TimeSpan.FromSeconds(2),
            BreakDuration = TimeSpan.FromSeconds(15),
        })
        {
            Name = "payments",
        };

        public RetryBudget PaymentsBudget { get; } = RetryBudget.Shared("payments");

        public Resilience Charge => Resilience.Http with
        {
            Name = "payments",
            Breaker = Payments,
            Budget = PaymentsBudget,
            Deadline = TimeSpan.FromSeconds(8),
        };
    }
    // </snippet:guide-protect-a-dependency>

    [Fact]
    public void A_health_endpoint_reads_the_breaker()
    {
        var dependencies = new Dependencies();

        // <snippet:guide-health-endpoint>
        // A breaker is an object with a name and a state, so an operator can be told about it.
        var report = dependencies.Payments.State switch
        {
            BreakerState.Closed => "healthy",
            BreakerState.HalfOpen => "recovering",
            BreakerState.Isolated => "isolated by an operator",
            _ => $"open since {dependencies.Payments.OpenedAt:O}",
        };
        // </snippet:guide-health-endpoint>

        Assert.Equal("healthy", report);
    }

    [Fact]
    public void Configure_from_appsettings_end_to_end()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.resilience.json")
            .Build();
        var services = new ServiceCollection();

        // <snippet:guide-configure-from-configuration>
        // One policy per child of the section, each named by its key. Values reload; the roster is
        // read once, because a name that appears in the file after the container is built has
        // nothing to be injected into.
        services.AddResilience(configuration.GetSection("Resilience"));

        services.AddHttpClient("orders").AddResilience("api");
        // </snippet:guide-configure-from-configuration>

        using var provider = services.BuildServiceProvider();
        var policies = provider.GetRequiredService<IResiliencePolicies>();

        Assert.Equal(TimeSpan.FromSeconds(10), policies["api"].Deadline);
        Assert.NotNull(provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders"));
    }
}
