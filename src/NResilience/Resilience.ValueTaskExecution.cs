using NResilience.Internal;

namespace NResilience;

/// <content>
///     The <see cref="ValueTask" />-returning callback surface. The bodies live on the record, beside
///     the <see cref="Task" />-returning ones they mirror; the public overloads that reach them are the
///     extension methods below.
/// </content>
public sealed partial record Resilience
{
    internal ValueTask<T> RunValue<T>(Func<CancellationToken, ValueTask<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        // Handed straight back rather than wrapped: a ValueTask-returning callback under a policy
        // that imposes nothing is the one shape where even the wrapper is avoidable.
        if (IsPassthrough)
            return work(cancellationToken);

        if (Admit is not null)
            return ExecuteWithAdmitAsync<VoidResult, T, ValueStatelessInvoker<VoidResult, T>, T, ThrowingShaper<T>>(
                new ValueStatelessInvoker<VoidResult, T>(work), default, cancellationToken);

        return ExecuteAsync<VoidResult, T, ValueStatelessInvoker<VoidResult, T>, T, ThrowingShaper<T>>(
            new ValueStatelessInvoker<VoidResult, T>(work), default, cancellationToken);
    }

    internal ValueTask RunValue(Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
            return work(cancellationToken);

        if (Admit is not null)
            return Discard(ExecuteWithAdmitAsync<VoidResult, VoidResult, VoidValueStatelessInvoker<VoidResult>, VoidResult, ThrowingShaper<VoidResult>>(
                new VoidValueStatelessInvoker<VoidResult>(work), default, cancellationToken));

        return Discard(ExecuteAsync<VoidResult, VoidResult, VoidValueStatelessInvoker<VoidResult>, VoidResult, ThrowingShaper<VoidResult>>(
            new VoidValueStatelessInvoker<VoidResult>(work), default, cancellationToken));
    }

    internal ValueTask<T> RunValue<TState, T>(Func<TState, CancellationToken, ValueTask<T>> work, TState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
            return work(state, cancellationToken);

        if (Admit is not null)
            return ExecuteWithAdmitAsync<TState, T, ValueStatefulInvoker<TState, T>, T, ThrowingShaper<T>>(
                new ValueStatefulInvoker<TState, T>(work), state, cancellationToken);

        return ExecuteAsync<TState, T, ValueStatefulInvoker<TState, T>, T, ThrowingShaper<T>>(
            new ValueStatefulInvoker<TState, T>(work), state, cancellationToken);
    }

    internal ValueTask RunValue<TState>(Func<TState, CancellationToken, ValueTask> work, TState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
            return work(state, cancellationToken);

        if (Admit is not null)
            return Discard(ExecuteWithAdmitAsync<TState, VoidResult, VoidValueStatefulInvoker<TState>, VoidResult, ThrowingShaper<VoidResult>>(
                new VoidValueStatefulInvoker<TState>(work), state, cancellationToken));

        return Discard(ExecuteAsync<TState, VoidResult, VoidValueStatefulInvoker<TState>, VoidResult, ThrowingShaper<VoidResult>>(
            new VoidValueStatefulInvoker<TState>(work), state, cancellationToken));
    }

    internal ValueTask<CallResult<T>> TryRunValue<T>(Func<CancellationToken, ValueTask<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (Admit is not null)
            return ExecuteWithAdmitAsync<VoidResult, T, ValueStatelessInvoker<VoidResult, T>, CallResult<T>, ResultShaper<T>>(
                new ValueStatelessInvoker<VoidResult, T>(work), default, cancellationToken);

        return ExecuteAsync<VoidResult, T, ValueStatelessInvoker<VoidResult, T>, CallResult<T>, ResultShaper<T>>(
            new ValueStatelessInvoker<VoidResult, T>(work), default, cancellationToken);
    }

    internal ValueTask<CallResult> TryRunValue(Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (Admit is not null)
            return ExecuteWithAdmitAsync<VoidResult, VoidResult, VoidValueStatelessInvoker<VoidResult>, CallResult, VoidResultShaper>(
                new VoidValueStatelessInvoker<VoidResult>(work), default, cancellationToken);

        return ExecuteAsync<VoidResult, VoidResult, VoidValueStatelessInvoker<VoidResult>, CallResult, VoidResultShaper>(
            new VoidValueStatelessInvoker<VoidResult>(work), default, cancellationToken);
    }

