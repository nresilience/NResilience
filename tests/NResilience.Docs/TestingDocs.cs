using System.Net;
using Microsoft.Extensions.Time.Testing;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>NResilience.Testing: scripted callbacks, recorded events, and a clock you control.</summary>
public sealed class TestingDocs
{
    [Fact]
    public async Task A_scripted_callback_is_the_double()
    {
        // <snippet:testing-sequence>
        var calls = Sequence.For<HttpResponseMessage>()
            .Returns(result: new HttpResponseMessage(statusCode: HttpStatusCode.ServiceUnavailable), count: 2)
            .Returns(result: new HttpResponseMessage(statusCode: HttpStatusCode.OK));

        var policy = Resilience.Http with { Backoff = Backoff.None };

        var result = await policy.TryRunAsync(attempt => calls.NextAsync(cancellationToken: attempt));

        Assert.True(condition: result.IsSuccess);
        Assert.Equal(expected: 3, actual: calls.CallCount);
        Assert.Equal(expected: 3, actual: result.Attempts.Count);

        // </snippet:testing-sequence>
    }

    [Fact]
    public async Task Time_is_a_parameter()
    {
        // <snippet:testing-fake-time>
        // Pass the same clock to the policy and to the script, or a scripted delay is a real
        // sleep - and a real sleep is what makes timing tests slow and flaky.
        var time = new FakeTimeProvider();

        var calls = Sequence.For<int>(time: time)
            .Delays(delay: TimeSpan.FromSeconds(value: 30)) // longer than the attempt timeout
            .Returns(result: 1);

        var policy = Resilience.Default with
        {
            Time = time,
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromSeconds(value: 3),
        };

        var pending = policy.TryRunAsync(attempt => calls.NextAsync(cancellationToken: attempt)).AsTask();
        time.Advance(delta: TimeSpan.FromSeconds(value: 4));

        var result = await pending;

        Assert.IsType<AttemptTimeoutException>(@object: result.Exception);

        // </snippet:testing-fake-time>
    }

    [Fact]
    public async Task One_clock_drives_the_guards_the_library_builds()
    {
        var down = true;

        var transport = new Doubles.ScriptedTransport(() =>
            new HttpResponseMessage(statusCode: down ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

        // <snippet:testing-library-clock>
        // The per-host breaker is built by the handler, so it runs on the policy's clock rather
        // than on wall time - which is the only reason a break duration can be waited out in a
        // test without actually waiting.
        var time = new FakeTimeProvider();

        using var handler = new ResilienceHandler(
            innerHandler: transport,
            policy: Resilience.Http with
            {
                Time = time,
                Attempts = 1,
                Backoff = Backoff.None,
                AttemptTimeout = Timeout.InfiniteTimeSpan,
                Deadline = Timeout.InfiniteTimeSpan,
            },
            options: new HttpResilienceOptions
            {
                BreakerSettings = new BreakerSettings { ConsecutiveFailures = 2, BreakDuration = TimeSpan.FromSeconds(value: 15) },
            });

        using var client = new HttpClient(handler: handler);

        for (var i = 0; i < 2; i++)
        {
            (await client.GetAsync(requestUri: "https://api.example.com/orders")).Dispose();
        }

        Assert.Equal(expected: BreakerState.Open, actual: handler.BreakersByHost()[key: "api.example.com"].State);

        down = false;
        time.Advance(delta: TimeSpan.FromSeconds(value: 16)); // the break expires on the fake clock

        using var response = await client.GetAsync(requestUri: "https://api.example.com/orders");
        Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);

        // </snippet:testing-library-clock>
    }

    [Fact]
    public async Task Assert_on_the_whole_event_sequence()
    {
        // <snippet:testing-event-recorder>
        var events = new EventRecorder();
        var calls = Sequence.For<int>().Throws(exception: new IOException()).Returns(result: 42);

        var policy = Resilience.Default with { Backoff = Backoff.None, OnEvent = events.Record };

        await policy.RunAsync(attempt => calls.NextAsync(cancellationToken: attempt));

        // Assert on the order, not just the membership: if a telemetry surface raises the right
        // events in the wrong order, the log it produces is misleading even though every event
        // is present.
        Assert.Equal(
            expected: [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded],
            actual: events.Kinds);

        Assert.Equal(expected: VerdictKind.Transient, actual: events.OfKind(kind: CallEventKind.Attempt)[index: 0].Verdict.Kind);
        Assert.Equal(expected: 42, actual: events.Single(kind: CallEventKind.Succeeded).Result);

        // </snippet:testing-event-recorder>
    }

    [Fact]
    public async Task The_handler_is_testable_without_a_container()
    {
        // <snippet:testing-http-handler>
        var transport = new ScriptedTransport(
            () => new HttpResponseMessage(statusCode: HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(statusCode: HttpStatusCode.OK));

        using var client = ResilienceHttp.CreateClient(
            policy: Resilience.Http with { Backoff = Backoff.None },
            innerHandler: transport);

        using var response = await client.GetAsync(requestUri: new Uri(uriString: "https://api.example.com/orders/1"));

        Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
        Assert.Equal(expected: 2, actual: transport.Requests.Count);

        // </snippet:testing-http-handler>
    }

    /// <summary>The transport double the handler test above stands on.</summary>
    private sealed class ScriptedTransport(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _served;

        internal List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(item: request);
            return Task.FromResult(result: responses[Math.Min(val1: _served++, val2: responses.Length - 1)]());
        }
    }
}
