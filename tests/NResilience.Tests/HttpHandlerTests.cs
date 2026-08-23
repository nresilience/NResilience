using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
/// The HTTP handler: what it retries, what it clones, what it scopes per host, and what it
/// refuses to send twice.
/// </summary>
public sealed class HttpHandlerTests
{
    /// <summary>A policy that retries without sleeping and imposes no clock-dependent bound.</summary>
    private static Resilience Instant => Resilience.Http with
    {
        Backoff = Backoff.None,
        AttemptTimeout = Timeout.InfiniteTimeSpan,
        Deadline = Timeout.InfiniteTimeSpan,
    };

    [Fact]
    public async Task A_transient_status_is_retried_to_success()
    {
        var transport = new ScriptedTransport(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = Client(transport);
        using HttpResponseMessage response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task A_404_is_an_answer_rather_than_a_failure()
    {
        var transport = new ScriptedTransport(() => new HttpResponseMessage(HttpStatusCode.NotFound));

        using HttpClient client = Client(transport);
        using HttpResponseMessage response = await client.GetAsync(new Uri("https://api.test/missing"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task The_last_response_is_returned_when_the_attempts_run_out()
    {
        var transport = new ScriptedTransport(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using HttpClient client = Client(transport);
        using HttpResponseMessage response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, transport.Requests.Count);
    }

    [Fact]
    public async Task Each_attempt_gets_its_own_request()
    {
        var transport = new ScriptedTransport(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = Client(transport);
        using HttpResponseMessage response = await client.GetAsync(new Uri("https://api.test/thing"));

        // Re-sending one HttpRequestMessage throws "The request message was already sent"; the
        // point of cloning is that these are two distinct objects.
        Assert.NotSame(transport.Requests[0], transport.Requests[1]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_clone_carries_the_headers_the_body_and_the_uri()
    {
        var transport = new ScriptedTransport(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = Client(transport);

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri("https://api.test/thing?q=1"))
        {
            Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Trace", "abc");

        using HttpResponseMessage response = await client.SendAsync(request);

        HttpRequestMessage second = transport.Requests[1];
        Assert.Equal(HttpMethod.Put, second.Method);
        Assert.Equal(new Uri("https://api.test/thing?q=1"), second.RequestUri);
        Assert.Equal("abc", second.Headers.GetValues("X-Trace").Single());
        Assert.Equal("application/json", second.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("{\"a\":1}", transport.Bodies[1]);
        Assert.Equal(transport.Bodies[0], transport.Bodies[1]);
    }

    [Fact]
    public async Task A_POST_is_not_retried()
    {
        var transport = new ScriptedTransport(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using HttpClient client = Client(transport);
        using HttpResponseMessage response = await client.PostAsync(new Uri("https://api.test/orders"), new StringContent("order"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task A_POST_is_retried_when_the_client_opts_in()
    {
        var transport = new ScriptedTransport(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = Client(transport, new HttpResilienceOptions { RetryUnsafeMethods = true });
        using HttpResponseMessage response = await client.PostAsync(new Uri("https://api.test/orders"), new StringContent("order"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal("order", transport.Bodies[1]);
    }

    [Fact]
    public async Task A_POST_is_retried_when_the_request_declares_itself_repeatable()
    {
        var transport = new ScriptedTransport(
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = Client(transport);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.test/orders"))
        {
            Content = new StringContent("order"),
        };
        request.Options.Set(ResilienceHttp.Repeatable, true);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task A_request_declared_not_repeatable_is_not_retried_even_when_its_method_is_safe()
    {
        var transport = new ScriptedTransport(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        using HttpClient client = Client(transport, new HttpResilienceOptions { RetryUnsafeMethods = true });

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));
        request.Options.Set(ResilienceHttp.Repeatable, false);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("PUT", true)]
    [InlineData("DELETE", true)]
    [InlineData("OPTIONS", true)]
    [InlineData("TRACE", true)]
    [InlineData("POST", false)]
    [InlineData("PATCH", false)]
    [InlineData("MADEUP", false)]
    public void The_idempotent_methods_are_the_retryable_ones(string method, bool retried)
    {
        using var handler = new ResilienceHandler(new ScriptedTransport(), Instant);
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri("https://api.test/thing"));

        Assert.Equal(retried, handler.WillRetry(request));
    }

    [Fact]
    public void A_single_attempt_policy_retries_nothing_whatever_the_method_says()
    {
        using var handler = new ResilienceHandler(new ScriptedTransport(), Instant with { Attempts = 1 });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));

        Assert.False(handler.WillRetry(request));
    }

    [Fact]
    public async Task A_superseded_response_is_disposed()
    {
        var first = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new TrackedContent() };
        var second = new HttpResponseMessage(HttpStatusCode.OK) { Content = new TrackedContent() };
        var transport = new ScriptedTransport(() => first, () => second);

        using HttpClient client = Client(transport);
        using HttpResponseMessage response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.True(((TrackedContent)first.Content).Disposed);
        Assert.False(((TrackedContent)second.Content!).Disposed);
    }

    [Fact]
    public async Task Every_host_gets_its_own_breaker_and_budget()
    {
        var transport = new ScriptedTransport(() => new HttpResponseMessage(HttpStatusCode.OK));
        using var handler = new ResilienceHandler(transport, Instant);
        using var client = new HttpClient(handler);

        (await client.GetAsync(new Uri("https://one.test/a"))).Dispose();
        (await client.GetAsync(new Uri("https://two.test/a"))).Dispose();
        (await client.GetAsync(new Uri("https://one.test/b"))).Dispose();

        IReadOnlyDictionary<string, Breaker> breakers = handler.BreakersByHost();
        Assert.Equal(2, breakers.Count);
        Assert.NotSame(breakers["one.test"], breakers["two.test"]);
        Assert.Equal(2, handler.BudgetsByHost().Count);
    }

    [Fact]
    public async Task A_dead_host_does_not_break_a_healthy_one()
    {
        var transport = new ScriptedTransport(request =>
            request.RequestUri!.Host == "dead.test"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK));

        var policy = Instant with
        {
            Attempts = 1,
            Breaker = null,
        };

        using var handler = new ResilienceHandler(
            transport,
            policy,
            new HttpResilienceOptions { BreakerSettings = new BreakerSettings { ConsecutiveFailures = 2 } });
        using var client = new HttpClient(handler);

        for (int i = 0; i < 2; i++)
        {
            (await client.GetAsync(new Uri("https://dead.test/a"))).Dispose();
        }

        Assert.Equal(BreakerState.Open, handler.BreakersByHost()["dead.test"].State);

        // An open breaker refuses the call rather than making it, and says so.
        await Assert.ThrowsAsync<CallRejectedException>(async () =>
            await client.GetAsync(new Uri("https://dead.test/a")));

        using HttpResponseMessage healthy = await client.GetAsync(new Uri("https://healthy.test/a"));
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    [Fact]
    public async Task An_explicit_breaker_survives_per_host_scoping()
    {
        var shared = new Breaker { Name = "shared" };
        var transport = new ScriptedTransport(() => new HttpResponseMessage(HttpStatusCode.OK));

        using var handler = new ResilienceHandler(transport, Instant with { Breaker = shared });
        using var client = new HttpClient(handler);

        (await client.GetAsync(new Uri("https://one.test/a"))).Dispose();
        (await client.GetAsync(new Uri("https://two.test/a"))).Dispose();

        IReadOnlyDictionary<string, Breaker> breakers = handler.BreakersByHost();
        Assert.Same(shared, breakers["one.test"]);
        Assert.Same(shared, breakers["two.test"]);
    }

    [Fact]
    public async Task A_retrying_client_stamps_the_nested_retry_header()
    {
        var transport = new ScriptedTransport(() => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = Client(transport);
        (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.True(transport.Requests[0].Headers.Contains(ResilienceHttp.NestedRetryHeader));
    }

    [Fact]
    public async Task A_client_that_cannot_retry_stamps_nothing()
    {
        var transport = new ScriptedTransport(() => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = Client(transport, policy: Instant with { Attempts = 1 });
        (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.False(transport.Requests[0].Headers.Contains(ResilienceHttp.NestedRetryHeader));
    }

    [Fact]
    public async Task An_inbound_stamp_makes_the_retry_a_reported_nested_one()
    {
        var recorder = new EventRecorder();
        var transport = new ScriptedTransport(() => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = Client(transport, policy: Instant with { OnEvent = recorder.Record });

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));
        request.Headers.Add(ResilienceHttp.NestedRetryHeader, "1");

        (await client.SendAsync(request)).Dispose();

        Assert.True(recorder.Contains(CallEventKind.NestedRetry));
    }

    [Fact]
    public async Task One_retrying_client_inside_another_is_a_reported_nested_retry()
    {
        var recorder = new EventRecorder();

        var inner = new ScriptedTransport(() => new HttpResponseMessage(HttpStatusCode.OK));
        using HttpClient innerClient = Client(inner, policy: Instant with { OnEvent = recorder.Record });

        // The outer client's transport is the inner client: exactly the shape a service that calls
        // another service has, and the one whose amplification nothing in .NET reports.
        var outerTransport = new ScriptedTransport(async (request, ct) =>
            await innerClient.GetAsync(new Uri("https://downstream.test/a"), ct).ConfigureAwait(false));

        using HttpClient outerClient = Client(outerTransport);
        (await outerClient.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.True(recorder.Contains(CallEventKind.NestedRetry));
    }

    [Fact]
    public async Task Nesting_detection_can_be_turned_off()
    {
        var recorder = new EventRecorder();
        var transport = new ScriptedTransport(() => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = Client(
            transport,
            new HttpResilienceOptions { DetectNestedRetries = false },
            Instant with { OnEvent = recorder.Record });

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));
        request.Headers.Add(ResilienceHttp.NestedRetryHeader, "1");

        (await client.SendAsync(request)).Dispose();

        Assert.False(recorder.Contains(CallEventKind.NestedRetry));
    }

    [Fact]
    public async Task Nesting_is_reported_on_every_call_not_just_the_first()
    {
        // The AsyncLocal the handler uses to carry nesting state across the call must be restored
        // to its previous value in the finally block, so a second call through the same handler
        // is still detected as nested when it runs inside an outer retrying client.
        var recorder = new EventRecorder();
        var inner = new ScriptedTransport(() => new HttpResponseMessage(HttpStatusCode.OK));
        using HttpClient innerClient = Client(inner, policy: Instant with { OnEvent = recorder.Record });

        var outerTransport = new ScriptedTransport(async (request, ct) =>
            await innerClient.GetAsync(new Uri("https://downstream.test/a"), ct).ConfigureAwait(false));

        using HttpClient outerClient = Client(outerTransport);

        // Two sequential calls: each one runs the inner client inside the outer, and each must
        // report nesting. A finally that cleared the flag unconditionally would leave the second
        // call unable to detect that it is running inside a retrying context.
        (await outerClient.GetAsync(new Uri("https://api.test/first"))).Dispose();
        Assert.True(recorder.Contains(CallEventKind.NestedRetry),
            "the inner client should report nesting on the first call");

        recorder.Clear();

        (await outerClient.GetAsync(new Uri("https://api.test/second"))).Dispose();
        Assert.True(recorder.Contains(CallEventKind.NestedRetry),
            "the inner client should still report nesting on the second call");
    }

    [Fact]
    public void CreateClient_takes_ownership_of_the_transport_timeout()
    {
        using HttpClient owned = ResilienceHttp.CreateClient(Instant, innerHandler: new ScriptedTransport());
        Assert.Equal(Timeout.InfiniteTimeSpan, owned.Timeout);

        using HttpClient borrowed = ResilienceHttp.CreateClient(
            Instant,
            new HttpResilienceOptions { OwnTransportTimeout = false },
            new ScriptedTransport());
        Assert.NotEqual(Timeout.InfiniteTimeSpan, borrowed.Timeout);
    }

    [Fact]
    public async Task Retry_After_on_a_429_beats_the_backoff_curve()
    {
        var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));

        var transport = new ScriptedTransport(() => throttled, () => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = Client(transport, policy: Instant with { Backoff = Backoff.Constant(TimeSpan.FromMinutes(5)) });
        using HttpResponseMessage response = await client.GetAsync(new Uri("https://api.test/thing"));

        // A five-minute backoff would have made this test time out; the server's own hint is the
        // one that was served.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Events_carry_the_host_scoped_policy_name()
    {
        var recorder = new EventRecorder();
        var transport = new ScriptedTransport(() => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = Client(transport, policy: Instant with { OnEvent = recorder.Record });
        (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.Equal("http:api.test", recorder[0].PolicyName);
    }

    [Fact]
    public async Task A_cancelled_call_disposes_the_response_nobody_receives()
    {
        var tracked = new TrackedContent();
        using var cancellation = new CancellationTokenSource();

        var transport = new ScriptedTransport(
        [
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = tracked }),
            Task<HttpResponseMessage> (_, _) =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            },
        ]);

        using HttpClient client = Client(transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.GetAsync(new Uri("https://api.test/thing"), cancellation.Token));

        Assert.True(tracked.Disposed);
    }

    [Fact]
    public void The_synchronous_send_is_refused()
    {
        using var handler = new ResilienceHandler(new ScriptedTransport(), Instant);
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));

        Assert.Throws<NotSupportedException>(() => client.Send(request));
    }

    private static HttpClient Client(
        ScriptedTransport transport,
        HttpResilienceOptions? options = null,
        Resilience? policy = null) =>
        new(new ResilienceHandler(transport, policy ?? Instant, options));

    /// <summary>An inner handler that serves a script, and keeps every request it was sent.</summary>
    private sealed class ScriptedTransport : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] _steps;
        private int _index = -1;

        internal ScriptedTransport(params Func<HttpResponseMessage>[] steps)
            : this(steps.Select(step => new Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>(
                (_, _) => Task.FromResult(step()))).ToArray())
        {
        }

        internal ScriptedTransport(Func<HttpRequestMessage, HttpResponseMessage> constant)
            : this([(request, _) => Task.FromResult(constant(request))])
        {
        }

        internal ScriptedTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> constant)
            : this([constant])
        {
        }

        internal ScriptedTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps) =>
            _steps = steps;

        /// <summary>Every request that reached the wire, in order.</summary>
        internal List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>Each request's body, read before the response is produced.</summary>
        internal List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            if (_steps.Length == 0)
            {
                throw new InvalidOperationException("The transport was given no script.");
            }

            // The last step repeats, so "always 503" is one step rather than as many as the policy
            // happens to allow.
            int next = Math.Min(Interlocked.Increment(ref _index), _steps.Length - 1);
            return await _steps[next](request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Content that remembers being disposed, which is the whole assertion.</summary>
    private sealed class TrackedContent : HttpContent
    {
        internal bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
