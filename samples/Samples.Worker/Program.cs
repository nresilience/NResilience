using System.Diagnostics.Metrics;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NResilience;
using NResilience.Extensions;

// Registration from configuration, a resilient HttpClient, and the meter - in a plain container, so
// the sample is readable without a hosting model in the way.
var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();

var services = new ServiceCollection();

// A registered policy logs, so this is the whole of it. Debug is what a retried call writes at; the
// default filter would show only the incident warnings. Each policy logs under NResilience.<name>,
// so "NResilience.reports": "Warning" would quieten one client without silencing the rest.
services.AddLogging(b => b
    .AddSimpleConsole(o => o.SingleLine = true)
    .SetMinimumLevel(LogLevel.Debug));

services.AddResilience(configuration.GetSection("Resilience"));
services.AddHttpClient("orders")
    .AddResilience("api")
    // After AddResilience, so the limiter is inner to the retries and takes one permit per
    // attempt. The other order is refused at registration.
    .AddRateLimit(configuration.GetSection("RateLimit"))
    .ConfigurePrimaryHttpMessageHandler(() => new FakeTransport());

using var provider = services.BuildServiceProvider();

// Everything the meter records, printed as it happens. In a real application this is
// AddOpenTelemetry().WithMetrics(m => m.AddMeter(ResilienceTelemetry.MeterName)).
using var listener = new MeterListener();
listener.InstrumentPublished = (instrument, l) =>
{
    if (instrument.Meter.Name == ResilienceTelemetry.MeterName)
    {
        l.EnableMeasurementEvents(instrument);
    }
};
listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
    Console.WriteLine($"  {instrument.Name} += {value}"));
listener.Start();

var policies = provider.GetRequiredService<IResiliencePolicies>();
Console.WriteLine($"Registered policies: {string.Join(", ", policies.Names)}");
Console.WriteLine($"  api: {policies["api"].Attempts} attempts, {policies["api"].Deadline} deadline");
Console.WriteLine();

Console.WriteLine("A call through the registered policy:");
var direct = await policies["api"].TryRunAsync(
    static attempt => Task.FromResult("answered"),
    CancellationToken.None);
Console.WriteLine($"  -> {direct.StopReason}");

Console.WriteLine();
Console.WriteLine("A call through the registered HttpClient, which sees a 503 first:");
var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
using var response = await client.GetAsync(new Uri("https://orders.example/1"), CancellationToken.None);
Console.WriteLine($"  -> {(int)response.StatusCode}");

Console.WriteLine();
Console.WriteLine("The limiter allows one call in flight per host. A second, while the first is held:");

var budget = RetryBudget.Of(minimumPerSecond: 1);
var limited = policies["api"] with { Backoff = Backoff.None, Budget = budget };

using var limiter = new RateLimitOptions { Concurrency = 1 }.ToLimiter();
using var held = await limiter.AcquireOrThrowAsync("orders");

var refused = await limited.TryRunAsync(
    async ct =>
    {
        using var lease = await limiter.AcquireOrThrowAsync("orders", ct);
        return 1;
    },
    CancellationToken.None);

Console.WriteLine($"  -> {refused.StopReason} after {refused.Attempts.Count} attempt(s)");
Console.WriteLine($"     verdict: {refused.Attempts[0].Verdict}");
Console.WriteLine($"     retry budget spent: {budget.Utilization:P0} - a refusal that never left the process is not charged");

Console.WriteLine();
Console.WriteLine("nresilience.attempts / nresilience.calls is the retry fraction - the number to alert on.");
Console.WriteLine("The dbug: lines above are the log records. Every event ID is tabled in docs/reference/events.md.");

internal sealed class FakeTransport : HttpMessageHandler
{
    private int _sends;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(++_sends == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
}