    internal ValueTask<CallResult<T>> TryRunValue<TState, T>(Func<TState, CancellationToken, ValueTask<T>> work, TState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (Admit is not null)
            return ExecuteWithAdmitAsync<TState, T, ValueStatefulInvoker<TState, T>, CallResult<T>, ResultShaper<T>>(
                new ValueStatefulInvoker<TState, T>(work), state, cancellationToken);

        return ExecuteAsync<TState, T, ValueStatefulInvoker<TState, T>, CallResult<T>, ResultShaper<T>>(
            new ValueStatefulInvoker<TState, T>(work), state, cancellationToken);
    }

    internal ValueTask<CallResult> TryRunValue<TState>(Func<TState, CancellationToken, ValueTask> work, TState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (Admit is not null)
            return ExecuteWithAdmitAsync<TState, VoidResult, VoidValueStatefulInvoker<TState>, CallResult, VoidResultShaper>(
                new VoidValueStatefulInvoker<TState>(work), state, cancellationToken);

        return ExecuteAsync<TState, VoidResult, VoidValueStatefulInvoker<TState>, CallResult, VoidResultShaper>(
            new VoidValueStatefulInvoker<TState>(work), state, cancellationToken);
    }
}

/// <summary>
///     The execution surface for callbacks that return <see cref="ValueTask" />: <c>Channel</c>,
///     <c>PipeReader</c>, <c>Socket</c>, <c>Stream</c>, and anything else built on
///     <c>IValueTaskSource</c>. Same names, same argument order and the same behavior as the
///     <see cref="Task" />-returning overloads on <see cref="Resilience" /> - a call reads identically
///     whichever shape the callback happens to have.
///     <para>
///         Extension methods rather than overloads on the record, and that is load-bearing rather than
///         stylistic. An <c>async</c> lambda is convertible to both delegate shapes with neither
///         conversion better, so declaring both as instance overloads makes
///         <c>async ct =&gt; await client.GetAsync(url, ct)</c> - the second thing anyone writes -
///         fail to compile with CS0121, and an explicit type argument does not rescue it. C# searches
///         for an extension method only when no instance method is applicable, so the <c>async</c>
///         lambda binds to the <see cref="Task" /> overload and never becomes ambiguous, while a lambda
///         that genuinely returns a <see cref="ValueTask" /> is convertible to no instance overload and
///         so finds these.
///     </para>
///     <para>
///         The point of them is the synchronous path. A callback wrapped as
///         <c>ct =&gt; reader.ReadAsync(ct).AsTask()</c> pays 72 B every call to build a task for an
///         answer it already has, on precisely the path the library reports as zero; these overloads
///         hand the answer straight to the attempt loop instead. When the callback really does suspend
///         they cost nothing either way. Gated in <c>NResilience.Gates</c>.
///     </para>
/// </summary>
/// <remarks>
///     There is no <see cref="ValueTask" /> analogue of <c>Sequence.NextVoidAsync</c>, because
///     <c>ValueTask&lt;T&gt;</c> is not a <c>ValueTask</c>: a result-returning callback can only bind to
///     the result-returning overload, so the void form needs no disambiguation.
/// </remarks>
public static class ResilienceValueTask
{
    /// <summary>Runs a callback, retrying and bounding it according to this policy.</summary>
    /// <typeparam name="T">What the callback returns. Inferred; there is nothing to declare.</typeparam>
    /// <param name="policy">The policy.</param>
    /// <param name="work">
    ///     The work, taking the attempt's cancellation token: cancelled when that attempt hits its
    ///     <see cref="Resilience.AttemptTimeout" />, and when <paramref name="cancellationToken" /> is.
    ///     Pass it into whatever you call, because that is what lets a timed-out attempt actually stop.
    /// </param>
    /// <param name="cancellationToken">The caller's token. Cancelling it aborts the operation immediately and is never treated as a failure.</param>
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
    /// <returns>A task that completes when the operation does.</returns>
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
    /// <returns>A task that completes when the operation does.</returns>
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
    /// <param name="cancellationToken">The caller's token. Its cancellation is the one thing this method still throws.</param>
    /// <returns>The outcome.</returns>
    public static ValueTask<CallResult<T>> TryRunAsync<T>(this Resilience policy, Func<CancellationToken, ValueTask<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.TryRunValue(work, cancellationToken);
    }

    /// <summary>Runs a callback that returns nothing, and reports the outcome instead of throwing.</summary>
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

    /// <summary>Runs a callback with caller state, and reports the outcome instead of throwing.</summary>
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

    /// <summary>Runs a callback with caller state that returns nothing, and reports the outcome.</summary>
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
