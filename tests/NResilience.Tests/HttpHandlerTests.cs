using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The HTTP handler: what it retries, what it clones, what it scopes per host, and what it
///     refuses to send twice.
/// </summary>
public sealed class HttpHandlerTests
{
    [Fact]
    public async Task A_transient_status_is_retried_to_success()
    {
        var transport = new ScriptedHttpHandler()
            .Responds(HttpStatusCode.ServiceUnavailable)
            .Responds(HttpStatusCode.OK);

        using var client = Client(transport);
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task A_404_is_an_answer_rather_than_a_failure()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.NotFound);

        using var client = Client(transport);
        using var response = await client.GetAsync(new Uri("https://api.test/missing"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task The_last_response_is_returned_when_the_attempts_run_out()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.ServiceUnavailable);

        using var client = Client(transport);
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, transport.CallCount);
    }

    [Fact]
    public async Task Each_attempt_gets_its_own_request()
    {
        var transport = new ScriptedHttpHandler()
            .Responds(HttpStatusCode.ServiceUnavailable)
            .Responds(HttpStatusCode.OK);

        using var client = Client(transport);
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        // Re-sending one HttpRequestMessage throws "The request message was already sent", so the
        // second attempt reaching the transport at all is proof the handler cloned rather than
        // resent it.
        Assert.Equal(2, transport.CallCount);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_clone_carries_the_headers_the_body_and_the_uri()
    {
        var transport = new ScriptedHttpHandler
            {
                CaptureBodies = true,
            }
            .Responds(HttpStatusCode.ServiceUnavailable)
            .Responds(HttpStatusCode.OK);

        using var client = Client(transport);

        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri("https://api.test/thing?q=1"))
        {
            Content = new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"),
        };

        request.Headers.Add("X-Trace", "abc");

        using var response = await client.SendAsync(request);

        var second = transport.Requests[1];
        Assert.Equal(HttpMethod.Put, second.Method);
        Assert.Equal(new Uri("https://api.test/thing?q=1"), second.RequestUri);
        Assert.Equal("abc", second.Headers.GetValues("X-Trace").Single());
        Assert.Equal("{\"a\":1}", transport.Requests[1].Body);
        Assert.Equal(transport.Requests[0].Body, transport.Requests[1].Body);
    }

    [Fact]
    public async Task A_POST_is_not_retried()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.ServiceUnavailable);

        using var client = Client(transport);
        using var response = await client.PostAsync(new Uri("https://api.test/orders"), new StringContent("order"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task A_non_retryable_POST_body_reaches_the_wire()
    {
        var transport = new ScriptedHttpHandler { CaptureBodies = true }.Responds(HttpStatusCode.OK);

        using var client = Client(transport);
        using var response = await client.PostAsync(new Uri("https://api.test/orders"), new StringContent("order"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, transport.CallCount);
        Assert.Equal("order", transport.Requests[0].Body);
    }

    [Fact]
    public async Task A_POST_is_retried_when_the_client_opts_in()
    {
        var transport = new ScriptedHttpHandler
            {
                CaptureBodies = true,
            }
            .Responds(HttpStatusCode.ServiceUnavailable)
            .Responds(HttpStatusCode.OK);

        using var client = Client(transport, new HttpResilienceOptions { RetryUnsafeMethods = true });
        using var response = await client.PostAsync(new Uri("https://api.test/orders"), new StringContent("order"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, transport.CallCount);
        Assert.Equal("order", transport.Requests[1].Body);
    }

    [Fact]
    public async Task A_POST_is_retried_when_the_request_declares_itself_repeatable()
    {
        var transport = new ScriptedHttpHandler()
            .Responds(HttpStatusCode.ServiceUnavailable)
            .Responds(HttpStatusCode.OK);

        using var client = Client(transport);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.test/orders"))
        {
            Content = new StringContent("order"),
        };

        request.MarkRepeatable();

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task A_request_declared_not_repeatable_is_not_retried_even_when_its_method_is_safe()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.ServiceUnavailable);

        using var client = Client(transport, new HttpResilienceOptions { RetryUnsafeMethods = true });

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));
        request.MarkSingleShot();

        using var response = await client.SendAsync(request);

        Assert.Equal(1, transport.CallCount);
    }

    /// <summary>
    ///     The two calls the helper replaces serve different consumers: the option tells this client to
    ///     retry, the header tells the service to discard the duplicate. A retryable POST needs both, so
    ///     one call writes both.
    /// </summary>
    [Fact]
    public void MarkRepeatable_sets_the_option_and_stamps_the_key()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.test/orders"));

        Assert.Same(request, request.MarkRepeatable("key-1"));

        Assert.True(request.Options.TryGetValue(HttpResilience.Repeatable, out var repeatable) && repeatable);
        Assert.Equal(["key-1"], request.Headers.GetValues("Idempotency-Key"));
    }

    /// <summary>
    ///     <c>Idempotency-Key</c> is an IETF draft rather than a standard, so the header name is a
    ///     parameter - the services that name it something else are exactly the callers who need this.
    /// </summary>
    [Fact]
    public void MarkRepeatable_stamps_the_key_under_the_named_header()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.test/orders"));

        request.MarkRepeatable("key-1", "X-Request-Id");

        Assert.Equal(["key-1"], request.Headers.GetValues("X-Request-Id"));
        Assert.False(request.Headers.Contains("Idempotency-Key"));
    }

    /// <summary>
    ///     No key leaves the headers alone: a service that does not deduplicate still wants the retry.
    /// </summary>
    [Fact]
    public void MarkRepeatable_without_a_key_touches_no_header()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.test/orders"));

        request.MarkRepeatable();

        Assert.True(request.Options.TryGetValue(HttpResilience.Repeatable, out var repeatable) && repeatable);
        Assert.False(request.Headers.Contains("Idempotency-Key"));
    }

    /// <summary>
    ///     <c>TryAddWithoutValidation</c> appends rather than replaces, and two idempotency keys on one
    ///     request is a request most services reject outright. The caller's own key wins.
    /// </summary>
    [Fact]
    public void MarkRepeatable_does_not_add_a_second_key()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("https://api.test/orders"));
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "mine");

        request.MarkRepeatable("key-1");

        Assert.Equal(["mine"], request.Headers.GetValues("Idempotency-Key"));
    }

    /// <summary>
    ///     <see cref="HttpResilience.Repeatable" /> beats <c>RetryUnsafeMethods</c> in both directions,
    ///     and the helper pair carries both of them.
    /// </summary>
    [Fact]
    public void MarkSingleShot_clears_the_option()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));

        Assert.Same(request, request.MarkSingleShot());

        Assert.True(request.Options.TryGetValue(HttpResilience.Repeatable, out var repeatable));
        Assert.False(repeatable);
    }

    [Fact]
    public void The_request_helpers_reject_a_null_request()
    {
        Assert.Throws<ArgumentNullException>(() => ((HttpRequestMessage)null!).MarkRepeatable());
        Assert.Throws<ArgumentNullException>(() => ((HttpRequestMessage)null!).MarkSingleShot());
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
        using var handler = new HttpResilienceHandler(new ScriptedHttpHandler(), TestPolicy.InstantHttp);
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri("https://api.test/thing"));

        Assert.Equal(retried, handler.WillRetry(request));
    }

    [Fact]
    public void A_single_attempt_policy_retries_nothing_whatever_the_method_says()
    {
        using var handler = new HttpResilienceHandler(new ScriptedHttpHandler(), TestPolicy.InstantHttp with { Attempts = 1 });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));

        Assert.False(handler.WillRetry(request));
    }

    [Fact]
    public async Task A_superseded_response_is_disposed()
    {
        var first = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new TrackedContent() };
        var second = new HttpResponseMessage(HttpStatusCode.OK) { Content = new TrackedContent() };
        var transport = new ScriptedHttpHandler().Responds(() => first).Responds(() => second);

        using var client = Client(transport);
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        Assert.True(((TrackedContent)first.Content).Disposed);
        Assert.False(((TrackedContent)second.Content!).Disposed);
    }

    [Fact]
    public async Task Every_host_gets_its_own_breaker_and_budget()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);
        using var handler = new HttpResilienceHandler(transport, TestPolicy.InstantHttp with { Budget = RetryBudget.Automatic });
        using var client = new HttpClient(handler);

        (await client.GetAsync(new Uri("https://one.test/a"))).Dispose();
        (await client.GetAsync(new Uri("https://two.test/a"))).Dispose();
        (await client.GetAsync(new Uri("https://one.test/b"))).Dispose();

        var breakers = handler.BreakersByHost();
        Assert.Equal(2, breakers.Count);
        Assert.NotSame(breakers["one.test"], breakers["two.test"]);

        // RetryBudget.Automatic is what per-host scoping is allowed to override; the test policy
        // turns the budget off, so the override is asked for here rather than inherited.
        // Counting the entries is not enough: one shared bucket behind two keys would let a storm
        // against one host throttle retries to the other, with BudgetPerHost still reporting true.
        var budgets = handler.BudgetsByHost();
        Assert.Equal(2, budgets.Count);
        Assert.NotSame(budgets["one.test"], budgets["two.test"]);
        Assert.False(budgets["one.test"].IsAutomatic);
    }

    [Fact]
    public async Task A_dead_host_does_not_break_a_healthy_one()
    {
        var transport = new ConditionalTransport((request, _) => Task.FromResult(
            request.RequestUri!.Host == "dead.test"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)));

        var policy = TestPolicy.InstantHttp with
        {
            Attempts = 1,
            Breaker = null,
        };

        using var handler = new HttpResilienceHandler(
            transport,
            policy,
            new HttpResilienceOptions { BreakerSettings = new BreakerSettings { ConsecutiveFailures = 2 } });

        using var client = new HttpClient(handler);

        for (var i = 0; i < 2; i++)
        {
            (await client.GetAsync(new Uri("https://dead.test/a"))).Dispose();
        }

        Assert.Equal(BreakerState.Open, handler.BreakersByHost()["dead.test"].State);

        // An open breaker refuses the call rather than making it, and says so.
        await Assert.ThrowsAsync<CallRejectedException>(async () =>
            await client.GetAsync(new Uri("https://dead.test/a")));

        using var healthy = await client.GetAsync(new Uri("https://healthy.test/a"));
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    /// <summary>
    ///     A per-host breaker is built by the library, so it runs on the policy's clock. Without that
    ///     there is no way to write a deterministic test for per-host breaker behavior at all: the
    ///     break would have to be waited out in real time.
    /// </summary>
    [Fact]
    public async Task A_fake_clock_on_the_policy_drives_a_per_host_breaker()
    {
        var down = true;

        var transport = new ConditionalTransport((_, _) => Task.FromResult(
            down
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)));

        var time = new FakeTimeProvider();

        using var handler = new HttpResilienceHandler(
            transport,
            TestPolicy.InstantHttp with { Attempts = 1, Breaker = null, Time = time },
            new HttpResilienceOptions
            {
                BreakerSettings = new BreakerSettings
                {
                    ConsecutiveFailures = 2,
                    BreakDuration = TimeSpan.FromSeconds(15),
                },
            });

        using var client = new HttpClient(handler);

        for (var i = 0; i < 2; i++)
        {
            (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();
        }

        var breaker = handler.BreakersByHost()["api.test"];
        Assert.Equal(BreakerState.Open, breaker.State);
        Assert.Same(time, breaker.Settings.Time);

        down = false;

        // On TimeProvider.System the break has another 15 s to run, so a call getting through here is
        // the fake clock and nothing else.
        time.Advance(TimeSpan.FromSeconds(16));

        using var response = await client.GetAsync(new Uri("https://api.test/thing"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    ///     Same for the per-host budget: its token bucket refills on the policy's clock, so advancing a
    ///     fake clock returns the spent capacity.
    /// </summary>
    [Fact]
    public async Task A_fake_clock_on_the_policy_drives_a_per_host_budget()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.ServiceUnavailable);
        var time = new FakeTimeProvider();

        // No per-host breaker: five failing calls would open one, and a refused call pauses on the
        // policy's clock, which is the clock this test is holding still.
        using var handler = new HttpResilienceHandler(
            transport,
            TestPolicy.InstantHttp with { Breaker = null, Time = time, Budget = RetryBudget.Automatic },
            new HttpResilienceOptions { BreakerPerHost = false });

        using var client = new HttpClient(handler);

        for (var i = 0; i < 5; i++)
        {
            (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();
        }

        var budget = handler.BudgetsByHost()["api.test"];
        Assert.True(budget.Utilization > 0);

        // The bucket refills at its floor rate, so a clock the test controls empties and refills it.
        time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(0, budget.Utilization);
    }

    [Fact]
    public async Task An_explicit_breaker_survives_per_host_scoping()
    {
        var shared = Breaker.Of(name: "shared");
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var handler = new HttpResilienceHandler(transport, TestPolicy.InstantHttp with { Breaker = shared });
        using var client = new HttpClient(handler);

        (await client.GetAsync(new Uri("https://one.test/a"))).Dispose();
        (await client.GetAsync(new Uri("https://two.test/a"))).Dispose();

        var breakers = handler.BreakersByHost();
        Assert.Same(shared, breakers["one.test"]);
        Assert.Same(shared, breakers["two.test"]);
    }

    [Fact]
    public async Task A_retrying_client_stamps_the_nested_retry_header()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var client = Client(transport);
        (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.True(transport.Requests[0].Headers.Contains(NestedRetry.Header));
    }

    [Fact]
    public async Task A_client_that_cannot_retry_stamps_nothing()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var client = Client(transport, policy: TestPolicy.InstantHttp with { Attempts = 1 });
        (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.False(transport.Requests[0].Headers.Contains(NestedRetry.Header));
    }

    [Fact]
    public async Task An_inbound_stamp_makes_the_retry_a_reported_nested_one()
    {
        var recorder = new EventRecorder();
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var client = Client(transport, policy: TestPolicy.InstantHttp with { OnEvent = recorder.Record });

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));
        request.Headers.Add(NestedRetry.Header, "1");

        (await client.SendAsync(request)).Dispose();

        Assert.True(recorder.CountOf(CallEventKind.NestedRetry) > 0);
    }

    [Fact]
    public async Task An_inbound_stamp_is_not_duplicated_on_the_outbound_request()
    {
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var client = Client(transport);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));
        request.Headers.Add(NestedRetry.Header, "1");

        (await client.SendAsync(request)).Dispose();

        Assert.Single(transport.Requests[0].Headers.GetValues(NestedRetry.Header));
    }

    [Fact]
    public async Task One_retrying_client_inside_another_is_a_reported_nested_retry()
    {
        var recorder = new EventRecorder();

        var inner = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);
        using var innerClient = Client(inner, policy: TestPolicy.InstantHttp with { OnEvent = recorder.Record });

        // The outer client's transport is the inner client: exactly the shape a service that calls
        // another service has, and the one whose amplification nothing in .NET reports.
        var outerTransport = new ConditionalTransport(async (request, ct) =>
            await innerClient.GetAsync(new Uri("https://downstream.test/a"), ct).ConfigureAwait(false));

        using var outerClient = Client(outerTransport);
        (await outerClient.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.True(recorder.CountOf(CallEventKind.NestedRetry) > 0);
    }

    [Fact]
    public async Task An_in_process_nested_retry_stamps_the_header_on_the_inner_request()
    {
        var inner = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);
        using var innerClient = Client(inner);

        // The inner transport receives a different request than the outer one, so the header
        // must be stamped on it independently of the outer request's header.
        var outerTransport = new ConditionalTransport(async (request, ct) =>
            await innerClient.GetAsync(new Uri("https://downstream.test/a"), ct).ConfigureAwait(false));

        using var outerClient = Client(outerTransport);
        (await outerClient.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.True(inner.Requests[0].Headers.Contains(NestedRetry.Header));
    }

    [Fact]
    public async Task Nesting_detection_can_be_turned_off()
    {
        var recorder = new EventRecorder();
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var client = Client(
            transport,
            new HttpResilienceOptions { DetectNestedRetries = false },
            TestPolicy.InstantHttp with { OnEvent = recorder.Record });

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));
        request.Headers.Add(NestedRetry.Header, "1");

        (await client.SendAsync(request)).Dispose();

        Assert.Equal(0, recorder.CountOf(CallEventKind.NestedRetry));
    }

    [Fact]
    public async Task Nesting_is_reported_on_every_call_not_just_the_first()
    {
        // The AsyncLocal the handler uses to carry nesting state across the call must be restored
        // to its previous value in the finally block, so a second call through the same handler
        // is still detected as nested when it runs inside an outer retrying client.
        var recorder = new EventRecorder();
        var inner = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);
        using var innerClient = Client(inner, policy: TestPolicy.InstantHttp with { OnEvent = recorder.Record });

        var outerTransport = new ConditionalTransport(async (request, ct) =>
            await innerClient.GetAsync(new Uri("https://downstream.test/a"), ct).ConfigureAwait(false));

        using var outerClient = Client(outerTransport);

        // Two sequential calls: each one runs the inner client inside the outer, and each must
        // report nesting. A finally that cleared the flag unconditionally would leave the second
        // call unable to detect that it is running inside a retrying context.
        (await outerClient.GetAsync(new Uri("https://api.test/first"))).Dispose();

        Assert.True(recorder.CountOf(CallEventKind.NestedRetry) > 0,
            "the inner client should report nesting on the first call");

        recorder.Clear();

        (await outerClient.GetAsync(new Uri("https://api.test/second"))).Dispose();

        Assert.True(recorder.CountOf(CallEventKind.NestedRetry) > 0,
            "the inner client should still report nesting on the second call");
    }

    [Fact]
    public async Task An_ambient_caller_retrying_flag_is_reported_as_nesting()
    {
        var recorder = new EventRecorder();
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var client = Client(transport, policy: TestPolicy.InstantHttp with { OnEvent = recorder.Record });

        // The inbound half: a server published the caller's marker as an ambient flag, and the
        // outbound calls this request makes are the ones that need to know.
        using var scope = NestedRetry.Begin(true);
        (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.True(recorder.CountOf(CallEventKind.NestedRetry) > 0);
    }

    [Fact]
    public async Task The_ambient_flag_is_ignored_when_detection_is_off()
    {
        var recorder = new EventRecorder();
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var client = Client(
            transport,
            new HttpResilienceOptions { DetectNestedRetries = false },
            TestPolicy.InstantHttp with { OnEvent = recorder.Record });

        using var scope = NestedRetry.Begin(true);
        (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.Equal(0, recorder.CountOf(CallEventKind.NestedRetry));
    }

    [Fact]
    public async Task The_ambient_flag_is_ignored_for_a_single_attempt_client()
    {
        var recorder = new EventRecorder();
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var client = Client(transport, policy: TestPolicy.InstantHttp with { Attempts = 1, OnEvent = recorder.Record });

        using var scope = NestedRetry.Begin(true);
        (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.Equal(0, recorder.CountOf(CallEventKind.NestedRetry));
    }

    [Fact]
    public async Task The_handler_restores_the_in_process_flag_it_found()
    {
        var recorder = new EventRecorder();
        var inner = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        // The regression test for the restore: wasInside is the value the finally restores, and it
        // must stay "what this handler's own AsyncLocal was before this send" - not OR-ed with the
        // ambient flag, which would leave InsideRetryingClient = true on a context where it was
        // false and make every later call through this context report nesting from nothing.
        using var client = Client(inner, policy: TestPolicy.InstantHttp with { OnEvent = recorder.Record });

        using (NestedRetry.Begin(true))
        {
            (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();

            Assert.True(recorder.CountOf(CallEventKind.NestedRetry) > 0,
                "the ambient flag should make the send report nesting");
        }

        recorder.Clear();

        // Outside the ambient scope, on the same execution context the first send ran on: the
        // handler must have restored InsideRetryingClient to the false it found, so a second send
        // reports nothing - nesting comes only from a source that is present, never from a leak.
        (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.True(recorder.CountOf(CallEventKind.NestedRetry) == 0,
            "a leaked InsideRetryingClient would make a clean send report nesting");
    }

    [Fact]
    public void CreateClient_takes_ownership_of_the_transport_timeout()
    {
        using var owned = HttpResilience.CreateClient(TestPolicy.InstantHttp, innerHandler: new ScriptedHttpHandler());
        Assert.Equal(Timeout.InfiniteTimeSpan, owned.Timeout);

        using var borrowed = HttpResilience.CreateClient(
            TestPolicy.InstantHttp,
            new HttpResilienceOptions { OwnTransportTimeout = false },
            new ScriptedHttpHandler());

        Assert.NotEqual(Timeout.InfiniteTimeSpan, borrowed.Timeout);
    }

    [Fact]
    public async Task Retry_After_on_a_429_beats_the_backoff_curve()
    {
        var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));

        var transport = new ScriptedHttpHandler().Responds(() => throttled).Responds(HttpStatusCode.OK);

        using var client = Client(transport, policy: TestPolicy.InstantHttp with { Backoff = Backoff.Constant(TimeSpan.FromMinutes(5)) });
        using var response = await client.GetAsync(new Uri("https://api.test/thing"));

        // A five-minute backoff would have made this test time out; the server's own hint is the
        // one that was served.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Events_carry_the_host_scoped_policy_name()
    {
        var recorder = new EventRecorder();
        var transport = new ScriptedHttpHandler().Responds(HttpStatusCode.OK);

        using var client = Client(transport, policy: TestPolicy.InstantHttp with { OnEvent = recorder.Record });
        (await client.GetAsync(new Uri("https://api.test/thing"))).Dispose();

        Assert.Equal("http:api.test", recorder[0].PolicyName);
    }

    [Fact]
    public async Task A_cancelled_call_disposes_the_response_nobody_receives()
    {
        var tracked = new TrackedContent();
        using var cancellation = new CancellationTokenSource();

        var transport = new ScriptedHttpHandler()
            .Responds(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = tracked })
            .Throws(() =>
            {
                cancellation.Cancel();
                return new OperationCanceledException(cancellation.Token);
            });

        using var client = Client(transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.GetAsync(new Uri("https://api.test/thing"), cancellation.Token));

        Assert.True(tracked.Disposed);
    }

    [Fact]
    public void The_synchronous_send_is_refused()
    {
        using var handler = new HttpResilienceHandler(new ScriptedHttpHandler(), TestPolicy.InstantHttp);
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://api.test/thing"));

        Assert.Throws<NotSupportedException>(() => client.Send(request));
    }

    private static HttpClient Client(
        HttpMessageHandler transport,
        HttpResilienceOptions? options = null,
        Resilience? policy = null) =>
        new(new HttpResilienceHandler(transport, policy ?? TestPolicy.InstantHttp, options));

    /// <summary>
    ///     A transport that routes by request, observes a mutable flag toggled from outside it, or
    ///     forwards to another <see cref="HttpClient" /> - the shapes <see cref="ScriptedHttpHandler" />
    ///     cannot express.
    /// </summary>
    private sealed class ConditionalTransport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    /// <summary>Content that remembers being disposed, which is the whole assertion.</summary>
    private sealed class TrackedContent : HttpContent
    {
        internal bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

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
