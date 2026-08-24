using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using NResilience.Http;

namespace NResilience.Docs;

/// <summary>Quick start and key concepts: the on-ramp pages.</summary>
public sealed class GettingStarted
{
    private sealed record User(string Name);

    // <snippet:quick-start-http-client>
    // One client for the application's lifetime, with the policy already inside it.
    private static readonly HttpClient Client = ResilienceHttp.CreateClient();

    private static async Task<User?> GetUserAsync(int id, CancellationToken cancellationToken) =>
        await Client.GetFromJsonAsync<User>(new Uri($"https://api.example.com/users/{id}"), cancellationToken);
    // </snippet:quick-start-http-client>

    [Fact]
    public async Task The_first_call_is_a_client_call()
    {
        var transport = new Doubles.ScriptedTransport(
            () => Doubles.Status(HttpStatusCode.ServiceUnavailable),
            () => Doubles.Json(new User("ada")));
        using var client = ResilienceHttp.CreateClient(
            Resilience.Http with { Backoff = Backoff.None },
            innerHandler: transport);

        var user = await client.GetFromJsonAsync<User>(
            new Uri("https://api.example.com/users/1"), TestContext.Current.CancellationToken);

        Assert.Equal("ada", user?.Name);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task Any_call_that_takes_the_attempts_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var db = new Doubles.Database("ada");
        var id = 1;

        // <snippet:quick-start-run-any-call>
        var api = Resilience.Default;

        var name = await api.RunAsync(attempt => db.ReadNameAsync(id, attempt), cancellationToken);
        // </snippet:quick-start-run-any-call>

        Assert.Equal("ada", name);
        Assert.Equal(2, db.Reads);
    }

    [Fact]
    public async Task The_outcome_without_an_exception()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var api = Resilience.Default with { Attempts = 2, Backoff = Backoff.None };

        // <snippet:quick-start-outcome>
        var result = await api.TryRunAsync(attempt => FetchAsync(attempt), cancellationToken);

        if (!result.TryGetValue(out var user))
        {
            // Why it stopped, and everything that happened on the way.
            Console.WriteLine(result.StopReason);   // AttemptsExhausted
            Console.WriteLine(result.Attempts);     // 2 attempts over 1.2ms: Transient IOException (0.6ms), ...
        }
        // </snippet:quick-start-outcome>

        Assert.Equal(StopReason.AttemptsExhausted, result.StopReason);
        Assert.Equal(2, result.Attempts.Count);
    }

    [Fact]
    public void A_policy_is_a_value()
    {
        // <snippet:key-concepts-policy-value>
        var api = Resilience.Http;                              // a preset
        var patient = api with { Deadline = TimeSpan.FromMinutes(1) };  // a variant
        var once = patient with { Attempts = 1 };               // a variant of the variant

        Console.WriteLine(api == Resilience.Http);              // True - it is a value
        Console.WriteLine(once.Deadline);                       // 00:01:00 - `with` copies the rest
        // </snippet:key-concepts-policy-value>

        Assert.True(api == Resilience.Http);
        Assert.Equal(TimeSpan.FromMinutes(1), once.Deadline);
        Assert.Equal(1, once.Attempts);
    }

    [Fact]
    public void The_two_bounds_are_different_things()
    {
        // <snippet:key-concepts-two-bounds>
        var api = Resilience.Http with
        {
            Deadline = TimeSpan.FromSeconds(10),        // the whole call, retries and backoff included
            AttemptTimeout = TimeSpan.FromSeconds(3),   // one attempt, capped by whatever is left of the deadline
        };
        // </snippet:key-concepts-two-bounds>

        Assert.Equal(TimeSpan.FromSeconds(10), api.Deadline);
        Assert.Equal(TimeSpan.FromSeconds(3), api.AttemptTimeout);
    }

    [Fact]
    public void Every_outcome_gets_one_of_four_verdicts()
    {
        // <snippet:key-concepts-verdicts>
        var classify = Classifier.Http
            .On<MyTransportException>(Verdict.Transient)                  // retried, short curve
            .On<MyQuotaException>(ex => Verdict.Throttled(ex.RetryAfter)) // retried, long curve or the server's own delay
            .On<MyValidationException>(Verdict.Permanent);                // never retried

        var api = Resilience.Http with { Classify = classify };
        // </snippet:key-concepts-verdicts>

        Assert.Equal(VerdictKind.Transient, api.Classify.ClassifyException(new MyTransportException()).Kind);
        Assert.Equal(VerdictKind.Permanent, api.Classify.ClassifyException(new MyValidationException()).Kind);
        Assert.Equal(TimeSpan.FromSeconds(4), api.Classify.ClassifyException(new MyQuotaException()).RetryAfter);
    }

    private static Task<User> FetchAsync(CancellationToken cancellationToken) =>
        Task.FromException<User>(new IOException("the socket went away"));

    private sealed class MyTransportException : Exception;

    private sealed class MyValidationException : Exception;

    private sealed class MyQuotaException : Exception
    {
        internal TimeSpan RetryAfter => TimeSpan.FromSeconds(4);
    }
}

// <snippet:quick-start-house-policy>
public static class Policies
{
    public static readonly Resilience Api = Resilience.Http with
    {
        Deadline = TimeSpan.FromSeconds(10),
        AttemptTimeout = TimeSpan.FromSeconds(3),
    };

    public static readonly Resilience Realtime = Api with
    {
        Attempts = 1,
        AttemptTimeout = TimeSpan.FromMilliseconds(250),
    };
}
// </snippet:quick-start-house-policy>
