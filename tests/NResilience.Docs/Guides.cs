using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NResilience.Extensions;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>The complete examples the guides are built around.</summary>
public sealed class Guides
{
    private static readonly HttpClient Client = new();

    [Fact]
    public async Task Retry_an_http_call_end_to_end()
    {
        var transport = new ScriptedHttpHandler()
            .Respond(() => Doubles.Status(status: HttpStatusCode.ServiceUnavailable))
            .Respond(() => Doubles.Json(value: new Order(Id: "A-1", Status: "shipped")));

        var order = await ReadOrderAsync(
            client: ResilienceHttp.CreateClient(policy: Resilience.Http with { Backoff = Backoff.None }, innerHandler: transport),
            id: "A-1",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "shipped", actual: order?.Status);
        Assert.Equal(expected: 2, actual: transport.Requests.Count);
    }

    // <snippet:guide-retry-an-http-call>
    private static async Task<Order?> ReadOrderAsync(HttpClient client, string id, CancellationToken cancellationToken)
    {
        // Resilience.Http knows that a 503 is transient, a 429 is throttling and a 404 is an
        // answer. Three attempts, a 30 s deadline and a 10 s attempt ceiling are the defaults.
        var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10) };

        var result = await api.TryRunAsync(
            attempt => client.GetFromJsonAsync<Order>(requestUri: new Uri(uriString: $"https://api.example.com/orders/{id}"), cancellationToken: attempt),
            cancellationToken: cancellationToken);

        if (result.TryGetValue(value: out var order))
            return order;

        // The failure, and everything that led to it, without an exception.
        Console.WriteLine(value: $"{result.StopReason}: {result.Attempts}");
        return null;
    }

    // </snippet:guide-retry-an-http-call>

    [Fact]
    public async Task Protect_one_dependency_without_touching_the_others()
    {
        var dependencies = new Dependencies();
        Assert.Equal(expected: BreakerState.Closed, actual: dependencies.Payments.State);

        var result = await dependencies.Charge.TryRunAsync(
            attempt => Task.FromResult(result: "charged"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: result.IsSuccess);
    }

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

        Assert.Equal(expected: "healthy", actual: report);
    }

    [Fact]
    public void Configure_from_appsettings_end_to_end()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path: "appsettings.resilience.json")
            .Build();

        var services = new ServiceCollection();

        // <snippet:guide-configure-from-configuration>
        // One policy per child of the section, each named by its key. Values reload; the roster is
        // read once, because a name that appears in the file after the container is built has
        // nothing to be injected into.
        services.AddResilience(section: configuration.GetSection(key: "Resilience"));

        services.AddHttpClient(name: "orders").AddResilience(policyName: "api");

        // </snippet:guide-configure-from-configuration>

        using var provider = services.BuildServiceProvider();
        var policies = provider.GetRequiredService<IResiliencePolicies>();

        Assert.Equal(expected: TimeSpan.FromSeconds(value: 10), actual: policies[name: "api"].Deadline);
        Assert.NotNull(@object: provider.GetRequiredService<IHttpClientFactory>().CreateClient(name: "orders"));
    }

    private sealed record Order(string Id, string Status);

    [Fact]
    public async Task A_layered_pipeline_translates_to_hooks_and_a_call_site_branch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tokens = new TokenSource();
        var cache = new UserCache(lastKnownGood: new User(Name: "cached"));
        var policy = TranslatedPolicy(cache: cache, tokens: tokens);

        var served = await ReadUserAsync(policy: policy, cache: cache, cancellationToken: cancellationToken);

        Assert.Equal(expected: "cached", actual: served.Name);
        Assert.Equal(expected: 1, actual: tokens.Refreshes);
    }

    // <snippet:guide-translating-a-layered-pipeline>
    // A traditional pipeline stacks four middleware layers around the call:
    //   auth-refresh -> cache-check -> retry/timeout -> fallback.
    // The flat executor has no chain, so each concern moves to a targeted
    // insertion point, and the fallback becomes a branch at the call site.
    private static Resilience TranslatedPolicy(UserCache cache, TokenSource tokens) =>
        Resilience.Http with
        {
            // The outermost layer - "refresh the token before the call" - maps to
            // BeforeAttempt. It runs before every attempt, outside the classified
            // region. If the auth server is down, the exception escapes the loop
            // instead of being retried, which is the behavior an outer middleware
            // layer would have given.
            BeforeAttempt = next => tokens.RefreshAsync(cancellationToken: next.CancellationToken),
        };

    // The callback is the seam for everything that returns a value or needs to
    // run inside the classified region. A cache check belongs here, not in Admit:
    // Admit returns a verdict (admit or refuse), and a cache hit is a value, not
    // a verdict. Checking the cache at the top of the callback serves the hit
    // without calling the dependency, and a miss falls through to the real call.
    private static async Task<User?> FetchAsync(HttpClient client, UserCache cache, CancellationToken cancellationToken)
    {
        if (cache.TryGet(out var cached))
            return cached;

        return await client.GetFromJsonAsync<User>(requestUri: new Uri(uriString: "https://api.example.com/users/1"), cancellationToken: cancellationToken);
    }

    // The outermost layer in a pipeline is usually a fallback. The flat executor
    // has no outermost layer, so the fallback is an `if` at the call site:
    // TryRunAsync hands back the outcome, and the caller branches on it.
    private static async Task<User> ReadUserAsync(Resilience policy, UserCache cache, CancellationToken cancellationToken)
    {
        var result = await policy.TryRunAsync(attempt => FetchAsync(client: Client, cache: cache, cancellationToken: attempt), cancellationToken: cancellationToken);

        return result.TryGetValue(value: out var user) && user is not null ? user : cache.LastKnownGood;
    }

    // </snippet:guide-translating-a-layered-pipeline>

    private sealed class TokenSource
    {
        internal int Refreshes { get; private set; }

        internal Task RefreshAsync(CancellationToken cancellationToken)
        {
            Refreshes++;
            return Task.CompletedTask;
        }
    }

    private sealed class UserCache(User lastKnownGood)
    {
        internal User LastKnownGood { get; } = lastKnownGood;

        internal bool TryGet(out User cached)
        {
            cached = LastKnownGood;
            return true;
        }
    }

    private sealed record User(string Name);

    // <snippet:guide-protect-a-dependency>
    public sealed class Dependencies
    {
        // One breaker per dependency, held where its lifetime is obvious. A storm against payments
        // must not trip calls to search, and here that is a property of the code.
        public Breaker Payments { get; } = new(settings: new BreakerSettings
        {
            ConsecutiveFailures = 5,
            SlowCallThreshold = TimeSpan.FromSeconds(value: 2),
            BreakDuration = TimeSpan.FromSeconds(value: 15),
        })
        {
            Name = "payments",
        };

        public RetryBudget PaymentsBudget { get; } = RetryBudget.Shared(name: "payments");

        public Resilience Charge => Resilience.Http with
        {
            Name = "payments",
            Breaker = Payments,
            Budget = PaymentsBudget,
            Deadline = TimeSpan.FromSeconds(value: 8),
        };
    }

    // </snippet:guide-protect-a-dependency>
}
