using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using NResilience.AspNetCore;

namespace NResilience.IntegrationTests;

/// <summary>
///     The inbound half of criticality propagation over a real server and a real socket, and the
///     refusal that shares its pass: a request that arrives with nothing left to spend.
///     <para>
///         The unit tests prove the parsing and what the executor does with a level. What only a real
///         server can prove is that the level survives the request pipeline - an
///         <see cref="AsyncLocal{T}" /> across Kestrel's own awaits and a nested outbound call - and
///         that it reaches the second hop with the escalation guardrail applied.
///     </para>
/// </summary>
public sealed class CriticalityMiddlewareTests
{
    [Fact]
    public async Task The_level_a_caller_sent_is_readable_for_the_whole_request()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline();

            pipeline.Run(async context =>
            {
                await Task.Yield();
                await context.Response.WriteAsync(AmbientCriticality.Format(AmbientCriticality.Current));
            });
        });

        Assert.Equal("Sheddable", await GetAsync(app.Uri, ("Sheddable", AmbientCriticality.Header)));
    }

    [Fact]
    public async Task A_request_without_a_level_is_critical()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline();
            pipeline.Run(context => context.Response.WriteAsync(AmbientCriticality.Format(AmbientCriticality.Current)));
        });

        using var client = new HttpClient();
        Assert.Equal("Critical", await client.GetStringAsync(app.Uri));
    }

    [Fact]
    public async Task A_caller_cannot_escalate_itself_past_critical()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline();
            pipeline.Run(context => context.Response.WriteAsync(AmbientCriticality.Format(AmbientCriticality.Current)));
        });

        Assert.Equal("Critical", await GetAsync(app.Uri, ("CriticalPlus", AmbientCriticality.Header)));
    }

    [Fact]
    public async Task A_level_this_service_does_not_recognize_is_ignored()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline();
            pipeline.Run(context => context.Response.WriteAsync(AmbientCriticality.Format(AmbientCriticality.Current)));
        });

        Assert.Equal("Critical", await GetAsync(app.Uri, ("best-effort", AmbientCriticality.Header)));
    }

    [Fact]
    public async Task Reading_the_level_can_be_turned_off()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline(o => o.ReadCriticality = false);
            pipeline.Run(context => context.Response.WriteAsync(AmbientCriticality.Format(AmbientCriticality.Current)));
        });

        Assert.Equal("Critical", await GetAsync(app.Uri, ("Sheddable", AmbientCriticality.Header)));
    }

    [Fact]
    public async Task The_header_name_is_the_services_to_choose()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline(o => o.CriticalityHeader = "X-Importance");
            pipeline.Run(context => context.Response.WriteAsync(AmbientCriticality.Format(AmbientCriticality.Current)));
        });

        Assert.Equal("Sheddable", await GetAsync(app.Uri, ("Sheddable", "X-Importance")));
    }

    [Fact]
    public async Task Both_halves_of_one_pass_reach_the_handler()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline();

            pipeline.Run(async context =>
            {
                await Task.Yield();

                var remaining = AmbientDeadline.Remaining is { } left
                    ? left.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)
                    : "none";

                await context.Response.WriteAsync($"{AmbientCriticality.Format(AmbientCriticality.Current)}/{remaining}");
            });
        });

        var body = await GetAsync(app.Uri, ("SheddablePlus", AmbientCriticality.Header), ("5000", AmbientDeadline.Header));
        var parts = body.Split('/');

        Assert.Equal("SheddablePlus", parts[0]);
        Assert.InRange(double.Parse(parts[1], CultureInfo.InvariantCulture), 1, 5000);
    }

    [Fact]
    public async Task The_second_hop_is_told_the_level_the_first_was()
    {
        await using var downstream = await LoopbackHttp.StartAsync((_, _) => Task.FromResult(LoopbackResponse.Text(HttpStatusCode.OK, "ok")));

        var options = new HttpResilienceOptions { PropagateCriticality = true };

        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline();

            pipeline.Run(async context =>
            {
                using var client = new HttpClient(new HttpResilienceHandler(new SocketsHttpHandler(), Resilience.Http, options))
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                };

                using var hop = await client.GetAsync(downstream.BaseUri, context.RequestAborted);
                await context.Response.WriteAsync("done");
            });
        });

        Assert.Equal("done", await GetAsync(app.Uri, ("Sheddable", AmbientCriticality.Header)));

        // The whole feature in one assertion: the level travels, so the service three hops down knows
        // this is a backfill rather than a checkout.
        Assert.Equal("Sheddable", downstream.Requests.Single().Headers[AmbientCriticality.Header.ToLowerInvariant()]);
    }

    // ---- Refusing a request with nothing left to spend ----

    [Fact]
    public async Task A_request_that_arrives_with_nothing_left_can_be_refused()
    {
        var ran = false;

        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline(o =>
            {
                o.Reserve = TimeSpan.FromSeconds(2);
                o.RejectExpired = true;
            });

            pipeline.Run(context =>
            {
                ran = true;
                return context.Response.WriteAsync("ran");
            });
        });

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Uri);
        request.Headers.TryAddWithoutValidation(AmbientDeadline.Header, "500");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(ran);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("urn:nresilience:deadline-expired-on-arrival", problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(504, problem.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_request_that_arrives_in_time_is_not_refused()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline(o =>
            {
                o.Reserve = TimeSpan.FromSeconds(2);
                o.RejectExpired = true;
            });

            pipeline.Run(context => context.Response.WriteAsync("ran"));
        });

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Uri);
        request.Headers.TryAddWithoutValidation(AmbientDeadline.Header, "5000");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ran", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Nothing_is_refused_by_default()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline(o => o.Reserve = TimeSpan.FromSeconds(2));
            pipeline.Run(context => context.Response.WriteAsync("ran"));
        });

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, app.Uri);
        request.Headers.TryAddWithoutValidation(AmbientDeadline.Header, "500");

        using var response = await client.SendAsync(request);

        // The request may still be answerable from cache, and refusing it is a decision about this
        // service that the library has no standing to make on its own.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_request_carrying_no_deadline_is_never_refused()
    {
        await using var app = await TestApp.StartAsync(pipeline =>
        {
            pipeline.UseResilienceDeadline(o =>
            {
                o.Reserve = TimeSpan.FromSeconds(2);
                o.RejectExpired = true;
            });

            pipeline.Run(context => context.Response.WriteAsync("ran"));
        });

        using var client = new HttpClient();

        // No header is not an expired deadline; it is no deadline. There is nothing to prove
        // undeliverable.
        Assert.Equal("ran", await client.GetStringAsync(app.Uri));
    }

    private static async Task<string> GetAsync(Uri uri, params (string Value, string Header)[] headers)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        foreach (var (value, header) in headers)
        {
            request.Headers.TryAddWithoutValidation(header, value);
        }

        using var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }
}
