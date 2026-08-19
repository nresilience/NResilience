using System.Runtime.CompilerServices;
using NResilience.Internal;

namespace NResilience;

/// <content>
/// The execution engine: admission, deadline, attempt loop, per-attempt timeout, classification,
/// backoff and the inline attempt log, all in <b>one</b> <c>async</c> frame.
/// <para>
/// This is the architectural decision the whole design turns on. Every <c>async</c> frame that
/// suspends heap-allocates its own state-machine box, and depth is a linear multiplier — so a
/// chain of composed strategies pays a box per layer on the path every real I/O call takes.
/// Collapsing the layers into one method removes all but one of them.
/// </para>
/// </content>
public sealed partial record Resilience
{
    /// <summary>Runs a callback, retrying and bounding it according to this policy.</summary>
    /// <typeparam name="T">What the callback returns. Inferred; there is nothing to declare.</typeparam>
    /// <param name="work">The work. It must take the attempt's cancellation token — there is no overload that lets you forget.</param>
    /// <param name="cancellationToken">The caller's token. Cancelling it aborts the operation immediately and is never treated as a failure.</param>
    /// <returns>What the last attempt returned.</returns>
    public ValueTask<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
        {
            return new ValueTask<T>(work(cancellationToken));
        }

        return ExecuteAsync<VoidResult, T, StatelessInvoker<VoidResult, T>, T, ThrowingShaper<T>>(
            new StatelessInvoker<VoidResult, T>(work), default, cancellationToken);
    }

    /// <summary>Runs a callback that returns nothing.</summary>
    /// <param name="work">The work.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when the operation does.</returns>
    public ValueTask RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
        {
            return new ValueTask(work(cancellationToken));
        }

        return Discard(ExecuteAsync<VoidResult, VoidResult, VoidStatelessInvoker<VoidResult>, VoidResult, ThrowingShaper<VoidResult>>(
            new VoidStatelessInvoker<VoidResult>(work), default, cancellationToken));
    }

    /// <summary>
    /// Runs a callback with caller state, so the lambda can be <c>static</c> and allocate no
    /// closure. Same length as the simple form, and zero-allocation on a synchronously-completing
    /// call.
    /// </summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <typeparam name="T">What the callback returns.</typeparam>
    /// <param name="work">The work.</param>
    /// <param name="state">Handed to the callback on every attempt.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>What the last attempt returned.</returns>
    public ValueTask<T> RunAsync<TState, T>(Func<TState, CancellationToken, Task<T>> work, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
        {
            return new ValueTask<T>(work(state, cancellationToken));
        }

        return ExecuteAsync<TState, T, StatefulInvoker<TState, T>, T, ThrowingShaper<T>>(
            new StatefulInvoker<TState, T>(work), state, cancellationToken);
    }

    /// <summary>Runs a callback with caller state that returns nothing.</summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <param name="work">The work.</param>
    /// <param name="state">Handed to the callback on every attempt.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when the operation does.</returns>
    public ValueTask RunAsync<TState>(Func<TState, CancellationToken, Task> work, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
        {
            return new ValueTask(work(state, cancellationToken));
        }

        return Discard(ExecuteAsync<TState, VoidResult, VoidStatefulInvoker<TState>, VoidResult, ThrowingShaper<VoidResult>>(
            new VoidStatefulInvoker<TState>(work), state, cancellationToken));
    }

    /// <summary>
    /// Runs a callback and reports the outcome instead of throwing. This is what replaces a
    /// fallback strategy — a fallback is an <c>if</c>.
    /// <para>
    /// Unlike the throwing forms, this always materialises the attempt log: its caller has
    /// explicitly asked for a result object, and a history that vanished on success would make
    /// "assert this succeeded on the third attempt" impossible to write.
    /// </para>
    /// </summary>
    /// <typeparam name="T">What the callback returns.</typeparam>
    /// <param name="work">The work.</param>
    /// <param name="cancellationToken">The caller's token. Its cancellation is the one thing this method still throws.</param>
    /// <returns>The outcome.</returns>
    public ValueTask<CallResult<T>> TryRunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        return ExecuteAsync<VoidResult, T, StatelessInvoker<VoidResult, T>, CallResult<T>, ResultShaper<T>>(
            new StatelessInvoker<VoidResult, T>(work), default, cancellationToken);
    }

    /// <summary>Runs a callback that returns nothing, and reports the outcome instead of throwing.</summary>
    /// <param name="work">The work.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The outcome.</returns>
    public ValueTask<CallResult> TryRunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        return ExecuteAsync<VoidResult, VoidResult, VoidStatelessInvoker<VoidResult>, CallResult, VoidResultShaper>(
            new VoidStatelessInvoker<VoidResult>(work), default, cancellationToken);
    }

    /// <summary>Runs a callback with caller state, and reports the outcome instead of throwing.</summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <typeparam name="T">What the callback returns.</typeparam>
    /// <param name="work">The work.</param>
    /// <param name="state">Handed to the callback on every attempt.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The outcome.</returns>
    public ValueTask<CallResult<T>> TryRunAsync<TState, T>(Func<TState, CancellationToken, Task<T>> work, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        return ExecuteAsync<TState, T, StatefulInvoker<TState, T>, CallResult<T>, ResultShaper<T>>(
            new StatefulInvoker<TState, T>(work), state, cancellationToken);
    }

    /// <summary>Runs a callback with caller state that returns nothing, and reports the outcome.</summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <param name="work">The work.</param>
    /// <param name="state">Handed to the callback on every attempt.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The outcome.</returns>
    public ValueTask<CallResult> TryRunAsync<TState>(Func<TState, CancellationToken, Task> work, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        return ExecuteAsync<TState, VoidResult, VoidStatefulInvoker<TState>, CallResult, VoidResultShaper>(
            new VoidStatefulInvoker<TState>(work), state, cancellationToken);
    }

    /// <summary>
    /// Drops a <see cref="VoidResult"/> without adding a frame.
    /// <para>
    /// When the core suspended, its <c>ValueTask&lt;VoidResult&gt;</c> is backed by a
    /// <c>Task&lt;VoidResult&gt;</c>, which <i>is</i> a <see cref="Task"/> — so <c>AsTask()</c>
    /// hands back the object that already exists rather than creating one. Awaiting the core here
    /// instead would cost a second state-machine box on the suspending path, which is the whole
    /// thing this design exists to avoid.
    /// </para>
    /// </summary>
    private static ValueTask Discard(ValueTask<VoidResult> pending) =>
        pending.IsCompletedSuccessfully ? default : new ValueTask(pending.AsTask());

    private async ValueTask<TOut> ExecuteAsync<TState, T, TInvoker, TOut, TShaper>(
        TInvoker invoker,
        TState state,
        CancellationToken cancellationToken)
        where TInvoker : struct, IInvoker<TState, T>
        where TShaper : struct, IOutcomeShaper<T, TOut>
    {
        // Deliberately almost nothing is hoisted into a local here. Every local live across the
        // attempt await is a field in the state-machine box, and the box is the allocation this
        // whole design exists to minimise: caching the policy's Backoff in a local costs 56 bytes
        // on every suspending call to save a field load that the JIT keeps in a register anyway.
        // `this` is already a field of the box, so reading a property off it is free.
        bool bounded = Deadline != Timeout.InfiniteTimeSpan;
        TShaper shaper = default;

        long start = Time.GetTimestamp();
        AttemptSink log = default;

        // One slot each, reused every iteration, rather than a current and a previous pair.
        Verdict verdict = Verdict.Ok;
        Exception? error = null;
        T value = default!;
        bool hasValue = false;
        StopReason reason;

        // Caller cancellation is never a failure. Checked here, after every attempt returns, and
        // after every backoff delay, because a token cancelled 400 ms into a backoff must abort
        // the operation rather than start another attempt.
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            TimeSpan remaining = Remaining(Time, start, Deadline, bounded);
            if (remaining == TimeSpan.Zero)
            {
                reason = StopReason.DeadlineExceeded;
                break;
            }

            if (BeforeAttempt is { } beforeAttempt)
            {
                // Awaited as a Task rather than a ValueTask on purpose: Roslyn shares one hoisted
                // awaiter field between await sites of the same awaiter type, so this await reuses
                // the one the attempt and the backoff delay already need. A ValueTask-returning
                // hook measured 16 B/call more on every suspending call, configured or not.
                await beforeAttempt(new NextAttempt(log.Count + 1, verdict, error, remaining, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                remaining = Remaining(Time, start, Deadline, bounded);
                if (remaining == TimeSpan.Zero)
                {
                    reason = StopReason.DeadlineExceeded;
                    break;
                }
            }

            TimeSpan effective = Effective(AttemptTimeout, remaining);

            CancellationTokenSource? timer = null;
            CancellationTokenSource? attemptSource = null;
            CancellationToken attemptToken = cancellationToken;

            if (effective != Timeout.InfiniteTimeSpan)
            {
                // A pooled source drives the timer, and the attempt links it with the caller's
                // token to make the token the callback receives. The pooled source's own token is
                // never handed out: TryReset preserves token identity, so a callback that outlived
                // its attempt would observe the next operation's cancellation.
                //
                // The tempting shortcut - one fresh CreateLinkedTokenSource(caller) with
                // CancelAfter on it, dodging the second source - measures 96 B/call *worse*.
                // A pooled source keeps its TimerQueueTimer across TryReset, so its CancelAfter
                // allocates nothing, and a fresh source's cannot. Measured, not reasoned.
                timer = CtsPool.Rent(Time);
                timer.CancelAfter(effective);
                attemptSource = cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token)
                    : CancellationTokenSource.CreateLinkedTokenSource(timer.Token);

                attemptToken = attemptSource.Token;
            }

            long attemptStart = Time.GetTimestamp();
            error = null;
            hasValue = false;

            try
            {
                Task attempt = invoker.Invoke(state, attemptToken);
                await attempt.ConfigureAwait(false);
                value = invoker.Result(attempt);
                hasValue = true;
                verdict = Classify.ClassifyResult(value);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Never retried, never counted, never converted into a timeout, and no classifier
                // can override it.
                throw;
            }
            catch (OperationCanceledException canceled) when (attemptSource is not null && attemptSource.IsCancellationRequested)
            {
                // Our own attempt timeout. It never reaches the classifier: the executor knows
                // which source fired, and disambiguating that from caller cancellation is the
                // classic bug in timeout implementations.
                verdict = Verdict.Transient;
                error = new AttemptTimeoutException(effective, canceled);
            }
            catch (Exception exception)
            {
                verdict = Classify.ClassifyException(exception);

                // An exception cannot be turned into a value, so a classifier that calls one Ok
                // is read as "stop, do not retry".
                if (verdict.Kind == VerdictKind.Ok)
                {
                    verdict = Verdict.Permanent;
                }

                error = exception;
            }
            finally
            {
                attemptSource?.Dispose();
                if (timer is not null)
                {
                    CtsPool.Return(timer, Time);
                }
            }

            log.Record(
                Time.GetElapsedTime(start, attemptStart).Ticks,
                Time.GetElapsedTime(attemptStart).Ticks,
                verdict.Kind,
                error);

            if (verdict.Kind == VerdictKind.Ok)
            {
                // Deliberately checked *after* the success return rather than before it. The
                // post-attempt cancellation check exists to stop the loop starting another attempt;
                // a caller who cancelled while an attempt was already succeeding has waited for
                // that attempt either way, and throwing away work that is done and paid for helps
                // nobody.
                AttemptLog succeeded = shaper.WantsLogOnSuccess
                    ? log.Materialise(Time.GetElapsedTime(start), Deadline, bounded)
                    : AttemptLog.Empty;

                return shaper.Success(value, succeeded);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (verdict.Kind == VerdictKind.Permanent)
            {
                reason = StopReason.Permanent;
                break;
            }

            if (log.Count >= Attempts)
            {
                reason = StopReason.AttemptsExhausted;
                break;
            }

            TimeSpan left = Remaining(Time, start, Deadline, bounded);
            if (left == TimeSpan.Zero)
            {
                reason = StopReason.DeadlineExceeded;
                break;
            }

            TimeSpan delay = Backoff.Compute(new NextAttempt(log.Count + 1, verdict, error, left, cancellationToken));

            // A delay that would consume the rest of the budget leaves nothing for the attempt
            // after it, so the deadline stops the operation here rather than sleeping through it.
            if (bounded && delay >= left)
            {
                reason = StopReason.DeadlineExceeded;
                break;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, Time, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        AttemptLog attempts = log.Materialise(Time.GetElapsedTime(start), Deadline, bounded);
        return shaper.Failure(value, hasValue, error, reason, Deadline, attempts);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TimeSpan Remaining(TimeProvider time, long start, TimeSpan deadline, bool bounded)
    {
        if (!bounded)
        {
            return Timeout.InfiniteTimeSpan;
        }

        TimeSpan left = deadline - time.GetElapsedTime(start);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TimeSpan Effective(TimeSpan attemptTimeout, TimeSpan remaining)
    {
        if (attemptTimeout == Timeout.InfiniteTimeSpan)
        {
            return remaining;
        }

        if (remaining == Timeout.InfiniteTimeSpan)
        {
            return attemptTimeout;
        }

        return attemptTimeout < remaining ? attemptTimeout : remaining;
    }
}
