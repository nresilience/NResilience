using System.Collections.Concurrent;
using System.Net;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NResilience.Grpc;
using NResilience.IntegrationTests.Grpc;

namespace NResilience.IntegrationTests;

/// <summary>
///     Server-streaming over a real HTTP/2 connection to a real Kestrel-hosted gRPC service: the
///     things the scripted suite cannot prove, because they are grpc-dotnet's behavior rather than
///     ours - what reaches the wire, what the server observes, and what a cancelled stream does to
///     a connection that exists.
/// </summary>
public sealed class GrpcStreamingIntegrationTests
{
    [Fact]
    public async Task A_stream_that_fails_before_its_first_message_is_retried_over_a_real_connection()
    {
        await using var server = await StartAsync();
        var client = Client(server, Policy());

        var scenario = Scenarios.New();
        var call = client.Watch(new WatchRequest { Scenario = scenario, Messages = 3, Failures = 2 });

        var read = new List<string>();

        using (call)
        {
            await foreach (var reply in call.ResponseStream.ReadAllAsync(TestContext.Current.CancellationToken))
                read.Add(reply.Message);
        }

        Assert.Equal(["0", "1", "2"], read);
        Assert.Equal(3, Scenarios.Calls(scenario));
    }

    [Fact]
    public async Task A_failure_after_the_first_message_reaches_the_consumer_unretried()
    {
        await using var server = await StartAsync();
        var client = Client(server, Policy());

        var scenario = Scenarios.New();
        using var call = client.Watch(new WatchRequest { Scenario = scenario, Messages = 2, FaultAfterFirst = true });

        var read = new List<string>();

        var failure = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await foreach (var reply in call.ResponseStream.ReadAllAsync(TestContext.Current.CancellationToken))
                read.Add(reply.Message);
        });

        Assert.Equal(StatusCode.Unavailable, failure.StatusCode);
        Assert.Equal(["0", "1"], read);
        Assert.Equal(1, Scenarios.Calls(scenario));
    }

    [Fact]
    public async Task The_deadline_the_server_reads_is_the_whole_budget_rather_than_the_attempt_ceiling()
    {
        await using var server = await StartAsync();

        // The gap is the point: an attempt ceiling of two seconds and a budget of five minutes.
        var policy = Policy() with { AttemptTimeout = TimeSpan.FromSeconds(2), Deadline = TimeSpan.FromMinutes(5) };
        var client = Client(server, policy);

        var scenario = Scenarios.New();
        using var call = client.Watch(new WatchRequest { Scenario = scenario, Messages = 1 });

        var first = await FirstAsync(call);

        // grpc-timeout, read off the wire by the server. A stream's deadline is fixed when the call
        // starts and cannot be moved, so writing the ceiling would kill a healthy stream at two
        // seconds - which is exactly what this asserts did not happen.
        Assert.True(first.DeadlineMs > TimeSpan.FromMinutes(4).TotalMilliseconds, $"the server saw {first.DeadlineMs} ms");
    }

    [Fact]
    public async Task A_retried_stream_tells_the_server_it_is_inside_a_retry_loop()
    {
        await using var server = await StartAsync();
        var client = Client(server, Policy());

        var scenario = Scenarios.New();
        using var call = client.Watch(new WatchRequest { Scenario = scenario, Messages = 1, Failures = 1 });

        await FirstAsync(call);

        Assert.All(Scenarios.Markers(scenario), marker => Assert.Equal(ResilienceNestedRetry.Marker, marker));
    }

    [Fact]
    public async Task Cancelling_mid_stream_ends_the_call_and_the_server_sees_it()
    {
        await using var server = await StartAsync();
        var client = Client(server, Policy());

        using var caller = new CancellationTokenSource();
        var scenario = Scenarios.New();

        using var call = client.Watch(
            new WatchRequest { Scenario = scenario, Messages = 1000, DelayMs = 20 },
            cancellationToken: caller.Token);

        var read = 0;

        var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in call.ResponseStream.ReadAllAsync(TestContext.Current.CancellationToken))
            {
                if (++read == 2)
                    await caller.CancelAsync();
            }
        });

        Assert.True(failure is RpcException or OperationCanceledException, failure.GetType().Name);

        // Nothing was retried: the consumer had messages in hand, so cancellation ends the call.
        Assert.Equal(1, Scenarios.Calls(scenario));
        Assert.True(await Scenarios.EndedAsync(scenario), "the server never saw the stream end");
    }

    [Fact]
    public async Task Disposing_the_call_early_ends_the_stream_the_server_is_writing()
    {
        await using var server = await StartAsync();
        var client = Client(server, Policy());

        var scenario = Scenarios.New();
        var call = client.Watch(new WatchRequest { Scenario = scenario, Messages = 1000, DelayMs = 20 });

        await FirstAsync(call);
        call.Dispose();

        Assert.True(await Scenarios.EndedAsync(scenario), "the server never saw the stream end");
    }

    /// <summary>The shipped preset with the backoff taken out, so a retry costs no wall clock.</summary>
    private static Resilience Policy() => GrpcResilience.Default with { Backoff = Backoff.None };

    private static async Task<WatchReply> FirstAsync(AsyncServerStreamingCall<WatchReply> call)
    {
        Assert.True(await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken));
        return call.ResponseStream.Current;
    }

    private static Watcher.WatcherClient Client(TestServer server, Resilience policy)
    {
        var services = new ServiceCollection();

        services
            .AddGrpcClient<Watcher.WatcherClient>(options => options.Address = server.Uri)
            .AddGrpcResilience(policy);

        return services.BuildServiceProvider().GetRequiredService<Watcher.WatcherClient>();
    }

    private static async Task<TestServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.Services.AddGrpc();

        // h2c on an ephemeral port: gRPC needs HTTP/2, and cleartext keeps the suite free of
        // certificate setup, exactly as the sample does.
        builder.WebHost.ConfigureKestrel(static options =>
            options.Listen(IPAddress.Loopback, 0, static listener => listener.Protocols = HttpProtocols.Http2));

        var app = builder.Build();
        app.MapGrpcService<WatcherService>();

        await app.StartAsync();

        return new TestServer(app);
    }
}

