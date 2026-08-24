using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using NResilience.Extensions;

namespace NResilience.Tests;

/// <summary>
/// <c>AddHttpClient(…).AddResilience()</c> - the one line most people need.
/// <para>
/// The handler itself is tested in <c>HttpHandlerTests</c>. What is under test here is the wiring: that
/// the registration takes ownership of the transport timeout, which a <see cref="DelegatingHandler"/>
/// cannot do for itself; that a named policy is resolved from the container; and that the span
/// covering a retry sequence is outside the handler that produces the retries, not inside it.
/// </para>
/// </summary>
public sealed class HttpRegistrationTests
{
    /// <summary>A transport that answers from a script and records what reached it.</summary>
    private sealed class Transport : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _steps;
        private int _index = -1;

        internal Transport(params Func<HttpResponseMessage>[] steps) =>
            _steps = [.. steps.Select(step => new Func<HttpRequestMessage, HttpResponseMessage>(_ => step()))];

        internal Transport(Func<HttpRequestMessage, HttpResponseMessage> byRequest) =>
            _steps = [byRequest];

        internal List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var index = Math.Min(Interlocked.Increment(ref _index), _steps.Length - 1);
            return Task.FromResult(_steps[index](request));
        }
    }

    private static ServiceProvider Provider(Action<IServiceCollection> register, Transport transport)
    {
        var services = new ServiceCollection();
        register(services);

        // Pinning the primary handler is how a test reaches the wire without one; the resilience
        // handler sits in front of it exactly as it would in front of a socket.
        services.ConfigureAll<HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = transport));

        return services.BuildServiceProvider();
    }

    private static HttpClient Client(ServiceProvider provider, string name = "api") =>
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);

    // ---- The registration ----

    [Fact]
    public async Task A_registered_client_retries_a_transient_status()
    {
        var transport = new Transport(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK));

        using var provider = Provider(
            s => s.AddHttpClient("api").AddResilience(Resilience.Http with { Backoff = Backoff.None }),
            transport);

        using var client = Client(provider);
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, transport.Requests.Count);
    }

    /// <summary>
    /// The transport timeout. <see cref="HttpClient.Timeout"/> defaults to 100 seconds and applies
    /// to the <i>whole</i> retry sequence, not per attempt - a silent cap that nothing in the policy
    /// can see, and the reason a five-minute deadline would otherwise be a lie. The registration is
    /// the only place that can fix it, because a handler cannot reach the client in front of it.
    /// </summary>
    [Fact]
    public void The_registration_takes_ownership_of_the_transport_timeout()
    {
        using var provider = Provider(
            s => s.AddHttpClient("api").AddResilience(),
            new Transport(() => new HttpResponseMessage(HttpStatusCode.OK)));

        using var client = Client(provider);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    /// <summary>Turning it off leaves the platform default alone, because somebody who says so means it.</summary>
    [Fact]
    public void Ownership_of_the_transport_timeout_can_be_declined()
    {
        using var provider = Provider(
            s => s.AddHttpClient("api").AddResilience(configureOptions: o => o.OwnTransportTimeout = false),
            new Transport(() => new HttpResponseMessage(HttpStatusCode.OK)));

        using var client = Client(provider);

        Assert.NotEqual(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    /// <summary>
    /// The policy is named after the client, then after the host it is talking to.
    /// <para>
    /// The client name matters because <see cref="Resilience.Http"/> is itself called "http": left
    /// alone, every client in a process would report under that one name and four of them would be
    /// indistinguishable in the metrics. The host suffix is the per-host scoping showing
    /// through, and it is the more specific fact - a breaker is per host, so the name that appears
    /// beside it should be too.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_client_names_its_policy_after_itself_and_the_host()
    {
        var names = new List<string?>();

        using var provider = Provider(
            s => s.AddHttpClient("api").AddResilience(
                Resilience.Http with { Backoff = Backoff.None, OnEvent = e => names.Add(e.PolicyName) }),
            new Transport(() => new HttpResponseMessage(HttpStatusCode.OK)));

        using var client = Client(provider);
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.Equal("api:api.test", name));
    }

    // ---- Named policies ----

    [Fact]
    public async Task A_client_can_use_a_policy_registered_by_name()
    {
        var transport = new Transport(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK));

        using var provider = Provider(
            s =>
            {
                s.AddResilience("shared", Resilience.Http with { Attempts = 4, Backoff = Backoff.None });
                s.AddHttpClient("api").AddResilience("shared");
            },
            transport);

        using var client = Client(provider);
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, transport.Requests.Count);
    }

    /// <summary>A name nothing was registered under fails when the handler is built, naming what is registered.</summary>
    [Fact]
    public void An_unregistered_policy_name_fails_when_the_client_is_created()
    {
        using var provider = Provider(
            s =>
            {
                s.AddResilience("shared", Resilience.Http);
                s.AddHttpClient("api").AddResilience("typo");
            },
            new Transport(() => new HttpResponseMessage(HttpStatusCode.OK)));

        var error = Assert.Throws<ResilienceConfigurationException>(() => Client(provider));

        Assert.Contains("typo", error.Message, StringComparison.Ordinal);
    }

    // ---- Idempotency, through the registration ----

    /// <summary>
    /// The idempotency decision survives registration, which is the point of testing it here as
    /// well as on the handler: this is the path people actually take, and a POST retried by a
    /// registration nobody inspected is the duplicate-order bug the design cites.
    /// </summary>
    [Fact]
    public async Task A_post_is_not_retried_through_the_registration()
    {
        var transport = new Transport(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var provider = Provider(
            s => s.AddHttpClient("api").AddResilience(Resilience.Http with { Backoff = Backoff.None }),
            transport);

        using var client = Client(provider);
        using var response = await client.PostAsync(new Uri("https://api.test/orders"), new StringContent("{}"));

        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task A_client_can_opt_into_retrying_unsafe_methods()
    {
        var transport = new Transport(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using var provider = Provider(
            s => s.AddHttpClient("api").AddResilience(
                Resilience.Http with { Backoff = Backoff.None },
                o => o.RetryUnsafeMethods = true),
            transport);

        using var client = Client(provider);
        using var response = await client.PostAsync(new Uri("https://api.test/orders"), new StringContent("{}"));

        Assert.Equal(3, transport.Requests.Count);
    }

    // ---- Tracing ----

    /// <summary>
    /// The span covers the whole retry sequence rather than one attempt, which is the boundary a
    /// per-attempt HTTP span cannot show: without it, three attempts against a flaky dependency are
    /// three unrelated spans and the trace never says they were one call that eventually succeeded.
    /// </summary>
    [Fact]
    public async Task One_span_covers_every_attempt()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ResilienceTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };

        ActivitySource.AddActivityListener(listener);

        var transport = new Transport(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK));

        using var provider = Provider(
            s => s.AddHttpClient("api").AddResilience(Resilience.Http with { Backoff = Backoff.None }),
            transport);

        using var client = Client(provider);
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        var span = Assert.Single(spans);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal("succeeded", span.GetTagItem("nresilience.outcome"));
        Assert.Equal(200, span.GetTagItem("http.response.status_code"));
        Assert.Equal(2, span.Events.Count(e => e.Name == "nresilience.attempt"));
    }

    /// <summary>Telemetry is a switch, and turning it off leaves no span behind.</summary>
    [Fact]
    public async Task Telemetry_can_be_turned_off_for_a_client()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ResilienceTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };

        ActivitySource.AddActivityListener(listener);

        using var provider = Provider(
            s => s.AddHttpClient("api").AddResilience(Resilience.Http with { Backoff = Backoff.None }, telemetry: false),
            new Transport(() => new HttpResponseMessage(HttpStatusCode.OK)));

        using var client = Client(provider);
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Empty(spans);
    }

    // ---- Per-host scoping, through the registration ----

    /// <summary>
    /// A bad endpoint on one host must not take out the healthy ones. Both hosts are served by one
    /// client here, which is the shape that makes the claim worth testing: the breaker is scoped by
    /// the host it protects rather than by the client that happened to register it.
    /// </summary>
    [Fact]
    public async Task A_dead_host_does_not_break_a_healthy_one()
    {
        var transport = new Transport(request =>
            request.RequestUri!.Host == "dead.test"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK));

        using var provider = Provider(
            s => s.AddHttpClient("api").AddResilience(Resilience.Http with { Backoff = Backoff.None }),
            transport);

        using var client = Client(provider);

        // Enough 503s to trip the dead host's breaker, whichever way it trips.
        var rejected = false;
        for (var i = 0; i < 10 && !rejected; i++)
        {
            try
            {
                using var dead = await client.GetAsync(new Uri("https://dead.test/thing"));
            }
            catch (CallRejectedException)
            {
                rejected = true;
            }
        }

        Assert.True(rejected, "the dead host's breaker never opened");

        using var ok = await client.GetAsync(new Uri("https://healthy.test/thing"));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }
}
