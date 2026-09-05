using System.Net;
using NResilience;

// The HTTP handler, driven against an in-process fake transport so the sample needs no network.
var transport = new FakeTransport();

using var client = HttpResilience.CreateClient(
    Resilience.Http with
    {
        Name = "orders",
        Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(20)),
        OnEvent = e => Console.WriteLine($"  {e}"),
    },
    new HttpResilienceOptions { BreakerPerHost = true, BudgetPerHost = true },
    transport);

Console.WriteLine("A GET that gets a 503 and then a 200:");

using (var response = await client.GetAsync(new Uri("https://orders.example/1"), CancellationToken.None))
{
    Console.WriteLine($"  -> {(int)response.StatusCode} after {transport.Sends} send(s)");
}

Console.WriteLine();
Console.WriteLine("A POST is not retried, because a retried POST is a duplicate order:");
transport.Reset();

using (var post = new HttpRequestMessage(HttpMethod.Post, "https://orders.example") { Content = new StringContent("{}") })
using (var response = await client.SendAsync(post, CancellationToken.None))
{
    Console.WriteLine($"  -> {(int)response.StatusCode} after {transport.Sends} send(s)");
}

Console.WriteLine();
Console.WriteLine("Unless the request says it is safe to repeat:");
transport.Reset();

using (var post = new HttpRequestMessage(HttpMethod.Post, "https://orders.example") { Content = new StringContent("{}") })
{
    // One call writes both halves: the option this client retries on, and the key the service
    // deduplicates on.
    post.MarkRepeatable(Guid.NewGuid().ToString());

    using var response = await client.SendAsync(post, CancellationToken.None);
    Console.WriteLine($"  -> {(int)response.StatusCode} after {transport.Sends} send(s)");
}

Console.WriteLine();
Console.WriteLine("The handler's own view, per host - what a health endpoint would report:");
var handler = new ResilienceHandler(new FakeTransport(), Resilience.Http);

using (HttpClient probe = new(handler))
{
    using var _ = await probe.GetAsync(new Uri("https://orders.example/1"), CancellationToken.None);
}

foreach (var (host, breaker) in handler.BreakersByHost())
{
    Console.WriteLine($"  {host}: {breaker.State}");
}

/// <summary>A transport that answers 503 once per logical call and 200 afterwards.</summary>
internal sealed class FakeTransport : HttpMessageHandler
{
    internal int Sends { get; private set; }

    internal void Reset() => Sends = 0;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(++Sends == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
}