/// <summary>
///     The service under test's dependency: it fails on demand, streams on demand, and records what
///     each attempt told it - which is how the assertions above read the wire without parsing it.
/// </summary>
internal sealed class WatcherService : Watcher.WatcherBase
{
    public override async Task Watch(WatchRequest request, IServerStreamWriter<WatchReply> responseStream, ServerCallContext context)
    {
        var state = Scenarios.For(request.Scenario);
        var attempt = state.Enter();

        state.Markers.Add(context.RequestHeaders.GetValue("x-nresilience-retrying") ?? string.Empty);

        if (attempt <= request.Failures)
            throw new RpcException(new Status(StatusCode.Unavailable, "the shard is moving"));

        var deadline = context.Deadline == DateTime.MaxValue
            ? -1
            : (long)(context.Deadline - DateTime.UtcNow).TotalMilliseconds;

        try
        {
            for (var i = 0; i < request.Messages; i++)
            {
                await responseStream.WriteAsync(new WatchReply { Message = i.ToString(), DeadlineMs = deadline }, context.CancellationToken);

                if (request.DelayMs > 0)
                    await Task.Delay(request.DelayMs, context.CancellationToken);
            }

            if (request.FaultAfterFirst)
                throw new RpcException(new Status(StatusCode.Unavailable, "the shard moved mid-stream"));
        }
        finally
        {
            // Set whether the stream ran out, faulted, or the consumer walked away - which is the
            // fact a cancellation test needs and cannot get from the client side.
            state.Ended.TrySetResult(true);
        }
    }
}

/// <summary>
///     Per-scenario server-side state, so tests sharing a process and a service do not share
///     counters.
/// </summary>
internal static class Scenarios
{
    private static readonly ConcurrentDictionary<string, Scenario> Live = new();

    internal static string New() => Guid.NewGuid().ToString("n");

    internal static Scenario For(string scenario) => Live.GetOrAdd(scenario, static _ => new Scenario());

    internal static int Calls(string scenario) => For(scenario).Calls;

    internal static IEnumerable<string> Markers(string scenario) => For(scenario).Markers;

    /// <summary>Whether the server's handler ended - the fact a cancelled stream cannot report from the client side.</summary>
    internal static async Task<bool> EndedAsync(string scenario)
    {
        var ended = For(scenario).Ended.Task;
        var finished = await Task.WhenAny(ended, Task.Delay(TimeSpan.FromSeconds(5)));

        return finished == ended;
    }

    internal sealed class Scenario
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal TaskCompletionSource<bool> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ConcurrentBag<string> Markers { get; } = [];

        internal int Enter() => Interlocked.Increment(ref _calls);
    }
}
