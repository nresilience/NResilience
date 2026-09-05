namespace NResilience.Internal;

/// <summary>
///     What the streaming loop reports to <c>TryRunAsync</c> instead of throwing.
///     <para>
///         A class rather than an <c>out</c> parameter because the loop is an iterator, and an
///         iterator cannot have one. One allocation, on a path that has already conceded the
///         iterator box and a <see cref="CallResult{T}" /> the caller asked for by name - the
///         throwing <c>RunAsync</c> passes <c>null</c> and pays nothing.
///     </para>
/// </summary>
internal sealed class StreamOutcome
{
    /// <summary>Everything that happened, materialized on both exits because the caller asked for a result object.</summary>
    public AttemptLog Attempts { get; set; } = AttemptLog.Empty;

    /// <summary>The failure the loop would have thrown, or <c>null</c> when the stream started.</summary>
    public Exception? Error { get; set; }

    /// <summary>Why it stopped.</summary>
    public StopReason Reason { get; set; } = StopReason.Succeeded;
}

/// <summary>
///     A stream whose first element has already been pulled: what a successful
///     <c>TryRunAsync</c> hands back as the value of its <see cref="CallResult{T}" />.
///     <para>
///         The retry loop is over by the time this exists - the policy started the stream, judged
///         its first element and handed over. So this type imposes nothing: it re-yields the
///         element the loop already has and then delegates, and the enumeration's faults are the
///         consumer's, exactly as they are after the first element of a throwing
///         <c>RunAsync</c>.
///     </para>
///     <para>
///         Enumerable once, because the elements behind it are a live enumerator rather than a
///         source that can be re-run. It is also <see cref="IAsyncDisposable" />, which is the
///         only shape in the library a caller must remember: a successful result owns a live
///         enumerator and its token, so a caller who decides not to enumerate after all disposes
///         this instead of dropping it.
///     </para>
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
internal sealed class StartedStream<T>(IAsyncEnumerator<T> started, bool hasFirst) : IAsyncEnumerable<T>, IAsyncDisposable
{
    private IAsyncEnumerator<T>? _started = started;

    /// <summary>
    ///     Hands over the started enumeration, once.
    /// </summary>
    /// <param name="cancellationToken">
    ///     Ignored, and it has to be: the enumeration is already running under the token the call
    ///     was started with, and a token supplied now could not be woven into an attempt that has
    ///     already produced its first element. Cancel the token you passed to <c>TryRunAsync</c>.
    /// </param>
    /// <returns>The first element, then the rest of the enumeration.</returns>
    /// <exception cref="InvalidOperationException">The stream has already been enumerated or disposed.</exception>
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var enumerator = Interlocked.Exchange(ref _started, null);

        if (enumerator is null)
        {
            throw new InvalidOperationException(
                "This stream has already been enumerated or disposed. A CallResult from TryRunAsync carries a stream whose first element has already been pulled, so it can be enumerated once; run the policy again for a second enumeration.");
        }

        return hasFirst ? new Enumerator(enumerator) : enumerator;
    }

    /// <summary>Releases the started enumeration for a caller who will not enumerate it.</summary>
    /// <returns>The disposal, or a completed task when the stream was already handed over.</returns>
    public ValueTask DisposeAsync()
    {
        var enumerator = Interlocked.Exchange(ref _started, null);
        return enumerator?.DisposeAsync() ?? default;
    }

    /// <summary>
    ///     Re-yields the element the loop already pulled, then delegates. One bool, checked once
    ///     per enumeration rather than per element in any meaningful sense: after the first
    ///     <c>MoveNextAsync</c> the branch is perfectly predicted.
    /// </summary>
    private sealed class Enumerator(IAsyncEnumerator<T> inner) : IAsyncEnumerator<T>
    {
        private bool _delivered;

        public T Current => inner.Current;

        public ValueTask<bool> MoveNextAsync()
        {
            if (_delivered)
                return inner.MoveNextAsync();

            _delivered = true;
            return new ValueTask<bool>(true);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
