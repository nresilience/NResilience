using System.Net;
using NResilience.Http;

namespace NResilience.Docs;

/// <summary>The HTTP handler: the five things a policy on its own cannot do.</summary>
public sealed class HttpDocs
{
    [Fact]
    public async Task A_client_with_the_handler_in_front_of_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var transport = new Doubles.ScriptedTransport(
            () => Doubles.Status(status: HttpStatusCode.ServiceUnavailable),
            () => Doubles.Status(status: HttpStatusCode.OK));

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
                DetectNestedRetries = true,
            });

        // </snippet:http-options>

        Assert.Equal(expected: Timeout.InfiniteTimeSpan, actual: client.Timeout);
    }

    [Fact]
    public async Task A_post_carrying_an_idempotency_key_can_opt_in()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var transport = new Doubles.ScriptedTransport(
            () => Doubles.Status(status: HttpStatusCode.ServiceUnavailable),
            () => Doubles.Status(status: HttpStatusCode.OK));

        using var client = ResilienceHttp.CreateClient(
            policy: Resilience.Http with { Backoff = Backoff.None },
            innerHandler: transport);

        var key = Guid.NewGuid().ToString();
        HttpContent body = new StringContent(content: "{}");

        // <snippet:http-repeatable>
        // POST is not retried by default, because a retried POST is a duplicate order. Per request,
        // this is the finer instrument, and it beats the per-client switch in both directions.
        using var request = new HttpRequestMessage(method: HttpMethod.Post, requestUri: "https://api.example.com/orders") { Content = body };
        request.Headers.Add(name: "Idempotency-Key", value: key);
        request.Options.Set(key: ResilienceHttp.Repeatable, value: true);

        using var response = await client.SendAsync(request: request, cancellationToken: cancellationToken);

        // </snippet:http-repeatable>

        Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
        Assert.Equal(expected: 2, actual: transport.Requests.Count);
    }

    [Fact]
    public void Per_host_state_is_readable_for_a_health_endpoint()
    {
        var handler = new ResilienceHandler(innerHandler: new Doubles.ScriptedTransport(() => Doubles.Status(status: HttpStatusCode.OK)));

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
        var handler = new ResilienceHandler(innerHandler: new Doubles.ScriptedTransport(() => Doubles.Status(status: HttpStatusCode.OK)));

        // <snippet:http-will-retry>
        using var get = new HttpRequestMessage(method: HttpMethod.Get, requestUri: "https://api.example.com/orders/1");
        using var post = new HttpRequestMessage(method: HttpMethod.Post, requestUri: "https://api.example.com/orders");

        Console.WriteLine(value: handler.WillRetry(request: get)); // True
        Console.WriteLine(value: handler.WillRetry(request: post)); // False

        // </snippet:http-will-retry>

        Assert.True(condition: handler.WillRetry(request: get));
        Assert.False(condition: handler.WillRetry(request: post));
    }
}
