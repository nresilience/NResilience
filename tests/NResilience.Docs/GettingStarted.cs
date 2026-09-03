using System.Net;
using System.Net.Http.Json;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>Quick start and key concepts: the on-ramp pages.</summary>
public sealed class GettingStarted
{
    // <snippet:quick-start-http-client>
    // One client for the application's lifetime, with the policy already inside it.
    private static readonly HttpClient Client = HttpResilience.CreateClient();

    private static async Task<User?> GetUserAsync(int id, CancellationToken cancellationToken) =>
        await Client.GetFromJsonAsync<User>(requestUri: new Uri(uriString: $"https://api.example.com/users/{id}"), cancellationToken: cancellationToken);

    // </snippet:quick-start-http-client>

    [Fact]
    public async Task The_first_call_is_a_client_call()
    {
        var transport = new ScriptedHttpHandler()
            .Respond(() => Doubles.Status(status: HttpStatusCode.ServiceUnavailable))
            .Respond(() => Doubles.Json(value: new User(Name: "ada")));

        using var client = HttpResilience.CreateClient(
            policy: Resilience.Http with { Backoff = Backoff.None },
            innerHandler: transport);

        var user = await client.GetFromJsonAsync<User>(
            requestUri: new Uri(uriString: "https://api.example.com/users/1"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: "ada", actual: user?.Name);
        Assert.Equal(expected: 2, actual: transport.Requests.Count);
    }

    [Fact]
    public async Task Any_call_that_takes_the_attempts_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var db = new Doubles.Database(name: "ada");
        var id = 1;

        // <snippet:quick-start-run-any-call>
        var api = Resilience.Default;

        var name = await api.RunAsync(attempt => db.ReadNameAsync(id: id, cancellationToken: attempt), cancellationToken: cancellationToken);

        // </snippet:quick-start-run-any-call>

        Assert.Equal(expected: "ada", actual: name);
        Assert.Equal(expected: 2, actual: db.Reads);
    }

    [Fact]
    public async Task The_outcome_without_an_exception()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var api = Resilience.Default with { Attempts = 2, Backoff = Backoff.None };

        // <snippet:quick-start-outcome>
        var result = await api.TryRunAsync(attempt => FetchAsync(cancellationToken: attempt), cancellationToken: cancellationToken);

        if (!result.TryGetValue(value: out var user))
        {
            // Why it stopped, and everything that happened on the way.
            Console.WriteLine(value: result.StopReason); // AttemptsExhausted
            Console.WriteLine(value: result.Attempts); // 2 attempts over 1.2ms: Transient IOException (0.6ms), ...
        }

        // </snippet:quick-start-outcome>

        Assert.Equal(expected: StopReason.AttemptsExhausted, actual: result.StopReason);
        Assert.Equal(expected: 2, actual: result.Attempts.Count);
    }

    [Fact]
    public void A_policy_is_a_value()
    {
        // <snippet:key-concepts-policy-value>
        var api = Resilience.Http; // a preset
        var patient = api with { Deadline = TimeSpan.FromMinutes(value: 1) }; // a variant
        var once = patient with { Attempts = 1 }; // a variant of the variant

        Console.WriteLine(value: api == Resilience.Http); // True - it is a value
        Console.WriteLine(value: once.Deadline); // 00:01:00 - `with` copies the rest

        // </snippet:key-concepts-policy-value>

        Assert.True(condition: api == Resilience.Http);
        Assert.Equal(expected: TimeSpan.FromMinutes(value: 1), actual: once.Deadline);
        Assert.Equal(expected: 1, actual: once.Attempts);
    }

    [Fact]
    public void The_two_bounds_are_different_things()
    {
        // <snippet:key-concepts-two-bounds>
        var api = Resilience.Http with
        {
            Deadline = TimeSpan.FromSeconds(value: 10), // the whole call, retries and backoff included
            AttemptTimeout = TimeSpan.FromSeconds(value: 3), // one attempt, capped by whatever is left of the deadline
        };

        // </snippet:key-concepts-two-bounds>

        Assert.Equal(expected: TimeSpan.FromSeconds(value: 10), actual: api.Deadline);
        Assert.Equal(expected: TimeSpan.FromSeconds(value: 3), actual: api.AttemptTimeout);
    }

    [Fact]
    public void Every_outcome_gets_one_of_four_verdicts()
    {
        // <snippet:key-concepts-verdicts>
        var classify = Classifier.Http
            .On<MyTransportException>(verdict: Verdict.Transient) // retried, short curve
            .On<MyQuotaException>(ex => Verdict.Throttled(retryAfter: ex.RetryAfter)) // retried, long curve or the server's own delay
            .On<MyValidationException>(verdict: Verdict.Permanent); // never retried

        var api = Resilience.Http with { Classify = classify };

        // </snippet:key-concepts-verdicts>

        Assert.Equal(expected: VerdictKind.Transient, actual: api.Classify.ClassifyException(exception: new MyTransportException()).Kind);
        Assert.Equal(expected: VerdictKind.Permanent, actual: api.Classify.ClassifyException(exception: new MyValidationException()).Kind);
        Assert.Equal(expected: TimeSpan.FromSeconds(value: 4), actual: api.Classify.ClassifyException(exception: new MyQuotaException()).RetryAfter);
    }

    private static Task<User> FetchAsync(CancellationToken cancellationToken) =>
        Task.FromException<User>(exception: new IOException(message: "the socket went away"));

    [Fact]
    public async Task A_callback_that_returns_a_ValueTask()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var api = Resilience.Default;
        var db = new Doubles.Database(name: "ada");
        var feed = new Doubles.Feed(name: "ada");
        var id = 1;

        // <snippet:reference-valuetask-callback>
        // ReadAsync returns a ValueTask, so this binds to the ValueTask overload. 
        // Buffered reads allocate nothing.
        var buffered = await api.RunAsync(
            static (source, attempt) => source.ReadAsync(cancellationToken: attempt),
            state: feed,
            cancellationToken: cancellationToken);

        // ReadNameAsync returns a Task, so this binds to the Task overload. Same name, same shape.
        var name = await api.RunAsync(attempt => db.ReadNameAsync(id: id, cancellationToken: attempt), cancellationToken: cancellationToken);

        // </snippet:reference-valuetask-callback>

        Assert.Equal(expected: "ada", actual: buffered);
        Assert.Equal(expected: "ada", actual: name);
        Assert.Equal(expected: 2, actual: feed.Reads);
    }

    private sealed record User(string Name);

    private sealed class MyTransportException : Exception;

    private sealed class MyValidationException : Exception;

    private sealed class MyQuotaException : Exception
    {
        internal TimeSpan RetryAfter => TimeSpan.FromSeconds(value: 4);
    }
}

// <snippet:quick-start-house-policy>
public static class Policies
{
    public static readonly Resilience Api = Resilience.Http with
    {
        Deadline = TimeSpan.FromSeconds(value: 10),
        AttemptTimeout = TimeSpan.FromSeconds(value: 3),
    };

    public static readonly Resilience Realtime = Api with
    {
        Attempts = 1,
        AttemptTimeout = TimeSpan.FromMilliseconds(value: 250),
    };
}

// </snippet:quick-start-house-policy>
