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
    public void A_limiter_is_one_of_three_shapes()
    {
        // <snippet:limit-shapes>
        // A published per-second quota.
        using var perSecond = Limit.PerSecond(permits: 100);

        // A longer quota. The window slides in eight segments, so you cannot spend it all at the
        // end of one window and all of the next at the start of the following one.
        using var perMinute = Limit.PerWindow(permits: 1_000, window: TimeSpan.FromMinutes(value: 1));

        // The bulkhead: at most 20 calls in flight at once, whatever their rate.
        using var inFlight = Limit.Concurrency(permits: 20);

        // </snippet:limit-shapes>

        Assert.NotNull(@object: perSecond);
        Assert.NotNull(@object: perMinute);
        Assert.NotNull(@object: inFlight);
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
                o.PermitsPerSecond = 100; // one of three shapes; set exactly one
                o.PerHost = true; // the default, scoped like the breakers
            });

        // </snippet:limit-http>

        services.ConfigureAll<HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new ScriptedHttpHandler().Respond(HttpStatusCode.OK)));

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
        // Three different guards, and a section that asks for two of them is a section whose
        // author expected one to win. Every problem is listed at once.
        var error = Assert.Throws<ResilienceConfigurationException>(() => new RateLimitOptions { PermitsPerSecond = 100, Concurrency = 20 }.Validate());

        // </snippet:limit-validate>

        Assert.Single(collection: error.Problems);
    }

    private static Task<int> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(result: 42);
}
