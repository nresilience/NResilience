using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using NResilience.Grpc;

namespace NResilience.Docs;

/// <summary>The gRPC integration: the registration, the classifier, the wire deadline, and repeatability.</summary>
public sealed class GrpcDocs
{
    private static readonly Method<string, string> Watch =
        new(MethodType.ServerStreaming, "orders.Orders", "Watch", Marshallers.StringMarshaller, Marshallers.StringMarshaller);

    private static readonly Method<string, string> Get =
        new(MethodType.Unary, "orders.Orders", "Get", Marshallers.StringMarshaller, Marshallers.StringMarshaller);

    [Fact]
    public void The_registration_is_one_line()
    {
        var services = new ServiceCollection();

        // <snippet:grpc-register>
        services.AddGrpcClient<OrdersClient>(o => o.Address = new Uri("https://orders.internal:5001"))
            .AddGrpcResilience();
        // </snippet:grpc-register>

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<OrdersClient>());
    }

    [Fact]
    public void The_registration_takes_a_policy_and_the_grpc_switches()
    {
        var services = new ServiceCollection();

        // <snippet:grpc-register-options>
        services.AddGrpcClient<OrdersClient>(o => o.Address = new Uri("https://orders.internal:5001"))
            .AddGrpcResilience(
                GrpcResilience.Default with { Attempts = 4 },
                o =>
                {
                    // A charge must not be repeated, whatever the transport says.
                    o.IsRepeatable = static method => method.Name != "ChargeCard";

                    // One breaker per method rather than per service.
                    o.ScopeBy = static method => method.FullName;
                });
        // </snippet:grpc-register-options>

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<OrdersClient>());
    }

    [Fact]
    public void The_classifier_knows_what_a_status_code_means()
    {
        // <snippet:grpc-classifier>
        // GrpcResilience.Default is Resilience.Default with this classifier already on it.
        var classifier = GrpcResilience.Classifier;

        var unavailable = classifier.ClassifyException(new RpcException(new Status(StatusCode.Unavailable, "moving")));
        var notFound = classifier.ClassifyException(new RpcException(new Status(StatusCode.NotFound, "no such order")));
        var exhausted = classifier.ClassifyException(new RpcException(new Status(StatusCode.ResourceExhausted, "quota")));

        Console.WriteLine(unavailable.Kind); // Transient
        Console.WriteLine(notFound.Kind); // Permanent - an answer, not a failure
        Console.WriteLine(exhausted.Kind); // Throttled - the dependency is defending itself
        // </snippet:grpc-classifier>

        Assert.Equal(VerdictKind.Transient, unavailable.Kind);
        Assert.Equal(VerdictKind.Permanent, notFound.Kind);
        Assert.Equal(VerdictKind.Throttled, exhausted.Kind);
    }

    [Fact]
    public void Every_status_is_one_line_to_override()
    {
        // <snippet:grpc-classifier-override>
        // Aborted is a transaction conflict, and whether repeating one is safe depends on the store -
        // so the shipped verdict is Permanent and this is how a store that wants it says so.
        var policy = GrpcResilience.Default with
        {
            Classifier = GrpcResilience.Classifier.On<RpcException>(
                static e => e.StatusCode == StatusCode.Aborted
                    ? Verdict.Transient
                    : GrpcResilience.Classifier.ClassifyException(e)),
        };
        // </snippet:grpc-classifier-override>

        Assert.Equal(VerdictKind.Transient, policy.Classifier.ClassifyException(new RpcException(new Status(StatusCode.Aborted, ""))).Kind);
        Assert.Equal(VerdictKind.Permanent, policy.Classifier.ClassifyException(new RpcException(new Status(StatusCode.NotFound, ""))).Kind);
    }

    [Fact]
    public async Task A_call_site_can_refuse_repetition_without_touching_the_wire()
    {
        var interceptor = new ResilienceInterceptor(GrpcResilience.Default with { Backoff = Backoff.None });
        var script = new Script().Fail(StatusCode.Unavailable);

        // <snippet:grpc-single-shot>
        // Nothing on the wire, and it reaches a generated client that never exposes CallOptions.
        using (GrpcResilience.SingleShot())
        {
            await ChargeAsync();
        }
        // </snippet:grpc-single-shot>

        Assert.Equal(1, script.Calls);

        async Task ChargeAsync()
        {
            using var call = Call(interceptor, script);

            try
            {
                await call.ResponseAsync;
            }
            catch (RpcException)
            {
                // The point of the snippet is the attempt count, not the failure.
            }
        }
    }

    [Fact]
    public void The_wire_deadline_is_the_attempt_ceiling_plus_a_little()
    {
        // <snippet:grpc-deadlines>
        var options = new GrpcResilienceOptions
        {
            // Each attempt's ceiling is written into CallOptions.Deadline, which grpc-dotnet sends
            // as the standard grpc-timeout header. On by default.
            PropagateAttemptDeadline = true,

            // How much longer than the attempt ceiling the wire deadline is set. Not zero: it is
            // what keeps NResilience's own timer ahead of grpc-dotnet's, so a timed-out attempt
            // still produces AttemptTimeoutException.
            DeadlineSlack = TimeSpan.FromMilliseconds(50),

            // HttpClient.Timeout stops competing with the deadline. On by default.
            OwnTransportTimeout = true,
        };
        // </snippet:grpc-deadlines>

        options.Validate();

        Assert.Equal(TimeSpan.FromMilliseconds(50), options.DeadlineSlack);
    }

    [Fact]
    public async Task Guards_are_scoped_per_service_and_readable()
    {
        var interceptor = new ResilienceInterceptor(GrpcResilience.Default with { Backoff = Backoff.None });

        using (var call = Call(interceptor, new Script().Respond("ok")))
            await call.ResponseAsync;

        // <snippet:grpc-breakers>
        // One breaker and one budget per gRPC service by default, keyed by the service's full name -
        // so an operator can be told which dependency opened, not merely that something did.
        foreach (var (service, breaker) in interceptor.Breakers())
            Console.WriteLine($"{service}: {breaker.State}");
        // </snippet:grpc-breakers>

        Assert.Equal(["orders.Orders"], interceptor.Breakers().Keys);
    }

    [Fact]
    public async Task A_server_stream_is_retried_until_its_first_message()
    {
        var interceptor = new ResilienceInterceptor(GrpcResilience.Default with { Backoff = Backoff.None });
        var script = new StreamScript().Fail(StatusCode.Unavailable).Stream("shipped", "delivered");

        using var call = Stream(interceptor, script);

        var received = new List<string>();

        // <snippet:grpc-streaming-consume>
        // Retried until the first message arrives. Everything after it is the enumeration
        // the server is writing, handed over untouched.
        await foreach (var update in call.ResponseStream.ReadAllAsync())
            Console.WriteLine(update);
        // </snippet:grpc-streaming-consume>
        received.Add("read");

        Assert.Equal(2, script.Calls);
        Assert.Single(received);
    }

    private static AsyncServerStreamingCall<string> Stream(ResilienceInterceptor interceptor, StreamScript script) =>
        interceptor.AsyncServerStreamingCall("request", new ClientInterceptorContext<string, string>(Watch, null, default), script.Invoke);

    private static AsyncUnaryCall<string> Call(ResilienceInterceptor interceptor, Script script) =>
        interceptor.AsyncUnaryCall("request", new ClientInterceptorContext<string, string>(Get, null, default), script.Invoke);

    /// <summary>The minimum a generated gRPC client is: a type the factory builds from a call invoker.</summary>
    public sealed class OrdersClient(CallInvoker callInvoker)
    {
        public CallInvoker CallInvoker { get; } = callInvoker;
    }

    /// <summary>A scripted continuation, so the page's samples run with no channel and no codegen.</summary>
    private sealed class Script
    {
        private Func<AsyncUnaryCall<string>>? _step;

        internal int Calls { get; private set; }

        internal Script Respond(string response)
        {
            _step = () => new AsyncUnaryCall<string>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                static () => { });

            return this;
        }

        internal Script Fail(StatusCode status)
        {
            _step = () => new AsyncUnaryCall<string>(
                Task.FromException<string>(new RpcException(new Status(status, string.Empty))),
                Task.FromException<Metadata>(new RpcException(new Status(status, string.Empty))),
                () => new Status(status, string.Empty),
                static () => [],
                static () => { });

            return this;
        }

        internal AsyncUnaryCall<string> Invoke(string request, ClientInterceptorContext<string, string> context)
        {
            Calls++;
            return _step!();
        }
    }

    /// <summary>A scripted server-streaming continuation, on the same terms as <see cref="Script" />.</summary>
    private sealed class StreamScript
    {
        private readonly List<Func<AsyncServerStreamingCall<string>>> _steps = [];

        internal int Calls { get; private set; }

        internal StreamScript Fail(StatusCode status)
        {
            _steps.Add(() => new AsyncServerStreamingCall<string>(
                new Reader([], new Status(status, string.Empty)),
                Task.FromException<Metadata>(new RpcException(new Status(status, string.Empty))),
                () => new Status(status, string.Empty),
                static () => [],
                static () => { }));

            return this;
        }

        internal StreamScript Stream(params string[] messages)
        {
            _steps.Add(() => new AsyncServerStreamingCall<string>(
                new Reader(messages, null),
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                static () => { }));

            return this;
        }

        internal AsyncServerStreamingCall<string> Invoke(string request, ClientInterceptorContext<string, string> context)
        {
            var step = _steps[Math.Min(Calls, _steps.Count - 1)];
            Calls++;

            return step();
        }

        private sealed class Reader(string[] messages, Status? fault) : IAsyncStreamReader<string>
        {
            private int _index = -1;

            public string Current => messages[_index];

            public Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                if (fault is { } status)
                    return Task.FromException<bool>(new RpcException(status));

                if (_index + 1 >= messages.Length)
                    return Task.FromResult(false);

                _index++;
                return Task.FromResult(true);
            }
        }
    }
}
