namespace NResilience.Internal;

/// <summary>
///     A read-only stream that fails when the far side stops answering. Wrapped around a response
///     body so that the gap between two reads is bounded by
///     <see cref="Resilience.AttemptTimeout" />, which is the bound the body escaped entirely before
///     this type existed.
///     <para>
///         What is measured is how long <i>one pending read</i> takes, not how long the body takes and
///         not how long the consumer takes between reads. That distinction is the whole design: a 4 GB
///         download that keeps arriving is never cut off, and a consumer that spends a minute
///         processing each buffer is never blamed for it. Only a read that has been outstanding longer
///         than the bound is a stall, and a read that is outstanding is by definition waiting on the
///         far side.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         <b>The timer is armed once.</b> The obvious implementation re-arms a one-shot timer around
///         every read, which is two <c>ITimer.Change</c> calls per 8 KB buffer on a download. This one
///         arms a single self-adjusting timer at construction and writes two <c>long</c> fields per
///         read instead: when the timer fires it reads how long the current read has been pending and
///         either faults the stream or re-arms for the remainder. A body that is arriving normally
///         never touches the timer again after construction.
///     </para>
///     <para>
///         <b>The linked source is cached.</b> A stall has to cancel the inner read, and the consumer's
///         own token has to keep working, so the two must be linked - which is an allocation per read
///         if done naively. Consumers pass the same token for every read of one body, so the linked
///         source is built once and reused while the token is unchanged, and only a consumer that
///         varies its token per read pays per read. A consumer passing
///         <see cref="CancellationToken.None" /> pays nothing at all: there is nothing to link, and the
///         stall source is passed through directly.
///     </para>
/// </remarks>
internal sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly string? _policyName;
    private readonly Action<CallEvent>? _onEvent;
    private readonly TimeSpan _stall;
    private readonly CancellationTokenSource _stalled = new();
    private readonly TimeProvider _time;
    private readonly ITimer _timer;

    /// <summary>The consumer's token behind <see cref="_linked" />, so an unchanged one is recognized.</summary>
    private CancellationToken _linkedFor;

    private CancellationTokenSource? _linked;

    /// <summary>
    ///     When the pending read started, or <c>0</c> when no read is outstanding. Written before and
    ///     after every read and read by the timer callback, so it is accessed through
    ///     <see cref="Volatile" /> rather than declared <c>volatile</c> - the field is a
    ///     <see cref="long" />, which is not guaranteed atomic on 32-bit without it.
    /// </summary>
    private long _readingSince;

    private long _transferred;
    private bool _disposed;

    /// <summary>Creates the wrapper and arms the bound.</summary>
    /// <param name="inner">The body.</param>
    /// <param name="stall">How long one read may be outstanding.</param>
    /// <param name="time">The clock.</param>
    /// <param name="policyName">The policy's name, for the event.</param>
    /// <param name="onEvent">The policy's listener, told when a stall fires.</param>
    internal ProgressStream(Stream inner, TimeSpan stall, TimeProvider time, string? policyName, Action<CallEvent>? onEvent)
    {
        _inner = inner;
        _stall = stall;
        _time = time;
        _policyName = policyName;
        _onEvent = onEvent;

        // Created last, and after every field it reads is set: the callback can run on another
        // thread the instant this returns.
        _timer = time.CreateTimer(static state => ((ProgressStream)state!).Check(), this, stall, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>How much has arrived so far, for the exception a stall raises.</summary>
    internal long Transferred => Volatile.Read(ref _transferred);

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var token = Link(cancellationToken);

        Volatile.Write(ref _readingSince, _time.GetTimestamp());

        try
        {
            var read = await _inner.ReadAsync(buffer, token).ConfigureAwait(false);
            Progressed(read);
            return read;
        }
        catch (OperationCanceledException e) when (Faulted(cancellationToken))
        {
            throw Stall(e);
        }
        finally
        {
            Volatile.Write(ref _readingSince, 0);
        }
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Delegates, unbounded. A synchronous read cannot be cancelled by a timer, and the library is
    ///     async-only for exactly this family of reasons - see the FAQ. Reaching this on a response body
    ///     means something above called <c>Read</c> rather than <c>ReadAsync</c>, and the stall bound
    ///     does not apply to it.
    /// </summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="offset">The offset.</param>
    /// <param name="count">The count.</param>
    /// <returns>The bytes read.</returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Progressed(read);
        return read;
    }

    /// <inheritdoc cref="Read(byte[], int, int)" />
    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Progressed(read);
        return read;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        Teardown();
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Teardown();
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    ///     The consumer's token and the stall source as one token, reusing the linked source while the
    ///     consumer's token is the one it was built for.
    /// </summary>
    /// <param name="cancellationToken">The consumer's token for this read.</param>
    /// <returns>The token the inner read runs under.</returns>
    private CancellationToken Link(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
            return _stalled.Token;

        if (_linked is not null && cancellationToken == _linkedFor)
            return _linked.Token;

        // A consumer that varies its token per read lands here every time, which is correct and is
        // the cost of doing so. The superseded source is disposed rather than left to the finalizer,
        // and the registration it holds on the consumer's previous token goes with it.
        _linked?.Dispose();
        _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stalled.Token);
        _linkedFor = cancellationToken;

        return _linked.Token;
    }

    /// <summary>Records a read that returned something, which is what "progress" means here.</summary>
    /// <param name="read">The bytes read.</param>
    private void Progressed(int read)
    {
        if (read > 0)
            Volatile.Write(ref _transferred, Volatile.Read(ref _transferred) + read);
    }

    /// <summary>
    ///     Whether the cancellation that just escaped the inner read was this type's bound firing
    ///     rather than the consumer's own token. The consumer's token is checked first and wins, so a
    ///     consumer who cancels at the same moment a stall fires gets their own
    ///     <see cref="OperationCanceledException" /> - the same precedence the executor applies to its
    ///     attempt ceiling, and for the same reason.
    /// </summary>
    /// <param name="cancellationToken">The consumer's token.</param>
    /// <returns>True when the stall bound is what cancelled the read.</returns>
    private bool Faulted(CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && _stalled.IsCancellationRequested;

    /// <summary>
    ///     Builds the exception and raises the event. Called from the read that was cancelled rather
    ///     than from the timer callback, so the listener runs on the consumer's thread and never on a
    ///     timer thread.
    /// </summary>
    /// <param name="inner">The cancellation the bound produced.</param>
    /// <returns>The exception to throw.</returns>
    private AttemptStalledException Stall(OperationCanceledException inner)
    {
        var transferred = Transferred;
        var stalled = new AttemptStalledException(_stall, transferred, inner);

        if (_onEvent is { } listener)
        {
            try
            {
                listener(CallEvent.Create(
                    CallEventKind.Stalled,
                    _policyName,
                    verdict: Verdict.Transient,
                    delay: _stall,
                    exception: stalled));
            }
            catch
            {
                // Telemetry that can fail the operation it is observing is worse than no telemetry,
                // which is the rule the executor's own listener calls follow.
            }
        }

        return stalled;
    }

    /// <summary>
    ///     The timer callback: fault the stream, or re-arm for whatever is left of the bound.
    ///     <para>
    ///         Three cases. No read is outstanding, so the consumer is between reads and owes the far
    ///         side nothing - re-arm for the full bound. A read is outstanding and has been for long
    ///         enough - cancel it. A read is outstanding and has not - re-arm for the difference, which
    ///         is what makes one armed timer as precise as one armed per read.
    ///     </para>
    /// </summary>
    private void Check()
    {
        if (_disposed)
            return;

        var since = Volatile.Read(ref _readingSince);

        if (since == 0)
        {
            Rearm(_stall);
            return;
        }

        var pending = _time.GetElapsedTime(since);

        if (pending < _stall)
        {
            Rearm(_stall - pending);
            return;
        }

        try
        {
            _stalled.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposal raced the callback. The read this would have cancelled is already over.
        }
    }

    private void Rearm(TimeSpan due)
    {
        try
        {
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // As above: the stream went away while the callback was running.
        }
    }

    private void Teardown()
    {
        if (_disposed)
            return;

        _disposed = true;

        _timer.Dispose();
        _linked?.Dispose();
        _stalled.Dispose();
    }
}
