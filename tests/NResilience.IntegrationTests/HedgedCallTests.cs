using System.Net;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.IntegrationTests;

/// <summary>
///     Hedging over a real socket. The unit tests prove what the loop decides; these prove that two
///     copies of one request really do go out over two real connections, that the fast one is what the
///     caller gets, and that the slow one is cancelled rather than waited for.
/// </summary>
/// <remarks>
///     Real time rather than a fake clock, because the point of this file is the real
///     <c>SocketsHttpHandler</c>: the real connection pool, the real cancellation registration on a
///     socket, and the real response-stream disposal. The thresholds are chosen with a wide margin - a
///     50 ms floor against a 3-second stall - so a loaded runner changes nothing about the outcome.
/// </remarks>
public sealed class HedgedCallTests
{
    /// <summary>How long the estimate needs before a hedge can fire. Small, so the warm-up is quick.</summary>
    private const int MinimumSamples = 5;

    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task A_stalled_request_is_hedged_and_the_copy_answers()
    {
        var racing = false;
        var stalled = 0;

        await using var server = await LoopbackHttp.StartAsync((_, _) =>
        {
            if (!racing)
                return Task.FromResult(new LoopbackResponse(HttpStatusCode.OK, "warm"u8.ToArray()));

            // The first request of the race stalls; the copy answers at once. Both are real sends over
            // real connections, which is the thing a scripted handler cannot produce.
            return Task.FromResult(Interlocked.Increment(ref stalled) == 1
                ? LoopbackResponse.Text(HttpStatusCode.OK, "slow", Stall)
                : new LoopbackResponse(HttpStatusCode.OK, "fast"u8.ToArray()));
        });

        var events = new EventRecorder();
        using var client = ResilienceHttp.CreateClient(Hedging(events));

        await WarmAsync(client, server);

        racing = true;

        var started = DateTimeOffset.UtcNow;
        using var response = await client.GetAsync(server.BaseUri);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fast", await response.Content.ReadAsStringAsync());

        // The caller did not wait for the stalled request. This is the whole feature, stated as a
        // number: the hedge fires at the 50 ms floor, so the call comes back long before the 3 s stall.
        Assert.True(elapsed < Stall, $"the call took {elapsed.TotalMilliseconds:0} ms, so it waited for the stalled request");

        Assert.Single(events.OfKind(CallEventKind.HedgeStarted));
        Assert.Single(events.OfKind(CallEventKind.HedgeWon));
        Assert.Single(events.OfKind(CallEventKind.HedgeDiscarded));

        Assert.Equal(2, stalled);
    }

    /// <summary>
    ///     The loser is cancelled, not abandoned - and over a real socket that is checkable from the far
    ///     end. The loopback server's per-request token is cancelled when the client tears the
    ///     connection down, so a handler still holding the response finds out that nobody is listening.
    ///     That is what keeps a hedged call from leaking a connection per race.
    /// </summary>
    [Fact]
    public async Task The_request_that_loses_the_race_is_cancelled_at_the_far_end()
    {
        var racing = false;
        var sent = 0;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = await LoopbackHttp.StartAsync(async (_, perRequest) =>
        {
            if (!racing)
                return new LoopbackResponse(HttpStatusCode.OK, "warm"u8.ToArray());

            if (Interlocked.Increment(ref sent) != 1)
                return new LoopbackResponse(HttpStatusCode.OK, "fast"u8.ToArray());

            // Hold the first request open. If the client is still there in three seconds the delay
            // completes normally and the assertion below fails, which is the outcome worth failing on.
            try
            {
                await Task.Delay(Stall, perRequest);
            }
            catch (OperationCanceledException)
            {
                disconnected.TrySetResult();
                throw;
            }

            return new LoopbackResponse(HttpStatusCode.OK, "slow"u8.ToArray());
        });

        var events = new EventRecorder();
        using var client = ResilienceHttp.CreateClient(Hedging(events));

        await WarmAsync(client, server);

        racing = true;

        using var response = await client.GetAsync(server.BaseUri);

        Assert.Equal("fast", await response.Content.ReadAsStringAsync());
        Assert.Single(events.OfKind(CallEventKind.HedgeDiscarded));

        // Awaited after the call returned, not before: the call is not allowed to wait for its loser,
        // and the loser is not allowed to survive it.
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    ///     Gate 2, over a real socket: the handler will not repeat a POST, so it will not hedge one
    ///     either. A stalled POST is a slow call, not two calls.
    /// </summary>
    [Fact]
    public async Task A_stalled_post_is_not_hedged()
    {
        var racing = false;
        var posts = 0;

        await using var server = await LoopbackHttp.StartAsync((request, _) =>
        {
            if (!racing || request.Method != "POST")
                return Task.FromResult(new LoopbackResponse(HttpStatusCode.OK, "warm"u8.ToArray()));

            Interlocked.Increment(ref posts);
            return Task.FromResult(LoopbackResponse.Text(HttpStatusCode.OK, "slow", TimeSpan.FromMilliseconds(300)));
        });

        var events = new EventRecorder();
        using var client = ResilienceHttp.CreateClient(Hedging(events));

        await WarmAsync(client, server);

        racing = true;

        using var request = new HttpRequestMessage(HttpMethod.Post, server.BaseUri) { Content = new StringContent("{}") };
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, posts);
        Assert.False(events.Contains(CallEventKind.HedgeStarted));
    }

    private static Resilience Hedging(EventRecorder events) =>
        Resilience.Http with
        {
            Attempts = 3,
            OnEvent = events.Record,
            Hedge = Hedge.At(quantile: 0.95) with { MinimumSamples = MinimumSamples, MinimumDelay = Floor },
        };

    /// <summary>
    ///     Gives the latency estimate enough samples to have an opinion. Sequential and instant, so
    ///     nothing here can hedge - a call that comes back before the floor never arms anything.
    /// </summary>
    private static async Task WarmAsync(HttpClient client, LoopbackHttp server)
    {
        for (var i = 0; i < MinimumSamples + 5; i++)
        {
            using var response = await client.GetAsync(server.BaseUri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }
}
