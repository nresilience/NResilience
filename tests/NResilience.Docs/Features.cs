using System.Net;
using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>Retry, the two bounds, classification and backoff.</summary>
public sealed class Features
{
    [Fact]
    public async Task Attempts_is_the_total()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = Sequence.For<int>().Throws(exception: new IOException(), count: 2).Returns(result: 42);

        // <snippet:retry-attempts>
        // Three attempts: try, retry, retry. Not "one call plus three retries".
        var api = Resilience.Default with { Attempts = 3 };

        var value = await api.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);

        // </snippet:retry-attempts>

        Assert.Equal(expected: 42, actual: value);
        Assert.Equal(expected: 3, actual: calls.CallCount);
    }

    [Fact]
    public void Backoff_is_tuned_by_replacing_it()
    {
        // <snippet:retry-backoff-tuning>
        var api = Resilience.Http with
        {
            Backoff = Backoff.Exponential(
                transientBase: TimeSpan.FromMilliseconds(value: 200), // the first delay after a transient failure
                throttledBase: TimeSpan.FromSeconds(value: 2), // the first delay after being throttled
                factor: 2, // doubling
                max: TimeSpan.FromSeconds(value: 10)), // the cap on any single delay
        };

        // </snippet:retry-backoff-tuning>

        Assert.Equal(expected: TimeSpan.FromSeconds(value: 10), actual: api.Backoff.Max);
        Assert.Equal(expected: Jitter.Full, actual: api.Backoff.Jitter);
    }

    [Fact]
    public void Jitter_is_derived_not_rebuilt()
    {
        // <snippet:retry-jitter>
        // Full jitter is the default. `None` is for tests, and rarely right even there.
        var deterministic = Resilience.Default with
        {
            Backoff = Backoff.Default with { Jitter = Jitter.None },
        };

        // </snippet:retry-jitter>

        Assert.Equal(expected: Jitter.None, actual: deterministic.Backoff.Jitter);
    }

    [Fact]
    public async Task Backoff_can_be_computed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = Sequence.For<int>().Throws(exception: new IOException()).Returns(result: 7);

        // <snippet:retry-custom-backoff>
        var api = Resilience.Default with
        {
            Backoff = Backoff.Custom(next => next.PreviousVerdict.Kind == VerdictKind.Throttled
                ? TimeSpan.FromSeconds(value: 5)
                : TimeSpan.FromMilliseconds(value: 50 * next.Number)),
        };

        // </snippet:retry-custom-backoff>

        Assert.Equal(expected: 7, actual: await api.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task Work_that_has_to_be_rebuilt_goes_in_BeforeAttempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tokens = new TokenSource();
        var calls = Sequence.For<int>().Throws(exception: new IOException()).Returns(result: 1);

        // <snippet:retry-before-attempt>
        var api = Resilience.Http with
        {
            // Runs before every attempt, including the first. The place to refresh a token or
            // rebuild a request, because a retry re-invokes the callback from the top.
            BeforeAttempt = next => tokens.RefreshAsync(cancellationToken: next.CancellationToken),
        };

        // </snippet:retry-before-attempt>

        await api.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);
        Assert.Equal(expected: 2, actual: tokens.Refreshes);
    }

    [Fact]
    public async Task The_effective_attempt_timeout_is_the_smaller_bound()
    {
        var time = new FakeTimeProvider();

        // <snippet:deadline-effective>
        var api = Resilience.Default with
        {
            Deadline = TimeSpan.FromSeconds(value: 10), // the whole call
            AttemptTimeout = TimeSpan.FromSeconds(value: 3), // one attempt
        };

        // Attempt 1 gets 3 s. An attempt starting with 2 s left on the deadline gets 2 s, not 3 -
        // the effective ceiling is min(AttemptTimeout, time left), so there is no
        // "is that per attempt or total?" question to get wrong.
        // </snippet:deadline-effective>

        Assert.Equal(expected: TimeSpan.FromSeconds(value: 3), actual: api.AttemptTimeout);

        var calls = Sequence.For<int>(time: time).Delays(delay: TimeSpan.FromSeconds(value: 30)).Returns(result: 1);
        var pending = RunAsync(policy: api with { Time = time, Attempts = 1 }, calls: calls);

        time.Advance(delta: TimeSpan.FromSeconds(value: 4));
        var result = await pending;

        Assert.IsType<AttemptTimeoutException>(@object: result.Exception);
        Assert.Equal(expected: StopReason.AttemptsExhausted, actual: result.StopReason);
    }

    [Fact]
    public async Task A_deadline_that_runs_out_throws_a_TimeoutException()
    {
        var time = new FakeTimeProvider();
        var api = Resilience.Default with { Time = time, Deadline = TimeSpan.FromSeconds(value: 5), AttemptTimeout = Timeout.InfiniteTimeSpan };
        var calls = Sequence.For<int>(time: time).Delays(delay: TimeSpan.FromSeconds(value: 30)).Returns(result: 1);

        var pending = RunAsync(policy: api, calls: calls);
        time.Advance(delta: TimeSpan.FromSeconds(value: 6));
        var result = await pending;

        // <snippet:deadline-handle-exception>
        // DeadlineExceededException and AttemptTimeoutException are both TimeoutException, so one
        // catch covers "it did not answer in time" and the two are still distinguishable.
        try
        {
            result.ValueOrThrow();
        }
        catch (DeadlineExceededException deadline)
        {
            Console.WriteLine(value: $"gave up after {deadline.Deadline.TotalSeconds}s and {deadline.Attempts.Count} attempt(s)");
        }
        catch (TimeoutException attempt)
        {
            Console.WriteLine(value: $"one attempt overran: {attempt.Message}");
        }

        // </snippet:deadline-handle-exception>

        Assert.Equal(expected: StopReason.DeadlineExceeded, actual: result.StopReason);
    }

    [Fact]
    public async Task A_classifier_teaches_the_policy_about_your_own_exceptions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = Sequence.For<int>().Throws(exception: new MyDbException()).Returns(result: 1);

        // <snippet:classifier-custom-exception>
        // Classifier.Default does not retry an exception type it has never heard of - retrying a
        // programming error turns a fast, clear failure into a slow, confusing one. Teaching it
        // about yours is one line, and the receiver is unchanged.
        var api = Resilience.Default with
        {
            Classify = Classifier.Default.On<MyDbException>(verdict: Verdict.Transient),
            Backoff = Backoff.None,
        };

        // </snippet:classifier-custom-exception>

        Assert.Equal(expected: 1, actual: await api.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task A_result_rule_classifies_what_a_call_returned()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var calls = Sequence.For<Reply>()
            .Returns(result: new Reply(Code: "BUSY"))
            .Returns(result: new Reply(Code: "OK"));

        // <snippet:classifier-result-rule>
        // Nothing is thrown: the dependency reports failure in its own envelope. A result rule is
        // read by retry, the breaker and the budget alike, because they all read one classifier.
        var api = Resilience.Default with
        {
            Classify = Classifier.Default.OnResult<Reply>(reply => reply.Code switch
            {
                "OK" => Verdict.Ok,
                "BUSY" => Verdict.Throttled(retryAfter: TimeSpan.FromMilliseconds(value: 50)),
                _ => Verdict.Permanent,
            }),
        };

        // </snippet:classifier-result-rule>

        var reply = await api.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken);
        Assert.Equal(expected: "OK", actual: reply.Code);
    }

    [Fact]
    public void A_classifier_prints_its_own_rules()
    {
        // <snippet:classifier-print>
        // "What will this actually retry?" without reading the library's source.
        Console.WriteLine(value: Classifier.Http);

        // </snippet:classifier-print>

        Assert.Contains(expectedSubstring: "HttpResponseMessage", actualString: Classifier.Http.ToString(), comparisonType: StringComparison.Ordinal);

        Assert.Contains(expectedSubstring: "any other exception -> Permanent", actualString: Classifier.Http.ToString(),
            comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void The_http_classifier_reads_status_codes_the_way_they_are_meant()
    {
        // <snippet:classifier-http-table>
        var http = Classifier.Http;

        var throttled = http.ClassifyResult(value: new HttpResponseMessage(statusCode: HttpStatusCode.TooManyRequests)); // Throttled
        var transient = http.ClassifyResult(value: new HttpResponseMessage(statusCode: HttpStatusCode.BadGateway)); // Transient
        var answer = http.ClassifyResult(value: new HttpResponseMessage(statusCode: HttpStatusCode.NotFound)); // Ok - a 404 is an answer

        // </snippet:classifier-http-table>

        Assert.Equal(expected: VerdictKind.Throttled, actual: throttled.Kind);
        Assert.Equal(expected: VerdictKind.Transient, actual: transient.Kind);
        Assert.Equal(expected: VerdictKind.Ok, actual: answer.Kind);
    }

    private static async Task<CallResult<int>> RunAsync(Resilience policy, Sequence<int> calls) =>
        await policy.TryRunAsync(attempt => calls.NextAsync(cancellationToken: attempt));

    internal sealed record Reply(string Code);

    internal sealed class MyDbException : Exception;

    private sealed class TokenSource
    {
        internal int Refreshes { get; private set; }

        internal Task RefreshAsync(CancellationToken cancellationToken)
        {
            Refreshes++;
            return Task.CompletedTask;
        }
    }
}
