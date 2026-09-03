using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using NResilience.Extensions;
using NResilience.Extensions.Internal;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     <c>AddResilience().AddRateLimit(…)</c> - the limiter installed inner to the resilience handler,
///     which is what makes it acquire one permit per attempt rather than one per operation.
/// </summary>
public sealed class HttpRateLimitTests
{
    private static readonly Uri Thing = new("https://api.test/thing");
    private static readonly Uri Other = new("https://other.test/thing");

    // ---- One permit per attempt ----

    [Fact]
    public async Task The_handler_acquires_one_permit_per_attempt()
    {
        var limiter = new ScriptedLimiter();

        var transport = new ScriptedHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable, 2)
            .Respond(HttpStatusCode.OK);

        using var provider = Provider(
            services => services.AddHttpClient("api")
                .AddResilience(TestPolicy.InstantHttp, telemetry: false)
                .AddRateLimit(limiter, "api"),
            transport);

        using var response = await Client(provider).GetAsync(Thing);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, transport.CallCount);

        // Three attempts, three permits. A limiter outside the resilience handler would have been
        // asked exactly once, and the two retries would have bypassed the quota entirely.
        Assert.Equal(3, limiter.Acquisitions);
        limiter.Dispose();
    }

    [Fact]
    public async Task A_refused_permit_is_a_retry_rather_than_a_failed_call()
    {
        var limiter = new ScriptedLimiter([true, false, true], TimeSpan.Zero);

        var transport = new ScriptedHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.OK);

        using var provider = Provider(
            services => services.AddHttpClient("api")
                .AddResilience(TestPolicy.InstantHttp, telemetry: false)
                .AddRateLimit(limiter, "api"),
            transport);

        using var response = await Client(provider).GetAsync(Thing);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The refused attempt never reached the wire, so the transport saw two of the three.
        Assert.Equal(3, limiter.Acquisitions);
        Assert.Equal(2, transport.CallCount);
        limiter.Dispose();
    }

    [Fact]
    public async Task A_refusal_is_not_charged_to_the_per_host_retry_budget()
    {
        var limiter = new ScriptedLimiter([false, false, false], TimeSpan.Zero);
        var transport = new ScriptedHttpHandler().Respond(HttpStatusCode.OK);

        var handler = new ResilienceHandler(TestPolicy.InstantHttp with { Budget = RetryBudget.Automatic }, new HttpResilienceOptions());
        var limitHandler = new RateLimitHandler(limiter, "api", false) { InnerHandler = transport };
        handler.InnerHandler = limitHandler;

        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync(Thing));

        var budget = Assert.Single(handler.BudgetsByHost()).Value;
        Assert.Equal(0, budget.Utilization);
        Assert.Empty(transport.Requests);
        limiter.Dispose();
    }

    // ---- Releasing the lease ----

    [Fact]
    public async Task A_permit_is_released_when_an_attempt_times_out()
    {
        // One permit for the whole client. If the first attempt's lease is not released when the
        // attempt times out, the second attempt can never get one and the call fails as limited
        // rather than succeeding.
        using var limiter = Limit.Concurrency(1);
        var calls = 0;

        var transport = new ConditionalTransport(async (_, ct) =>
        {
            if (calls++ == 0)
                await Task.Delay(Timeout.Infinite, ct);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var policy = Resilience.Http with
        {
            Backoff = Backoff.None,
            AttemptTimeout = TimeSpan.FromMilliseconds(100),
            Deadline = Timeout.InfiniteTimeSpan,
        };

        using var provider = Provider(
            services => services.AddHttpClient("api")
                .AddResilience(policy, telemetry: false)
                .AddRateLimit(limiter, "api"),
            transport);

        using var response = await Client(provider).GetAsync(Thing);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_permit_is_released_when_the_transport_throws()
    {
        using var limiter = Limit.Concurrency(1);

        var transport = new ScriptedHttpHandler()
            .Throw(() => new HttpRequestException("reset"))
            .Respond(HttpStatusCode.OK);

        using var provider = Provider(
            services => services.AddHttpClient("api")
                .AddResilience(TestPolicy.InstantHttp, telemetry: false)
                .AddRateLimit(limiter, "api"),
            transport);

        using var response = await Client(provider).GetAsync(Thing);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Scoping ----

    [Fact]
    public async Task Per_host_quotas_are_independent()
    {
        var gate = new TaskCompletionSource();

        var transport = new ConditionalTransport(async (request, _) =>
        {
            if (request.RequestUri == Thing)
                await gate.Task;

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var provider = Provider(
            services => services.AddHttpClient("api")
                .AddResilience(TestPolicy.InstantHttp with { Attempts = 1 }, telemetry: false)
                .AddRateLimit(o =>
                {
                    o.Concurrency = 1;
                    o.PerHost = true;
                }),
            transport);

        var client = Client(provider);

        // The one permit for the first host is in flight and stays there. A second host has its own
        // permit, so it goes through - which is the same setup the PerHost = false arm below
        // refuses, and the only difference between them is the partitioning.
        var held = client.GetAsync(Thing);

        using var other = await client.GetAsync(Other);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        gate.SetResult();
        using var first = await held;
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    }

    [Fact]
    public async Task A_shared_quota_is_what_PerHost_false_asks_for()
    {
        var gate = new TaskCompletionSource();

        var transport = new ConditionalTransport(async (request, _) =>
        {
            if (request.RequestUri == Thing)
                await gate.Task;

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var provider = Provider(
            services => services.AddHttpClient("api")
                .AddResilience(TestPolicy.InstantHttp with { Attempts = 1 }, telemetry: false)
                .AddRateLimit(o =>
                {
                    o.Concurrency = 1;
                    o.PerHost = false;
                }),
            transport);

        var client = Client(provider);

        var held = client.GetAsync(Thing);

        // The one permit is in flight against the first host, so a different host is refused: that
        // is the whole difference PerHost makes.
        await Assert.ThrowsAsync<RateLimitedException>(() => client.GetAsync(Other));

        gate.SetResult();
        using var first = await held;
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
    }

    // ---- Registration ----

    [Fact]
    public void AddRateLimit_before_AddResilience_is_refused()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<ResilienceConfigurationException>(() => services.AddHttpClient("api")
            .AddRateLimit(o => o.PermitsPerSecond = 10)
            .AddResilience(TestPolicy.InstantHttp, telemetry: false));

        Assert.Contains("AddRateLimit() before AddResilience()", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_right_order_is_accepted()
    {
        var services = new ServiceCollection();

        services.AddHttpClient("api")
            .AddResilience(TestPolicy.InstantHttp, telemetry: false)
            .AddRateLimit(o => o.PermitsPerSecond = 10);
    }

    [Fact]
    public void A_limiter_only_client_is_allowed()
    {
        // No retries to bypass, so no ordering to get wrong.
        var services = new ServiceCollection();
        services.AddHttpClient("api").AddRateLimit(o => o.Concurrency = 2);
    }

    [Fact]
    public void Two_clients_do_not_confuse_each_other_s_ordering()
    {
        var services = new ServiceCollection();

        services.AddHttpClient("limited").AddRateLimit(o => o.Concurrency = 2);
        services.AddHttpClient("resilient").AddResilience(TestPolicy.InstantHttp, telemetry: false);
    }

    [Fact]
    public void Options_that_describe_no_limiter_are_refused_at_registration()
    {
        var services = new ServiceCollection();

        Assert.Throws<ResilienceConfigurationException>(() =>
            services.AddHttpClient("api").AddResilience(TestPolicy.InstantHttp, telemetry: false).AddRateLimit(_ => { }));
    }

    // ---- Ownership ----

    [Fact]
    public async Task A_limiter_the_caller_passed_in_outlives_the_handler()
    {
        using var limiter = Limit.Concurrency(1);
        var transport = new ScriptedHttpHandler().Respond(HttpStatusCode.OK);

        var handler = new RateLimitHandler(limiter, "api", false) { InnerHandler = transport };
        handler.Dispose();

        // Still usable: one limiter shared across several clients must not be disposed by the first
        // handler that goes away.
        using var lease = await limiter.AcquireOrThrowAsync();
        Assert.True(lease.IsAcquired);
    }

    // ---- Harness ----

    private static ServiceProvider Provider(Action<IServiceCollection> register, HttpMessageHandler transport)
    {
        var services = new ServiceCollection();
        register(services);

        services.ConfigureAll<HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = transport));

        return services.BuildServiceProvider();
    }

    private static HttpClient Client(ServiceProvider provider, string name = "api") =>
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

    /// <summary>A limiter that grants or refuses to a script, and counts what it was asked.</summary>
    private sealed class ScriptedLimiter(bool[]? grants = null, TimeSpan? retryAfter = null) : RateLimiter
    {
        private readonly bool[] _grants = grants ?? [true];
        private int _acquisitions;

        internal int Acquisitions => Volatile.Read(ref _acquisitions);

        public override TimeSpan? IdleDuration => null;

        public override RateLimiterStatistics? GetStatistics() => null;

        protected override RateLimitLease AttemptAcquireCore(int permitCount) => Next();

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            new(Next());

        private RateLimitLease Next()
        {
            var index = Interlocked.Increment(ref _acquisitions) - 1;
            return new Lease(_grants[Math.Min(index, _grants.Length - 1)], retryAfter);
        }

        private sealed class Lease(bool acquired, TimeSpan? retryAfter) : RateLimitLease
        {
            public override bool IsAcquired => acquired;

            public override IEnumerable<string> MetadataNames =>
                retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

            public override bool TryGetMetadata(string metadataName, out object? metadata)
            {
                if (retryAfter is { } after && metadataName == MetadataName.RetryAfter.Name)
                {
                    metadata = after;
                    return true;
                }

                metadata = null;
                return false;
            }
        }
    }

    /// <summary>
    ///     A transport that routes by request or observes cancellation - the two things
    ///     <see cref="ScriptedHttpHandler" /> cannot express.
    /// </summary>
    private sealed class ConditionalTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
