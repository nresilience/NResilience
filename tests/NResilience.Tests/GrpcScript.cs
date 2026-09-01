using Grpc.Core;
using Grpc.Core.Interceptors;

namespace NResilience.Tests;

/// <summary>
///     A scripted gRPC continuation: the interceptor's outer seam, with no channel, no socket and no
///     codegen behind it.
///     <para>
///         <see cref="Marshallers.StringMarshaller" /> supplies the request and response type, so the
///         behavioral suite needs no protobuf either - nothing is ever serialized, because nothing is
///         ever sent.
///     </para>
/// </summary>
internal sealed class GrpcScript
{
    private readonly List<Func<CallOptions, ScriptedCall>> _steps = [];

    /// <summary>Every call this script produced, in order, so a test can ask which were disposed.</summary>
    internal List<ScriptedCall> Calls { get; } = [];

    /// <summary>The <see cref="CallOptions" /> each attempt was made with: headers, deadline, token.</summary>
    internal List<CallOptions> Seen { get; } = [];

    internal int CallCount => Seen.Count;

    /// <summary>Responds successfully. The last step repeats once the script runs out.</summary>
    internal GrpcScript Respond(string response, Metadata? headers = null)
    {
        _steps.Add(_ => ScriptedCall.Succeeding(response, headers ?? []));
        return this;
    }

    /// <summary>Fails with a gRPC status, the way grpc-dotnet surfaces one.</summary>
    internal GrpcScript Fail(StatusCode status, string detail = "")
    {
        _steps.Add(_ => ScriptedCall.Failing(new Status(status, detail)));
        return this;
    }

    /// <summary>Runs an action against the attempt's options first - cancelling a token, recording a deadline - then fails.</summary>
    internal GrpcScript FailAfter(Action<CallOptions> before, StatusCode status)
    {
        _steps.Add(options =>
        {
            before(options);
            return ScriptedCall.Failing(new Status(status, string.Empty));
        });

        return this;
    }

    /// <summary>
    ///     Never completes until the attempt's token is cancelled, which is what a real call does:
    ///     grpc-dotnet surfaces a cancelled token as an <see cref="RpcException" />.
    /// </summary>
    internal GrpcScript Hang()
    {
        _steps.Add(ScriptedCall.Hanging);
        return this;
    }

    internal AsyncUnaryCall<string> Invoke(string request, ClientInterceptorContext<string, string> context)
    {
        var index = Seen.Count;
        Seen.Add(context.Options);

        var call = _steps[Math.Min(index, _steps.Count - 1)](context.Options);
        Calls.Add(call);

        return call.Call;
    }
}

/// <summary>One scripted gRPC call, and whether anybody disposed it.</summary>
internal sealed class ScriptedCall
{
    private ScriptedCall(Task<string> response, Task<Metadata> headers, Func<Status> status, Func<Metadata> trailers)
    {
        Trailers = [];
        Call = new AsyncUnaryCall<string>(response, headers, status, trailers, () => Disposed = true);
    }

    internal AsyncUnaryCall<string> Call { get; }

    internal Metadata Trailers { get; }

    internal bool Disposed { get; private set; }

    internal static ScriptedCall Succeeding(string response, Metadata headers)
    {
        ScriptedCall? built = null;

        built = new ScriptedCall(
            Task.FromResult(response),
            Task.FromResult(headers),
            () => Status.DefaultSuccess,
            () => built!.Trailers);

        return built;
    }

    internal static ScriptedCall Failing(Status status)
    {
        ScriptedCall? built = null;

        built = new ScriptedCall(
            Task.FromException<string>(new RpcException(status)),
            Task.FromException<Metadata>(new RpcException(status)),
            () => status,
            () => built!.Trailers);

        return built;
    }

    /// <summary>
    ///     A call that never finishes, and whose status and trailers therefore throw - which is what
    ///     grpc-dotnet's own call object does before completion.
    /// </summary>
    internal static ScriptedCall Hanging(CallOptions options)
    {
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var headers = new TaskCompletionSource<Metadata>(TaskCreationOptions.RunContinuationsAsynchronously);

        var cancelled = new RpcException(new Status(StatusCode.Cancelled, "Call canceled by the client."));

        options.CancellationToken.Register(() =>
        {
            response.TrySetException(cancelled);
            headers.TrySetException(cancelled);
        });

        return new ScriptedCall(
            response.Task,
            headers.Task,
            () => throw new InvalidOperationException("Unable to get the status because the call is not complete."),
            () => throw new InvalidOperationException("Can\'t get the call trailers because the call has not completed successfully."));
    }
}
