using System.Runtime.CompilerServices;

namespace NResilience.Internal;

/// <summary>
/// How the executor calls the callback.
/// <para>
/// A non-<c>async</c> generic struct, so the stateful and stateless entry points — and the typed
/// and void ones — share one loop without adding a frame. Measured: a non-async call through a
/// struct constrained to an interface devirtualizes and inlines completely. Struct-ness buys
/// nothing if the layer is itself <c>async</c>; only the <c>async</c> keyword decides whether a
/// box is allocated.
/// </para>
/// <para>
/// <see cref="Invoke"/> returns the non-generic <see cref="Task"/> so that the loop awaits one
/// awaiter type regardless of <typeparamref name="T"/>, and <see cref="Result"/> reads the value
/// off the already-completed task.
/// </para>
/// </summary>
/// <typeparam name="TState">Caller state threaded to the callback, or <see cref="VoidResult"/>.</typeparam>
/// <typeparam name="T">What the callback returns, or <see cref="VoidResult"/>.</typeparam>
internal interface IInvoker<in TState, out T>
{
    /// <summary>
    /// Starts one attempt. Called afresh every time: a <see cref="ValueTask"/> may be awaited
    /// exactly once, so a retry loop that stored attempt one's task would pass every test written
    /// against a <see cref="Task"/>-returning callback and fail in production against an
    /// <c>IValueTaskSource</c>-backed one.
    /// </summary>
    /// <param name="state">The caller's state.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <returns>The callback's task.</returns>
    Task Invoke(TState state, CancellationToken cancellationToken);

    /// <summary>Reads the result off a task that has already completed successfully.</summary>
    /// <param name="completed">The task returned by <see cref="Invoke"/>, already awaited.</param>
    /// <returns>Its result.</returns>
    T Result(Task completed);
}

internal readonly struct StatelessInvoker<TState, T> : IInvoker<TState, T>
{
    private readonly Func<CancellationToken, Task<T>> _work;

    public StatelessInvoker(Func<CancellationToken, Task<T>> work) => _work = work;

    public Task Invoke(TState state, CancellationToken cancellationToken) => _work(cancellationToken);

    // The invariant is local and total: this is the task Invoke just returned.
    public T Result(Task completed) => Unsafe.As<Task<T>>(completed).Result;
}

internal readonly struct StatefulInvoker<TState, T> : IInvoker<TState, T>
{
    private readonly Func<TState, CancellationToken, Task<T>> _work;

    public StatefulInvoker(Func<TState, CancellationToken, Task<T>> work) => _work = work;

    public Task Invoke(TState state, CancellationToken cancellationToken) => _work(state, cancellationToken);

    public T Result(Task completed) => Unsafe.As<Task<T>>(completed).Result;
}

internal readonly struct VoidStatelessInvoker<TState> : IInvoker<TState, VoidResult>
{
    private readonly Func<CancellationToken, Task> _work;

    public VoidStatelessInvoker(Func<CancellationToken, Task> work) => _work = work;

    public Task Invoke(TState state, CancellationToken cancellationToken) => _work(cancellationToken);

    public VoidResult Result(Task completed) => default;
}

internal readonly struct VoidStatefulInvoker<TState> : IInvoker<TState, VoidResult>
{
    private readonly Func<TState, CancellationToken, Task> _work;

    public VoidStatefulInvoker(Func<TState, CancellationToken, Task> work) => _work = work;

    public Task Invoke(TState state, CancellationToken cancellationToken) => _work(state, cancellationToken);

    public VoidResult Result(Task completed) => default;
}

/// <summary>
/// Stands in for "no result" and for "no state". Internal, so a caller can never register an
/// <c>OnResult</c> judge for it and a void operation can never be classified as a failed result.
/// </summary>
internal readonly struct VoidResult;
