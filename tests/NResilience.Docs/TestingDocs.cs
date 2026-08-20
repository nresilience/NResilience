using System.Net;
using System.Net.Http;
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
        Sequence<HttpResponseMessage> calls = Sequence.For<HttpResponseMessage>()
            .Returns(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), count: 2)
            .Returns(new HttpResponseMessage(HttpStatusCode.OK));

        var policy = Resilience.Http with { Backoff = Backoff.None };

        CallResult<HttpResponseMessage> result = await policy.TryRunAsync(attempt => calls.NextAsync(attempt));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, calls.CallCount);
        Assert.Equal(3, result.Attempts.Count);
        // </snippet:testing-sequence>
    }

    [Fact]
    public async Task Time_is_a_parameter()
    {
        // <snippet:testing-fake-time>
        // Pass the same clock to the policy and to the script, or a scripted delay is a real
        // sleep - and a real sleep is what makes timing tests slow and flaky.
        var time = new FakeTimeProvider();

        Sequence<int> calls = Sequence.For<int>(time)
            .Delays(TimeSpan.FromSeconds(30))   // longer than the attempt timeout
            .Returns(1);

        var policy = Resilience.Default with
        {
            Time = time,
            Attempts = 1,
            AttemptTimeout = TimeSpan.FromSeconds(3),
        };

        Task<CallResult<int>> pending = policy.TryRunAsync(attempt => calls.NextAsync(attempt)).AsTask();
        time.Advance(TimeSpan.FromSeconds(4));

        CallResult<int> result = await pending;

        Assert.IsType<AttemptTimeoutException>(result.Exception);
        // </snippet:testing-fake-time>
    }

    [Fact]
    public async Task Assert_on_the_whole_event_sequence()
    {
        // <snippet:testing-event-recorder>
        var events = new EventRecorder();
        Sequence<int> calls = Sequence.For<int>().Throws(new IOException()).Returns(42);

        var policy = Resilience.Default with { Backoff = Backoff.None, OnEvent = events.Record };

        await policy.RunAsync(attempt => calls.NextAsync(attempt));

        // Assert on the order, not just the membership: if a telemetry surface raises the right
        // events in the wrong order, the log it produces is misleading even though every event
        // is present.
        Assert.Equal(
            [CallEventKind.Attempt, CallEventKind.Retrying, CallEventKind.Attempt, CallEventKind.Succeeded],
            events.Kinds);

        Assert.Equal(VerdictKind.Transient, events.OfKind(CallEventKind.Attempt)[0].Verdict.Kind);
        Assert.Equal(42, events.Single(CallEventKind.Succeeded).Result);
        // </snippet:testing-event-recorder>
    }

    [Fact]
    public async Task The_handler_is_testable_without_a_container()
    {
        // <snippet:testing-http-handler>
        var transport = new ScriptedTransport(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = ResilienceHttp.CreateClient(
            Resilience.Http with { Backoff = Backoff.None },
            innerHandler: transport);

        using HttpResponseMessage response = await client.GetAsync(new Uri("https://api.example.com/orders/1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, transport.Requests.Count);
        // </snippet:testing-http-handler>
    }

    /// <summary>The transport double the handler test above stands on.</summary>
    private sealed class ScriptedTransport(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _served;

        internal List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responses[Math.Min(_served++, responses.Length - 1)]());
        }
    }
}
