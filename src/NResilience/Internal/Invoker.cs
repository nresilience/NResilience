using System.Runtime.CompilerServices;

namespace NResilience.Internal;

/// <summary>
///     How the executor calls the callback.
///     <para>
///         A non-<c>async</c> generic struct, so the stateful and stateless entry points - and the typed
///         and void ones, and the <see cref="Task" />-returning and <see cref="ValueTask" />-returning
///         ones - share one loop without adding a frame. Measured: a non-async call through a struct
///         constrained to an interface devirtualizes and inlines completely. Struct-ness buys nothing if
///         the layer is itself <c>async</c>; only the <c>async</c> keyword decides whether a box is
///         allocated.
///     </para>
///     <para>
///         <see cref="Invoke" /> returns the non-generic <see cref="Task" /> so that the loop awaits one
///         awaiter type regardless of <typeparamref name="T" />. This is why a
///         <see cref="ValueTask" />-returning callback costs the loop nothing: adding an
///         <c>await</c> on a second awaitable type would give the generated state machine a second
///         hoisted awaiter field, and a hoisted field is a property of the state-machine <b>type</b> -
///         every caller would pay for it, whichever callback shape they passed.
///     </para>
/// </summary>
/// <typeparam name="TState">Caller state threaded to the callback, or <see cref="VoidResult" />.</typeparam>
/// <typeparam name="T">What the callback returns, or <see cref="VoidResult" />.</typeparam>
internal interface IInvoker<in TState, T>
{
    /// <summary>
    ///     Starts one attempt. Called afresh every time: a <see cref="ValueTask" /> may be awaited
    ///     exactly once, so a retry loop that stored attempt one's task would pass every test written
    ///     against a <see cref="Task" />-returning callback and fail in production against an
    ///     <c>IValueTaskSource</c>-backed one.
    /// </summary>
    /// <param name="state">The caller's state.</param>
    /// <param name="cancellationToken">The attempt's token.</param>
    /// <param name="synchronous">
    ///     Written with the attempt's result when this returns <c>null</c>, and left alone otherwise.
    ///     <c>ref</c> rather than <c>out</c>, so that an attempt which produces no value of its own
    ///     leaves the last one an attempt did produce in place - which is what
    ///     <see cref="CallResult{T}.Value" /> promises to hand back, and what lets a caller dispose a
    ///     final response the policy judged a failure.
    /// </param>
    /// <returns>
    ///     The callback's task, or <c>null</c> when the attempt already completed successfully and
    ///     <paramref name="synchronous" /> holds its result.
    ///     <para>
    ///         The null is what keeps a synchronously-completing <see cref="ValueTask" /> free. Handing
    ///         it back as a <see cref="Task" /> means <c>AsTask()</c>, which materializes a task for a
    ///         result that is already sitting in a register - measured at 72 B/call, on the one path the
    ///         library promises allocates nothing. The result is written into a variable the executor
    ///         already keeps, so parking it costs no box growth either.
    ///     </para>
    /// </returns>
    Task? Invoke(TState state, CancellationToken cancellationToken, ref T synchronous);

    /// <summary>Reads the result off a task that has already completed successfully.</summary>
    /// <param name="completed">
    ///     The task returned by <see cref="Invoke" />, already awaited. Never <c>null</c>: the executor
    ///     calls this only on the branch it took because a task was returned.
    /// </param>
    /// <returns>Its result.</returns>
    T Result(Task completed);
}

internal readonly struct StatelessInvoker<TState, T>(Func<CancellationToken, Task<T>> work) : IInvoker<TState, T>
{
    public Task? Invoke(TState state, CancellationToken cancellationToken, ref T synchronous) => work(cancellationToken);

    // The invariant is local and total: this is the task Invoke just returned.
    public T Result(Task completed) => Unsafe.As<Task<T>>(completed).Result;
}

internal readonly struct StatefulInvoker<TState, T>(Func<TState, CancellationToken, Task<T>> work) : IInvoker<TState, T>
{
    public Task? Invoke(TState state, CancellationToken cancellationToken, ref T synchronous) => work(state, cancellationToken);

    public T Result(Task completed) => Unsafe.As<Task<T>>(completed).Result;
}

internal readonly struct VoidStatelessInvoker<TState>(Func<CancellationToken, Task> work) : IInvoker<TState, VoidResult>
{
    public Task? Invoke(TState state, CancellationToken cancellationToken, ref VoidResult synchronous) => work(cancellationToken);

    public VoidResult Result(Task completed) => default;
}

internal readonly struct VoidStatefulInvoker<TState>(Func<TState, CancellationToken, Task> work) : IInvoker<TState, VoidResult>
{
    public Task? Invoke(TState state, CancellationToken cancellationToken, ref VoidResult synchronous) => work(state, cancellationToken);

    public VoidResult Result(Task completed) => default;
}

/// <summary>
///     The <see cref="ValueTask" />-returning shapes.
///     <para>
///         A callback that already has its answer never becomes a task at all. One that does not is
///         handed to the loop as a <see cref="Task" />, which for a pending <c>IValueTaskSource</c>
///         costs the one allocation a <see cref="Task" />-returning callback would have made anyway -
///         so the <see cref="ValueTask" /> shapes are never the more expensive of the two.
///     </para>
/// </summary>
internal readonly struct ValueStatelessInvoker<TState, T>(Func<CancellationToken, ValueTask<T>> work) : IInvoker<TState, T>
{
    public Task? Invoke(TState state, CancellationToken cancellationToken, ref T synchronous)
    {
        var attempt = work(cancellationToken);

        if (!attempt.IsCompletedSuccessfully)
            return attempt.AsTask();

        synchronous = attempt.Result;
        return null;
    }

    public T Result(Task completed) => Unsafe.As<Task<T>>(completed).Result;
}

internal readonly struct ValueStatefulInvoker<TState, T>(Func<TState, CancellationToken, ValueTask<T>> work) : IInvoker<TState, T>
{
    public Task? Invoke(TState state, CancellationToken cancellationToken, ref T synchronous)
    {
        var attempt = work(state, cancellationToken);

        if (!attempt.IsCompletedSuccessfully)
            return attempt.AsTask();

        synchronous = attempt.Result;
        return null;
    }

    public T Result(Task completed) => Unsafe.As<Task<T>>(completed).Result;
}

internal readonly struct VoidValueStatelessInvoker<TState>(Func<CancellationToken, ValueTask> work) : IInvoker<TState, VoidResult>
{
    public Task? Invoke(TState state, CancellationToken cancellationToken, ref VoidResult synchronous)
    {
        var attempt = work(cancellationToken);
        return attempt.IsCompletedSuccessfully ? null : attempt.AsTask();
    }

    public VoidResult Result(Task completed) => default;
}

internal readonly struct VoidValueStatefulInvoker<TState>(Func<TState, CancellationToken, ValueTask> work) : IInvoker<TState, VoidResult>
{
    public Task? Invoke(TState state, CancellationToken cancellationToken, ref VoidResult synchronous)
    {
        var attempt = work(state, cancellationToken);
        return attempt.IsCompletedSuccessfully ? null : attempt.AsTask();
    }

    public VoidResult Result(Task completed) => default;
}

/// <summary>
///     Stands in for "no result" and for "no state". Internal, so a caller can never register an
///     <c>OnResult</c> judge for it and a void operation can never be classified as a failed result.
/// </summary>
internal readonly struct VoidResult;
