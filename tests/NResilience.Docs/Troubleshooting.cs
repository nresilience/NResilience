using System.Net;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>The fixes on the troubleshooting page and in the FAQ.</summary>
public sealed class Troubleshooting
{
    [Fact]
    public async Task Nothing_was_retried_because_the_exception_was_not_recognized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = Sequence.For<int>().Throws(exception: new MyDbException()).Returns(result: 1);

        // <snippet:troubleshoot-not-retried>
        // Classifier.Default treats an exception type it has never heard of as Permanent. Teach it
        // about yours, and the NotRetried event names the type it did not recognize.
        var api = Resilience.Default with
        {
            Backoff = Backoff.None,
            Classifier = Classifier.Default.On<MyDbException>(verdict: Verdict.Transient),
        };

        // </snippet:troubleshoot-not-retried>

        Assert.Equal(expected: 1, actual: await api.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task The_history_of_a_failed_call()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = Sequence.For<int>().Throws(exception: new IOException(), count: 3);
        var api = Resilience.Default with { Backoff = Backoff.None };

        // <snippet:troubleshoot-attempt-log>
        // Every failure carries its own history: on CallResult, on the exceptions the library
        // invents, and on Exception.Data for an original exception it rethrew unchanged.
        var result = await api.TryRunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);

        Console.WriteLine(value: result.StopReason); // AttemptsExhausted
        Console.WriteLine(value: result.Attempts); // 3 attempts over 0.9ms: Transient IOException (0.2ms), ...

        foreach (var attempt in result.Attempts)
        {
            Console.WriteLine(value: $"#{attempt.Number} {attempt.Verdict.Kind} after {attempt.DelayBefore.TotalMilliseconds}ms");
        }

        // </snippet:troubleshoot-attempt-log>

        Assert.Equal(expected: 3, actual: result.Attempts.Count);
    }

    [Fact]
    public void The_transport_timeout_is_the_one_bound_the_policy_cannot_see()
    {
        // <snippet:troubleshoot-transport-timeout>
        // HttpClient.Timeout defaults to 100 seconds and covers the whole retry sequence, so it
        // silently caps any deadline longer than that. On a client you build yourself, hand the
        // bound to the policy.
        using var client = new HttpClient(handler: new ResilienceHandler(innerHandler: new HttpClientHandler()))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // </snippet:troubleshoot-transport-timeout>

        Assert.Equal(expected: Timeout.InfiniteTimeSpan, actual: client.Timeout);
    }

    [Fact]
    public async Task A_post_is_not_retried_until_you_say_it_is_safe()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var transport = new ScriptedHttpHandler()
            .Responds(HttpStatusCode.ServiceUnavailable)
            .Responds(HttpStatusCode.OK);

        using var client = HttpResilience.CreateClient(
            policy: Resilience.Http with { Backoff = Backoff.None },
            innerHandler: transport);

        // <snippet:troubleshoot-post-not-retried>
        using var request = new HttpRequestMessage(method: HttpMethod.Post, requestUri: "https://api.example.com/orders");
        request.MarkRepeatable(idempotencyKey: Guid.NewGuid().ToString()); // the option this client retries on, plus the key the service deduplicates on

        // </snippet:troubleshoot-post-not-retried>

        using var response = await client.SendAsync(request: request, cancellationToken: cancellationToken);

        Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
    }

    [Fact]
    public void A_bad_policy_lists_every_problem_at_once()
    {
        // This policy is invalid on purpose: the page is about the message it produces. NRES003 says the
        // same thing at build time, which is the analyzer working rather than something to fix here.
#pragma warning disable NRES003

        // <snippet:troubleshoot-validate>
        var api = Resilience.Default with { Attempts = 0, Deadline = TimeSpan.FromSeconds(value: -1) };

        var problem = Assert.Throws<ResilienceConfigurationException>(testCode: api.Validate);

        Console.WriteLine(value: string.Join(separator: Environment.NewLine, values: problem.Problems));

        // Attempts must be at least 1; it is 0.
        // Deadline must be positive, or Timeout.InfiniteTimeSpan for no bound; it is -00:00:01.
        // </snippet:troubleshoot-validate>
#pragma warning restore NRES003

        Assert.Equal(expected: 2, actual: problem.Problems.Count);
    }

    internal sealed class MyDbException : Exception;
}
