using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using NResilience.AspNetCore;
using NResilience.Http;

namespace NResilience.IntegrationTests;

/// <summary>
///     Inbound nested-retry propagation over a real server and a real socket: the marker a retrying
///     caller sent goes out, the middleware reads it back in, and the outbound calls this request
///     makes report <see cref="CallEventKind.NestedRetry" /> themselves.
///     <para>
///         The unit tests prove the flag's mechanics. What only a real server can prove is the claim
///         the feature exists for: that a marker arriving on an inbound request is visible to a
///         retrying handler on an outbound call - across Kestrel's own awaits, routing, and a second
///         socket - which is what makes the middle hop of a three-hop chain able to see the
///         amplification it is part of. And that the library reports and does not intervene: the
///         downstream sees exactly one request.
///     </para>
/// </summary>
public sealed class NestedRetryMiddlewareTests
{
    [Fact]
    public async Task The_marker_a_caller_sent_is_readable_for_the_whole_request()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceNestedRetry();

            pipeline.Run(async context =>
            {
                // After an await, and after the framework's own: the flag is ambient to the request
                // rather than to a stack frame.
                await Task.Yield();
                await context.Response.WriteAsync(ResilienceNestedRetry.IsCallerRetrying ? "retrying" : "not");
            });
        });

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Uri);
        request.Headers.TryAddWithoutValidation(HttpResilience.NestedRetryHeader, ResilienceNestedRetry.Marker);

        using var response = await client.SendAsync(request);

        Assert.Equal("retrying", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_request_without_the_marker_inherits_nothing()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceNestedRetry();
            pipeline.Run(context => context.Response.WriteAsync(ResilienceNestedRetry.IsCallerRetrying ? "retrying" : "not"));
        });

        using var client = new HttpClient();
        Assert.Equal("not", await client.GetStringAsync(app.Uri));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    public async Task A_header_carrying_something_other_than_the_marker_is_ignored(string value)
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceNestedRetry();
            pipeline.Run(context => context.Response.WriteAsync(ResilienceNestedRetry.IsCallerRetrying ? "retrying" : "not"));
        });

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Uri);
        request.Headers.TryAddWithoutValidation(HttpResilience.NestedRetryHeader, value);

        using var response = await client.SendAsync(request);

        Assert.Equal("not", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_marker_is_read_from_any_value_on_the_header()
    {
        // The same question ResilienceHandler.CarriesRetryMarker asks of an outbound request. An
        // intermediary that appends its own empty value to a header a retrying caller really did
        // send must not turn the marker off - a presence marker does not get less true for having
        // something written after it.
        //
        // The two values are set on the request rather than sent by the client on purpose:
        // HttpClient joins repeated headers into one comma-separated line, which Kestrel surfaces
        // as the single value "1, " and neither half of this feature treats as the marker. The
        // multi-value StringValues this guards against comes from a raw multi-line request, and
        // setting it in the pipeline is how to reach that shape without hand-writing a socket.
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.Use((context, next) =>
            {
                context.Request.Headers[HttpResilience.NestedRetryHeader] =
                    new StringValues([ResilienceNestedRetry.Marker, string.Empty]);

                return next(context);
            });

            pipeline.UseResilienceNestedRetry();
            pipeline.Run(context => context.Response.WriteAsync(ResilienceNestedRetry.IsCallerRetrying ? "retrying" : "not"));
        });

        using var client = new HttpClient();

        Assert.Equal("retrying", await client.GetStringAsync(app.Uri));
    }

    [Fact]
    public async Task A_custom_header_name_is_read()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceNestedRetry(o => o.Header = "X-Custom-Retrying");
            pipeline.Run(context => context.Response.WriteAsync(ResilienceNestedRetry.IsCallerRetrying ? "retrying" : "not"));
        });

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Uri);
        request.Headers.TryAddWithoutValidation("X-Custom-Retrying", ResilienceNestedRetry.Marker);

        using var response = await client.SendAsync(request);

        Assert.Equal("retrying", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_outbound_call_detects_nesting_from_the_inbound_marker()
    {
        // The middle hop of a three-hop chain: this service received a marker from a retrying
        // caller, and its own outbound call is the one whose amplification was invisible until now.
        await using var downstream = await LoopbackHttp.StartAsync((_, _) => Task.FromResult(LoopbackResponse.Text(HttpStatusCode.OK, "ok")));

        var events = new List<CallEventKind>();
        var policy = Resilience.Http with { OnEvent = e => events.Add(e.Kind) };

        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceNestedRetry();

            pipeline.Run(async context =>
            {
                using var client = new HttpClient(new ResilienceHandler(new SocketsHttpHandler(), policy))
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                };

                using var hop = await client.GetAsync(downstream.BaseUri, context.RequestAborted);
                await context.Response.WriteAsync("done");
            });
        });

        using var caller = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Uri);
        request.Headers.TryAddWithoutValidation(HttpResilience.NestedRetryHeader, ResilienceNestedRetry.Marker);

        using var response = await caller.SendAsync(request);
        Assert.Equal("done", await response.Content.ReadAsStringAsync());

        // The feature in two assertions: the outbound call reports the nesting it inherited, and the
        // library reports and does not intervene - the downstream saw exactly one request.
        Assert.Contains(CallEventKind.NestedRetry, events);
        Assert.Equal(1, downstream.RequestCount);
    }

    [Fact]
    public async Task An_outbound_call_without_an_inbound_marker_reports_nothing()
    {
        // The negative control: same setup, no inbound header, and the outbound call reports no
        // nesting - which is what makes the positive test a claim about the marker rather than
        // about the setup.
        await using var downstream = await LoopbackHttp.StartAsync((_, _) => Task.FromResult(LoopbackResponse.Text(HttpStatusCode.OK, "ok")));

        var events = new List<CallEventKind>();
        var policy = Resilience.Http with { OnEvent = e => events.Add(e.Kind) };

        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceNestedRetry();

            pipeline.Run(async context =>
            {
                using var client = new HttpClient(new ResilienceHandler(new SocketsHttpHandler(), policy))
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                };

                using var hop = await client.GetAsync(downstream.BaseUri, context.RequestAborted);
                await context.Response.WriteAsync("done");
            });
        });

        using var caller = new HttpClient();
        using var response = await caller.GetAsync(app.Uri);
        Assert.Equal("done", await response.Content.ReadAsStringAsync());

        Assert.DoesNotContain(CallEventKind.NestedRetry, events);
        Assert.Equal(1, downstream.RequestCount);
    }

    [Fact]
    public async Task The_flag_does_not_leak_between_requests()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceNestedRetry();
            pipeline.Run(context => context.Response.WriteAsync(ResilienceNestedRetry.IsCallerRetrying ? "retrying" : "not"));
        });

        using var client = new HttpClient();

        using (var request = new HttpRequestMessage(HttpMethod.Get, app.Uri))
        {
            request.Headers.TryAddWithoutValidation(HttpResilience.NestedRetryHeader, ResilienceNestedRetry.Marker);
            using var response = await client.SendAsync(request);
            Assert.Equal("retrying", await response.Content.ReadAsStringAsync());
        }

        // Same client, same server, immediately after: a request without the marker must find the
        // flag false again, not inherited from its predecessor.
        Assert.Equal("not", await client.GetStringAsync(app.Uri));
    }
}
