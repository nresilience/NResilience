using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NResilience;
using NResilience.Grpc;
using Samples.Grpc;

// The gRPC integration, over a real HTTP/2 connection to a service this process hosts itself - so
// the sample needs no network and no certificate.
var server = WebApplication.CreateSlimBuilder();

server.Logging.ClearProviders();

server.WebHost.ConfigureKestrel(static options =>
{
    // Port 0, h2c: gRPC needs HTTP/2, and cleartext keeps the sample free of certificate setup.
    options.Listen(IPAddress.Loopback, 0, static listener => listener.Protocols = HttpProtocols.Http2);
});

server.Services.AddGrpc();

var app = server.Build();
app.MapGrpcService<FlakyOrders>();

await app.StartAsync();

var address = app.Urls.First();

Console.WriteLine($"gRPC service listening on {address}");
Console.WriteLine();

// The registration. AddResilience() would compile here too and do nothing: every gRPC call is an
// HTTP POST, which the HTTP handler refuses to retry.
var services = new ServiceCollection();

services
    .AddGrpcClient<Orders.OrdersClient>("orders", options => options.Address = new Uri(address))
    .AddGrpcResilience(
        GrpcResilience.Default with
        {
            Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(20)),
            AttemptTimeout = TimeSpan.FromSeconds(2),
            OnEvent = e => Console.WriteLine($"  {e}"),
        },

        // ChargeCard takes money. One attempt, whatever the transport says.
        options => options.IsRepeatable = static method => method.Name != "ChargeCard");

await using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<Orders.OrdersClient>();

Console.WriteLine("A read that gets two Unavailable replies and then succeeds:");

var reply = await client.GetAsync(new GetRequest { Id = "1" });

Console.WriteLine($"  -> {reply.Status} after {FlakyOrders.Reads} attempt(s)");
Console.WriteLine();

// The attempt ceiling is written into CallOptions.Deadline, which grpc-dotnet sends as the standard
// grpc-timeout header. Deadline propagation for gRPC, for free.
Console.WriteLine($"The server was told the winning attempt had {reply.Deadline}.");
Console.WriteLine();

Console.WriteLine("A write is not retried, because a retried charge is a duplicate charge:");

try
{
    await client.ChargeCardAsync(new ChargeRequest { Id = "1" });
}
catch (RpcException failure)
{
    Console.WriteLine($"  -> {failure.StatusCode} after {FlakyOrders.Charges} attempt(s)");
}

Console.WriteLine();
Console.WriteLine("A server stream is retried until its first message, and never after it:");

using (var watch = client.Watch(new GetRequest { Id = "1" }))
{
    await foreach (var update in watch.ResponseStream.ReadAllAsync())
    {
        Console.WriteLine($"  -> {update.Event}");
    }
}

Console.WriteLine($"  the stream took {FlakyOrders.Watches} attempt(s) to start, and none after it");
Console.WriteLine();

Console.WriteLine("And a read can be made single-shot at the call site, without touching the wire:");
FlakyOrders.Reset();

try
{
    using (GrpcResilience.SingleShot())
    {
        await client.GetAsync(new GetRequest { Id = "1" });
    }
}
catch (RpcException failure)
{
    Console.WriteLine($"  -> {failure.StatusCode} after {FlakyOrders.Reads} attempt(s)");
}

await app.StopAsync();

/// <summary>
///     The dependency, misbehaving on purpose: the first two reads fail with the one status that is
///     canonically worth retrying, and every charge fails so the single-attempt rule is visible.
/// </summary>
internal sealed class FlakyOrders : Orders.OrdersBase
{
    private static int _reads;
    private static int _charges;
    private static int _watches;

    internal static int Reads => _reads;

    internal static int Charges => _charges;

    internal static int Watches => _watches;

    internal static void Reset() => Interlocked.Exchange(ref _reads, 0);

    public override Task<GetReply> Get(GetRequest request, ServerCallContext context)
    {
        if (Interlocked.Increment(ref _reads) < 3)
            throw new RpcException(new Status(StatusCode.Unavailable, "the shard is moving"));

        // What the peer was told this attempt has left. grpc-dotnet's server reads the grpc-timeout
        // header off the wire and hands it over as a deadline, so this is the number the interceptor
        // wrote into CallOptions.Deadline arriving on the other side.
        var deadline = context.Deadline == DateTime.MaxValue
            ? "(none)"
            : $"{(context.Deadline - DateTime.UtcNow).TotalMilliseconds:0} ms left";

        return Task.FromResult(new GetReply { Status = "shipped", Deadline = deadline });
    }

    /// <summary>
    ///     The stream fails to start once, which is the window a stream is retried in, and then
    ///     writes three events. A failure after the first event would reach the consumer untouched.
    /// </summary>
    public override async Task Watch(GetRequest request, IServerStreamWriter<WatchReply> responseStream, ServerCallContext context)
    {
        if (Interlocked.Increment(ref _watches) < 2)
            throw new RpcException(new Status(StatusCode.Unavailable, "the watch stream is not ready"));

        foreach (var name in new[] { "picked", "packed", "shipped" })
        {
            await responseStream.WriteAsync(new WatchReply { Event = name }, context.CancellationToken);
        }
    }

    public override Task<ChargeReply> ChargeCard(ChargeRequest request, ServerCallContext context)
    {
        Interlocked.Increment(ref _charges);
        throw new RpcException(new Status(StatusCode.Unavailable, "the payment gateway is down"));
    }
}
