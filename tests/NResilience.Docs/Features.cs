using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>Retry, the two bounds, classification and backoff.</summary>
public sealed class Features
{
    [Fact]
    public async Task Attempts_is_the_total()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Sequence<int> calls = Sequence.For<int>().Throws(new IOException(), 2).Returns(42);

        // <snippet:retry-attempts>
        // Three attempts: try, retry, retry. Not "one call plus three retries".
        var api = Resilience.Default with { Attempts = 3 };

        int value = await api.RunAsync(ct => calls.NextAsync(ct), cancellationToken);
        // </snippet:retry-attempts>

        Assert.Equal(42, value);
        Assert.Equal(3, calls.CallCount);
    }

    [Fact]
    public void Backoff_is_tuned_by_replacing_it()
    {
        // <snippet:retry-backoff-tuning>
        var api = Resilience.Http with
        {
            Backoff = Backoff.Exponential(
                transientBase: TimeSpan.FromMilliseconds(200),   // the first delay after a transient failure
                throttledBase: TimeSpan.FromSeconds(2),          // the first delay after being throttled
                factor: 2,                                       // doubling
                max: TimeSpan.FromSeconds(10)),                  // the cap on any single delay
        };
        // </snippet:retry-backoff-tuning>

        Assert.Equal(TimeSpan.FromSeconds(10), api.Backoff.Max);
        Assert.Equal(Jitter.Full, api.Backoff.Jitter);
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

        Assert.Equal(Jitter.None, deterministic.Backoff.Jitter);
    }

    [Fact]
    public async Task Backoff_can_be_computed()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Sequence<int> calls = Sequence.For<int>().Throws(new IOException()).Returns(7);

        // <snippet:retry-custom-backoff>
        var api = Resilience.Default with
        {
            Backoff = Backoff.Custom(next => next.PreviousVerdict.Kind == VerdictKind.Throttled
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromMilliseconds(50 * next.Number)),
        };
        // </snippet:retry-custom-backoff>

        Assert.Equal(7, await api.RunAsync(ct => calls.NextAsync(ct), cancellationToken));
    }

    [Fact]
    public async Task Work_that_has_to_be_rebuilt_goes_in_BeforeAttempt()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var tokens = new TokenSource();
        Sequence<int> calls = Sequence.For<int>().Throws(new IOException()).Returns(1);

        // <snippet:retry-before-attempt>
        var api = Resilience.Http with
        {
            // Runs before every attempt, including the first. The place to refresh a token or
            // rebuild a request, because a retry re-invokes the callback from the top.
            BeforeAttempt = next => tokens.RefreshAsync(next.CancellationToken),
        };
        // </snippet:retry-before-attempt>

        await api.RunAsync(ct => calls.NextAsync(ct), cancellationToken);
        Assert.Equal(2, tokens.Refreshes);
    }

    [Fact]
    public async Task The_effective_attempt_timeout_is_the_smaller_bound()
    {
        var time = new FakeTimeProvider();

        // <snippet:deadline-effective>
        var api = Resilience.Default with
        {
            Deadline = TimeSpan.FromSeconds(10),        // the whole call
            AttemptTimeout = TimeSpan.FromSeconds(3),   // one attempt
        };

        // Attempt 1 gets 3 s. An attempt starting with 2 s left on the deadline gets 2 s, not 3 -
        // the effective ceiling is min(AttemptTimeout, time left), so there is no
        // "is that per attempt or total?" question to get wrong.
        // </snippet:deadline-effective>

        Assert.Equal(TimeSpan.FromSeconds(3), api.AttemptTimeout);

        Sequence<int> calls = Sequence.For<int>(time).Delays(TimeSpan.FromSeconds(30)).Returns(1);
        Task<CallResult<int>> pending = RunAsync(api with { Time = time, Attempts = 1 }, calls);

        time.Advance(TimeSpan.FromSeconds(4));
        CallResult<int> result = await pending;

        Assert.IsType<AttemptTimeoutException>(result.Exception);
        Assert.Equal(StopReason.AttemptsExhausted, result.StopReason);
    }

    [Fact]
    public async Task A_deadline_that_runs_out_throws_a_TimeoutException()
    {
        var time = new FakeTimeProvider();
        var api = Resilience.Default with { Time = time, Deadline = TimeSpan.FromSeconds(5), AttemptTimeout = Timeout.InfiniteTimeSpan };
        Sequence<int> calls = Sequence.For<int>(time).Delays(TimeSpan.FromSeconds(30)).Returns(1);

        Task<CallResult<int>> pending = RunAsync(api, calls);
        time.Advance(TimeSpan.FromSeconds(6));
        CallResult<int> result = await pending;

        // <snippet:deadline-handle-exception>
        // DeadlineExceededException and AttemptTimeoutException are both TimeoutException, so one
        // catch covers "it did not answer in time" and the two are still distinguishable.
        try
        {
            result.ValueOrThrow();
        }
        catch (DeadlineExceededException deadline)
        {
            Console.WriteLine($"gave up after {deadline.Deadline.TotalSeconds}s and {deadline.Attempts.Count} attempt(s)");
        }
        catch (TimeoutException attempt)
        {
            Console.WriteLine($"one attempt overran: {attempt.Message}");
        }
        // </snippet:deadline-handle-exception>

        Assert.Equal(StopReason.DeadlineExceeded, result.StopReason);
    }

    [Fact]
    public async Task A_classifier_teaches_the_policy_about_your_own_exceptions()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Sequence<int> calls = Sequence.For<int>().Throws(new MyDbException()).Returns(1);

        // <snippet:classifier-custom-exception>
        // Classifier.Default does not retry an exception type it has never heard of - retrying a
        // programming error turns a fast, clear failure into a slow, confusing one. Teaching it
        // about yours is one line, and the receiver is unchanged.
        var api = Resilience.Default with
        {
            Classify = Classifier.Default.On<MyDbException>(Verdict.Transient),
            Backoff = Backoff.None,
        };
        // </snippet:classifier-custom-exception>

        Assert.Equal(1, await api.RunAsync(ct => calls.NextAsync(ct), cancellationToken));
    }

    [Fact]
    public async Task A_result_rule_classifies_what_a_call_returned()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Sequence<Reply> calls = Sequence.For<Reply>()
            .Returns(new Reply("BUSY"))
            .Returns(new Reply("OK"));

        // <snippet:classifier-result-rule>
        // Nothing is thrown: the dependency reports failure in its own envelope. A result rule is
        // read by retry, the breaker and the budget alike, because they all read one classifier.
        var api = Resilience.Default with
        {
            Classify = Classifier.Default.OnResult<Reply>(reply => reply.Code switch
            {
                "OK" => Verdict.Ok,
                "BUSY" => Verdict.Throttled(TimeSpan.FromMilliseconds(50)),
                _ => Verdict.Permanent,
            }),
        };
        // </snippet:classifier-result-rule>

        Reply reply = await api.RunAsync(ct => calls.NextAsync(ct), cancellationToken);
        Assert.Equal("OK", reply.Code);
    }

    [Fact]
    public void A_classifier_prints_its_own_rules()
    {
        // <snippet:classifier-print>
        // "What will this actually retry?" without reading the library's source.
        Console.WriteLine(Classifier.Http);
        // </snippet:classifier-print>

        Assert.Contains("HttpResponseMessage", Classifier.Http.ToString(), StringComparison.Ordinal);
        Assert.Contains("any other exception -> Permanent", Classifier.Http.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_http_classifier_reads_status_codes_the_way_they_are_meant()
    {
        // <snippet:classifier-http-table>
        Classifier http = Classifier.Http;

        Verdict throttled = http.ClassifyResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));  // Throttled
        Verdict transient = http.ClassifyResult(new HttpResponseMessage(HttpStatusCode.BadGateway));       // Transient
        Verdict answer = http.ClassifyResult(new HttpResponseMessage(HttpStatusCode.NotFound));            // Ok - a 404 is an answer
        // </snippet:classifier-http-table>

        Assert.Equal(VerdictKind.Throttled, throttled.Kind);
        Assert.Equal(VerdictKind.Transient, transient.Kind);
        Assert.Equal(VerdictKind.Ok, answer.Kind);
    }

    private static async Task<CallResult<int>> RunAsync(Resilience policy, Sequence<int> calls) =>
        await policy.TryRunAsync(ct => calls.NextAsync(ct));

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
