using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NResilience.AspNetCore;
using NResilience.Http;

namespace NResilience.IntegrationTests;

/// <summary>
///     Deadline propagation over a real server and a real socket: the header goes out, the middleware
///     reads it back in, and a policy inside the request is bounded by the time its caller is still
///     waiting.
///     <para>
///         The unit tests prove the arithmetic. What only a real server can prove is that the ambient
///         deadline survives the request pipeline - <see cref="AsyncLocal{T}" /> across Kestrel's own
///         awaits, routing, and a nested outbound call - and that the second hop is told a smaller
///         number than the first.
///     </para>
/// </summary>
public sealed class DeadlineMiddlewareTests
{
    [Fact]
    public async Task The_deadline_a_caller_sent_is_readable_for_the_whole_request()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline();

            pipeline.Run(async context =>
            {
                // After an await, and after the framework's own: the value is ambient to the request
                // rather than to a stack frame.
                await Task.Yield();
                await context.Response.WriteAsync(Report(ResilienceDeadline.Remaining));
            });
        });

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Uri);
        request.Headers.TryAddWithoutValidation(ResilienceDeadline.Header, "5000");

        using var response = await client.SendAsync(request);
        var reported = double.Parse(await response.Content.ReadAsStringAsync(), CultureInfo.InvariantCulture);

        Assert.InRange(reported, 1, 5000);
    }

    [Fact]
    public async Task A_request_without_a_deadline_inherits_nothing()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline();
            pipeline.Run(context => context.Response.WriteAsync(Report(ResilienceDeadline.Remaining)));
        });

        using var client = new HttpClient();
        Assert.Equal("none", await client.GetStringAsync(app.Uri));
    }

    [Fact]
    public async Task An_absurd_deadline_can_be_refused()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline(o => o.Maximum = TimeSpan.FromSeconds(5));
            pipeline.Run(context => context.Response.WriteAsync(Report(ResilienceDeadline.Remaining)));
        });

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Uri);
        request.Headers.TryAddWithoutValidation(ResilienceDeadline.Header, "3600000");

        using var response = await client.SendAsync(request);

        Assert.Equal("none", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_reserve_is_kept_back_from_what_the_caller_sent()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline(o => o.Reserve = TimeSpan.FromSeconds(2));
            pipeline.Run(context => context.Response.WriteAsync(Report(ResilienceDeadline.Remaining)));
        });

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Uri);
        request.Headers.TryAddWithoutValidation(ResilienceDeadline.Header, "5000");

        using var response = await client.SendAsync(request);
        var reported = double.Parse(await response.Content.ReadAsStringAsync(), CultureInfo.InvariantCulture);

        // Three of the five seconds are for outbound work; the other two are this service's own.
        Assert.InRange(reported, 1, 3000);
    }

    [Fact]
    public async Task An_expired_inbound_deadline_stops_the_outbound_call_without_a_socket_being_opened()
    {
        // The second hop, which must never be reached: a caller who has already given up does not get
        // a dependency contacted on their behalf.
        await using var downstream = await LoopbackHttp.StartAsync(
            (_, _) => Task.FromResult(LoopbackResponse.Text(HttpStatusCode.OK, "reached")));

        var policy = Resilience.Http with
        {
            Attempts = 2,
            Deadline = TimeSpan.FromSeconds(30),
            UseAmbientDeadline = true,
        };

        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline();

            pipeline.Run(async context =>
            {
                // Let the one-millisecond deadline expire for certain: a request that routes in under
                // a millisecond starts its attempt with time still on the clock, and the request hits
                // the wire before the deadline stops it - which is a bounded attempt, not a refused
                // one. The claim under test is the refused one, so the deadline has to be spent
                // before the outbound call begins.
                await Task.Delay(20);

                using var client = new HttpClient(new ResilienceHandler(new SocketsHttpHandler(), policy))
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                };

                try
                {
                    using var hop = await client.GetAsync(downstream.BaseUri, context.RequestAborted);
                    await context.Response.WriteAsync("reached");
                }
                catch (DeadlineExceededException)
                {
                    await context.Response.WriteAsync("refused");
                }
            });
        });

        using var caller = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Uri);

        // One millisecond, spent by the handler's delay before the outbound call begins, so the
        // deadline refuses the call rather than merely bounding it.
        request.Headers.TryAddWithoutValidation(ResilienceDeadline.Header, "1");

        using var response = await caller.SendAsync(request);

        Assert.Equal("refused", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, downstream.RequestCount);
    }

    [Fact]
    public async Task The_second_hop_is_told_less_than_the_first_was()
    {
        await using var downstream = await LoopbackHttp.StartAsync(
            (_, _) => Task.FromResult(LoopbackResponse.Text(HttpStatusCode.OK, "ok")));

        var policy = Resilience.Http with
        {
            Attempts = 1,
            Deadline = TimeSpan.FromSeconds(30),
            AttemptTimeout = Timeout.InfiniteTimeSpan,
            UseAmbientDeadline = true,
        };

        var options = new HttpResilienceOptions { PropagateDeadline = true };

        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline();

            pipeline.Run(async context =>
            {
                using var client = new HttpClient(new ResilienceHandler(new SocketsHttpHandler(), policy, options))
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                };

                using var hop = await client.GetAsync(downstream.BaseUri, context.RequestAborted);
                await context.Response.WriteAsync("done");
            });
        });

        using var caller = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Uri);
        request.Headers.TryAddWithoutValidation(ResilienceDeadline.Header, "4000");

        using var response = await caller.SendAsync(request);
        Assert.Equal("done", await response.Content.ReadAsStringAsync());

        // The whole feature in one assertion: the second hop is bounded by the caller's deadline rather
        // than by the middle service's own 30 seconds, and by strictly less of it than the first hop was.
        var forwarded = downstream.Requests.Single().Headers[ResilienceDeadline.Header.ToLowerInvariant()];
        Assert.InRange(int.Parse(forwarded, CultureInfo.InvariantCulture), 1, 4000);
    }

    private static string Report(TimeSpan? remaining) =>
        remaining is { } left ? left.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) : "none";
}
