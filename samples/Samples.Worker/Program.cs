using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NResilience;
using NResilience.Extensions;

// Registration from configuration, a resilient HttpClient, and the meter - in a plain container, so
// the sample is readable without a hosting model in the way.
IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();

var services = new ServiceCollection();
services.AddResilience(configuration.GetSection("Resilience"));
services.AddHttpClient("orders")
    .AddResilience("api")
    .ConfigurePrimaryHttpMessageHandler(() => new FakeTransport());

using ServiceProvider provider = services.BuildServiceProvider();

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
CallResult<string> direct = await policies["api"].TryRunAsync(
    static attempt => Task.FromResult("answered"),
    CancellationToken.None);
Console.WriteLine($"  -> {direct.StopReason}");

Console.WriteLine();
Console.WriteLine("A call through the registered HttpClient, which sees a 503 first:");
HttpClient client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
using HttpResponseMessage response = await client.GetAsync(new Uri("https://orders.example/1"), CancellationToken.None);
Console.WriteLine($"  -> {(int)response.StatusCode}");

Console.WriteLine();
Console.WriteLine("nresilience.attempts / nresilience.calls is the retry fraction - the number to alert on.");

internal sealed class FakeTransport : HttpMessageHandler
{
    private int _sends;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(++_sends == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
}
