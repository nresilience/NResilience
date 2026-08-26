using System.Runtime.CompilerServices;
using NResilience.Internal;

namespace NResilience;

/// <content>
///     The execution engine: admission, deadline, attempt loop, per-attempt timeout, classification,
///     backoff and the inline attempt log, all in <b>one</b> <c>async</c> frame.
///     <para>
///         This is the architectural decision the whole design turns on. Every <c>async</c> frame that
///         suspends heap-allocates its own state-machine box, and depth is a linear multiplier - so a
///         chain of composed strategies pays a box per layer on the path every real I/O call takes.
///         Collapsing the layers into one method removes all but one of them.
///     </para>
/// </content>
public sealed partial record Resilience
{
    /// <summary>
    ///     How long a refused call pauses before it is reported. See <see cref="GuardDelay" /> for why it
    ///     pauses at all.
    ///     <para>
    ///         Not configurable, and short enough that no caller needs it to be: it exists to put a floor
    ///         under the rate of a rejection loop, not to be tuned. A static field on a record does not
    ///         participate in the synthesized equality.
    ///     </para>
    /// </summary>
    private static readonly TimeSpan RejectionDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    ///     How far past its own attempt timeout a callback has to run before the overrun is reported
    ///     as <see cref="CallEventKind.OrphanedWork" /> rather than as ordinary scheduling noise.
    ///     <para>
    ///         Not configurable. It is a threshold for a diagnostic, not a bound on behavior, and a
    ///         second is far beyond any delay the thread pool or a cancellation registration can account
    ///         for while being short enough that a callback which genuinely ignored its token always
    ///         crosses it.
    ///     </para>
    /// </summary>
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromSeconds(1);

    /// <summary>Runs a callback, retrying and bounding it according to this policy.</summary>
    /// <typeparam name="T">What the callback returns. Inferred; there is nothing to declare.</typeparam>
    /// <param name="work">
    ///     The work, taking the attempt's cancellation token: cancelled when that attempt hits its
    ///     <see cref="AttemptTimeout" />, and when <paramref name="cancellationToken" /> is. Pass it into
    ///     whatever you call, because that is what lets a timed-out attempt actually stop. Every overload
    ///     takes it, so there is none that lets you forget.
    /// </param>
    /// <param name="cancellationToken">The caller's token. Cancelling it aborts the operation immediately and is never treated as a failure.</param>
    /// <returns>What the last attempt returned.</returns>
    public ValueTask<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
            return new ValueTask<T>(work(cancellationToken));

        if (Admit is not null)
            return ExecuteWithAdmitAsync<VoidResult, T, StatelessInvoker<VoidResult, T>, T, ThrowingShaper<T>>(
                new StatelessInvoker<VoidResult, T>(work), default, cancellationToken);

