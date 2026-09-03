using System.Net;
using Microsoft.Extensions.DependencyInjection;
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

        var transport = new ScriptedHttpHandler()
            .Respond(() => new HttpResponseMessage(statusCode: down ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));

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
    public void A_listener_is_testable_without_the_executor()
    {
        // <snippet:testing-call-event-create>
        // The listener under test counts the two refusal kinds separately, as "the dependency
        // is down" and "we are retrying too hard" require opposite responses.
        var unavailable = 0;
        var overRetried = 0;
 
        void Listener(CallEvent e)
        {
            if (e.Kind == CallEventKind.RejectedByBreaker)
            {
                unavailable++;
            }
            else if (e.Kind == CallEventKind.RejectedByBudget)
            {
                overRetried++;
            }
        }
 
        // CallEvent.Create builds the event the executor would raise.
        Listener(CallEvent.Create(kind: CallEventKind.RejectedByBreaker, policyName: "orders", reason: StopReason.DependencyUnavailable));
        Listener(CallEvent.Create(kind: CallEventKind.RejectedByBudget, policyName: "orders", reason: StopReason.BudgetExhausted));
        Listener(CallEvent.Create(kind: CallEventKind.Succeeded, policyName: "orders", reason: StopReason.Succeeded));
 
        Assert.Equal(expected: 1, actual: unavailable);
        Assert.Equal(expected: 1, actual: overRetried);
 
        // </snippet:testing-call-event-create>
    }

    [Fact]
    public async Task The_handler_is_testable_without_a_container()
    {
        // <snippet:testing-http-handler>
        var transport = new ScriptedHttpHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.OK);

        using var client = HttpResilience.CreateClient(
            policy: Resilience.Http with { Backoff = Backoff.None },
            innerHandler: transport);

        using var response = await client.GetAsync(requestUri: new Uri(uriString: "https://api.example.com/orders/1"));

        Assert.Equal(expected: HttpStatusCode.OK, actual: response.StatusCode);
        Assert.Equal(expected: 2, actual: transport.CallCount);

        // </snippet:testing-http-handler>
    }

    [Fact]
    public async Task Chaos_injects_faults_the_policy_then_handles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var orders = new OrderService();

        var policy = Resilience.Default with { Attempts = 3, Backoff = Backoff.None, Budget = null };

        // <snippet:chaos-callback>
        // One call in ten fails and one in five is slow. Chaos wraps the callback rather than the
        // policy, so an injected fault is classified, retried, counted against the breaker and
        // written to the attempt log exactly like a real one.
        var chaos = new Chaos
        {
            Enabled = true,
            FaultRate = 0.1,
            LatencyRate = 0.2,
            Latency = TimeSpan.FromSeconds(value: 2),
        };

        var result = await policy.TryRunAsync(
            work: chaos.Inject(work: attempt => orders.FetchAsync(cancellationToken: attempt)),
            cancellationToken: cancellationToken);

        // </snippet:chaos-callback>

        Assert.True(condition: result.IsSuccess || result.Attempts.Count > 0);
    }

    [Fact]
    public async Task Chaos_can_be_pinned_to_a_seed_and_a_gate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var orders = new OrderService();
        var tenant = "acme";

        var policy = Resilience.Default with { Attempts = 3, Backoff = Backoff.None, Budget = null };

        // <snippet:chaos-deterministic>
        // Seed fixes the random stream, so a test that asserts how many calls were injected is
        // repeatable. Gate narrows the blast radius past anything a rate can express - one tenant,
        // one region, one shard - and is asked before the dice are rolled.
        var chaos = new Chaos
        {
            Enabled = true,
            FaultRate = 1.0,
            Seed = 1234,
            Gate = () => tenant == "acme",
        };

        var failed = await policy.TryRunAsync(
            work: chaos.Inject(work: attempt => orders.FetchAsync(cancellationToken: attempt)),
            cancellationToken: cancellationToken);

        Assert.False(condition: failed.IsSuccess);
        Assert.IsType<IOException>(@object: failed.Exception);

        // </snippet:chaos-deterministic>
    }

    [Fact]
    public void Chaos_injects_into_an_http_pipeline()
    {
        var services = new ServiceCollection();

        // <snippet:chaos-http>
        // Add this after AddResilience() to make it inner to the resilience handler. Adding it
        // before would inject faults outside the policy, so the policy would not retry them.
        services.AddHttpClient(name: "orders")
            .AddResilience()
            .AddHttpMessageHandler(() => new ChaosHandler(
                chaos: new Chaos { Enabled = true, FaultRate = 0.05 },
                response: () => new HttpResponseMessage(statusCode: HttpStatusCode.ServiceUnavailable)));

        // </snippet:chaos-http>

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(@object: provider.GetRequiredService<IHttpClientFactory>().CreateClient(name: "orders"));
    }

    /// <summary>A dependency that always answers, so only the injected failures are visible.</summary>
    private sealed class OrderService
    {
        internal Task<int> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(result: 1);
    }
}
