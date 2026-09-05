using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using NResilience.Extensions;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>Rate limiting and concurrency limiting: the limiter, and where it goes.</summary>
public sealed class RateLimiting
{
    [Fact]
    public async Task A_limiter_goes_inside_the_callback()
    {
        // <snippet:limit-callback>
        // 100 calls per second, with one second of burst. The limiter is an object you hold: give
        // it the lifetime of whatever it protects, and dispose it with that.
        using var limiter = Limit.PerSecond(permits: 100);

        var api = Resilience.Http;

        var value = await api.RunAsync(async ct =>
        {
            // Inside the callback, not around the call. Retry re-invokes the callback, so a permit
            // taken here is taken once per attempt - and `using` is what releases a concurrency
            // permit when the attempt ends, however it ends.
            using var lease = await limiter.AcquireOrThrowAsync(cancellationToken: ct);
            return await FetchAsync(cancellationToken: ct);
        });

        // </snippet:limit-callback>

        Assert.Equal(expected: 42, actual: value);
    }

    [Fact]
    public void A_limiter_is_one_of_four_shapes()
    {
        // <snippet:limit-shapes>
        // A published per-second quota.
        using var perSecond = Limit.PerSecond(permits: 100);

        // A longer quota. The window slides in eight segments, so you cannot spend it all at the
        // end of one window and all of the next at the start of the following one.
        using var perMinute = Limit.PerWindow(permits: 1_000, window: TimeSpan.FromMinutes(value: 1));

        // The bulkhead: at most 20 calls in flight at once, whatever their rate.
        using var inFlight = Limit.Concurrency(permits: 20);

        // The bulkhead you do not have to size. Set the range it may move within; the number
        // inside it is measured from how the dependency responds under load.
        using var adaptive = Limit.Adaptive(new AdaptiveLimitOptions { Minimum = 4, Maximum = 200 });

        // </snippet:limit-shapes>

        Assert.NotNull(@object: perSecond);
        Assert.NotNull(@object: perMinute);
        Assert.NotNull(@object: inFlight);
        Assert.NotNull(@object: adaptive);
    }

    [Fact]
    public async Task An_adaptive_limit_is_discovered_rather_than_configured()
    {
        // <snippet:limit-adaptive>
        // No permit count. Minimum and Maximum are guardrails - what the loop may never leave -
        // and the limit between them is read from latency: a round of calls slower than this
        // dependency normally is means a queue downstream, and the limit backs off.
        using var limiter = Limit.Adaptive(new AdaptiveLimitOptions { Minimum = 4, Maximum = 200 }, name: "payments");

        var api = Resilience.Http;

        var value = await api.RunAsync(async ct =>
        {
            // The lease is the measurement: how long the permit is held is the round-trip time
            // the control loop reads. `using` is what frees the slot *and* reports the sample.
            using var lease = await limiter.AcquireOrThrowAsync(name: "payments", cancellationToken: ct);
            return await FetchAsync(cancellationToken: ct);
        });

        // What it has settled on, for a dashboard. Null until it has seen enough calls to have
        // an opinion, at which point it holds at Initial rather than guessing.
        int discovered = limiter.CurrentLimit;
        TimeSpan? normal = limiter.Baseline;

        // </snippet:limit-adaptive>

        Assert.Equal(expected: 42, actual: value);
        Assert.Equal(expected: 20, actual: discovered);
        Assert.Null(@object: normal);
    }

    [Fact]
    public async Task A_refusal_is_a_retry_that_does_not_touch_the_budget()
    {
        var budget = RetryBudget.Of(minimumPerSecond: 1);

        var api = Resilience.Default with
        {
            Attempts = 3,
            Backoff = Backoff.None,
            Budget = budget,
        };

        // <snippet:limit-verdict>
        var result = await api.TryRunAsync(_ =>
            Task.FromException<int>(exception: new RateLimitedException(limiter: "payments", retryAfter: TimeSpan.FromSeconds(value: 2))));

        var refused = result.Attempts[index: 0];

        // Throttling, so it takes the long backoff curve and honors the limiter's own hint.
        Assert.Equal(expected: VerdictKind.Throttled, actual: refused.Verdict.Kind);

        // And it says where it came from. This is the bit the retry budget reads: a refusal that
        // never left the process is not charged, because retrying it costs the dependency nothing.
        Assert.True(condition: refused.Verdict.SelfImposed);

        // </snippet:limit-verdict>

        Assert.Equal(expected: 0, actual: budget.Utilization);
    }

    [Fact]
    public async Task The_registration_puts_the_limiter_in_the_right_place()
    {
        var services = new ServiceCollection();

        // <snippet:limit-http>
        services.AddHttpClient(name: "api")
            .AddResilience() // outer: makes the attempts
            .AddRateLimit(o =>
            {
                o.PermitsPerSecond = 100; // one of four shapes; set exactly one
                o.PerHost = true; // the default, scoped like the breakers
            });

        // </snippet:limit-http>

        services.ConfigureAll<HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new ScriptedHttpHandler().Responds(HttpStatusCode.OK)));

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name: "api");
        using var response = await client.GetAsync(requestUri: new Uri(uriString: "https://api.test/thing"));

        Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
    }

    [Fact]
    public void The_wrong_order_is_refused_at_registration()
    {
        var services = new ServiceCollection();

        // <snippet:limit-order>
        // Handlers run in registration order, outermost first, so this puts the limiter *outside*
        // the retries - one permit for an operation that goes on to make three calls. Refused at
        // registration rather than accepted and silently wrong.
        var error = Assert.Throws<ResilienceConfigurationException>(() => services.AddHttpClient(name: "api")
            .AddRateLimit(o => o.PermitsPerSecond = 100)
            .AddResilience());

        // </snippet:limit-order>

        Assert.Contains(expectedSubstring: "AddRateLimit() before AddResilience()", actualString: error.Message, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void Two_limits_at_once_are_refused_rather_than_resolved()
    {
        // <snippet:limit-validate>
        // Four different guards, and a section that asks for two of them is a section whose
        // author expected one to win. Every problem is listed at once.
        var error = Assert.Throws<ResilienceConfigurationException>(() => new RateLimitOptions { PermitsPerSecond = 100, Concurrency = 20 }.Validate());

        // </snippet:limit-validate>

        Assert.Single(collection: error.Problems);
    }

    [Fact]
    public async Task An_adaptive_limit_is_reachable_from_configuration()
    {
        var services = new ServiceCollection();

        // <snippet:limit-adaptive-http>
        services.AddHttpClient(name: "api")
            .AddResilience()
            .AddRateLimit(o =>
            {
                // The presence of the section is what turns it on - every property inside has a
                // working default, so this is a complete configuration. Per host, like the
                // breakers, because each host queues on its own.
                o.Adaptive = new AdaptiveLimitOptions { Minimum = 4, Maximum = 200 };
                o.Name = "api";
            });

        // </snippet:limit-adaptive-http>

        services.ConfigureAll<HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new ScriptedHttpHandler().Responds(HttpStatusCode.OK)));

        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name: "api");
        using var response = await client.GetAsync(requestUri: new Uri(uriString: "https://api.test/thing"));

        Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
    }

    private static Task<int> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(result: 42);
}
