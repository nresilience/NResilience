using NResilience.Internal;

namespace NResilience;

/// <content>
///     The <see cref="ValueTask" />-returning callback surface. The implementation lives on the
///     <see cref="Resilience" /> record, mirroring the <see cref="Task" />-returning versions.
///     Extension methods provide the public API.
/// </content>
public sealed partial record Resilience
{
    internal ValueTask<T> RunValue<T>(Func<CancellationToken, ValueTask<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        // Return the callback directly. When a policy imposes no restrictions, the wrapper is avoidable.
        if (IsPassthrough)
            return work(cancellationToken);

        return Dispatch<VoidResult, T, ValueStatelessInvoker<VoidResult, T>, T, ThrowingShaper<T>>(
            new ValueStatelessInvoker<VoidResult, T>(work), default, cancellationToken);
    }

    internal ValueTask RunValue(Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
            return work(cancellationToken);

        return Discard(Dispatch<VoidResult, VoidResult, VoidValueStatelessInvoker<VoidResult>, VoidResult, ThrowingShaper<VoidResult>>(
            new VoidValueStatelessInvoker<VoidResult>(work), default, cancellationToken));
    }

    internal ValueTask<T> RunValue<TState, T>(Func<TState, CancellationToken, ValueTask<T>> work, TState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
            return work(state, cancellationToken);

        return Dispatch<TState, T, ValueStatefulInvoker<TState, T>, T, ThrowingShaper<T>>(
            new ValueStatefulInvoker<TState, T>(work), state, cancellationToken);
    }

    internal ValueTask RunValue<TState>(Func<TState, CancellationToken, ValueTask> work, TState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
            return work(state, cancellationToken);

        return Discard(Dispatch<TState, VoidResult, VoidValueStatefulInvoker<TState>, VoidResult, ThrowingShaper<VoidResult>>(
            new VoidValueStatefulInvoker<TState>(work), state, cancellationToken));
    }

    internal ValueTask<CallResult<T>> TryRunValue<T>(Func<CancellationToken, ValueTask<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        return Dispatch<VoidResult, T, ValueStatelessInvoker<VoidResult, T>, CallResult<T>, ResultShaper<T>>(
            new ValueStatelessInvoker<VoidResult, T>(work), default, cancellationToken);
    }

    internal ValueTask<CallResult> TryRunValue(Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        return Dispatch<VoidResult, VoidResult, VoidValueStatelessInvoker<VoidResult>, CallResult, VoidResultShaper>(
            new VoidValueStatelessInvoker<VoidResult>(work), default, cancellationToken);
    }

    internal ValueTask<CallResult<T>> TryRunValue<TState, T>(Func<TState, CancellationToken, ValueTask<T>> work, TState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        return Dispatch<TState, T, ValueStatefulInvoker<TState, T>, CallResult<T>, ResultShaper<T>>(
            new ValueStatefulInvoker<TState, T>(work), state, cancellationToken);
    }

    internal ValueTask<CallResult> TryRunValue<TState>(Func<TState, CancellationToken, ValueTask> work, TState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        return Dispatch<TState, VoidResult, VoidValueStatefulInvoker<TState>, CallResult, VoidResultShaper>(
            new VoidValueStatefulInvoker<TState>(work), state, cancellationToken);
    }
}

