using Grpc.Core;
using Grpc.Core.Interceptors;
using NResilience.Grpc;

namespace NResilience.Tests;

/// <summary>
///     The gRPC server-streaming wrapper: what it retries, when it stops retrying, what reaches the
///     wire, and what it hands back.
/// </summary>
public sealed class GrpcStreamingTests
{
    private static readonly Method<string, string> Watch =
        new(MethodType.ServerStreaming, "orders.Orders", "Watch", Marshallers.StringMarshaller, Marshallers.StringMarshaller);

    private static readonly Method<string, string> Drain =
        new(MethodType.ServerStreaming, "orders.Orders", "Drain", Marshallers.StringMarshaller, Marshallers.StringMarshaller);

    [Fact]
    public async Task A_failure_before_the_first_message_is_retried()
    {
        var script = new GrpcStreamScript().Fail(StatusCode.Unavailable).Fail(StatusCode.Unavailable).Stream("a", "b");

        using var call = Call(Interceptor(), script);

        Assert.Equal(["a", "b"], await Read(call));
        Assert.Equal(3, script.CallCount);
    }

    [Fact]
    public async Task A_failure_after_the_first_message_belongs_to_the_consumer()
    {
        var script = new GrpcStreamScript().StreamThenFail(["a", "b"], StatusCode.Unavailable);

        using var call = Call(Interceptor(), script);

        var read = new List<string>();

        var failure = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await foreach (var message in call.ResponseStream.ReadAllAsync())
            {
                read.Add(message);
            }
        });

        Assert.Equal(StatusCode.Unavailable, failure.StatusCode);
        Assert.Equal(["a", "b"], read);

        // One call, and no second attempt: the consumer has already acted on two messages, so there
        // is nothing left that a retry could honestly repeat.
        Assert.Equal(1, script.CallCount);
    }

    [Fact]
    public async Task A_permanent_status_is_not_retried()
    {
        var script = new GrpcStreamScript().Fail(StatusCode.NotFound);

        using var call = Call(Interceptor(), script);

        var failure = await Assert.ThrowsAsync<RpcException>(async () => await Read(call));

        Assert.Equal(StatusCode.NotFound, failure.StatusCode);
        Assert.Equal(1, script.CallCount);
    }

    [Fact]
    public async Task A_stream_that_completes_empty_is_a_success()
    {
        var script = new GrpcStreamScript().Stream();

        using var call = Call(Interceptor(), script);

        Assert.Empty(await Read(call));
        Assert.Equal(1, script.CallCount);
    }

    [Fact]
    public async Task The_calls_a_retry_supersedes_are_disposed()
    {
        var script = new GrpcStreamScript().Fail(StatusCode.Unavailable).Fail(StatusCode.Unavailable).Stream("a");

        using var call = Call(Interceptor(), script);

        await Read(call);

        Assert.True(script.Calls[0].Disposed);
        Assert.True(script.Calls[1].Disposed);
    }

    [Fact]
    public async Task The_winning_call_is_disposed_when_the_consumer_is_done()
    {
        var script = new GrpcStreamScript().Stream("a", "b");

        using var call = Call(Interceptor(), script);

        await Read(call);

        Assert.True(script.Calls[0].Disposed);
    }

    [Fact]
    public async Task Breaking_out_of_the_enumeration_disposes_the_call()
    {
        var script = new GrpcStreamScript().Stream("a", "b", "c");

        var call = Call(Interceptor(), script);

        await foreach (var _ in call.ResponseStream.ReadAllAsync())
        {
            break;
        }

        // The reader itself is not disposable - IAsyncStreamReader has no such member - so stopping
        // early and disposing the call is how a consumer says they have read enough.
        call.Dispose();

        Assert.True(script.Calls[0].Disposed);
    }

    [Fact]
    public async Task A_hanging_stream_is_retried_when_the_attempt_ceiling_expires()
    {
        var policy = Policy() with { AttemptTimeout = TimeSpan.FromMilliseconds(50) };
        var script = new GrpcStreamScript().Hang().Stream("a");

        using var call = Call(Interceptor(policy), script);

        Assert.Equal(["a"], await Read(call));
        Assert.Equal(2, script.CallCount);
    }

    [Fact]
    public async Task The_callers_cancellation_is_never_treated_as_a_failure()
    {
        using var caller = new CancellationTokenSource();
        var script = new GrpcStreamScript().Hang();

        using var call = Call(Interceptor(), script, new CallOptions(cancellationToken: caller.Token));

        var reading = Task.Run(async () => await Read(call));

        while (script.CallCount == 0)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reading);
        Assert.Equal(1, script.CallCount);
    }

    [Fact]
    public async Task The_wire_deadline_is_the_remaining_budget_rather_than_the_attempt_ceiling()
    {
        var policy = Policy() with { AttemptTimeout = TimeSpan.FromSeconds(2), Deadline = TimeSpan.FromMinutes(5) };
        var script = new GrpcStreamScript().Stream("a");

        using var call = Call(Interceptor(policy), script);

        await Read(call);

        // The unary path writes the attempt ceiling. A stream cannot: CallOptions.Deadline is fixed
        // when the call starts, and the ceiling bounds only the time to the first message.
        var written = Assert.IsType<DateTime>(script.Seen[0].Deadline);

        Assert.True(written - DateTime.UtcNow > TimeSpan.FromMinutes(4), $"expected the whole budget, got {written - DateTime.UtcNow}");
    }

    [Fact]
    public async Task A_tighter_deadline_the_caller_set_is_not_overwritten()
    {
        var theirs = DateTime.UtcNow.AddSeconds(1);
        var script = new GrpcStreamScript().Stream("a");

        using var call = Call(Interceptor(Policy() with { Deadline = TimeSpan.FromMinutes(5) }), script, new CallOptions(deadline: theirs));

        await Read(call);

        Assert.Equal(theirs, script.Seen[0].Deadline);
    }

    [Fact]
    public async Task A_policy_with_no_deadline_writes_none()
    {
        var script = new GrpcStreamScript().Stream("a");

        using var call = Call(Interceptor(Policy() with { Deadline = Timeout.InfiniteTimeSpan }), script);

        await Read(call);

        Assert.Null(script.Seen[0].Deadline);
    }

    [Fact]
    public async Task Every_attempt_carries_the_nested_retry_marker()
    {
        var script = new GrpcStreamScript().Fail(StatusCode.Unavailable).Stream("a");

        using var call = Call(Interceptor(), script);

        await Read(call);

        Assert.All(script.Seen, options => Assert.Equal(
            NestedRetry.Marker,
            options.Headers?.GetValue("x-nresilience-retrying")));
    }

    [Fact]
    public async Task A_method_that_may_not_be_repeated_gets_one_attempt()
    {
        var options = new GrpcResilienceOptions { RepeatableWhen = static method => method.Name != "Drain" };
        var script = new GrpcStreamScript().Fail(StatusCode.Unavailable);

        using var call = Call(new ResilienceInterceptor(Policy(), options), script, method: Drain);

        await Assert.ThrowsAsync<RpcException>(async () => await Read(call));
        Assert.Equal(1, script.CallCount);
    }

    [Fact]
    public async Task A_single_shot_scope_reaches_a_stream_too()
    {
        var script = new GrpcStreamScript().Fail(StatusCode.Unavailable);
        AsyncServerStreamingCall<string> call;

        using (GrpcResilience.SingleShot())
        {
            call = Call(Interceptor(), script);
        }

        using (call)
        {
            await Assert.ThrowsAsync<RpcException>(async () => await Read(call));
        }

        Assert.Equal(1, script.CallCount);
    }

    [Fact]
    public void A_hedged_policy_is_refused_at_the_call_site()
    {
        var policy = Policy() with { Hedge = Hedge.At() };

        Assert.Throws<ResilienceConfigurationException>(() => Call(Interceptor(policy), new GrpcStreamScript().Stream("a")));
    }

    [Fact]
    public async Task The_headers_are_the_winning_attempts()
    {
        var script = new GrpcStreamScript()
            .Fail(StatusCode.Unavailable)
            .StreamWithHeaders(new Metadata { { "shard", "b" } }, "a");

        using var call = Call(Interceptor(), script);

        await Read(call);

        var headers = await call.ResponseHeadersAsync;

        Assert.Equal("b", headers.GetValue("shard"));
    }

    [Fact]
    public async Task The_headers_of_a_stream_that_never_started_fault_rather_than_hang()
    {
        var script = new GrpcStreamScript().Fail(StatusCode.NotFound);

        using var call = Call(Interceptor(), script);

        await Assert.ThrowsAsync<RpcException>(async () => await Read(call));
        await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseHeadersAsync);
    }

    [Fact]
    public async Task The_status_answers_for_the_call_once_it_is_complete()
    {
        var script = new GrpcStreamScript().Stream("a");

        using var call = Call(Interceptor(), script);

        Assert.Throws<InvalidOperationException>(() => call.GetStatus());

        await Read(call);

        Assert.Equal(StatusCode.OK, call.GetStatus().StatusCode);
        Assert.NotNull(call.GetTrailers());
    }

    [Fact]
    public async Task A_stream_read_after_the_call_was_disposed_says_so()
    {
        var script = new GrpcStreamScript().Stream("a");

        var call = Call(Interceptor(), script);
        call.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await Read(call));
        Assert.Equal(0, script.CallCount);
    }

    [Fact]
    public void Client_streaming_and_duplex_calls_are_not_wrapped_at_all()
    {
        // Passthrough is the inherited implementation, not an override that happens to do nothing:
        // repeating a request stream the failed attempt has already partially consumed is the
        // duplicates-or-buffering problem, not a resilience feature.
        var wrapper = typeof(ResilienceInterceptor);

        Assert.Equal(typeof(Interceptor), wrapper.GetMethod("AsyncClientStreamingCall")!.DeclaringType);
        Assert.Equal(typeof(Interceptor), wrapper.GetMethod("AsyncDuplexStreamingCall")!.DeclaringType);
    }

    [Fact]
    public async Task A_stream_and_a_unary_call_share_the_service_scope()
    {
        var interceptor = Interceptor(Policy() with { Breaker = new Breaker() });

        using (var stream = Call(interceptor, new GrpcStreamScript().Stream("a")))
        {
            await Read(stream);
        }

        Assert.Single(interceptor.Breakers());
        Assert.Equal(["orders.Orders"], interceptor.Breakers().Keys);
    }

    /// <summary>The shipped policy with the backoff taken out, so a suite of retries costs no wall clock.</summary>
    private static Resilience Policy() => GrpcResilience.Default with { Backoff = Backoff.None };

    private static ResilienceInterceptor Interceptor(Resilience? policy = null) => new(policy ?? Policy());

    private static AsyncServerStreamingCall<string> Call(
        ResilienceInterceptor interceptor,
        GrpcStreamScript script,
        CallOptions options = default,
        Method<string, string>? method = null) =>
        interceptor.AsyncServerStreamingCall(
            "request",
            new ClientInterceptorContext<string, string>(method ?? Watch, null, options),
            script.Invoke);

    private static async Task<List<string>> Read(AsyncServerStreamingCall<string> call)
    {
        var read = new List<string>();

        await foreach (var message in call.ResponseStream.ReadAllAsync())
        {
            read.Add(message);
        }

        return read;
    }
}
