using System.Net;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>
///     Criticality propagation: publishing how much a call matters, letting the executor hold back
///     amplification for work nobody is waiting for, and carrying the level to the next hop.
/// </summary>
public sealed class Criticalities
{
    [Fact]
    public void A_policy_can_hold_back_amplification_for_work_nobody_is_waiting_for()
    {
        // <snippet:criticality-inherit>
        // The inbound half. The policy reads how much this call matters, and two things follow: a
        // Sheddable call is never hedged, and it is refused a retry once the retry budget is more
        // than half spent. Every other level behaves exactly as it does without this.
        var api = Resilience.Http with { UseAmbientCriticality = true };

        // In an ASP.NET Core app, UseResilienceDeadline() reads what the caller sent. Anywhere else -
        // a queue consumer reading a level off a message, or a backfill labeling its own work -
        // publish it yourself.
        using var scope = AmbientCriticality.Begin(criticality: Criticality.Sheddable);

        // </snippet:criticality-inherit>

        Assert.Equal(expected: Criticality.Sheddable, actual: AmbientCriticality.Current);

        api.Validate();
    }

    [Fact]
    public void An_unlabeled_call_is_critical()
    {
        // <snippet:criticality-default>
        // Nothing published, so nothing is held back. An unlabeled call is Critical, never Sheddable:
        // a default that sheds is a default that loses requests during the first incident after an
        // upgrade.
        var level = AmbientCriticality.Current;

        // </snippet:criticality-default>

        Assert.Equal(expected: Criticality.Critical, actual: level);
    }

    [Fact]
    public async Task Each_request_can_carry_how_much_it_matters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new ScriptedHttpHandler().Responds(status: HttpStatusCode.OK);
        var uri = new Uri(uriString: "https://api.example.com/backfill");

        // <snippet:criticality-propagate>
        // The outbound half. Every request carries the level this call is running at, so the service
        // three hops down can tell a backfill from a checkout. Off by default.
        var options = new HttpResilienceOptions { PropagateCriticality = true };

        using var scope = AmbientCriticality.Begin(criticality: Criticality.Sheddable);
        using var client = new HttpClient(handler: new HttpResilienceHandler(innerHandler: transport, policy: Resilience.Http, options: options));
        using var response = await client.GetAsync(requestUri: uri, cancellationToken: cancellationToken);

        // X-Criticality: Sheddable, on every attempt of the call.
        // </snippet:criticality-propagate>

        Assert.Equal(expected: "Sheddable", actual: transport.Requests[0].Headers.GetValues(name: AmbientCriticality.Header).Single());
    }

    [Fact]
    public async Task What_a_level_means_for_this_service_is_the_services_own_decision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var refused = 0;

        // <snippet:criticality-admit>
        // The library never sheds. What a level means for a given service is a decision it has to
        // make itself, and Admit is where that decision goes - it reads a value that traveled with
        // the request rather than guessing one.
        var api = TestPolicy.Instant with
        {
            Admit = _ => Task.FromResult(result: AmbientCriticality.Current < Criticality.Critical && Overloaded()
                ? Verdict.Refused(retryAfter: TimeSpan.FromSeconds(value: 1))
                : Verdict.Ok),
        };

        using var scope = AmbientCriticality.Begin(criticality: Criticality.Sheddable);
        var result = await api.TryRunAsync(work: _ => Task.FromResult(result: 1), cancellationToken: cancellationToken);

        // </snippet:criticality-admit>

        Assert.False(condition: result.IsSuccess);
        Assert.True(condition: refused > 0);

        bool Overloaded()
        {
            refused++;
            return true;
        }
    }

    [Fact]
    public void A_criticality_with_nothing_to_gate_is_refused()
    {
        // <snippet:criticality-validate>
        // Refused at validation rather than ignored. The two consumers are the hedge and the retry
        // budget, so a single-attempt policy with no hedge has nothing for a level to gate - and a
        // policy that silently does nothing is how you end up believing your backfill is holding back.
        var api = Resilience.Default with { Attempts = 1, UseAmbientCriticality = true };

        var problems = Assert.Throws<ResilienceConfigurationException>(testCode: api.Validate);

        // </snippet:criticality-validate>

        Assert.Contains(expectedSubstring: "nothing for it to gate", actualString: problems.Message, comparisonType: StringComparison.Ordinal);
    }
}
