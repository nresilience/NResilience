using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>Hedging: the second copy of a slow attempt, and the quantile that decides when to start one.</summary>
public sealed class Hedging
{
    [Fact]
    public void Hedging_is_configured_by_naming_a_quantile()
    {
        // <snippet:hedging-configure>
        // The threshold is always a live quantile of recent latency, never a constant. A brownout moves
        // the quantile with it, so the fraction of calls that hedge stays at about 1 - Quantile whatever
        // the dependency is doing - which is why there is deliberately no Hedge.After(TimeSpan).
        var api = Resilience.Http with
        {
            Attempts = 3, // at most 3 calls reach the dependency, whatever shape they run in
            Hedge = Hedge.At(quantile: 0.95), // the 2nd may start before the 1st comes back
        };

        // </snippet:hedging-configure>

        api.Validate();
        Assert.Equal(expected: 0.95, actual: api.Hedge!.Value.Quantile);
    }

    [Fact]
    public void Every_knob_has_a_working_default()
    {
        // <snippet:hedging-tuning>
        // Hedge.At fills in the rest, so change only what you mean to. The quantile is the load: 0.99
        // hedges 1% of calls and shortens a smaller part of the tail than 0.95 does.
        var api = Resilience.Http with
        {
            Hedge = Hedge.At(quantile: 0.99, maxConcurrent: 3) with
            {
                MinimumSamples = 50, // wait for 50 recent calls before hedging anything
                MinimumDelay = TimeSpan.FromMilliseconds(value: 25), // never hedge sooner than this
                Window = TimeSpan.FromMinutes(value: 1), // how much history the estimate covers
            },
        };

        // </snippet:hedging-tuning>

        api.Validate();
        Assert.Equal(expected: 3, actual: api.Hedge!.Value.MaxConcurrent);
    }

    /// <summary>
    ///     Closed is not the same as healthy, and the gap between them is where a dependency that is
    ///     failing on a third of its calls gets hedged anyway. The suppression point is the line.
    /// </summary>
    [Fact]
    public void Hedging_steps_aside_while_the_dependency_is_failing()
    {
        // <snippet:hedging-suppression>
        // Hedging costs about 5% extra load, and a dependency that is already failing is the last one
        // that needs it. The policy's breaker measures the error rate anyway, so hedging stops once
        // that rate reaches a fraction of the rate that would open the breaker - long before the
        // breaker does. The default fraction is half; this one gives up on hedging sooner.
        var hedge = Hedge.At(quantile: 0.95) with
        {
            SuppressAt = 0.25, // stop hedging a quarter of the way to the trip point
        };

        // </snippet:hedging-suppression>

        Assert.Equal(expected: 0.25, actual: hedge.SuppressAt);
    }

    /// <summary>
    ///     The other question, and the one no configuration can answer in advance: whether a second
    ///     attempt is independent enough of the first to ever win.
    /// </summary>
    [Fact]
    public void Hedging_stops_once_it_stops_winning()
    {
        // <snippet:hedging-win-rate>
        // Hedging only shortens the tail if the second attempt is independent enough of the first to
        // win sometimes. Against a dependency that is uniformly slow because it is overloaded, it
        // never is - so track how often hedges actually win, and hedge less when they stop.
        var api = Resilience.Http with
        {
            Hedge = Hedge.At(quantile: 0.95) with
            {
                // Keep hedging while at least one hedge in five produces the answer.
                WinRate = WinRate.AtLeast(minimum: 0.2),
            },
        };

        // </snippet:hedging-win-rate>

        api.Validate();

        Assert.Equal(expected: 0.2, actual: api.Hedge!.Value.WinRate!.Value.Minimum);
    }

    /// <summary>
    ///     The estimate belongs to the policy instance, so the policy has to outlive the call. Exactly
    ///     the rule the automatic retry budget already follows, and the failure mode is the same: a
    ///     policy rebuilt per call never learns anything and therefore never hedges.
    /// </summary>
    [Fact]
    public async Task The_policy_value_is_held_rather_than_rebuilt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = Sequence.For<int>().Returns(result: 42);

        // <snippet:hedging-static>
        var value = await Policies.Search.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);

        // </snippet:hedging-static>

        Assert.Equal(expected: 42, actual: value);
    }

    [Fact]
    public void An_http_client_hedges_the_requests_it_would_retry()
    {
        var services = new ServiceCollection();

        // <snippet:hedging-http>
        // Nothing HTTP-specific is needed. The handler already scopes a policy per host - so each host
        // gets its own latency estimate - and already refuses to repeat a POST, which is the same gate a
        // hedge has to pass.
        services.AddHttpClient<SearchClient>()
            .AddResilience(Resilience.Http with { Hedge = Hedge.At(quantile: 0.95) });

        // </snippet:hedging-http>

        Assert.NotEmpty(services);
    }

    [Fact]
    public void A_post_is_hedged_only_when_it_says_it_is_repeatable()
    {
        var uri = new Uri(uriString: "https://api.test/orders");

        // <snippet:hedging-repeatable>
        // A hedge is a concurrent retry, so the idempotency key that makes a retried POST safe is what
        // makes a hedged one safe. Without this the request is sent exactly once, whatever Hedge says.
        using var request = new HttpRequestMessage(method: HttpMethod.Post, requestUri: uri);
        request.MarkRepeatable();

        // </snippet:hedging-repeatable>

        Assert.True(request.Options.TryGetValue(key: ResilienceHttp.Repeatable, value: out var repeatable) && repeatable);
    }

    /// <summary>
    ///     What a hedged call looks like from a listener. The threshold on
    ///     <see cref="CallEventKind.HedgeStarted" /> is the adaptive number itself, which is the one an
    ///     operator wants on a dashboard during an incident.
    /// </summary>
    [Fact]
    public void The_events_report_what_the_extra_load_bought()
    {
        var started = 0;
        var won = 0;

        // <snippet:hedging-events>
        var api = Resilience.Http with
        {
            Hedge = Hedge.At(quantile: 0.95),
            OnEvent = e =>
            {
                if (e.Kind == CallEventKind.HedgeStarted)
                    started++; // e.Delay is the quantile the hedge fired at

                if (e.Kind == CallEventKind.HedgeWon)
                    won++; // the copy answered, so this call saw the shorter of two draws
            },
        };

        // </snippet:hedging-events>

        api.Validate();

        api.OnEvent!(CallEvent.Create(kind: CallEventKind.HedgeStarted, delay: TimeSpan.FromMilliseconds(value: 40)));
        api.OnEvent!(CallEvent.Create(kind: CallEventKind.HedgeWon));

        Assert.Equal(expected: 1, actual: started);
        Assert.Equal(expected: 1, actual: won);
    }

    /// <summary>The shape a hedging policy is held in. Part of the <c>hedging-static</c> snippet.</summary>
    // <snippet:hedging-static-policy>
    public static class Policies
    {
        // One instance, for the lifetime of the process. The latency estimate is private to this
        // instance, exactly as the automatic retry budget is, so a `with` expression inside a method
        // would hand every call a policy that has never seen a single latency sample.
        public static readonly Resilience Search = (Resilience.Http with
        {
            Attempts = 3,
            Hedge = Hedge.At(quantile: 0.95),
        }).Validated();
    }

    // </snippet:hedging-static-policy>

    /// <summary>A typed client, so the registration snippet reads the way a reader's would.</summary>
    public sealed class SearchClient(HttpClient client)
    {
        public HttpClient Client { get; } = client;
    }
}