        return ExecuteAsync<VoidResult, T, StatelessInvoker<VoidResult, T>, T, ThrowingShaper<T>>(
            new StatelessInvoker<VoidResult, T>(work), default, cancellationToken);
    }

    /// <summary>Runs a callback that returns nothing.</summary>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when the operation does.</returns>
    public ValueTask RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
            return new ValueTask(work(cancellationToken));

        if (Admit is not null)
            return Discard(ExecuteWithAdmitAsync<VoidResult, VoidResult, VoidStatelessInvoker<VoidResult>, VoidResult, ThrowingShaper<VoidResult>>(
                new VoidStatelessInvoker<VoidResult>(work), default, cancellationToken));

        return Discard(ExecuteAsync<VoidResult, VoidResult, VoidStatelessInvoker<VoidResult>, VoidResult, ThrowingShaper<VoidResult>>(
            new VoidStatelessInvoker<VoidResult>(work), default, cancellationToken));
    }

    /// <summary>
    ///     Runs a callback with caller state, so the lambda can be <c>static</c> and allocate no
    ///     closure. Same length as the simple form, and zero-allocation on a synchronously-completing
    ///     call.
    /// </summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <typeparam name="T">What the callback returns.</typeparam>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="state">Handed to the callback on every attempt.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>What the last attempt returned.</returns>
    public ValueTask<T> RunAsync<TState, T>(Func<TState, CancellationToken, Task<T>> work, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
            return new ValueTask<T>(work(state, cancellationToken));

        if (Admit is not null)
            return ExecuteWithAdmitAsync<TState, T, StatefulInvoker<TState, T>, T, ThrowingShaper<T>>(
                new StatefulInvoker<TState, T>(work), state, cancellationToken);

        return ExecuteAsync<TState, T, StatefulInvoker<TState, T>, T, ThrowingShaper<T>>(
            new StatefulInvoker<TState, T>(work), state, cancellationToken);
    }

    /// <summary>Runs a callback with caller state that returns nothing.</summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="state">Handed to the callback on every attempt.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when the operation does.</returns>
    public ValueTask RunAsync<TState>(Func<TState, CancellationToken, Task> work, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (IsPassthrough)
            return new ValueTask(work(state, cancellationToken));

        if (Admit is not null)
            return Discard(ExecuteWithAdmitAsync<TState, VoidResult, VoidStatefulInvoker<TState>, VoidResult, ThrowingShaper<VoidResult>>(
                new VoidStatefulInvoker<TState>(work), state, cancellationToken));

        return Discard(ExecuteAsync<TState, VoidResult, VoidStatefulInvoker<TState>, VoidResult, ThrowingShaper<VoidResult>>(
            new VoidStatefulInvoker<TState>(work), state, cancellationToken));
    }

    /// <summary>
    ///     Runs a callback and reports the outcome instead of throwing. This is what replaces a
    ///     fallback strategy - a fallback is an <c>if</c>.
    ///     <para>
    ///         Unlike the throwing forms, this always materializes the attempt log: its caller has
    ///         explicitly asked for a result object, and a history that vanished on success would make
    ///         "assert this succeeded on the third attempt" impossible to write.
    ///     </para>
    /// </summary>
    /// <typeparam name="T">What the callback returns.</typeparam>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="cancellationToken">The caller's token. Its cancellation is the one thing this method still throws.</param>
    /// <returns>The outcome.</returns>
    public ValueTask<CallResult<T>> TryRunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (Admit is not null)
            return ExecuteWithAdmitAsync<VoidResult, T, StatelessInvoker<VoidResult, T>, CallResult<T>, ResultShaper<T>>(
                new StatelessInvoker<VoidResult, T>(work), default, cancellationToken);

        return ExecuteAsync<VoidResult, T, StatelessInvoker<VoidResult, T>, CallResult<T>, ResultShaper<T>>(
            new StatelessInvoker<VoidResult, T>(work), default, cancellationToken);
    }

    /// <summary>Runs a callback that returns nothing, and reports the outcome instead of throwing.</summary>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The outcome.</returns>
    public ValueTask<CallResult> TryRunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (Admit is not null)
            return ExecuteWithAdmitAsync<VoidResult, VoidResult, VoidStatelessInvoker<VoidResult>, CallResult, VoidResultShaper>(
                new VoidStatelessInvoker<VoidResult>(work), default, cancellationToken);

        return ExecuteAsync<VoidResult, VoidResult, VoidStatelessInvoker<VoidResult>, CallResult, VoidResultShaper>(
            new VoidStatelessInvoker<VoidResult>(work), default, cancellationToken);
    }

    /// <summary>Runs a callback with caller state, and reports the outcome instead of throwing.</summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <typeparam name="T">What the callback returns.</typeparam>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="state">Handed to the callback on every attempt.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The outcome.</returns>
    public ValueTask<CallResult<T>> TryRunAsync<TState, T>(Func<TState, CancellationToken, Task<T>> work, TState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (Admit is not null)
            return ExecuteWithAdmitAsync<TState, T, StatefulInvoker<TState, T>, CallResult<T>, ResultShaper<T>>(
                new StatefulInvoker<TState, T>(work), state, cancellationToken);

        return ExecuteAsync<TState, T, StatefulInvoker<TState, T>, CallResult<T>, ResultShaper<T>>(
            new StatefulInvoker<TState, T>(work), state, cancellationToken);
    }

    /// <summary>Runs a callback with caller state that returns nothing, and reports the outcome.</summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <param name="work">The work, taking the attempt's cancellation token. Pass it into whatever you call.</param>
    /// <param name="state">Handed to the callback on every attempt.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The outcome.</returns>
    public ValueTask<CallResult> TryRunAsync<TState>(Func<TState, CancellationToken, Task> work, TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ExecutionState.EnsureValidated(this);

        if (Admit is not null)
            return ExecuteWithAdmitAsync<TState, VoidResult, VoidStatefulInvoker<TState>, CallResult, VoidResultShaper>(
                new VoidStatefulInvoker<TState>(work), state, cancellationToken);

        return ExecuteAsync<TState, VoidResult, VoidStatefulInvoker<TState>, CallResult, VoidResultShaper>(
            new VoidStatefulInvoker<TState>(work), state, cancellationToken);
    }

    /// <summary>
    ///     Drops a <see cref="VoidResult" /> without adding a frame.
    ///     <para>
    ///         When the core suspended, its <c>ValueTask&lt;VoidResult&gt;</c> is backed by a
    ///         <c>Task&lt;VoidResult&gt;</c>, which <i>is</i> a <see cref="Task" /> - so <c>AsTask()</c>
    ///         hands back the object that already exists rather than creating one. Awaiting the core here
    ///         instead would cost a second state-machine box on the suspending path, which is the whole
    ///         thing this design exists to avoid.
    ///     </para>
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
        // whole design exists to minimize: caching the policy's Backoff in a local costs 56 bytes
        // on every suspending call to save a field load that the JIT keeps in a register anyway.
        // `this` is already a field of the box, so reading a property off it is free.
        var bounded = Deadline != Timeout.InfiniteTimeSpan;
        TShaper shaper = default;

        // The one local the breaker and budget add to the box, at 8 bytes: either the policy's own
        // budget or the automatic one private to this policy instance. Resolved once here rather
        // than at each of the two points that need it, because re-resolving after the attempt await
        // would miss the per-thread cache - a continuation resumes on whichever pool thread is free.
        var budget = ExecutionState.BudgetFor(this);

        var start = Time.GetTimestamp();
        AttemptSink log = default;

        // One slot each, reused every iteration, rather than a current and a previous pair.
        var verdict = Verdict.Ok;
        Exception? error = null;
        T value = default!;
        var hasValue = false;
        StopReason reason;

        // Caller cancellation is never a failure. Checked here, after every attempt returns, and
        // after every backoff delay, because a token cancelled 400 ms into a backoff must abort
        // the operation rather than start another attempt.
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            // Admission. Checked per attempt rather than once per operation, because the breaker
            // samples attempts - so a first attempt that trips it must stop the second, which is
            // the whole point of having tripped. It is also why "does the breaker see attempts or
            // whole operations?" has one answer here instead of depending on composition order.
            if (Breaker is { } breaker)
            {
                var admitted = breaker.TryEnter(out var admission);

                // Raised outside the breaker's lock, on purpose: a listener is arbitrary user code
                // and one slow listener holding that lock would serialize every call through the
                // breaker.
                if (admission != BreakerTransition.None && OnEvent is not null)
                    NotifyBreaker(admission, log.Count + 1, Time.GetElapsedTime(start));

                if (!admitted)
                {
                    reason = StopReason.DependencyUnavailable;
                    var pause = GuardDelay(Remaining(Time, start, Deadline, bounded));

                    if (OnEvent is not null)
                        Notify(CallEventKind.RejectedByBreaker, log.Count + 1, verdict, Time.GetElapsedTime(start), pause, error, null,
                            StopReason.DependencyUnavailable);

                    await Delay(Time, pause, cancellationToken).ConfigureAwait(false);
                    break;
                }
            }

            // The breaker admitted this attempt. If the attempt never reaches the recording point -
            // because the deadline expired, the caller cancelled, or the BeforeAttempt hook threw -
            // the probe slot it consumed must be returned or the breaker wedges in HalfOpen forever.
            // The flag is set when Record is called, and the finally below releases only when it
            // was not. A bool live across the await costs one byte in the state-machine box; the
            // alternative is a liveness bug.
            var recorded = false;

            try
            {
                var remaining = Remaining(Time, start, Deadline, bounded);

                if (remaining == TimeSpan.Zero)
                {
                    reason = StopReason.DeadlineExceeded;
                    NotifyDeadline(log.Count + 1, verdict, Time.GetElapsedTime(start), error);
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
                        NotifyDeadline(log.Count + 1, verdict, Time.GetElapsedTime(start), error);
                        break;
                    }
                }

                var effective = Effective(AttemptTimeout, remaining);

                CancellationTokenSource? timer = null;
                CancellationTokenSource? attemptSource = null;
                var attemptToken = cancellationToken;

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

                var attemptStart = Time.GetTimestamp();
                error = null;
                hasValue = false;

                try
                {
                    var attempt = invoker.Invoke(state, attemptToken);
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
                catch (RateLimitedException limited)
                {
                    // Local admission control. It never reaches the classifier either: a refusal this
                    // process imposed on itself is not evidence about the dependency, and a user
                    // predicate that called it Transient would open a circuit against a service that
                    // was never contacted. The verdict is Throttled - so the long backoff curve and the
                    // limiter's own hint apply - and carries the flag that keeps the retry budget from
                    // being charged for a call that never left.
                    verdict = Verdict.Limited(limited.RetryAfter);
                    error = limited;
                }
                catch (Exception exception)
                {
                    verdict = Classify.ClassifyException(exception);

                    // An exception cannot be turned into a value, so a classifier that calls one Ok
                    // is read as "stop, do not retry".
                    if (verdict.Kind == VerdictKind.Ok)
                        verdict = Verdict.Permanent;

                    error = exception;
                }
                finally
                {
                    attemptSource?.Dispose();

                    if (timer is not null)
                        CtsPool.Return(timer, Time);
                }

                var duration = Time.GetElapsedTime(attemptStart);

                log.Record(
                    Time.GetElapsedTime(start, attemptStart).Ticks,
                    duration.Ticks,
                    verdict.Kind,
                    verdict.SelfImposed,
                    error);

                if (OnEvent is not null)
                {
                    var observed = ResultOf(value, hasValue);
                    Notify(CallEventKind.Attempt, log.Count, verdict, duration, null, error, observed);

                    // A callback that kept running well past the timeout that was supposed to stop it
                    // is the ecosystem's most-hit footgun, and it is invisible from inside: the
                    // executor is blocked on the very task that ignored its token. So it is reported
                    // retrospectively, the moment the work finally does return, by comparing what the
                    // attempt was allowed against what it actually took.
                    if (attemptSource is not null && duration >= effective + OrphanGrace)
                        Notify(CallEventKind.OrphanedWork, log.Count, verdict, duration, null, error, observed);
                }

                // The duration goes with it because the breaker trips on brownouts as well as errors:
                // a dependency returning 200s at 30x normal latency is the most common real degradation,
                // and an error-rate breaker sits closed through the entire incident.
                if (Breaker is { } sampled)
                {
                    var outcome = sampled.Record(verdict.Kind, duration);
                    recorded = true;

                    if (outcome != BreakerTransition.None && OnEvent is not null)
                        NotifyBreaker(outcome, log.Count, Time.GetElapsedTime(start));
                }

                if (verdict.Kind == VerdictKind.Ok)
                {
                    // A success is what funds future retries. Deposits and withdrawals are both on the
                    // budget rather than split across it and the breaker, so the fraction the budget
                    // enforces is a fraction of the traffic that actually reached the dependency.
                    budget?.Deposit();

                    // Deliberately checked *after* the success return rather than before it. The
                    // post-attempt cancellation check exists to stop the loop starting another attempt;
                    // a caller who cancelled while an attempt was already succeeding has waited for
                    // that attempt either way, and throwing away work that is done and paid for helps
                    // nobody.
                    if (OnEvent is not null)
                        Notify(CallEventKind.Succeeded, log.Count, verdict, Time.GetElapsedTime(start), null, null, ResultOf(value, hasValue),
                            StopReason.Succeeded);

                    var succeeded = shaper.WantsLogOnSuccess
                        ? log.Materialize(Time.GetElapsedTime(start), Deadline, bounded)
                        : AttemptLog.Empty;

                    return shaper.Success(value, succeeded);
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (verdict.Kind == VerdictKind.Permanent)
                {
                    reason = StopReason.Permanent;

                    if (OnEvent is not null)
                    {
                        // The event that makes "Classifier.Default did not recognize your exception
                        // type" visible rather than mysterious: the type is right there on it.
                        Notify(CallEventKind.NotRetried, log.Count, verdict, Time.GetElapsedTime(start), null, error, ResultOf(value, hasValue),
                            StopReason.Permanent);
                    }

                    break;
                }

                if (log.Count >= Attempts)
                {
                    reason = StopReason.AttemptsExhausted;

                    if (OnEvent is not null)
                        Notify(CallEventKind.Exhausted, log.Count, verdict, Time.GetElapsedTime(start), null, error, ResultOf(value, hasValue),
                            StopReason.AttemptsExhausted);

                    break;
                }

                var left = Remaining(Time, start, Deadline, bounded);

                if (left == TimeSpan.Zero)
                {
                    reason = StopReason.DeadlineExceeded;
                    NotifyDeadline(log.Count, verdict, Time.GetElapsedTime(start), error);
                    break;
                }

                // Throttle, after the deadline check so a retry there is no time for is never
                // charged for. The per-attempt limit above cannot prevent a retry storm on its own,
                // because every caller independently believes it is being reasonable; only a budget
                // expressed as a fraction of traffic bounds the aggregate.
                //
                // A self-imposed refusal is exempt. The budget is a fraction of the traffic that
                // actually reached the dependency, and a retry of a call local admission control
                // stopped costs the dependency nothing - charging for it would let a burst of
                // self-throttling quietly drain the capacity real transient failures need.
                if (budget is not null && !verdict.SelfImposed && !budget.TrySpend())
                {
                    reason = StopReason.BudgetExhausted;
                    var refused = GuardDelay(left);

                    if (OnEvent is not null)
                        Notify(CallEventKind.RejectedByBudget, log.Count, verdict, Time.GetElapsedTime(start), refused, error, ResultOf(value, hasValue),
                            StopReason.BudgetExhausted);

                    await Delay(Time, refused, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var delay = Backoff.Compute(new NextAttempt(log.Count + 1, verdict, error, left, cancellationToken));

                // A delay that would consume the rest of the budget leaves nothing for the attempt
                // after it, so the deadline stops the operation here rather than sleeping through it.
                if (bounded && delay >= left)
                {
                    reason = StopReason.DeadlineExceeded;
                    NotifyDeadline(log.Count, verdict, Time.GetElapsedTime(start), error);
                    break;
                }

                if (OnEvent is not null)
                {
                    // Raised before the backoff is served rather than after it, so a listener sees the
                    // retry coming and can report how long the call is about to sit idle.
                    Notify(CallEventKind.Retrying, log.Count + 1, verdict, Time.GetElapsedTime(start), delay, error, ResultOf(value, hasValue));
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, Time, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                if (Breaker is { } b && !recorded)
                    b.ReleaseProbe();
            }
        }

        var attempts = log.Materialize(Time.GetElapsedTime(start), Deadline, bounded);

        // Read after the guarded delay rather than before it, which is both more accurate - the
        // hint is that much shorter by the time the caller sees it - and keeps a TimeSpan? out of
        // the state-machine box.
        var retryAfter = reason switch
        {
            StopReason.DependencyUnavailable => Breaker?.RetryAfterHint(),
            StopReason.BudgetExhausted => budget?.RetryAfterHint(),
            _ => null,
        };

        return shaper.Failure(value, hasValue, error, reason, Deadline, attempts, retryAfter);
    }

    /// <summary>
    ///     The same loop as <see cref="ExecuteAsync{TState,T,TInvoker,TOut,TShaper}" />, selected only
    ///     when <see cref="Admit" /> is configured.
    ///     <para>
    ///         A second, separate <c>async</c> method rather than one shared loop with a conditional
    ///         await, because a hoisted awaiter field is a property of the generated state-machine
    ///         <b>type</b>: an <c>await Admit(...)</c> written once in <see cref="ExecuteAsync{TState,T,TInvoker,TOut,TShaper}" />
    ///         would cost every caller that field, whether or not the hook is set. Splitting the loop
    ///         charges the field only to the callers who actually select this method - see "One bit, zero
    ///         bytes" in the admission control deep dive for the general form of this argument.
    ///     </para>
    ///     <para>
    ///         Everything except the admission check is identical to <see cref="ExecuteAsync{TState,T,TInvoker,TOut,TShaper}" />
    ///         on purpose: the two are meant to drift only in the one place this comment marks, and every
    ///         change to the shared shell has to be made in both. <see cref="Admit" /> is awaited inside
    ///         the same inner <c>try</c> the attempt itself runs in, using the same <c>attemptToken</c>,
    ///         so it gets the three properties the callback-based recipe already has: per attempt,
    ///         bounded, and classified. A verdict other than <see cref="VerdictKind.Ok" /> skips the
    ///         attempt entirely and is processed exactly as if the callback had produced that verdict -
    ///         the same log entry, telemetry, breaker and budget treatment. An exception <see cref="Admit" />
    ///         throws falls into the same <c>catch</c> clauses the attempt's own exceptions do, so it is
    ///         classified rather than special-cased.
    ///     </para>
    /// </summary>
    private async ValueTask<TOut> ExecuteWithAdmitAsync<TState, T, TInvoker, TOut, TShaper>(
        TInvoker invoker,
        TState state,
        CancellationToken cancellationToken)
        where TInvoker : struct, IInvoker<TState, T>
        where TShaper : struct, IOutcomeShaper<T, TOut>
    {
        var admit = Admit!;

        var bounded = Deadline != Timeout.InfiniteTimeSpan;
        TShaper shaper = default;
        var budget = ExecutionState.BudgetFor(this);
        var start = Time.GetTimestamp();
        AttemptSink log = default;

        var verdict = Verdict.Ok;
        Exception? error = null;
        T value = default!;
        var hasValue = false;
        StopReason reason;

        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            if (Breaker is { } breaker)
            {
                var admitted = breaker.TryEnter(out var admission);

                if (admission != BreakerTransition.None && OnEvent is not null)
                    NotifyBreaker(admission, log.Count + 1, Time.GetElapsedTime(start));

                if (!admitted)
                {
                    reason = StopReason.DependencyUnavailable;
                    var pause = GuardDelay(Remaining(Time, start, Deadline, bounded));

                    if (OnEvent is not null)
                        Notify(CallEventKind.RejectedByBreaker, log.Count + 1, verdict, Time.GetElapsedTime(start), pause, error, null,
                            StopReason.DependencyUnavailable);

                    await Delay(Time, pause, cancellationToken).ConfigureAwait(false);
                    break;
                }
            }

            var recorded = false;

            try
            {
                var remaining = Remaining(Time, start, Deadline, bounded);

                if (remaining == TimeSpan.Zero)
                {
                    reason = StopReason.DeadlineExceeded;
                    NotifyDeadline(log.Count + 1, verdict, Time.GetElapsedTime(start), error);
                    break;
                }

                if (BeforeAttempt is { } beforeAttempt)
                {
                    await beforeAttempt(new NextAttempt(log.Count + 1, verdict, error, remaining, cancellationToken)).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    remaining = Remaining(Time, start, Deadline, bounded);

                    if (remaining == TimeSpan.Zero)
                    {
                        reason = StopReason.DeadlineExceeded;
                        NotifyDeadline(log.Count + 1, verdict, Time.GetElapsedTime(start), error);
                        break;
                    }
                }

                var effective = Effective(AttemptTimeout, remaining);

                CancellationTokenSource? timer = null;
                CancellationTokenSource? attemptSource = null;
                var attemptToken = cancellationToken;

                if (effective != Timeout.InfiniteTimeSpan)
                {
                    timer = CtsPool.Rent(Time);
                    timer.CancelAfter(effective);

                    attemptSource = cancellationToken.CanBeCanceled
                        ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token)
                        : CancellationTokenSource.CreateLinkedTokenSource(timer.Token);

                    attemptToken = attemptSource.Token;
                }

                var attemptStart = Time.GetTimestamp();
                error = null;
                hasValue = false;

                try
                {
                    // The one place this loop differs from ExecuteAsync: a verdict other than Ok skips
                    // the attempt and is processed exactly as if the callback had produced it.
                    var decision = await admit(new NextAttempt(log.Count + 1, verdict, error, remaining, attemptToken)).ConfigureAwait(false);

                    if (decision.Kind == VerdictKind.Ok)
                    {
                        var attempt = invoker.Invoke(state, attemptToken);
                        await attempt.ConfigureAwait(false);
                        value = invoker.Result(attempt);
                        hasValue = true;
                        verdict = Classify.ClassifyResult(value);
                    }
                    else
                    {
                        verdict = decision;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException canceled) when (attemptSource is not null && attemptSource.IsCancellationRequested)
                {
                    verdict = Verdict.Transient;
                    error = new AttemptTimeoutException(effective, canceled);
                }
                catch (RateLimitedException limited)
                {
                    verdict = Verdict.Limited(limited.RetryAfter);
                    error = limited;
                }
                catch (Exception exception)
                {
                    verdict = Classify.ClassifyException(exception);

                    if (verdict.Kind == VerdictKind.Ok)
                        verdict = Verdict.Permanent;

                    error = exception;
                }
                finally
                {
                    attemptSource?.Dispose();

                    if (timer is not null)
                        CtsPool.Return(timer, Time);
                }

                var duration = Time.GetElapsedTime(attemptStart);

                log.Record(
                    Time.GetElapsedTime(start, attemptStart).Ticks,
                    duration.Ticks,
                    verdict.Kind,
                    verdict.SelfImposed,
                    error);

                if (OnEvent is not null)
                {
                    var observed = ResultOf(value, hasValue);
                    Notify(CallEventKind.Attempt, log.Count, verdict, duration, null, error, observed);

                    if (attemptSource is not null && duration >= effective + OrphanGrace)
                        Notify(CallEventKind.OrphanedWork, log.Count, verdict, duration, null, error, observed);
                }

                if (Breaker is { } sampled)
                {
                    var outcome = sampled.Record(verdict.Kind, duration);
                    recorded = true;

                    if (outcome != BreakerTransition.None && OnEvent is not null)
                        NotifyBreaker(outcome, log.Count, Time.GetElapsedTime(start));
                }

                if (verdict.Kind == VerdictKind.Ok)
                {
                    budget?.Deposit();

                    if (OnEvent is not null)
                        Notify(CallEventKind.Succeeded, log.Count, verdict, Time.GetElapsedTime(start), null, null, ResultOf(value, hasValue),
                            StopReason.Succeeded);

                    var succeeded = shaper.WantsLogOnSuccess
                        ? log.Materialize(Time.GetElapsedTime(start), Deadline, bounded)
                        : AttemptLog.Empty;

                    return shaper.Success(value, succeeded);
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (verdict.Kind == VerdictKind.Permanent)
                {
                    reason = StopReason.Permanent;

                    if (OnEvent is not null)
                    {
                        Notify(CallEventKind.NotRetried, log.Count, verdict, Time.GetElapsedTime(start), null, error, ResultOf(value, hasValue),
                            StopReason.Permanent);
                    }

                    break;
                }

                if (log.Count >= Attempts)
                {
                    reason = StopReason.AttemptsExhausted;

                    if (OnEvent is not null)
                        Notify(CallEventKind.Exhausted, log.Count, verdict, Time.GetElapsedTime(start), null, error, ResultOf(value, hasValue),
                            StopReason.AttemptsExhausted);

                    break;
                }

                var left = Remaining(Time, start, Deadline, bounded);

                if (left == TimeSpan.Zero)
                {
                    reason = StopReason.DeadlineExceeded;
                    NotifyDeadline(log.Count, verdict, Time.GetElapsedTime(start), error);
                    break;
                }

                if (budget is not null && !verdict.SelfImposed && !budget.TrySpend())
                {
                    reason = StopReason.BudgetExhausted;
                    var refused = GuardDelay(left);

                    if (OnEvent is not null)
                        Notify(CallEventKind.RejectedByBudget, log.Count, verdict, Time.GetElapsedTime(start), refused, error, ResultOf(value, hasValue),
                            StopReason.BudgetExhausted);

                    await Delay(Time, refused, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var delay = Backoff.Compute(new NextAttempt(log.Count + 1, verdict, error, left, cancellationToken));

                if (bounded && delay >= left)
                {
                    reason = StopReason.DeadlineExceeded;
                    NotifyDeadline(log.Count, verdict, Time.GetElapsedTime(start), error);
                    break;
                }

                if (OnEvent is not null)
                {
                    Notify(CallEventKind.Retrying, log.Count + 1, verdict, Time.GetElapsedTime(start), delay, error, ResultOf(value, hasValue));
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, Time, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                if (Breaker is { } b && !recorded)
                    b.ReleaseProbe();
            }
        }

        var attempts = log.Materialize(Time.GetElapsedTime(start), Deadline, bounded);

        var retryAfter = reason switch
        {
            StopReason.DependencyUnavailable => Breaker?.RetryAfterHint(),
            StopReason.BudgetExhausted => budget?.RetryAfterHint(),
            _ => null,
        };

        return shaper.Failure(value, hasValue, error, reason, Deadline, attempts, retryAfter);
    }

    /// <summary>
    ///     The pause a refused call serves before it is reported.
    ///     <para>
    ///         <b>Guarded rejection is not fail-fast.</b> A cheap rejection inside a caller's
    ///         <c>while (true)</c> polling loop is a CPU spin: without a forced pause, a tripped breaker or a
    ///         depleted budget turns into errors returned with no delay, spiking client CPU and generating
    ///         more traffic than the call it refused would have. AWS carves out an explicit exception for
    ///         exactly this on its long-polling operations.
    ///     </para>
    ///     <para>
    ///         Bounded by the time left on the deadline, so a refusal can never make a call overrun the
    ///         budget its caller set. Returns <see cref="Task" /> so the await shares the hoisted awaiter
    ///         field the attempt and the backoff delay already need.
    ///     </para>
    /// </summary>
    private static TimeSpan GuardDelay(TimeSpan remaining) =>
        remaining == Timeout.InfiniteTimeSpan || remaining > RejectionDelay ? RejectionDelay : remaining;

    /// <summary>
    ///     The pause itself, split from <see cref="GuardDelay" /> so a listener can be told how long a
    ///     refusal is about to sit before it sits there. Returns <see cref="Task" /> so the await shares
    ///     the hoisted awaiter field the attempt and the backoff delay already need.
    /// </summary>
    private static Task Delay(TimeProvider time, TimeSpan delay, CancellationToken cancellationToken) =>
        delay > TimeSpan.Zero ? Task.Delay(delay, time, cancellationToken) : Task.CompletedTask;

    /// <summary>
    ///     Boxes an attempt's result for a listener, and only for a listener.
    ///     <para>
    ///         <c>typeof(T)</c> is a JIT constant in each closed instantiation, so the void entry points
    ///         fold this to a constant null rather than handing out a meaningless box of the internal
    ///         no-result type.
    ///     </para>
    /// </summary>
    private static object? ResultOf<T>(T value, bool hasValue) =>
        hasValue && typeof(T) != typeof(VoidResult) ? value : null;

    /// <summary>
    ///     Raises one event.
    ///     <para>
    ///         Every call site is already guarded by a <c>OnEvent is not null</c> test, because the
    ///         arguments - a boxed result, an elapsed-time read - are themselves work not worth doing for
    ///         a policy nobody is listening to. The delegate is read once here rather than twice, so a
    ///         listener detached by another thread between the guard and the raise cannot produce a null
    ///         dereference.
    ///     </para>
    /// </summary>
    private void Notify(CallEventKind kind, int attemptNumber, Verdict verdict, TimeSpan duration, TimeSpan? delay, Exception? error, object? result,
        StopReason? reason = null)
    {
        var listener = OnEvent;

        if (listener is null)
            return;

        try
        {
            listener(new CallEvent(kind, Name, attemptNumber, verdict, duration, delay, error, result, reason));
        }
        catch (Exception)
        {
            // Telemetry that can fail the operation it is observing is worse than no telemetry.
            // There is nowhere honest to report this to - a logger is exactly the thing that just
            // threw - so it is swallowed, and that is documented on OnEvent rather than hidden.
        }
    }

    /// <summary>Raises a breaker transition, which carries no verdict and no result of its own.</summary>
    private void NotifyBreaker(BreakerTransition transition, int attemptNumber, TimeSpan elapsed)
    {
        var kind = transition switch
        {
            BreakerTransition.Opened => CallEventKind.BreakerOpened,
            BreakerTransition.Closed => CallEventKind.BreakerClosed,
            _ => CallEventKind.BreakerHalfOpened,
        };

        Notify(kind, attemptNumber, Verdict.Ok, elapsed, null, null, null);
    }

    /// <summary>
    ///     Raises <see cref="CallEventKind.DeadlineExceeded" />. Unlike the other helpers this checks
    ///     the listener itself, because it is called from four places whose only other work is to set
    ///     the stop reason and leave.
    /// </summary>
    private void NotifyDeadline(int attemptNumber, Verdict verdict, TimeSpan elapsed, Exception? error)
    {
        if (OnEvent is not null)
            Notify(CallEventKind.DeadlineExceeded, attemptNumber, verdict, elapsed, null, error, null, StopReason.DeadlineExceeded);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TimeSpan Remaining(TimeProvider time, long start, TimeSpan deadline, bool bounded)
    {
        if (!bounded)
            return Timeout.InfiniteTimeSpan;

        var left = deadline - time.GetElapsedTime(start);
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
