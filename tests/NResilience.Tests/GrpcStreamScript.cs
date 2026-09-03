using Grpc.Core;
using Grpc.Core.Interceptors;

namespace NResilience.Tests;

/// <summary>
///     A scripted server-streaming continuation: one <see cref="AsyncServerStreamingCall{T}" /> per
///     attempt, with no channel, no socket and no codegen behind it.
/// </summary>
internal sealed class GrpcStreamScript
{
    private readonly List<Func<CallOptions, ScriptedStreamCall>> _steps = [];

    /// <summary>Every call this script produced, in order, so a test can ask which were disposed.</summary>
    internal List<ScriptedStreamCall> Calls { get; } = [];

    /// <summary>The <see cref="CallOptions" /> each attempt was made with: headers, deadline, token.</summary>
    internal List<CallOptions> Seen { get; } = [];

    internal int CallCount => Seen.Count;

    /// <summary>Fails before the first message, which is the window a stream is retried in.</summary>
    internal GrpcStreamScript Fail(StatusCode status, string detail = "")
    {
        _steps.Add(_ => ScriptedStreamCall.Failing(new Status(status, detail)));
        return this;
    }

    /// <summary>Streams the messages and completes.</summary>
    internal GrpcStreamScript Stream(params string[] messages)
    {
        _steps.Add(_ => ScriptedStreamCall.Streaming(messages, null, []));
        return this;
    }

    /// <summary>Streams the messages, then faults - the post-start failure that belongs to the consumer.</summary>
    internal GrpcStreamScript StreamThenFail(string[] messages, StatusCode status)
    {
        _steps.Add(_ => ScriptedStreamCall.Streaming(messages, new Status(status, string.Empty), []));
        return this;
    }

    /// <summary>Streams the messages with response headers of its own, so a test can ask whose headers won.</summary>
    internal GrpcStreamScript StreamWithHeaders(Metadata headers, params string[] messages)
    {
        _steps.Add(_ => ScriptedStreamCall.Streaming(messages, null, headers));
        return this;
    }

    /// <summary>Never produces a first message until the attempt's token is cancelled.</summary>
    internal GrpcStreamScript Hang()
    {
        _steps.Add(ScriptedStreamCall.Hanging);
        return this;
    }

    internal AsyncServerStreamingCall<string> Invoke(string request, ClientInterceptorContext<string, string> context)
    {
        var index = Seen.Count;
        Seen.Add(context.Options);

        var call = _steps[Math.Min(index, _steps.Count - 1)](context.Options);
        Calls.Add(call);

        return call.Call;
    }
}

/// <summary>One scripted server-streaming call, and whether anybody disposed it.</summary>
internal sealed class ScriptedStreamCall
{
    private ScriptedStreamCall(IAsyncStreamReader<string> reader, Task<Metadata> headers, Func<Status> status, Func<Metadata> trailers)
    {
        Trailers = [];
        Call = new AsyncServerStreamingCall<string>(reader, headers, status, trailers, () => Disposed = true);
    }

    internal AsyncServerStreamingCall<string> Call { get; }

    internal Metadata Trailers { get; }

    internal bool Disposed { get; private set; }

    internal static ScriptedStreamCall Failing(Status status)
    {
        ScriptedStreamCall? built = null;

        built = new ScriptedStreamCall(
            new FailingReader(status),
            Task.FromException<Metadata>(new RpcException(status)),
            () => status,
            () => built!.Trailers);

        return built;
    }

    internal static ScriptedStreamCall Streaming(string[] messages, Status? fault, Metadata headers)
    {
        ScriptedStreamCall? built = null;

        built = new ScriptedStreamCall(
            new ScriptedReader(messages, fault),
            Task.FromResult(headers),
            () => fault ?? Status.DefaultSuccess,
            () => built!.Trailers);

        return built;
    }

    /// <summary>
    ///     A stream whose first message never arrives, and whose status and trailers therefore throw -
    ///     which is what grpc-dotnet's own call object does before completion.
    /// </summary>
    internal static ScriptedStreamCall Hanging(CallOptions options) =>
        new(
            new HangingReader(),
            new TaskCompletionSource<Metadata>(TaskCreationOptions.RunContinuationsAsynchronously).Task,
            () => throw new InvalidOperationException("Unable to get the status because the call is not complete."),
            () => throw new InvalidOperationException("Can't get the call trailers because the call has not completed successfully."));

    private sealed class FailingReader(Status status) : IAsyncStreamReader<string>
    {
        public string Current => throw new InvalidOperationException("No current element is available.");

        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromException<bool>(new RpcException(status));
    }

    private sealed class ScriptedReader(string[] messages, Status? fault) : IAsyncStreamReader<string>
    {
        private int _index = -1;

        public string Current => messages[_index];

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_index + 1 < messages.Length)
            {
                _index++;
                return Task.FromResult(true);
            }

            return fault is { } status
                ? Task.FromException<bool>(new RpcException(status))
                : Task.FromResult(false);
        }
    }

    /// <summary>Completes only when the attempt's token does, the way a real call surfaces cancellation.</summary>
    private sealed class HangingReader : IAsyncStreamReader<string>
    {
        public string Current => throw new InvalidOperationException("No current element is available.");

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
            {
                await cancelled.Task.ConfigureAwait(false);
            }

            throw new RpcException(new Status(StatusCode.Cancelled, "Call canceled by the client."));
        }
    }
}
