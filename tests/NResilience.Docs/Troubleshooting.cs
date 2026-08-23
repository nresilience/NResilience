using System.Net;
using System.Net.Http;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>The fixes on the troubleshooting page and in the FAQ.</summary>
public sealed class Troubleshooting
{
    [Fact]
    public async Task Nothing_was_retried_because_the_exception_was_not_recognized()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Sequence<int> calls = Sequence.For<int>().Throws(new MyDbException()).Returns(1);

        // <snippet:troubleshoot-not-retried>
        // Classifier.Default treats an exception type it has never heard of as Permanent. Teach it
        // about yours, and the NotRetried event names the type it did not recognize.
        var api = Resilience.Default with
        {
            Backoff = Backoff.None,
            Classify = Classifier.Default.On<MyDbException>(Verdict.Transient),
        };
        // </snippet:troubleshoot-not-retried>

        Assert.Equal(1, await api.RunAsync(attempt => calls.NextAsync(attempt), cancellationToken));
    }

    [Fact]
    public async Task The_history_of_a_failed_call()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Sequence<int> calls = Sequence.For<int>().Throws(new IOException(), 3);
        var api = Resilience.Default with { Backoff = Backoff.None };

        // <snippet:troubleshoot-attempt-log>
        // Every failure carries its own history: on CallResult, on the exceptions the library
        // invents, and on Exception.Data for an original exception it rethrew unchanged.
        CallResult<int> result = await api.TryRunAsync(attempt => calls.NextAsync(attempt), cancellationToken);

        Console.WriteLine(result.StopReason);   // AttemptsExhausted
        Console.WriteLine(result.Attempts);     // 3 attempts over 0.9ms: Transient IOException (0.2ms), ...

        foreach (Attempt attempt in result.Attempts)
        {
            Console.WriteLine($"#{attempt.Number} {attempt.Verdict.Kind} after {attempt.DelayBefore.TotalMilliseconds}ms");
        }
        // </snippet:troubleshoot-attempt-log>

        Assert.Equal(3, result.Attempts.Count);
    }

    [Fact]
    public void The_transport_timeout_is_the_one_bound_the_policy_cannot_see()
    {
        // <snippet:troubleshoot-transport-timeout>
        // HttpClient.Timeout defaults to 100 seconds and covers the whole retry sequence, so it
        // silently caps any deadline longer than that. On a client you build yourself, hand the
        // bound to the policy.
        using var client = new HttpClient(new ResilienceHandler(new HttpClientHandler()))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        // </snippet:troubleshoot-transport-timeout>

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public async Task A_post_is_not_retried_until_you_say_it_is_safe()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var transport = new Doubles.ScriptedTransport(
            () => Doubles.Status(HttpStatusCode.ServiceUnavailable),
            () => Doubles.Status(HttpStatusCode.OK));
        using HttpClient client = ResilienceHttp.CreateClient(
            Resilience.Http with { Backoff = Backoff.None },
            innerHandler: transport);

        // <snippet:troubleshoot-post-not-retried>
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/orders");
        request.Options.Set(ResilienceHttp.Repeatable, true);   // this one carries an idempotency key
        // </snippet:troubleshoot-post-not-retried>

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void A_bad_policy_lists_every_problem_at_once()
    {
        // This policy is invalid on purpose: the page is about the message it produces. NRES003 says the
        // same thing at build time, which is the analyzer working rather than something to fix here.
        #pragma warning disable NRES003
        // <snippet:troubleshoot-validate>
        var api = Resilience.Default with { Attempts = 0, Deadline = TimeSpan.FromSeconds(-1) };

        var problem = Assert.Throws<ResilienceConfigurationException>(api.Validate);

        Console.WriteLine(string.Join(Environment.NewLine, problem.Problems));
        // Attempts must be at least 1; it is 0.
        // Deadline must be positive, or Timeout.InfiniteTimeSpan for no bound; it is -00:00:01.
        // </snippet:troubleshoot-validate>
        #pragma warning restore NRES003

        Assert.Equal(2, problem.Problems.Count);
    }

    internal sealed class MyDbException : Exception;
}
