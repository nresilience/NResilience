using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace NResilience.Probes;

/// <summary>
///     A stand-in for the shipping executor. It implements admission, deadline,
///     the attempt loop, per-attempt timeout, classification, breaker, budget, backoff,
///     and the inline attempt log, all within a single <c>async</c> method.
///     This implementation is deliberately comprehensive. The question it exists to answer is
///     whether the fused-frame advantage survives contact with a realistic loop. This can only
///     be answered by a loop that hoists a realistic amount of state across the attempt
///     <c>await</c>. Every local variable below is live across that await and is therefore stored
///     in the state-machine box.
/// </summary>
public sealed class FusedExecutor
{
    private readonly bool _passthrough;
    private readonly bool _recordAttempts;

    /// <param name="policy">The policy the loop enforces.</param>
    /// <param name="recordAttempts">
    ///     Whether the loop keeps the inline attempt log. This is always true in the
    ///     shipping design. The false option exists so the log can be priced
    ///     against the same loop without it.
    /// </param>
    public FusedExecutor(FusedPolicy policy, bool recordAttempts = true)
    {
        Policy = policy;
        _passthrough = policy.IsPassthrough;
        _recordAttempts = recordAttempts;
    }

    public FusedPolicy Policy { get; }

    public ValueTask<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        // Resilience.None is a single branch and returns the callback's own task. No frame, no box.
        if (_passthrough)
            return new ValueTask<T>(work(cancellationToken));

        var invoker = new StatelessInvoker<VoidResult, T>(work);

        return _recordAttempts
            ? RunCoreAsync<VoidResult, T, StatelessInvoker<VoidResult, T>, InlineAttemptSink>(invoker, default, cancellationToken)
            : RunCoreAsync<VoidResult, T, StatelessInvoker<VoidResult, T>, NoAttemptSink>(invoker, default, cancellationToken);
    }

    public ValueTask<T> RunAsync<TState, T>(Func<TState, CancellationToken, Task<T>> work, TState state, CancellationToken cancellationToken = default)
    {
        if (_passthrough)
            return new ValueTask<T>(work(state, cancellationToken));

        var invoker = new StatefulInvoker<TState, T>(work);

        return _recordAttempts
            ? RunCoreAsync<TState, T, StatefulInvoker<TState, T>, InlineAttemptSink>(invoker, state, cancellationToken)
            : RunCoreAsync<TState, T, StatefulInvoker<TState, T>, NoAttemptSink>(invoker, state, cancellationToken);
    }

    private async ValueTask<T> RunCoreAsync<TState, T, TInvoker, TSink>(TInvoker invoker, TState state, CancellationToken cancellationToken)
        where TInvoker : struct, IInvoker<TState, T>
        where TSink : struct, IAttemptSink
    {
        var policy = Policy;
        var time = policy.Time;
        var breaker = policy.Breaker;
        var budget = policy.Budget;
        var bounded = policy.Deadline != Timeout.InfiniteTimeSpan;
        var startTimestamp = bounded ? time.GetTimestamp() : 0L;

        TSink log = default;
        var attempts = 0;
        ExceptionDispatchInfo? lastError = null;

        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            if (breaker is not null && !breaker.TryEnter(time))
                throw new ProbeBreakerOpenException();

            var remaining = Remaining(policy, time, startTimestamp, bounded);

            if (remaining == TimeSpan.Zero)
                throw new ProbeDeadlineException();

            var effective = Effective(policy.AttemptTimeout, remaining);

            CancellationTokenSource? timer = null;
            CancellationTokenSource? linked = null;
            var attemptToken = cancellationToken;

            if (effective != Timeout.InfiniteTimeSpan)
            {
                timer = CtsPool.Rent(time);
                timer.CancelAfter(effective);

                // The pooled source's token is never handed to user code: TryReset preserves token
                // identity, so a callback that outlived its attempt would observe the next
                // operation's cancellation.
                linked = cancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token)
                    : CancellationTokenSource.CreateLinkedTokenSource(timer.Token);

                attemptToken = linked.Token;
            }

            var attemptStart = time.GetTimestamp();
            Verdict verdict;
            T result = default!;
            var succeeded = false;

            try
            {
                result = await invoker.Invoke(state, attemptToken).ConfigureAwait(false);
                verdict = Verdict.Ok;
                succeeded = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancellation is never a failure: never retried, never counted, never
                // converted into a timeout.
                throw;
            }
            catch (OperationCanceledException) when (timer is not null && timer.IsCancellationRequested)
            {
                // Our own attempt timeout. It never reaches the classifier, because the executor
                // knows which source fired and a user predicate must not be able to get that wrong.
                verdict = Verdict.Transient;
                lastError = ExceptionDispatchInfo.Capture(new TimeoutException("The attempt timed out."));
            }
            catch (Exception exception)
            {
                verdict = ProbeClassifier.Classify(exception);
                lastError = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                linked?.Dispose();

                if (timer is not null)
                    CtsPool.Return(timer, time);
            }

            log.Record(attempts, attemptStart, time.GetTimestamp() - attemptStart, verdict.Kind);
            attempts++;

            cancellationToken.ThrowIfCancellationRequested();

            if (succeeded)
            {
                breaker?.RecordSuccess();
                budget?.Refund();
                return result;
            }

            // Only Transient is evidence about the dependency's health.
            if (verdict.Kind == VerdictKind.Transient)
                breaker?.RecordFailure(time);

            var retryable = verdict.Kind is VerdictKind.Transient or VerdictKind.Throttled;

            if (!retryable || attempts >= policy.Attempts)
                break;

            if (budget is not null && !budget.TrySpend())
                break;

            var delay = ProbeBackoff.Compute(policy, verdict, attempts);

            if (bounded)
            {
                var left = Remaining(policy, time, startTimestamp, true);

                if (left == TimeSpan.Zero || delay >= left)
                    break;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);

                // A token cancelled 400 ms into a backoff must abort the operation, not start
                // another attempt.
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        lastError?.Throw();
        throw new ProbeExhaustedException(attempts);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TimeSpan Remaining(FusedPolicy policy, TimeProvider time, long startTimestamp, bool bounded)
    {
        if (!bounded)
            return Timeout.InfiniteTimeSpan;

        var elapsed = time.GetElapsedTime(startTimestamp);
        var left = policy.Deadline - elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TimeSpan Effective(TimeSpan attemptTimeout, TimeSpan remaining)
    {
        if (attemptTimeout == Timeout.InfiniteTimeSpan)
            return remaining;

        if (remaining == Timeout.InfiniteTimeSpan)
            return attemptTimeout;

        return attemptTimeout < remaining ? attemptTimeout : remaining;
    }
}
