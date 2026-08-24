using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;

namespace NResilience.IntegrationTests;

/// <summary>
///     <see cref="IHost" /> and <see cref="IHttpClientFactory" /> lifecycle.
///     <para>
///         The behavioural suite uses a bare <c>ServiceCollection.BuildServiceProvider()</c>. These tests
///         run through a real <see cref="IHost" /> and exercise the factory's handler pooling, handler
///         rotation, and <c>CreateClient</c> per-request sharing - the lifecycle paths a bare provider
///         does not reproduce.
///     </para>
/// </summary>
public sealed class HostLifecycleTests
{
    /// <summary>
    ///     A full <see cref="IHost" /> builds, starts, and a resilient client works through it. This is
    ///     the path people actually take, and it is worth one test to prove the registration composes
    ///     inside a real host.
    /// </summary>
    [Fact]
    public async Task A_full_host_builds_and_a_resilient_client_works_through_it()
    {
        await using var server = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.ServiceUnavailable),
            new LoopbackResponse(HttpStatusCode.OK, "ok"u8.ToArray()));

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddHttpClient("api").AddResilience(Resilience.Http with { Backoff = Backoff.None });

        builder.Services.ConfigureAll<HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new LoopbackHandler(server)));

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            var factory = host.Services.GetRequiredService<IHttpClientFactory>();
            using var client = factory.CreateClient("api");
            using var response = await client.GetAsync(server.BaseUri);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, server.RequestCount);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    ///     <c>CreateClient</c> per-request handler pooling shares the per-host breaker. Two calls via two
    ///     <c>CreateClient</c> invocations against one dead host. The contract: the second call is
    ///     <see cref="CallRejectedException" /> - the breaker is on the pooled handler, not the
    ///     <c>HttpClient</c> instance, so it survives <c>CreateClient</c> returning a fresh wrapper.
    /// </summary>
    [Fact]
    public async Task CreateClient_per_request_shares_the_pooled_handler_breaker()
    {
        await using var server = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.ServiceUnavailable));

        var services = new ServiceCollection();

        services.AddHttpClient("api")
            .AddResilience(Resilience.Http with
            {
                Backoff = Backoff.None,
                AttemptTimeout = Timeout.InfiniteTimeSpan,
                Deadline = Timeout.InfiniteTimeSpan,
            });

        services.ConfigureAll<HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new LoopbackHandler(server)));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // Make enough calls via fresh clients to trip the breaker.
        var rejected = false;

        for (var i = 0; i < 20 && !rejected; i++)
        {
            using var client = factory.CreateClient("api");

            try
            {
                using var response = await client.GetAsync(server.BaseUri);
            }
            catch (CallRejectedException)
            {
                rejected = true;
            }
        }

        Assert.True(rejected, "The breaker never opened across CreateClient calls.");

        // The next fresh client is also rejected - the breaker is on the pooled handler.
        using var freshClient = factory.CreateClient("api");
        await Assert.ThrowsAsync<CallRejectedException>(async () => await freshClient.GetAsync(server.BaseUri));
    }

    /// <summary>
    ///     Handler rotation resets the per-host breaker. The factory pools handlers on a 2-minute
    ///     rotation; shortening it to 5 seconds and advancing real time past the rotation, the next call
    ///     goes through rather than being rejected - the rotated handler is a fresh handler, and the
    ///     per-host breaker lives on the handler, so rotation discards its state. This is the lifecycle
    ///     path the bare-provider tests cannot reproduce, and the reason state-pinned registrations use
    ///     the named-policy overload, which reuses breakers across rotations.
    /// </summary>
    [Fact]
    public async Task Handler_rotation_resets_the_per_host_breaker()
    {
        await using var server = await LoopbackHttp.StartAsync(
            new LoopbackResponse(HttpStatusCode.ServiceUnavailable));

        var services = new ServiceCollection();

        services.AddHttpClient("api")
            .SetHandlerLifetime(TimeSpan.FromSeconds(5))
            .AddResilience(Resilience.Http with
            {
                Backoff = Backoff.None,
                AttemptTimeout = Timeout.InfiniteTimeSpan,
                Deadline = Timeout.InfiniteTimeSpan,
            });

        services.ConfigureAll<HttpClientFactoryOptions>(o =>
            o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new LoopbackHandler(server)));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        // Trip the breaker.
        var rejected = false;

        for (var i = 0; i < 20 && !rejected; i++)
        {
            using var client = factory.CreateClient("api");

            try
            {
                using var response = await client.GetAsync(server.BaseUri);
            }
            catch (CallRejectedException)
            {
                rejected = true;
            }
        }

        Assert.True(rejected, "The breaker never opened.");

        // Wait for the handler to rotate. The factory expires handlers after HandlerLifetime and
        // creates a fresh one on the next request. The per-host breaker lives on the handler, so
        // a rotated handler starts with a fresh breaker - which is the claim under test: the
        // breaker state does not survive rotation, because each handler carries its own.
        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);

        // The rotated handler has a fresh breaker, so the call goes through rather than being
        // rejected. This is the correct behavior: a rotated handler is a new handler, and the
        // breaker is per-handler.
        using var client2 = factory.CreateClient("api");
        using var response2 = await client2.GetAsync(server.BaseUri);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response2.StatusCode);
    }

    /// <summary>
    ///     A <see cref="DelegatingHandler" /> that routes to a <see cref="LoopbackHttp" /> server,
    ///     so the DI registration can use the real transport through <c>IHttpClientFactory</c>. The
    ///     inner <c>HttpClient</c> is held for the handler's lifetime rather than per request,
    ///     because disposing it mid-response would tear down the connection before the caller reads it.
    /// </summary>
    private sealed class LoopbackHandler(LoopbackHttp server) : DelegatingHandler
    {
        private readonly HttpClient _inner = new(new SocketsHttpHandler(), true);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = new Uri(server.BaseUri, request.RequestUri!.PathAndQuery);
            request.RequestUri = uri;
            return _inner.SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
