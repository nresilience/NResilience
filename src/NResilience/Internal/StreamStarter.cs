namespace NResilience.Internal;

/// <summary>
///     How the streaming execution path starts a cold source.
///     <para>
///         The same shape and the same reasoning as <see cref="IInvoker{TState,T}" />: a
///         non-<c>async</c> generic struct, so the stateful and stateless entry points share one
///         iterator without adding a frame. A lambda cannot be an iterator in any shipped C#, so
///         the callback a caller hands in returns the <c>IAsyncEnumerable&lt;T&gt;</c> itself and the
///         iterator body is the only place that pulls from it.
///     </para>
///     <para>
///         The iterator calls <see cref="Start" /> afresh on every attempt, which is the whole
///         design: the callback is the "fresh request" builder for a stream, exactly as the HTTP
///         handler builds a fresh request per attempt.
///     </para>
/// </summary>
/// <typeparam name="TState">Caller state threaded to the callback, or <see cref="VoidResult" />.</typeparam>
/// <typeparam name="T">The element type of the source.</typeparam>
internal interface IStreamStarter<in TState, out T>
{
    /// <summary>Invokes the callback to produce one attempt's cold source.</summary>
    /// <param name="state">The caller's state.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The cold source the attempt will pull its first element from.</returns>
    IAsyncEnumerable<T> Start(TState state, CancellationToken cancellationToken);
}

internal readonly struct StatelessStreamStarter<TState, T>(Func<CancellationToken, IAsyncEnumerable<T>> source) : IStreamStarter<TState, T>
{
    public IAsyncEnumerable<T> Start(TState state, CancellationToken cancellationToken) => source(cancellationToken);
}

internal readonly struct StatefulStreamStarter<TState, T>(Func<TState, CancellationToken, IAsyncEnumerable<T>> source) : IStreamStarter<TState, T>
{
    public IAsyncEnumerable<T> Start(TState state, CancellationToken cancellationToken) => source(state, cancellationToken);
}
