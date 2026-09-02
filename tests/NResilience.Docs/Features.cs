using System.Data.Common;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using NResilience.Http;
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
    public async Task The_attempt_ceiling_can_be_measured_instead_of_guessed()
    {
        var time = new FakeTimeProvider();

        // <snippet:deadline-measured-ceiling>
        var api = Resilience.Http with
        {
            AttemptTimeout = TimeSpan.FromSeconds(value: 5), // the ceiling. Never exceeded.
            Timeouts = AttemptTimeouts.Above(multiple: 3), // and usually far below it: 3x the recent p95.
        };

        // The measured term can only lower the ceiling, so AttemptTimeout stops being a guess about how
        // long this dependency takes and becomes what it reads as - the point beyond which you stop
        // caring. A dependency whose p95 is 40 ms gets a 120 ms ceiling; one whose p95 is 2 s gets the
        // configured 5 s, because 3x its p95 is above that and the clamp is what wins.
        // </snippet:deadline-measured-ceiling>

        var policy = api with { Time = time, Attempts = 1, Timeouts = AttemptTimeouts.Above(multiple: 3) with { Window = TimeSpan.FromHours(value: 1) } };

        // Twenty successful calls at 40 ms is what an estimate needs before it bounds anything.
        for (var i = 0; i < 20; i++)
        {
            await policy.RunAsync(_ =>
                {
                    time.Advance(delta: TimeSpan.FromMilliseconds(value: 40));
                    return Task.FromResult(result: 1);
                },
                cancellationToken: CancellationToken.None);
        }

        // Three times the measured p95, and two orders of magnitude under the configured 5 s.
        Assert.InRange(actual: policy.MeasuredAttemptTimeout!.Value, low: TimeSpan.FromMilliseconds(value: 120), high: TimeSpan.FromMilliseconds(value: 135));
    }

    [Fact]
    public async Task An_exact_bound_can_keep_a_floor_under_the_measured_ceiling()
    {
        var time = new FakeTimeProvider();

        // <snippet:deadline-sla-floor>
        // An exact SLA: this call has 10 seconds, full stop. Deadline is that bound, and nothing here
        // lowers or raises it.
        var api = Resilience.Http with
        {
            Deadline = TimeSpan.FromSeconds(value: 10),
            AttemptTimeout = TimeSpan.FromSeconds(value: 5),

            // And this endpoint legitimately takes up to 2 s sometimes, so no attempt may be
            // cancelled before then. Adaptation is confined to [2 s, 5 s]: it can trim the dead time
            // above 2 s and can never cut into the allowance below it.
            Timeouts = AttemptTimeouts.Above(multiple: 3) with { Floor = TimeSpan.FromSeconds(value: 2) },
        };
        // </snippet:deadline-sla-floor>

        var policy = api with { Time = time, Attempts = 1, Timeouts = api.Timeouts!.Value with { Window = TimeSpan.FromHours(value: 1) } };

        // Twenty fast calls, so the raw measurement is about 120 ms - far below the floor.
        for (var i = 0; i < 20; i++)
        {
            await policy.RunAsync(_ =>
                {
                    time.Advance(delta: TimeSpan.FromMilliseconds(value: 40));
                    return Task.FromResult(result: 1);
                },
                cancellationToken: CancellationToken.None);
        }

        // The floor is what the attempt gets, not the measurement.
        Assert.Equal(expected: TimeSpan.FromSeconds(value: 2), actual: policy.MeasuredAttemptTimeout);
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
    public async Task A_call_inherits_the_deadline_its_caller_sent()
    {
        var time = new FakeTimeProvider();

        // <snippet:deadline-inherit>
        // The inbound half. The policy is bounded by the inherited deadline, so its
        // effective deadline is min(Deadline, time the caller is still waiting), resolved once
        // at the start of the call.
        var api = Resilience.Http with { UseAmbientDeadline = true };

        // In an ASP.NET Core app, UseResilienceDeadline() publishes what the caller sent. Anywhere else -
        // a queue consumer reading a deadline off a message, or a test - publish it yourself.
        using var inbound = ResilienceDeadline.Begin(remaining: TimeSpan.FromMilliseconds(value: 200));
        // </snippet:deadline-inherit>

        var calls = Sequence.For<int>(time: time).Delays(delay: TimeSpan.FromSeconds(value: 30)).Returns(result: 1);
        var pending = RunAsync(policy: api with { Time = time, AttemptTimeout = Timeout.InfiniteTimeSpan }, calls: calls);

        time.Advance(delta: TimeSpan.FromSeconds(value: 1));
        var result = await pending;

        // Stopped by the caller's 200 ms rather than by the policy's own 30 s, and reported against the
        // deadline that actually applied.
        Assert.Equal(expected: StopReason.DeadlineExceeded, actual: result.StopReason);

        // At most the 200 ms the caller sent - the inbound deadline is measured against the system clock
        // here, because that is the clock the snippet's Begin defaults to.
        var reported = Assert.IsType<DeadlineExceededException>(@object: result.Exception).Deadline;
        Assert.InRange(actual: reported, low: TimeSpan.FromMilliseconds(value: 100), high: TimeSpan.FromMilliseconds(value: 200));
    }

    [Fact]
    public async Task Each_attempt_tells_the_peer_how_long_it_will_be_waited_for()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new ScriptedHttpHandler().Respond(status: HttpStatusCode.OK);
        var uri = new Uri(uriString: "https://api.example.com/orders");

        // <snippet:deadline-propagate>
        // The outbound half. Every attempt carries the time this side is prepared to wait:
        // min(AttemptTimeout, time left on the deadline). This allows peers to stop
        // work that is no longer needed. Off by default.
        var api = Resilience.Http with
        {
            Deadline = TimeSpan.FromSeconds(value: 10),
            AttemptTimeout = TimeSpan.FromSeconds(value: 3),
        };

        var options = new HttpResilienceOptions { PropagateDeadline = true };

        using var client = new HttpClient(handler: new ResilienceHandler(innerHandler: transport, policy: api, options: options));
        using var response = await client.GetAsync(requestUri: uri, cancellationToken: cancellationToken);

        // X-Deadline-Ms: 3000 on the first attempt, and less on every attempt after it.
        // </snippet:deadline-propagate>

        Assert.True(condition: transport.Requests[index: 0].Headers.TryGetValues(name: ResilienceDeadline.Header, out var sent));
        Assert.Equal(expected: "3000", actual: sent.Single());
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
    public async Task A_database_failure_is_classified_by_the_provider_that_raised_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var calls = Sequence.For<int>().Throws(exception: new TransientDbException()).Returns(result: 1);

        // <snippet:classifier-data>
        // Classifier.Data reads DbException.IsTransient, which maintained ADO.NET providers
        // implement. This avoids using a driver package or a manual table of error numbers.
        // Providers that do not implement this property report false, making Classifier.Data
        // equivalent to Classifier.Default.
        var db = Resilience.Default with
        {
            Classify = Classifier.Data,
            Backoff = Backoff.Constant(delay: TimeSpan.FromMilliseconds(value: 50)),
        };

        // </snippet:classifier-data>

        Assert.Equal(expected: 1, actual: await db.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt), cancellationToken: cancellationToken));
    }

    [Fact]
    public void A_resource_limit_can_be_called_throttling_with_one_rule_of_your_own()
    {
        // <snippet:classifier-data-throttled>
        // Providers cannot distinguish between a dependency failing and one defending itself.
        // For example, Azure SQL reports resource limits as 10928 and 10929. Both are
        // throttling: they use a long backoff curve and do not count as evidence against the
        // dependency's health.
        var classify = Classifier.Data.On<SqlLikeException>(e => e.Number is 10928 or 10929
            ? Verdict.Throttled()
            : Classifier.Data.ClassifyException(exception: e));

        var db = Resilience.Default with { Classify = classify };

        // </snippet:classifier-data-throttled>

        Assert.Equal(expected: VerdictKind.Throttled, actual: db.Classify.ClassifyException(exception: new SqlLikeException(number: 10928)).Kind);
        Assert.Equal(expected: VerdictKind.Transient, actual: db.Classify.ClassifyException(exception: new SqlLikeException(number: 4060)).Kind);
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

    /// <summary>Reports itself transient, the way a maintained ADO.NET provider does.</summary>
    internal sealed class TransientDbException() : DbException(message: "the connection reset")
    {
        public override bool IsTransient => true;
    }

    /// <summary>Stands in for SqlException, whose Number is what tells a resource limit from a fault.</summary>
    internal sealed class SqlLikeException(int number) : DbException(message: $"error {number}")
    {
        public override bool IsTransient => true;

        public int Number { get; } = number;
    }

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
