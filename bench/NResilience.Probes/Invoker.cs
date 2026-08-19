namespace NResilience.Probes;

/// <summary>
/// A non-<c>async</c> generic struct, so the state and stateless entry points share one loop
/// without adding a frame. Measured: a non-async call through a struct constrained to an
/// interface devirtualizes and inlines completely. The struct-ness buys nothing if the layer
/// is <c>async</c> — only the <c>async</c> keyword decides whether a box is allocated.
/// </summary>
internal interface IInvoker<in TState, T>
{
    Task<T> Invoke(TState state, CancellationToken cancellationToken);
}

internal readonly struct StatefulInvoker<TState, T> : IInvoker<TState, T>
{
    private readonly Func<TState, CancellationToken, Task<T>> _work;

    public StatefulInvoker(Func<TState, CancellationToken, Task<T>> work) => _work = work;

    public Task<T> Invoke(TState state, CancellationToken cancellationToken) => _work(state, cancellationToken);
}

internal readonly struct StatelessInvoker<TState, T> : IInvoker<TState, T>
{
    private readonly Func<CancellationToken, Task<T>> _work;

    public StatelessInvoker(Func<CancellationToken, Task<T>> work) => _work = work;

    public Task<T> Invoke(TState state, CancellationToken cancellationToken) => _work(cancellationToken);
}

/// <summary>Stand-in for the shipping internal <c>VoidResult</c>.</summary>
internal readonly struct VoidResult;
