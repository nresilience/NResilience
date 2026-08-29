using System.Net;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>The HTTP handler: the five things a policy on its own cannot do.</summary>
public sealed class HttpDocs
{
    [Fact]
    public async Task A_client_with_the_handler_in_front_of_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var transport = new ScriptedHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.OK);

        using var client = ResilienceHttp.CreateClient(
            policy: Resilience.Http with { Backoff = Backoff.None },
            innerHandler: transport);

        using var retried = await client.GetAsync(
            requestUri: new Uri(uriString: "https://api.example.com/orders/1"), cancellationToken: cancellationToken);

        Assert.Equal(expected: HttpStatusCode.OK, actual: retried.StatusCode);
        Assert.Equal(expected: 2, actual: transport.Requests.Count);
    }

    // <snippet:http-create-client>
    // One long-lived client. The per-host breakers and budgets live on the handler, and are worth
    // nothing to a client that is rebuilt per call.
    private static async Task<HttpStatusCode> ReadOrderAsync(CancellationToken cancellationToken)
    {
        using var client = ResilienceHttp.CreateClient();

        using var response = await client.GetAsync(
            requestUri: new Uri(uriString: "https://api.example.com/orders/1"), cancellationToken: cancellationToken);

        return response.StatusCode;
    }

    // </snippet:http-create-client>

    [Fact]
    public void The_switches_that_are_properties_of_http_rather_than_of_resilience()
    {
        // <snippet:http-options>
        using var client = ResilienceHttp.CreateClient(
            policy: Resilience.Http with { Attempts = 4 },
            options: new HttpResilienceOptions
            {
                RetryUnsafeMethods = false, // POST and PATCH are not retried. The default.
                OwnTransportTimeout = true, // HttpClient.Timeout stops competing with the deadline.
                BreakerPerHost = true, // a dead host does not trip calls to the healthy ones
                BudgetPerHost = true,
                MaxHosts = 1024, // the per-host registry is bounded; null is unbounded
                DetectNestedRetries = true,
            });

        // </snippet:http-options>

        Assert.Equal(expected: Timeout.InfiniteTimeSpan, actual: client.Timeout);
    }

    [Fact]
    public async Task A_post_carrying_an_idempotency_key_can_opt_in()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var transport = new ScriptedHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.OK);

        using var client = ResilienceHttp.CreateClient(
            policy: Resilience.Http with { Backoff = Backoff.None },
            innerHandler: transport);

        var key = Guid.NewGuid().ToString();
        HttpContent body = new StringContent(content: "{}");

        // <snippet:http-repeatable>
        // POST is not retried by default, because a retried POST is a duplicate order. Per request,
        // this is the finer instrument, and it beats the per-client switch in both directions.
        // MarkRepeatable writes both halves: the option this client retries on, and the key the
        // service deduplicates on.
        using var request = new HttpRequestMessage(method: HttpMethod.Post, requestUri: "https://api.example.com/orders") { Content = body };
        request.MarkRepeatable(idempotencyKey: key);

        using var response = await client.SendAsync(request: request, cancellationToken: cancellationToken);

        // </snippet:http-repeatable>

        Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
        Assert.Equal(expected: 2, actual: transport.Requests.Count);
    }

    [Fact]
    public void The_per_host_registry_is_bounded()
    {
        // <snippet:http-max-hosts>
        var handler = new ResilienceHandler(options: new HttpResilienceOptions { MaxHosts = 64 });

        // </snippet:http-max-hosts>

        Assert.Equal(expected: 64, actual: handler.Options.MaxHosts);

        handler.Dispose();
    }

    [Fact]
    public void Per_host_state_is_readable_for_a_health_endpoint()
    {
        var handler = new ResilienceHandler(innerHandler: new ScriptedHttpHandler().Respond(HttpStatusCode.OK));

        // <snippet:http-per-host>
        // A breaker whose scope is a variable with a name is one an operator can be told about.
        var breakers = handler.BreakersByHost();
        var budgets = handler.BudgetsByHost();

        foreach (var (host, breaker) in breakers)
        {
            Console.WriteLine(value: $"{host}: {breaker.State} since {breaker.OpenedAt:O}");
        }

        // </snippet:http-per-host>

        Assert.Empty(collection: breakers);
        Assert.Empty(collection: budgets);
    }

    [Fact]
    public void Whether_a_request_will_be_retried_is_a_question_you_can_ask()
    {
        var handler = new ResilienceHandler(innerHandler: new ScriptedHttpHandler().Respond(HttpStatusCode.OK));

        // <snippet:http-will-retry>
        using var get = new HttpRequestMessage(method: HttpMethod.Get, requestUri: "https://api.example.com/orders/1");
        using var post = new HttpRequestMessage(method: HttpMethod.Post, requestUri: "https://api.example.com/orders");

        Console.WriteLine(value: handler.WillRetry(request: get)); // True
        Console.WriteLine(value: handler.WillRetry(request: post)); // False

        // </snippet:http-will-retry>

        Assert.True(condition: handler.WillRetry(request: get));
        Assert.False(condition: handler.WillRetry(request: post));
    }

    [Fact]
    public async Task A_queue_consumer_publishes_the_marker_itself()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var transport = new ScriptedHttpHandler().Respond(HttpStatusCode.OK);

        // <snippet:nested-retry-publish>
        // In an ASP.NET Core app, UseResilienceNestedRetry() publishes what the caller sent. Anywhere
        // else - a queue consumer reading the retrying marker off a message - publish it yourself:
        // read the header the message carries and begin the scope with what it means.
        string? marker = "1";
        using var inbound = ResilienceNestedRetry.Begin(callerRetrying: ResilienceNestedRetry.IsMarker(marker));
        // </snippet:nested-retry-publish>

        var events = new EventRecorder();

        using var client = ResilienceHttp.CreateClient(
            policy: Resilience.Http with { OnEvent = events.Record },
            innerHandler: transport);

        using var response = await client.GetAsync(
            requestUri: new Uri(uriString: "https://api.example.com/orders/1"), cancellationToken: cancellationToken);

        // With the flag published, this service's own outbound call reports the nesting it is part of.
        Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
        Assert.Contains(collection: events.Events, filter: e => e.Kind == CallEventKind.NestedRetry);
    }
}
