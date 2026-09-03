using Grpc.Core;
using Grpc.Core.Interceptors;
using NResilience.Grpc;
using NResilience.Http;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The gRPC interceptor: what it retries, what it writes on the wire, what it hands back, and
///     what it refuses to send twice.
/// </summary>
public sealed class GrpcInterceptorTests
{
    private static readonly Method<string, string> Get =
        new(MethodType.Unary, "orders.Orders", "Get", Marshallers.StringMarshaller, Marshallers.StringMarshaller);

    private static readonly Method<string, string> Charge =
        new(MethodType.Unary, "orders.Orders", "ChargeCard", Marshallers.StringMarshaller, Marshallers.StringMarshaller);

    private static readonly Method<string, string> Ship =
        new(MethodType.Unary, "shipping.Shipping", "Ship", Marshallers.StringMarshaller, Marshallers.StringMarshaller);

    [Fact]
    public async Task An_unavailable_status_is_retried_to_success()
    {
        var script = new GrpcScript().Fail(StatusCode.Unavailable).Fail(StatusCode.Unavailable).Respond("ok");

        using var call = Call(Interceptor(), script);

        Assert.Equal("ok", await call.ResponseAsync);
        Assert.Equal(3, script.CallCount);
    }

    [Fact]
    public async Task An_invalid_argument_is_an_answer_rather_than_a_failure()
    {
        var script = new GrpcScript().Fail(StatusCode.InvalidArgument);

        using var call = Call(Interceptor(), script);

        var failure = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, failure.StatusCode);
        Assert.Equal(1, script.CallCount);
    }

    [Fact]
    public async Task Resource_exhausted_is_throttling_rather_than_a_transient_failure()
    {
        var events = new List<CallEvent>();
        var script = new GrpcScript().Fail(StatusCode.ResourceExhausted).Respond("ok");

        using var call = Call(Interceptor(Policy() with { OnEvent = events.Add }), script);

        Assert.Equal("ok", await call.ResponseAsync);
        Assert.Contains(events, e => e.Kind == CallEventKind.Attempt && e.Verdict.Kind == VerdictKind.Throttled);
    }

    [Theory]
    [InlineData(StatusCode.Unavailable, VerdictKind.Transient)]
    [InlineData(StatusCode.DeadlineExceeded, VerdictKind.Transient)]
    [InlineData(StatusCode.ResourceExhausted, VerdictKind.Throttled)]
    [InlineData(StatusCode.Internal, VerdictKind.Permanent)]
    [InlineData(StatusCode.Unauthenticated, VerdictKind.Permanent)]
    [InlineData(StatusCode.PermissionDenied, VerdictKind.Permanent)]
    [InlineData(StatusCode.NotFound, VerdictKind.Permanent)]
    [InlineData(StatusCode.Aborted, VerdictKind.Permanent)]
    [InlineData(StatusCode.Cancelled, VerdictKind.Permanent)]
    [InlineData(StatusCode.Unknown, VerdictKind.Permanent)]
    public void The_classifier_maps_every_status_it_ships_an_opinion_about(StatusCode status, VerdictKind expected)
    {
        var verdict = GrpcResilience.Classifier.ClassifyException(new RpcException(new Status(status, string.Empty)));

        Assert.Equal(expected, verdict.Kind);
    }

    [Fact]
    public void The_classifier_is_one_line_to_override()
    {
        var retryAborted = GrpcResilience.Classifier.On<RpcException>(
            static e => e.StatusCode == StatusCode.Aborted ? Verdict.Transient : GrpcResilience.Classifier.ClassifyException(e));

        Assert.Equal(VerdictKind.Transient, retryAborted.ClassifyException(new RpcException(new Status(StatusCode.Aborted, ""))).Kind);
        Assert.Equal(VerdictKind.Transient, retryAborted.ClassifyException(new RpcException(new Status(StatusCode.Unavailable, ""))).Kind);
        Assert.Equal(VerdictKind.Permanent, retryAborted.ClassifyException(new RpcException(new Status(StatusCode.NotFound, ""))).Kind);
    }

    [Fact]
    public void The_preset_carries_the_grpc_classifier_and_the_shipped_defaults()
    {
        Assert.Same(GrpcResilience.Classifier, GrpcResilience.Default.Classify);
        Assert.Equal(Resilience.Default.Attempts, GrpcResilience.Default.Attempts);
        Assert.Equal(Resilience.Default.Deadline, GrpcResilience.Default.Deadline);
        Assert.Equal(Resilience.Default.AttemptTimeout, GrpcResilience.Default.AttemptTimeout);
    }

    // The call object: five things supplied synchronously, before any attempt has run, that all have
    // to end up describing the attempt that won.

    [Fact]
    public void The_status_is_not_available_before_the_call_completes()
    {
        var script = new GrpcScript().Hang();

        using var call = Call(Interceptor(), script);

        Assert.Throws<InvalidOperationException>(() => call.GetStatus());
        Assert.Throws<InvalidOperationException>(() => call.GetTrailers());
    }

    [Fact]
    public async Task The_status_after_a_retry_is_the_winning_attempt_s()
    {
        var script = new GrpcScript().Fail(StatusCode.Unavailable).Respond("ok");

        using var call = Call(Interceptor(), script);
        await call.ResponseAsync;

        Assert.Equal(StatusCode.OK, call.GetStatus().StatusCode);
    }

    [Fact]
    public async Task The_status_after_every_attempt_failed_is_the_last_attempt_s()
    {
        var script = new GrpcScript().Fail(StatusCode.Unavailable, "gone");

        using var call = Call(Interceptor(), script);

        await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);

        Assert.Equal(StatusCode.Unavailable, call.GetStatus().StatusCode);
        Assert.Equal("gone", call.GetStatus().Detail);
    }

    [Fact]
    public async Task The_response_headers_are_the_winning_attempt_s()
    {
        var winner = new Metadata { { "served-by", "b" } };
        var script = new GrpcScript().Fail(StatusCode.Unavailable).Respond("ok", winner);

        using var call = Call(Interceptor(), script);
        await call.ResponseAsync;

        var headers = await call.ResponseHeadersAsync;

        Assert.Equal("b", headers.GetValue("served-by"));
    }

    [Fact]
    public async Task The_response_headers_fault_rather_than_hang_when_every_attempt_failed()
    {
        var script = new GrpcScript().Fail(StatusCode.Unavailable);

        using var call = Call(Interceptor(), script);

        await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
        await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseHeadersAsync);
    }

    [Fact]
    public async Task A_superseded_attempt_is_disposed_and_the_winner_is_the_caller_s()
    {
        var script = new GrpcScript().Fail(StatusCode.Unavailable).Fail(StatusCode.Unavailable).Respond("ok");

        var call = Call(Interceptor(), script);
        await call.ResponseAsync;

        Assert.True(script.Calls[0].Disposed);
        Assert.True(script.Calls[1].Disposed);
        Assert.False(script.Calls[2].Disposed);

        call.Dispose();

        Assert.True(script.Calls[2].Disposed);
    }

    [Fact]
    public async Task The_last_attempt_of_a_failed_call_is_disposed_because_nobody_will_receive_it()
    {
        var script = new GrpcScript().Fail(StatusCode.Unavailable);

        using var call = Call(Interceptor(), script);

        await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);

        Assert.All(script.Calls, c => Assert.True(c.Disposed));
    }

    // Metadata: never the caller's own object.

    [Fact]
    public async Task The_caller_s_metadata_is_never_mutated_and_carries_exactly_one_marker_per_attempt()
    {
        var callers = new Metadata { { "tenant", "acme" } };
        var script = new GrpcScript().Fail(StatusCode.Unavailable).Fail(StatusCode.Unavailable).Respond("ok");

        using var call = Call(Interceptor(), script, new CallOptions(callers));
        await call.ResponseAsync;

        Assert.Single(callers);

        foreach (var options in script.Seen)
        {
            Assert.NotSame(callers, options.Headers);
            Assert.Single(options.Headers!, e => e.Key == "x-nresilience-retrying");
            Assert.Equal("acme", options.Headers!.GetValue("tenant"));
        }
    }

    [Fact]
    public async Task A_null_metadata_collection_is_handled()
    {
        var script = new GrpcScript().Fail(StatusCode.Unavailable).Respond("ok");

        using var call = Call(Interceptor(), script);
        await call.ResponseAsync;

        Assert.Single(script.Seen[0].Headers!, e => e.Key == "x-nresilience-retrying");
    }

    [Fact]
    public async Task An_inbound_marker_is_not_stamped_a_second_time()
    {
        var callers = new Metadata { { "x-nresilience-retrying", ResilienceNestedRetry.Marker } };
        var script = new GrpcScript().Respond("ok");

        using var call = Call(Interceptor(), script, new CallOptions(callers));
        await call.ResponseAsync;

        Assert.Single(script.Seen[0].Headers!, e => e.Key == "x-nresilience-retrying");
    }

    [Fact]
    public void The_marker_travels_under_the_http_header_s_name_lowercased()
    {
        Assert.Equal("x-nresilience-retrying", HttpResilience.NestedRetryHeader.ToLowerInvariant());
    }

    [Fact]
    public async Task A_call_made_inside_an_inbound_retry_reports_the_nesting()
    {
        var events = new List<CallEvent>();
        var script = new GrpcScript().Respond("ok");

        using var scope = ResilienceNestedRetry.Begin(true);
        using var call = Call(Interceptor(Policy() with { OnEvent = events.Add }), script);
        await call.ResponseAsync;

        Assert.Contains(events, e => e.Kind == CallEventKind.NestedRetry);
    }

    // The wire deadline, and the ladder that keeps our own ceiling from being classified.

    [Fact]
    public async Task The_wire_deadline_is_the_attempt_ceiling_plus_the_slack()
    {
        var script = new GrpcScript().Respond("ok");
        var options = new GrpcResilienceOptions { DeadlineSlack = TimeSpan.FromMilliseconds(50) };
        var policy = Policy() with { AttemptTimeout = TimeSpan.FromSeconds(2), Deadline = TimeSpan.FromMinutes(5) };

        var before = DateTime.UtcNow;
        using var call = Call(new ResilienceInterceptor(policy, options), script);
        await call.ResponseAsync;

        var written = Assert.IsType<DateTime>(script.Seen[0].Deadline);

        Assert.InRange(written - before, TimeSpan.FromMilliseconds(2050), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task The_wire_deadline_is_the_remaining_call_deadline_when_that_is_tighter()
    {
        var script = new GrpcScript().Respond("ok");
        var policy = Policy() with { AttemptTimeout = TimeSpan.FromMinutes(5), Deadline = TimeSpan.FromSeconds(1) };

        var before = DateTime.UtcNow;
        using var call = Call(new ResilienceInterceptor(policy), script);
        await call.ResponseAsync;

        var written = Assert.IsType<DateTime>(script.Seen[0].Deadline);

        Assert.InRange(written - before, TimeSpan.FromMilliseconds(1000), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_deadline_the_caller_set_is_never_overwritten()
    {
        var theirs = DateTime.UtcNow.AddMilliseconds(200);
        var script = new GrpcScript().Respond("ok");
        var policy = Policy() with { AttemptTimeout = TimeSpan.FromSeconds(30) };

        using var call = Call(new ResilienceInterceptor(policy), script, new CallOptions(deadline: theirs));
        await call.ResponseAsync;

        Assert.Equal(theirs, script.Seen[0].Deadline);
    }

    [Fact]
    public async Task No_deadline_is_written_when_propagation_is_off()
    {
        var script = new GrpcScript().Respond("ok");
        var options = new GrpcResilienceOptions { PropagateAttemptDeadline = false };

        using var call = Call(new ResilienceInterceptor(Policy(), options), script);
        await call.ResponseAsync;

        Assert.Null(script.Seen[0].Deadline);
    }

    [Fact]
    public async Task A_deadline_we_set_that_grpc_s_own_timer_noticed_first_is_still_our_attempt_timeout()
    {
        // The row that is invisible until a cluster is slow enough to lose the race: grpc-dotnet
        // reports DeadlineExceeded while the executor's own token is still unfired. The classifier
        // here refuses every RpcException, so only the translation can produce a retry.
        var refusing = Policy() with { Classify = GrpcResilience.Classifier.On<RpcException>(Verdict.Permanent) };
        var script = new GrpcScript().Fail(StatusCode.DeadlineExceeded).Respond("ok");

        using var call = Call(new ResilienceInterceptor(refusing), script);

        Assert.Equal("ok", await call.ResponseAsync);
        Assert.Equal(2, script.CallCount);
    }

    [Fact]
    public async Task A_deadline_the_caller_set_stays_an_rpc_exception_for_the_classifier_to_judge()
    {
        var refusing = Policy() with { Classify = GrpcResilience.Classifier.On<RpcException>(Verdict.Permanent) };
        var script = new GrpcScript().Fail(StatusCode.DeadlineExceeded).Respond("ok");

        using var call = Call(
            new ResilienceInterceptor(refusing),
            script,
            new CallOptions(deadline: DateTime.UtcNow.AddMilliseconds(50)));

        await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
        Assert.Equal(1, script.CallCount);
    }

    [Fact]
    public async Task A_call_whose_every_attempt_hit_our_own_deadline_fails_as_an_attempt_timeout()
    {
        var script = new GrpcScript().Fail(StatusCode.DeadlineExceeded);
        var policy = Policy() with { Attempts = 2, AttemptTimeout = TimeSpan.FromSeconds(5) };

        using var call = Call(new ResilienceInterceptor(policy), script);

        await Assert.ThrowsAsync<AttemptTimeoutException>(async () => await call.ResponseAsync);
        Assert.Equal(2, script.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_never_retried_and_is_never_a_failure()
    {
        using var cancellation = new CancellationTokenSource();

        var script = new GrpcScript().FailAfter(_ => cancellation.Cancel(), StatusCode.Cancelled);

        using var call = Call(Interceptor(), script, new CallOptions(cancellationToken: cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call.ResponseAsync);
        Assert.Equal(1, script.CallCount);
    }

    [Fact]
    public async Task Disposing_the_call_stops_a_retry_loop_that_is_still_running()
    {
        var script = new GrpcScript().Hang();

        var call = Call(Interceptor(), script);
        call.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await call.ResponseAsync);
    }

    // Repeatability, inverted from HTTP.

    [Fact]
    public async Task Unary_calls_are_repeatable_by_default()
    {
        Assert.True(Interceptor().WillRetry(Get));

        var script = new GrpcScript().Fail(StatusCode.Unavailable).Respond("ok");
        using var call = Call(Interceptor(), script);
        await call.ResponseAsync;

        Assert.Equal(2, script.CallCount);
    }

    [Fact]
    public async Task A_method_the_registration_marked_unrepeatable_gets_one_attempt()
    {
        var options = new GrpcResilienceOptions { IsRepeatable = static m => m.Name != "ChargeCard" };
        var interceptor = new ResilienceInterceptor(Policy(), options);
        var script = new GrpcScript().Fail(StatusCode.Unavailable);

        Assert.False(interceptor.WillRetry(Charge));

        using var call = Call(interceptor, script, method: Charge);

        await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
        Assert.Equal(1, script.CallCount);
    }

    [Fact]
    public async Task A_single_shot_scope_gets_one_attempt_without_touching_the_wire()
    {
        var interceptor = Interceptor();
        var script = new GrpcScript().Fail(StatusCode.Unavailable);

        using (GrpcResilience.SingleShot())
        {
            Assert.False(interceptor.WillRetry(Get));

            using var call = Call(interceptor, script);
            await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
        }

        Assert.Equal(1, script.CallCount);
        Assert.DoesNotContain(script.Seen[0].Headers ?? [], e => e.Key.Contains("single", StringComparison.Ordinal));
        Assert.True(interceptor.WillRetry(Get));
    }

    // Scoping: per service by default.

    [Fact]
    public async Task Two_services_get_two_breakers_and_two_budgets_by_default()
    {
        var interceptor = Interceptor(Policy() with { Breaker = new Breaker() });

        using (var first = Call(interceptor, new GrpcScript().Respond("ok")))
            await first.ResponseAsync;

        using (var second = Call(interceptor, new GrpcScript().Respond("ok"), method: Ship))
            await second.ResponseAsync;

        Assert.Equal(["orders.Orders", "shipping.Shipping"], interceptor.Breakers().Keys.Order());
        Assert.Equal(["orders.Orders", "shipping.Shipping"], interceptor.Budgets().Keys.Order());
    }

    [Fact]
    public async Task Two_methods_of_one_service_share_a_breaker_by_default()
    {
        var interceptor = Interceptor(Policy() with { Breaker = new Breaker() });

        using (var first = Call(interceptor, new GrpcScript().Respond("ok")))
            await first.ResponseAsync;

        using (var second = Call(interceptor, new GrpcScript().Respond("ok"), method: Charge))
            await second.ResponseAsync;

        Assert.Single(interceptor.Breakers());
    }

    [Fact]
    public async Task A_null_scope_key_gives_the_whole_client_one_breaker()
    {
        var interceptor = new ResilienceInterceptor(Policy(), new GrpcResilienceOptions { ScopeBy = null }, "orders");

        using (var first = Call(interceptor, new GrpcScript().Respond("ok")))
            await first.ResponseAsync;

        using (var second = Call(interceptor, new GrpcScript().Respond("ok"), method: Ship))
            await second.ResponseAsync;

        Assert.Equal(["orders"], interceptor.Breakers().Keys);
    }

    [Fact]
    public async Task One_service_tripping_its_breaker_leaves_the_other_serving()
    {
        var options = new GrpcResilienceOptions { BreakerSettings = new BreakerSettings { ConsecutiveFailures = 1 } };
        var interceptor = new ResilienceInterceptor(Policy() with { Attempts = 1 }, options);

        using (var failing = Call(interceptor, new GrpcScript().Fail(StatusCode.Unavailable)))
            await Assert.ThrowsAsync<RpcException>(async () => await failing.ResponseAsync);

        using (var rejected = Call(interceptor, new GrpcScript().Respond("ok")))
            await Assert.ThrowsAsync<CallRejectedException>(async () => await rejected.ResponseAsync);

        using var other = Call(interceptor, new GrpcScript().Respond("ok"), method: Ship);

        Assert.Equal("ok", await other.ResponseAsync);
    }

    [Fact]
    public void An_interceptor_validates_its_policy_and_its_options_eagerly()
    {
        Assert.Throws<ResilienceConfigurationException>(
            () => new ResilienceInterceptor(Policy() with { Attempts = 0 }));

        Assert.Throws<ResilienceConfigurationException>(
            () => new ResilienceInterceptor(Policy(), new GrpcResilienceOptions { DeadlineSlack = TimeSpan.FromSeconds(-1) }));
    }

    [Fact]
    public void A_blocking_unary_call_is_refused_rather_than_quietly_unprotected()
    {
        var interceptor = Interceptor();

        Assert.Throws<NotSupportedException>(
            () => interceptor.BlockingUnaryCall(
                "request",
                new ClientInterceptorContext<string, string>(Get, null, default),
                static (_, _) => "unreachable"));
    }

    /// <summary>The shipped policy with the backoff taken out, so a suite of retries costs no wall clock.</summary>
    private static Resilience Policy() => GrpcResilience.Default with { Backoff = Backoff.None };

    private static ResilienceInterceptor Interceptor(Resilience? policy = null) => new(policy ?? Policy());

    private static AsyncUnaryCall<string> Call(
        ResilienceInterceptor interceptor,
        GrpcScript script,
        CallOptions options = default,
        Method<string, string>? method = null) =>
        interceptor.AsyncUnaryCall(
            "request",
            new ClientInterceptorContext<string, string>(method ?? Get, null, options),
            script.Invoke);
}