/// <summary>
///     The execution surface for callbacks that return <see cref="ValueTask" />, such as those from
///     <c>Channel</c>, <c>PipeReader</c>, <c>Socket</c>, <c>Stream</c>, or <c>IValueTaskSource</c>.
///     They use the same names, argument order, and behavior as the <see cref="Task" />-returning
///     overloads on <see cref="Resilience" />, so calls look the same regardless of the callback shape.
///     <para>
///         These are extension methods instead of record overloads to avoid ambiguity. An <c>async</c>
///         lambda is convertible to both <see cref="Task" /> and <see cref="ValueTask" /> delegates.
///         Declaring both as instance overloads would cause <c>async</c> lambdas to fail compilation
///         (CS0121). By using extension methods, <c>async</c> lambdas bind to the <see cref="Task" />
///         overload, while lambdas that genuinely return a <see cref="ValueTask" /> use these methods.
///     </para>
///     <para>
///         These overloads optimize the synchronous path. A callback wrapped as
///         <c>ct =&gt; reader.ReadAsync(ct).AsTask()</c> allocates 72 B per call to create a task for
///         an answer it already has. These overloads pass the result directly to the attempt loop,
///         eliminating that allocation. When the callback suspends, both shapes have the same cost.
///         This is verified in <c>NResilience.Gates</c>.
///     </para>
/// </summary>
/// <remarks>
///     The testing package's <c>Sequence.ForVoid</c> needs no counterpart here. Because
///     <c>ValueTask&lt;T&gt;</c> is distinct from <c>ValueTask</c>, result-returning callbacks only
///     bind to result-returning overloads, making the void form unambiguous.
/// </remarks>
public static class ValueTaskExtensions
{
    /// <summary>Runs a callback, retrying and bounding it according to the policy.</summary>
    /// <typeparam name="T">What the callback returns. Inferred; there is nothing to declare.</typeparam>
    /// <param name="policy">The policy.</param>
    /// <param name="work">
    ///     The work, taking the attempt's cancellation token: cancelled when that attempt hits its
    ///     <see cref="Resilience.AttemptTimeout" />, and when <paramref name="cancellationToken" /> is.
    ///     Pass it into whatever you call, because that is what lets a timed-out attempt actually stop.
    ///     Every overload takes it, so there is none that lets you forget.
    /// </param>
    /// <param name="cancellationToken">The caller's token. Cancelling it aborts the call immediately and is never treated as a failure.</param>
    /// <returns>What the last attempt returned.</returns>
    public static ValueTask<T> RunAsync<T>(this Resilience policy, Func<CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.RunValue(work, cancellationToken);
    }

    /// <summary>Runs a callback that returns nothing.</summary>
    /// <param name="policy">The policy.</param>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when the call does.</returns>
    public static ValueTask RunAsync(this Resilience policy, Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.RunValue(work, cancellationToken);
    }

    /// <summary>
    ///     Runs a callback with caller state, so the lambda can be <c>static</c> and allocate no
    ///     closure. Same length as the simple form, and zero-allocation on a synchronously-completing
    ///     call.
    /// </summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <typeparam name="T">What the callback returns.</typeparam>
    /// <param name="policy">The policy.</param>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="state">Handed to the callback on every attempt.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>What the last attempt returned.</returns>
    public static ValueTask<T> RunAsync<TState, T>(this Resilience policy, Func<TState, CancellationToken, ValueTask<T>> work, TState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.RunValue(work, state, cancellationToken);
    }

    /// <summary>Runs a callback with caller state that returns nothing.</summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <param name="policy">The policy.</param>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="state">Handed to the callback on every attempt.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when the call does.</returns>
    public static ValueTask RunAsync<TState>(this Resilience policy, Func<TState, CancellationToken, ValueTask> work, TState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.RunValue(work, state, cancellationToken);
    }

    /// <summary>
    ///     Runs a callback and reports the outcome instead of throwing. This is what replaces a
    ///     fallback strategy - a fallback is an <c>if</c>.
    /// </summary>
    /// <typeparam name="T">What the callback returns.</typeparam>
    /// <param name="policy">The policy.</param>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="cancellationToken">The caller's token. Cancelling it still throws; only a failed call is reported rather than thrown.</param>
    /// <returns>The outcome.</returns>
    public static ValueTask<CallResult<T>> TryRunAsync<T>(this Resilience policy, Func<CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.TryRunValue(work, cancellationToken);
    }

    /// <summary>Runs a callback that returns nothing and reports the outcome instead of throwing.</summary>
    /// <param name="policy">The policy.</param>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The outcome.</returns>
    public static ValueTask<CallResult> TryRunAsync(this Resilience policy, Func<CancellationToken, ValueTask> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.TryRunValue(work, cancellationToken);
    }

    /// <summary>Runs a callback with caller state and reports the outcome instead of throwing.</summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <typeparam name="T">What the callback returns.</typeparam>
    /// <param name="policy">The policy.</param>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="state">Handed to the callback on every attempt.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The outcome.</returns>
    public static ValueTask<CallResult<T>> TryRunAsync<TState, T>(this Resilience policy, Func<TState, CancellationToken, ValueTask<T>> work,
        TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.TryRunValue(work, state, cancellationToken);
    }

    /// <summary>Runs a callback with caller state that returns nothing and reports the outcome.</summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <param name="policy">The policy.</param>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="state">Handed to the callback on every attempt.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The outcome.</returns>
    public static ValueTask<CallResult> TryRunAsync<TState>(this Resilience policy, Func<TState, CancellationToken, ValueTask> work, TState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.TryRunValue(work, state, cancellationToken);
    }
}
