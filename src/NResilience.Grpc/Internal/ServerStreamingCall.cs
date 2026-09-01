using Grpc.Core;
using Grpc.Core.Interceptors;

namespace NResilience.Grpc.Internal;

/// <summary>
///     One logical server-streaming call: the policy's streaming path runs around it, and this is
///     the thing that answers for it afterwards.
///     <para>
///         The semantic is the core library's, unchanged: an attempt is <i>start the call and pull
///         one message</i>, and the first message is the success point. Before it, a stream is
///         indistinguishable from a call - a reset, a deadline, a throttling reply - and the
///         classifier judges it. After it, every fault belongs to the consumer, because a retry
///         would duplicate messages they have already acted on.
///     </para>
///     <para>
///         Two things differ from <see cref="UnaryCall{TRequest,TResponse}" />, and both come from
///         the same fact - the winning attempt outlives the attempt loop:
///         <list type="number">
///             <item>
///                 <description>
///                     The wire deadline is the <b>remaining whole-call budget</b>, not the attempt
///                     ceiling. <c>CallOptions.Deadline</c> is fixed when the call starts and cannot
///                     be moved afterwards, and <c>AttemptTimeout</c> bounds only the time to the
///                     first message - so writing the ceiling would kill a healthy stream mid-flight.
///                 </description>
///             </item>
///             <item>
///                 <description>
///                     The cancellation ladder has one rung, not two. A <c>DeadlineExceeded</c> is
///                     never translated: before the first message it is the whole call's budget
///                     expiring, which the executor's own deadline check stops the loop on, and after
///                     it, it is the consumer's, like every other post-start fault.
///                 </description>
///             </item>
///         </list>
///     </para>
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
internal sealed class ServerStreamingCall<TRequest, TResponse>
    where TRequest : class
    where TResponse : class
{
    private readonly CallOptions _callerOptions;
    private readonly Interceptor.AsyncServerStreamingCallContinuation<TRequest, TResponse> _continuation;

    /// <summary>The effective deadline for the whole call, resolved once, ambient clamp included.</summary>
    private readonly TimeSpan _deadline;

    private readonly object _gate = new();
    private readonly TaskCompletionSource<Metadata> _headers = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _host;
    private readonly Method<TRequest, TResponse> _method;
    private readonly GrpcResilienceOptions _options;

    /// <summary>
    ///     The call-scoped source. Cancelled by <see cref="Dispose" />, so a consumer who drops the
    ///     call - the ordinary way to stop reading a stream early - stops the enumeration rather than
    ///     leaving it running behind them.
    /// </summary>
    private readonly CancellationTokenSource _running;

    private readonly TRequest _request;

    /// <summary>Whether the marker is stamped and read. Retrying calls only, as for HTTP.</summary>
    private readonly bool _stamping;

    private readonly long _start;
    private readonly TimeProvider _time;

    private bool _callDisposed;
    private volatile bool _complete;
    private bool _sourceDisposed;
    private bool _streamDone;
    private bool _streamStarted;

    /// <summary>The reader the call object hands out, so disposing the call can stop it.</summary>
    private ResponseStream? _reader;

    /// <summary>
    ///     The last attempt's status and trailers, read as that attempt is torn down - which is the
    ///     only moment they are still readable, since tearing an attempt down disposes its gRPC call.
    /// </summary>
    private Status? _status;

    private Metadata? _trailers;

    internal ServerStreamingCall(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        Interceptor.AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation,
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
    ///     The call object handed back to the generated client. Built before any attempt has run, and
    ///     every part of it ends up describing the attempt that won.
    /// </summary>
    /// <remarks>
    ///     <c>RunAsync</c> is called here rather than lazily, so a policy that cannot run a stream at
    ///     all - one carrying a <c>Hedge</c> - throws at the call site rather than three frames later
    ///     inside somebody's <c>await foreach</c>.
    /// </remarks>
    internal AsyncServerStreamingCall<TResponse> ToCall()
    {
        var stream = Policy.RunAsync(static (call, token) => call.Attempt(token), this, _running.Token);
        _reader = new ResponseStream(this, stream);

        return new AsyncServerStreamingCall<TResponse>(_reader, _headers.Task, GetStatus, GetTrailers, Dispose);
    }

    /// <summary>
    ///     One attempt, as a cold source: enumerating it starts a fresh gRPC call, so every retry is
    ///     a new call with fresh metadata and a fresh deadline.
    /// </summary>
    /// <remarks>
    ///     A hand-written enumerator rather than the <c>async IAsyncEnumerable</c> iterator the shape
    ///     invites, for one reason: C# forbids <c>yield return</c> inside a <c>try</c> with a
    ///     <c>catch</c>, so an iterator cannot translate the cancellation that arrives on the
    ///     <b>first</b> pull - and that translation is what tells the executor "this was my ceiling"
    ///     rather than handing it an <see cref="RpcException" /> that says nothing. The enumerator
    ///     splits the two cases explicitly instead, which is the same rule the compiler was
    ///     enforcing: only the first pull is classified.
    /// </remarks>
    private IAsyncEnumerable<TResponse> Attempt(CancellationToken attemptToken) => new Attempts(this, attemptToken);

    /// <summary>
    ///     This attempt's <see cref="CallOptions" />: the attempt token, fresh metadata, and the wire
    ///     deadline - which for a stream is the whole call's remaining budget.
    /// </summary>
    private CallOptions PerAttempt(CancellationToken attemptToken)
    {
        var options = _callerOptions.WithCancellationToken(attemptToken);

        if (_stamping && !CarriesRetryMarker)
            options = GrpcCall.Stamp(options);

        if (!_options.PropagateAttemptDeadline)
            return options;

        var remaining = GrpcCall.Remaining(_time, _start, _deadline);

        if (remaining == Timeout.InfiniteTimeSpan)
            return options;

        // The whole budget, not AttemptTimeout: a deadline is fixed at call start, and the ceiling
        // bounds only the time to the first message. The slack is the same slack the unary path
        // uses - it keeps our own timers ahead of grpc-dotnet's.
        var wire = DateTime.UtcNow + remaining + _options.DeadlineSlack;

        // A deadline the caller set is never overwritten - whichever of the two is tighter wins.
        if (_callerOptions.Deadline is { } theirs && theirs <= wire)
            return options;

        return options.WithDeadline(wire);
    }

    /// <summary>
    ///     Records the headers of the attempt that produced a first message. <c>TrySet</c>, because
    ///     only the first attempt to get this far can be the winner: the executor abandons an
    ///     attempt's enumerator before it starts another.
    /// </summary>
    private void Publish(Metadata headers) => _headers.TrySetResult(headers);

    /// <summary>
    ///     Reads an attempt's status and trailers while they are still readable - immediately before
    ///     its gRPC call is disposed - so the call object can still answer for a call whose attempts
    ///     have all been torn down.
    /// </summary>
    private void Capture(AsyncServerStreamingCall<TResponse> call, Exception? failure)
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
            _status = status;
            _trailers = trailers;
        }
    }

    /// <summary>
    ///     Claims the right to enumerate. Refused once the call object has been disposed, which is
    ///     what keeps <see cref="_running" /> from being torn down under an attempt that is about to
    ///     ask it for a linked token.
    /// </summary>
    private bool TryStart()
    {
        lock (_gate)
        {
            if (_callDisposed)
                return false;

            _streamStarted = true;
            return true;
        }
    }

    /// <summary>The enumeration is over, one way or another: the call object may answer from here on.</summary>
    private void Completed(Exception? failure)
    {
        // Never leave the headers pending: a consumer awaiting ResponseHeadersAsync on a stream that
        // failed every attempt would otherwise wait forever. Observed immediately, because nobody is
        // required to await it.
        if (failure is not null && _headers.TrySetException(failure))
            _ = _headers.Task.Exception;

        _complete = true;

        // Safe here and nowhere earlier: the policy's iterator has been disposed by the caller of
        // this method, so nothing is going to ask this source for another linked token.
        lock (_gate)
            _streamDone = true;

        DisposeSource();
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
            if (_status is { } status)
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
            if (_trailers is { } trailers)
                return trailers;
        }

        throw new InvalidOperationException("Can't get the call trailers because the call has not completed successfully.");
    }

    /// <summary>
    ///     Stops the enumeration. Disposing a server-streaming call before its stream is exhausted is
    ///     how a consumer says "I have read enough", so this cancels rather than merely releasing:
    ///     the cancellation unwinds whichever attempt is in flight, and unwinding it disposes the
    ///     gRPC call underneath.
    /// </summary>
    private void Dispose()
    {
        bool release;

        lock (_gate)
        {
            _callDisposed = true;

            // Released here only when nothing is running: either the enumeration is over, or it
            // never began and TryStart will now refuse to begin one.
            release = _streamDone || !_streamStarted;
        }

        try
        {
            _running.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down. Nothing left to stop.
        }

        // Cancellation alone does not run any cleanup when nobody is reading: a consumer who stops
        // enumerating and disposes the call is the ordinary way to end a stream early, and there is
        // no await left to unwind. Abandoning the reader is what releases the gRPC call underneath.
        _reader?.Abandon();

        if (release)
            DisposeSource();
    }

    /// <summary>Disposes the call-scoped source once, whichever side gets there last.</summary>
    private void DisposeSource()
    {
        lock (_gate)
        {
            if (_sourceDisposed)
                return;

            _sourceDisposed = true;
        }

        _running.Dispose();
    }

    /// <summary>The cold source: one gRPC call per enumeration, which is one per attempt.</summary>
    private sealed class Attempts(ServerStreamingCall<TRequest, TResponse> owner, CancellationToken attemptToken)
        : IAsyncEnumerable<TResponse>
    {
        public IAsyncEnumerator<TResponse> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(owner, attemptToken.CanBeCanceled ? attemptToken : cancellationToken);
    }

    /// <summary>
    ///     One attempt's enumerator: the gRPC call, its response stream, and the one place the two
    ///     halves of the streaming semantic are told apart.
    /// </summary>
    private sealed class Enumerator(ServerStreamingCall<TRequest, TResponse> owner, CancellationToken attemptToken)
        : IAsyncEnumerator<TResponse>
    {
        private AsyncServerStreamingCall<TResponse>? _call;
        private bool _disposed;
        private bool _started;

        public TResponse Current => _call is { } call
            ? call.ResponseStream.Current
            : throw new InvalidOperationException("The enumeration has not started.");

        public async ValueTask<bool> MoveNextAsync()
        {
            if (_disposed)
                return false;

            if (_started)
            {
                // Past the first message. Nothing is classified, nothing is translated, and nothing
                // is retried: this is the consumer's enumeration now, and a fault in it is theirs -
                // the whole reason the narrow streaming semantic is the only honest one.
                return await _call!.ResponseStream.MoveNext(attemptToken).ConfigureAwait(false);
            }

            _started = true;

            // The call is started here rather than in the constructor, so that a source nobody
            // enumerates makes no gRPC call at all.
            var call = _call = owner._continuation(
                owner._request,
                new ClientInterceptorContext<TRequest, TResponse>(owner._method, owner._host, owner.PerAttempt(attemptToken)));

            bool moved;

            try
            {
                moved = await call.ResponseStream.MoveNext(attemptToken).ConfigureAwait(false);
            }
            catch (RpcException failure) when (failure.StatusCode == StatusCode.Cancelled && attemptToken.IsCancellationRequested)
            {
                // Our own cancellation - the caller's token, or this attempt's ceiling on the time to
                // the first message - arriving as grpc-dotnet maps it. Rethrown in the shape the
                // executor's catch ladder judges: it tells the two apart by asking which source
                // fired, and an RpcException tells it nothing.
                //
                // There is deliberately no DeadlineExceeded rung here. A stream's deadline is its
                // whole budget, so a DeadlineExceeded before the first message is the call running
                // out of time - classified transient, and stopped by the executor's own deadline
                // check on the next pass rather than translated here.
                throw new OperationCanceledException(failure.Message, failure, attemptToken);
            }

            if (moved)
            {
                // The success point. Publishing the headers here is what makes ResponseHeadersAsync
                // describe the attempt that won: every earlier attempt was torn down before this
                // line, and no later one exists.
                if (call.ResponseHeadersAsync is { IsCompletedSuccessfully: true } headers)
                    owner.Publish(headers.Result);
                else
                    await PublishAsync(call).ConfigureAwait(false);
            }

            return moved;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_call is not { } call)
                return;

            // The status and trailers are read before the call is disposed, because disposing it is
            // what makes them unreadable. A losing attempt is disposed by the executor before the
            // next one starts; the winner is disposed when the consumer stops reading.
            owner.Capture(call, null);
            call.Dispose();
        }

        /// <summary>
        ///     The headers of a call whose first message is already in hand. On a real call they have
        ///     arrived by definition; the await is for the transport that completes the task
        ///     separately, and the failure is swallowed because a headers task nobody promised must
        ///     not turn a stream that is working into one that failed.
        /// </summary>
        private async ValueTask PublishAsync(AsyncServerStreamingCall<TResponse> call)
        {
            try
            {
                owner.Publish(await call.ResponseHeadersAsync.ConfigureAwait(false));
            }
            catch (Exception)
            {
                // See above. The consumer's ResponseHeadersAsync simply stays unset until the
                // enumeration ends, and cannot reach the failure path from here.
            }
        }
    }

    /// <summary>
    ///     The adapter the override's return type requires: <c>IAsyncEnumerable&lt;T&gt;</c> is what
    ///     the policy produces, and <see cref="IAsyncStreamReader{T}" /> is what a generated client
    ///     reads. One enumerator, and the pull-model translation between them.
    /// </summary>
    private sealed class ResponseStream(ServerStreamingCall<TRequest, TResponse> owner, IAsyncEnumerable<TResponse> stream)
        : IAsyncStreamReader<TResponse>
    {
        private readonly object _gate = new();
        private bool _abandoned;
        private IAsyncEnumerator<TResponse>? _enumerator;
        private bool _finished;

        /// <summary>Whether a pull is in flight, which is what decides who ends the enumeration.</summary>
        private bool _reading;

        public TResponse Current => _enumerator is { } enumerator
            ? enumerator.Current
            : throw new InvalidOperationException("No current element is available.");

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_abandoned)
            {
                throw new ObjectDisposedException(
                    nameof(AsyncServerStreamingCall<TResponse>), "The call was disposed while its response stream was being read.");
            }

            if (_finished)
                return false;

            cancellationToken.ThrowIfCancellationRequested();

            if (_enumerator is null && !owner.TryStart())
            {
                throw new ObjectDisposedException(
                    nameof(AsyncServerStreamingCall<TResponse>),
                    "The call was disposed before its response stream was read.");
            }

            // The enumerator takes its token once, so the first reader's token is the one the
            // enumeration runs under. A generated client passes the same token to every pull, and
            // the check above keeps a later, different one honest anyway.
            _enumerator ??= stream.GetAsyncEnumerator(cancellationToken);

            lock (_gate)
                _reading = true;

            try
            {
                if (await _enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    lock (_gate)
                        _reading = false;

                    return true;
                }
            }
            catch (Exception failure)
            {
                await FinishAsync(failure).ConfigureAwait(false);
                throw;
            }

            await FinishAsync(null).ConfigureAwait(false);
            return false;
        }

        /// <summary>
        ///     Ends the enumeration once: the policy's own iterator releases the winning attempt in
        ///     its epilogue, and disposing the enumerator is what runs it - which is also what
        ///     captures the status the call object is about to be asked for.
        /// </summary>
        internal void Abandon()
        {
            IAsyncEnumerator<TResponse>? enumerator;

            lock (_gate)
            {
                // A pull in flight ends the enumeration itself: the call-scoped source has just been
                // cancelled, so that pull is about to throw and run the same teardown. Disposing an
                // enumerator underneath an in-flight MoveNextAsync is what must not happen.
                if (_finished || _reading)
                    return;

                _finished = true;
                _abandoned = true;
                enumerator = _enumerator;
                _enumerator = null;
            }

            if (enumerator is null)
            {
                owner.Completed(null);
                return;
            }

            // Fire and forget, because Dispose is synchronous and the disposal it starts is the
            // policy's own epilogue - which cancels nothing further and awaits nothing of the
            // consumer's. The fault is observed rather than left to the finalizer thread.
            _ = AbandonAsync(enumerator);
        }

        private async Task AbandonAsync(IAsyncEnumerator<TResponse> enumerator)
        {
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The consumer has walked away from this enumeration; there is nobody left to tell.
            }
            finally
            {
                owner.Completed(null);
            }
        }

        private async ValueTask FinishAsync(Exception? failure)
        {
            lock (_gate)
            {
                _finished = true;
                _reading = false;
            }

            if (_enumerator is { } enumerator)
                await enumerator.DisposeAsync().ConfigureAwait(false);

            owner.Completed(failure);
        }
    }
}
