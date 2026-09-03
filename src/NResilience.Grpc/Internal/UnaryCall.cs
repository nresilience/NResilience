using Grpc.Core;
using Grpc.Core.Interceptors;

namespace NResilience.Grpc.Internal;

/// <summary>
///     One logical unary call: the policy runs around it, and this is the thing that survives long
///     enough to answer for it.
///     <para>
///         The piece with no HTTP analog. <c>SendAsync</c> returns a <c>Task</c>, so the resilience
///         handler's job ends when the policy's task does;
///         <see cref="Interceptor.AsyncUnaryCall{TRequest,TResponse}" /> is <b>synchronous</b> and has
///         to hand back an <see cref="AsyncUnaryCall{TResponse}" /> - a response task, a headers task,
///         a status function, a trailers function and a dispose action - before any attempt has run.
///         Every one of those five has to end up describing the attempt that actually won.
///     </para>
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
internal sealed class UnaryCall<TRequest, TResponse>
    where TRequest : class
    where TResponse : class
{
    private readonly CallOptions _callerOptions;
    private readonly Interceptor.AsyncUnaryCallContinuation<TRequest, TResponse> _continuation;

    /// <summary>The effective deadline for the whole call, resolved once, ambient clamp included.</summary>
    private readonly TimeSpan _deadline;

    private readonly object _gate = new();
    private readonly TaskCompletionSource<Metadata> _headers = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _host;
    private readonly Method<TRequest, TResponse> _method;
    private readonly GrpcResilienceOptions _options;

    private readonly TRequest _request;

    /// <summary>
    ///     The call-scoped source. Linked to the caller's token so the executor's own attempt sources
    ///     chain off it, and cancelled by <see cref="Dispose" /> so a caller who drops the call before
    ///     it completes stops the retry loop rather than leaving it running behind them.
    /// </summary>
    private readonly CancellationTokenSource _running;

    /// <summary>Whether the marker is stamped and read. Retrying calls only, as for HTTP.</summary>
    private readonly bool _stamping;

    private readonly long _start;
    private readonly TimeProvider _time;

    private volatile bool _complete;

    /// <summary>The last failed attempt's status and trailers, read before it can be superseded.</summary>
    private Status? _lastStatus;

    private Metadata? _lastTrailers;

    private AsyncUnaryCall<TResponse>? _winner;
    private bool _won;

    internal UnaryCall(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context, Interceptor.AsyncUnaryCallContinuation<TRequest, TResponse> continuation,
        Resilience policy,
        GrpcResilienceOptions options,
        bool retrying)
    {
        _request = request;
        _method = context.Method;
        _host = context.Host;
        _callerOptions = context.Options;
        _continuation = continuation;
        Policy = policy;
        _options = options;
        _stamping = retrying && options.DetectNestedRetries;
        _time = policy.Time;

        // The same clamp the executor is about to apply, taken here as well because the wire deadline
        // has to be computed before the executor has started. ResilienceDeadline.Remaining is the
        // public half of what the executor reads.
        _deadline = GrpcCall.DeadlineFor(policy);

        _start = _time.GetTimestamp();

        _running = _callerOptions.CancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_callerOptions.CancellationToken)
            : new CancellationTokenSource();
    }

    internal Resilience Policy { get; }

    /// <summary>Whether the caller's own metadata already carries the marker, so it is not stamped twice.</summary>
    internal bool CarriesRetryMarker => GrpcCall.CarriesRetryMarker(_callerOptions);

    /// <summary>
    ///     The call object handed back to the generated client, built from the five things it needs
    ///     and nothing else.
    /// </summary>
    internal AsyncUnaryCall<TResponse> ToCall(Task<TResponse> response) =>
        new(response, _headers.Task, GetStatus, GetTrailers, Dispose);

    /// <summary>
    ///     Runs the policy around the call and settles everything the returned call object reads. The
    ///     ambient nested-retry flag is published for the duration, so an inner retrying client can
    ///     see it without a header.
    /// </summary>
    internal async Task<TResponse> RunAsync(AsyncLocal<bool> insideRetryingClient)
    {
        var wasInside = insideRetryingClient.Value;

        if (_stamping)
            insideRetryingClient.Value = true;

        try
        {
            return await Policy.RunAsync(static (call, token) => call.AttemptAsync(token), this, _running.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Never leave the headers pending: a caller who awaits ResponseHeadersAsync on a call
            // that has already failed would otherwise hang forever. Observed immediately, because
            // nobody is required to await it.
            if (_headers.TrySetException(exception))
                _ = _headers.Task.Exception;

            // Nobody is going to receive this response, so nobody is going to dispose it. The status
            // and trailers were captured before it went, so the call object still answers for it.
            DisposeWinner();
            throw;
        }
        finally
        {
            // Before the returned task completes, so a continuation that immediately calls
            // GetStatus() sees a call that is complete.
            _complete = true;

            if (_stamping)
            {
                // Restored rather than cleared: AsyncLocal changes in a child context do not flow back
                // to the parent, and leaving this context exactly as it was found is the correct
                // invariant either way.
                insideRetryingClient.Value = wasInside;
            }

            // Disposed here rather than in Dispose(), so no attempt can ever ask a disposed source
            // for a linked token: by this point the policy has finished making them.
            _running.Dispose();
        }
    }

    /// <summary>One attempt: a fresh gRPC call, with this attempt's deadline, token and metadata.</summary>
    private async Task<TResponse> AttemptAsync(CancellationToken attemptToken)
    {
        var options = PerAttempt(attemptToken, out var ours, out var ceiling);
        var call = _continuation(_request, new ClientInterceptorContext<TRequest, TResponse>(_method, _host, options));

        try
        {
            var response = await call.ResponseAsync.ConfigureAwait(false);

            Keep(call, true);

            try
            {
                _headers.TrySetResult(await call.ResponseHeadersAsync.ConfigureAwait(false));
            }
            catch (Exception)
            {
                // The response is in hand, so the headers are too on any real call. A fake or a
                // transport that faults this task separately must not turn a successful call into a
                // failed one; the caller's ResponseHeadersAsync simply stays unset until the
                // failure path faults it, and it cannot reach that path from here.
            }

            return response;
        }
        catch (RpcException failure)
        {
            Capture(call, failure);
            Keep(call, false);

            // Our own cancellation, arriving as grpc-dotnet maps it. Rethrown in the shape the
            // executor's catch ladder judges - it disambiguates caller cancellation from its own
            // attempt timeout by asking which source fired, and an RpcException tells it nothing.
            if (failure.StatusCode == StatusCode.Cancelled && attemptToken.IsCancellationRequested)
                throw new OperationCanceledException(failure.Message, failure, attemptToken);

            if (failure.StatusCode == StatusCode.DeadlineExceeded && ours)
            {
                // The deadline that fired is the one this interceptor wrote, which is the attempt
                // ceiling plus DeadlineSlack - so the executor's own timer has all but certainly
                // fired first and this is the ordinary timeout path.
                if (attemptToken.IsCancellationRequested)
                    throw new OperationCanceledException(failure.Message, failure, attemptToken);

                // The race the slack exists to prevent, lost anyway: grpc-dotnet's timer noticed the
                // ceiling before ours did. The caller still gets AttemptTimeoutException, classified
                // transient by Classifier.Default's TimeoutException rule, so the outcome matches the
                // ordinary path. What is not recoverable from out here is the executor's own
                // deadline-spent accounting, which is why the slack is not zero.
                throw new AttemptTimeoutException(ceiling, failure);
            }

            throw;
        }
        catch (Exception failure)
        {
            Capture(call, failure);
            Keep(call, false);
            throw;
        }
    }

    /// <summary>
    ///     This attempt's <see cref="CallOptions" />: the attempt token, fresh metadata, and the wire
    ///     deadline.
    /// </summary>
    /// <param name="attemptToken">The executor's token for this attempt.</param>
    /// <param name="ours">Whether the deadline on the call is the one written here.</param>
    /// <param name="ceiling">The attempt ceiling the deadline was derived from.</param>
    private CallOptions PerAttempt(CancellationToken attemptToken, out bool ours, out TimeSpan ceiling)
    {
        // A struct with With* methods that return copies, so nothing the caller handed in is mutated.
        var options = _callerOptions.WithCancellationToken(attemptToken);

        if (_stamping && !CarriesRetryMarker)
            options = GrpcCall.Stamp(options);

        ours = false;
        ceiling = Timeout.InfiniteTimeSpan;

        if (!_options.PropagateAttemptDeadline)
            return options;

        ceiling = GrpcCall.Tighter(Policy.AttemptTimeout, Remaining());

        if (ceiling == Timeout.InfiniteTimeSpan)
            return options;

        // grpc-dotnet enforces this locally with a timer of its own and converts it to the standard
        // grpc-timeout header for the peer. The slack is what keeps the executor's timer ahead of it;
        // see GrpcResilienceOptions.DeadlineSlack.
        var wire = DateTime.UtcNow + ceiling + _options.DeadlineSlack;

        // A deadline the caller set is never overwritten - whichever of the two is tighter wins, and
        // when it is theirs the flag stays false so a DeadlineExceeded from it reaches the classifier
        // as the transient status it is.
        if (_callerOptions.Deadline is { } theirs && theirs <= wire)
            return options;

        ours = true;
        return options.WithDeadline(wire);
    }

    /// <summary>
    ///     Records which attempt the returned call object answers for. A successful attempt wins and
    ///     is never replaced; a failed one holds the place until a later attempt takes it. Everything
    ///     it supersedes is disposed here, which is the gRPC analog of disposing the response a retry
    ///     supersedes.
    /// </summary>
    private void Keep(AsyncUnaryCall<TResponse> call, bool success)
    {
        AsyncUnaryCall<TResponse>? superseded;

        lock (_gate)
        {
            if (_won)
            {
                // A success is already in hand. Only a hedge's losing copy reaches this.
                superseded = call;
            }
            else
            {
                superseded = _winner;
                _winner = call;
                _won = success;
            }
        }

        superseded?.Dispose();
    }

    /// <summary>
    ///     Reads a failed attempt's status and trailers while they are still readable, so the call
    ///     object can answer for a call that failed every attempt and whose last gRPC call has been
    ///     disposed.
    /// </summary>
    private void Capture(AsyncUnaryCall<TResponse> call, Exception failure)
    {
        Status? status = null;
        Metadata? trailers = null;

        try
        {
            status = call.GetStatus();
            trailers = call.GetTrailers();
        }
        catch (InvalidOperationException)
        {
            // The attempt did not get far enough to have either - a cancelled call, or a fake. The
            // RpcException carries the status in that case, and there are no trailers to have.
            if (failure is RpcException rpc)
            {
                status = rpc.Status;
                trailers = rpc.Trailers;
            }
        }

        lock (_gate)
        {
            if (_won)
                return;

            _lastStatus = status;
            _lastTrailers = trailers;
        }
    }

    /// <summary>
    ///     The winning attempt's status. Before the call completes this throws
    ///     <see cref="InvalidOperationException" />, which is what grpc-dotnet's own call object does -
    ///     reproduced rather than replaced with something friendlier, because generated clients and
    ///     downstream interceptors are written against it.
    /// </summary>
    private Status GetStatus()
    {
        if (!_complete)
            throw new InvalidOperationException("Unable to get the status because the call is not complete.");

        lock (_gate)
        {
            if (_won && _winner is { } winner)
                return winner.GetStatus();

            if (_lastStatus is { } status)
                return status;
        }

        throw new InvalidOperationException("Unable to get the status because the call did not complete an attempt.");
    }

    /// <summary>The winning attempt's trailers, on the same terms as <see cref="GetStatus" />.</summary>
    private Metadata GetTrailers()
    {
        if (!_complete)
            throw new InvalidOperationException("Can't get the call trailers because the call has not completed successfully.");

        lock (_gate)
        {
            if (_won && _winner is { } winner)
                return winner.GetTrailers();

            if (_lastTrailers is { } trailers)
                return trailers;
        }

        throw new InvalidOperationException("Can't get the call trailers because the call has not completed successfully.");
    }

    /// <summary>
    ///     Disposes the winning attempt, and stops the retry loop when the caller disposes the call
    ///     before it has finished.
    /// </summary>
    private void Dispose()
    {
        if (!_complete)
        {
            try
            {
                _running.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The policy finished between the check and the call. Nothing left to stop.
            }
        }

        DisposeWinner();
    }

    private void DisposeWinner()
    {
        AsyncUnaryCall<TResponse>? winner;

        lock (_gate)
        {
            winner = _winner;
            _winner = null;
        }

        winner?.Dispose();
    }

    /// <summary>How much of the call's deadline is left, or <see cref="Timeout.InfiniteTimeSpan" /> when it has none.</summary>
    private TimeSpan Remaining() => GrpcCall.Remaining(_time, _start, _deadline);
}
