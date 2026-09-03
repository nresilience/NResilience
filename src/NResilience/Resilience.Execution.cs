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

        if (Hedge is not null)
            return ExecuteHedgedAsync<VoidResult, T, StatelessInvoker<VoidResult, T>, T, ThrowingShaper<T>>(
                new StatelessInvoker<VoidResult, T>(work), default, cancellationToken);

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

        if (Hedge is not null)
            return Discard(ExecuteHedgedAsync<VoidResult, VoidResult, VoidStatelessInvoker<VoidResult>, VoidResult, ThrowingShaper<VoidResult>>(
                new VoidStatelessInvoker<VoidResult>(work), default, cancellationToken));

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

        if (Hedge is not null)
            return ExecuteHedgedAsync<TState, T, StatefulInvoker<TState, T>, T, ThrowingShaper<T>>(
                new StatefulInvoker<TState, T>(work), state, cancellationToken);

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

        if (Hedge is not null)
            return Discard(ExecuteHedgedAsync<TState, VoidResult, VoidStatefulInvoker<TState>, VoidResult, ThrowingShaper<VoidResult>>(
                new VoidStatefulInvoker<TState>(work), state, cancellationToken));

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

        if (Hedge is not null)
            return ExecuteHedgedAsync<VoidResult, T, StatelessInvoker<VoidResult, T>, CallResult<T>, ResultShaper<T>>(
                new StatelessInvoker<VoidResult, T>(work), default, cancellationToken);

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

        if (Hedge is not null)
            return ExecuteHedgedAsync<VoidResult, VoidResult, VoidStatelessInvoker<VoidResult>, CallResult, VoidResultShaper>(
                new VoidStatelessInvoker<VoidResult>(work), default, cancellationToken);

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

        if (Hedge is not null)
            return ExecuteHedgedAsync<TState, T, StatefulInvoker<TState, T>, CallResult<T>, ResultShaper<T>>(
                new StatefulInvoker<TState, T>(work), state, cancellationToken);

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

        if (Hedge is not null)
            return ExecuteHedgedAsync<TState, VoidResult, VoidStatefulInvoker<TState>, CallResult, VoidResultShaper>(
                new VoidStatefulInvoker<TState>(work), state, cancellationToken);

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
        // The effective deadline: the policy's own, or the tighter of it and the one this call
        // inherited from its caller. Resolved once here rather than per attempt, because an inbound
        // deadline is a fixed point in time and re-reading the AsyncLocal it lives in would charge
        // every attempt for a value that cannot have changed. Costs 8 bytes of state-machine box on
        // the suspending path for every caller, set or not; see Budgets.AmbientDeadlineDelta.
        var deadline = UseAmbientDeadline ? ResilienceDeadline.Clamp(Deadline) : Deadline;
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
                    var pause = GuardDelay(Remaining(Time, start, deadline));

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

            // Whether the ceiling this attempt was given came from the deadline rather than from
            // AttemptTimeout. Set when that ceiling fires, and read below instead of the clock, which
            // buys a stop condition that does not depend on two clocks agreeing. Measured at zero: it
            // is a third bool live across the await and lands in the padding `recorded` and `hasValue`
            // already leave, so the suspending budgets do not move.
            var deadlineSpent = false;

            try
            {
                var remaining = Remaining(Time, start, deadline);

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

                    remaining = Remaining(Time, start, deadline);

                    if (remaining == TimeSpan.Zero)
                    {
                        reason = StopReason.DeadlineExceeded;
                        NotifyDeadline(log.Count + 1, verdict, Time.GetElapsedTime(start), error);
                        break;
                    }
                }

                var ceiling = Ceiling(log.Count + 1);
                var effective = Effective(ceiling, remaining);

                // Whether the deadline supplied this attempt's ceiling. Computed here, where both terms
                // are still in hand, rather than in the catch below: with AttemptCeiling configured,
                // `effective != AttemptTimeout` no longer means "the deadline won", and hoisting the
                // ceiling itself across the attempt await would cost every caller 8 bytes of
                // state-machine box where a fourth bool costs none - it lands in the padding
                // `recorded`, `hasValue` and `deadlineSpent` already leave.
                var deadlineCeiling = deadline != Timeout.InfiniteTimeSpan && effective != ceiling;

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
                    // Null means the callback already had its answer, which only a ValueTask-returning
                    // one can. The branch is what keeps that case off the heap; see IInvoker.Invoke.
                    var attempt = invoker.Invoke(state, attemptToken, ref value);

                    if (attempt is not null)
                    {
                        await attempt.ConfigureAwait(false);
                        value = invoker.Result(attempt);
                    }
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

                    // Effective() hands back whichever of AttemptTimeout and the remaining deadline is
                    // smaller. When the deadline supplied it, the ceiling that just fired *was* the
                    // deadline, and that is the fact to stop on - not what the clock says afterwards.
                    // CancelAfter measures in whole milliseconds, so a deadline with a fractional
                    // millisecond left on it arms the timer early and Remaining() still reports the
                    // remainder. Reading the clock instead spends the rest of the attempt budget on
                    // attempts that are cancelled before they can send anything, and reports
                    // AttemptsExhausted for a call the deadline stopped.
                    deadlineSpent = deadlineCeiling;
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

                var next = AfterAttempt(
                    ref log, ref recorded, start, attemptStart, deadline, attemptSource is not null, effective, deadlineSpent,
                    verdict, error, in value, hasValue, budget, cancellationToken, out var wait, out var stopped);

                if (next == NextStep.Succeeded)
                {
                    var succeeded = shaper.WantsLogOnSuccess
                        ? log.Materialize(Time.GetElapsedTime(start), deadline)
                        : AttemptLog.Empty;

                    return shaper.Success(value, succeeded);
                }

                if (next == NextStep.Stop)
                {
                    reason = stopped;

                    // Zero for every stop but a guarded one, and Delay() hands back a completed task
                    // for zero - so the three reasons that stop without pausing do not suspend here.
                    await Delay(Time, wait, cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, Time, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                if (Breaker is { } b && !recorded)
                    b.ReleaseProbe();
            }
        }

        var attempts = log.Materialize(Time.GetElapsedTime(start), deadline);

        // Read after the guarded delay rather than before it, which is both more accurate - the
        // hint is that much shorter by the time the caller sees it - and keeps a TimeSpan? out of
        // the state-machine box.
        var retryAfter = reason switch
        {
            StopReason.DependencyUnavailable => Breaker?.RetryAfterHint(),
            StopReason.BudgetExhausted => budget?.RetryAfterHint(),
            _ => null,
        };

        return shaper.Failure(value, hasValue, error, reason, deadline, attempts, retryAfter);
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
    ///         on purpose: the two are meant to drift only in the one place this comment marks. The half
    ///         of the iteration that follows an attempt is not duplicated at all - it lives in
    ///         <see cref="AfterAttempt{T}" />, which both loops call - so what is copied here is the
    ///         admission, deadline and attempt-invocation shell, and a change to the order in which
    ///         outcomes are judged is made once. <see cref="Admit" /> is awaited inside
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

        // The effective deadline: the policy's own, or the tighter of it and the one this call
        // inherited from its caller. Resolved once here rather than per attempt, because an inbound
        // deadline is a fixed point in time and re-reading the AsyncLocal it lives in would charge
        // every attempt for a value that cannot have changed. Costs 8 bytes of state-machine box on
        // the suspending path for every caller, set or not; see Budgets.AmbientDeadlineDelta.
        var deadline = UseAmbientDeadline ? ResilienceDeadline.Clamp(Deadline) : Deadline;
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
                    var pause = GuardDelay(Remaining(Time, start, deadline));

                    if (OnEvent is not null)
                        Notify(CallEventKind.RejectedByBreaker, log.Count + 1, verdict, Time.GetElapsedTime(start), pause, error, null,
                            StopReason.DependencyUnavailable);

                    await Delay(Time, pause, cancellationToken).ConfigureAwait(false);
                    break;
                }
            }

            var recorded = false;

            // Whether the ceiling this attempt was given came from the deadline rather than from
            // AttemptTimeout. Set when that ceiling fires, and read below instead of the clock, which
            // buys a stop condition that does not depend on two clocks agreeing. Measured at zero: it
            // is a third bool live across the await and lands in the padding `recorded` and `hasValue`
            // already leave, so the suspending budgets do not move.
            var deadlineSpent = false;

            try
            {
                var remaining = Remaining(Time, start, deadline);

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

                    remaining = Remaining(Time, start, deadline);

                    if (remaining == TimeSpan.Zero)
                    {
                        reason = StopReason.DeadlineExceeded;
                        NotifyDeadline(log.Count + 1, verdict, Time.GetElapsedTime(start), error);
                        break;
                    }
                }

                var ceiling = Ceiling(log.Count + 1);
                var effective = Effective(ceiling, remaining);

                // See ExecuteAsync: computed beside the ceiling so the ceiling itself is not live across
                // the attempt await.
                var deadlineCeiling = deadline != Timeout.InfiniteTimeSpan && effective != ceiling;

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
                        // Null means the callback already had its answer, which only a ValueTask-returning
                        // one can. The branch is what keeps that case off the heap; see IInvoker.Invoke.
                        var attempt = invoker.Invoke(state, attemptToken, ref value);

                        if (attempt is not null)
                        {
                            await attempt.ConfigureAwait(false);
                            value = invoker.Result(attempt);
                        }
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
                    deadlineSpent = deadlineCeiling;
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

                var next = AfterAttempt(
                    ref log, ref recorded, start, attemptStart, deadline, attemptSource is not null, effective, deadlineSpent,
                    verdict, error, in value, hasValue, budget, cancellationToken, out var wait, out var stopped);

                if (next == NextStep.Succeeded)
                {
                    var succeeded = shaper.WantsLogOnSuccess
                        ? log.Materialize(Time.GetElapsedTime(start), deadline)
                        : AttemptLog.Empty;

                    return shaper.Success(value, succeeded);
                }

                if (next == NextStep.Stop)
                {
                    reason = stopped;

                    // Zero for every stop but a guarded one, and Delay() hands back a completed task
                    // for zero - so the three reasons that stop without pausing do not suspend here.
                    await Delay(Time, wait, cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, Time, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            finally
            {
                if (Breaker is { } b && !recorded)
                    b.ReleaseProbe();
            }
        }

        var attempts = log.Materialize(Time.GetElapsedTime(start), deadline);

        var retryAfter = reason switch
        {
            StopReason.DependencyUnavailable => Breaker?.RetryAfterHint(),
            StopReason.BudgetExhausted => budget?.RetryAfterHint(),
            _ => null,
        };

        return shaper.Failure(value, hasValue, error, reason, deadline, attempts, retryAfter);
    }

    /// <summary>
    ///     What the loop does once <see cref="AfterAttempt{T}" /> has judged an attempt.
    /// </summary>
    private enum NextStep : byte
    {
        /// <summary>Serve the backoff and start another attempt.</summary>
        Retry,

        /// <summary>The attempt produced a result the classifier called <see cref="VerdictKind.Ok" />.</summary>
        Succeeded,

        /// <summary>The call is over. Serve the pause, if there is one, and leave the loop.</summary>
        Stop,
    }

    /// <summary>
    ///     Everything that happens between an attempt returning and the loop either retrying it,
    ///     returning it, or giving up. Two halves, run back to back:
    ///     <see cref="RecordAttempt{T}" /> writes down what happened, and <see cref="Decide{T}" /> asks
    ///     the six questions in the order they have to be asked - did it succeed, did the caller cancel,
    ///     is it permanent, are the attempts spent, is the deadline spent, will the budget fund another
    ///     one.
    ///     <para>
    ///         Extracted because it is the same code in both sequential execution loops and was
    ///         previously the same code <i>twice</i>, kept in step by a comment asking contributors to
    ///         remember. That is the drift this removes: a change to the order of those questions now
    ///         happens once. It is also what makes a third loop affordable, because the third loop would
    ///         otherwise be a third copy.
    ///     </para>
    ///     <para>
    ///         The two halves are separate methods because the hedged loop needs them separately: it
    ///         records every leg that comes back, and decides only once, when the last of a round's legs
    ///         has. A sequential attempt is the degenerate case of that - one leg per round - so it
    ///         calls both in a row, which is this method.
    ///     </para>
    ///     <para>
    ///         Not <c>async</c>, and that is the whole reason any of this can be shared. A hoisted
    ///         awaiter field is a property of the generated state-machine <b>type</b>, so lifting an
    ///         <c>await</c> out of a loop and into a helper would move the box rather than remove it -
    ///         see <see cref="ExecuteWithAdmitAsync{TState,T,TInvoker,TOut,TShaper}" /> for the same
    ///         argument in the other direction. Everything here is synchronous, so it compiles to an
    ///         ordinary call: the parameters travel in registers and on the stack, none of them reaches
    ///         the state-machine box, and the awaits stay in the loops where the box already pays for
    ///         them.
    ///     </para>
    ///     <para>
    ///         <paramref name="value" /> is passed <c>in</c> rather than by value because <c>T</c> is
    ///         whatever the caller's callback returns and may be a large struct. It is only ever read.
    ///     </para>
    /// </summary>
    /// <typeparam name="T">What the callback returns, or <c>VoidResult</c>.</typeparam>
    /// <param name="log">The inline attempt log, which this appends to.</param>
    /// <param name="recorded">
    ///     Set to true when the breaker was told about this attempt, so the loop's <c>finally</c> knows
    ///     not to return the probe slot a second time.
    /// </param>
    /// <param name="start">Timestamp the whole call started at.</param>
    /// <param name="attemptStart">Timestamp this attempt started at.</param>
    /// <param name="deadline">The effective deadline: <see cref="Deadline" />, clamped by an inbound one when <see cref="UseAmbientDeadline" /> is set.</param>
    /// <param name="timed">
    ///     Whether this attempt was given a cancellable ceiling, which is what makes an overrun
    ///     measurable and therefore reportable as <see cref="CallEventKind.OrphanedWork" />.
    /// </param>
    /// <param name="effective">The ceiling this attempt was given.</param>
    /// <param name="deadlineSpent">Whether the ceiling that fired was the deadline rather than <see cref="AttemptTimeout" />.</param>
    /// <param name="verdict">How the outcome was classified.</param>
    /// <param name="error">What the attempt threw, if it threw.</param>
    /// <param name="value">What the attempt returned, if it returned.</param>
    /// <param name="hasValue">Whether <paramref name="value" /> holds an answer from this attempt.</param>
    /// <param name="budget">The budget this call charges, already resolved.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <param name="wait">
    ///     How long the loop pauses before acting on the return value: the backoff for
    ///     <see cref="NextStep.Retry" />, the guarded pause for a <see cref="NextStep.Stop" /> that was
    ///     refused, and zero otherwise. One <c>out</c> for both because they are never both live, and
    ///     because it is the one value that has to survive the loop's <c>await</c> - two would cost the
    ///     state-machine box two fields.
    /// </param>
    /// <param name="reason">Why the call stopped. Only meaningful for <see cref="NextStep.Stop" />.</param>
    /// <returns>What the loop does next.</returns>
    private NextStep AfterAttempt<T>(
        ref AttemptSink log,
        ref bool recorded,
        long start,
        long attemptStart,
        TimeSpan deadline,
        bool timed,
        TimeSpan effective,
        bool deadlineSpent,
        Verdict verdict,
        Exception? error,
        in T value,
        bool hasValue,
        RetryBudget? budget,
        CancellationToken cancellationToken,
        out TimeSpan wait,
        out StopReason reason)
    {
        RecordAttempt(
            ref log, ref recorded, start, Time.GetElapsedTime(start, attemptStart).Ticks, Time.GetElapsedTime(attemptStart),
            timed, effective, verdict, error, in value, hasValue, AttemptFlags.None);

        return Decide(log.Count, start, deadline, deadlineSpent, verdict, error, in value, hasValue, budget, cancellationToken, out wait, out reason);
    }

    /// <summary>
    ///     Writes one finished attempt down: the inline log entry, the
    ///     <see cref="CallEventKind.Attempt" /> and <see cref="CallEventKind.OrphanedWork" /> events, and
    ///     the circuit breaker's sample of it.
    ///     <para>
    ///         Everything here is about the attempt that just finished and nothing here decides anything,
    ///         which is what lets the hedged loop call it once per leg while calling
    ///         <see cref="Decide{T}" /> once per round.
    ///     </para>
    /// </summary>
    /// <typeparam name="T">What the callback returns, or <c>VoidResult</c>.</typeparam>
    /// <param name="log">The inline attempt log, which this appends to.</param>
    /// <param name="recorded">Set to true when the breaker was told about this attempt.</param>
    /// <param name="start">Timestamp the whole call started at.</param>
    /// <param name="startOffsetTicks">How far into the call this attempt started.</param>
    /// <param name="duration">How long the attempt ran.</param>
    /// <param name="timed">Whether the attempt was given a cancellable ceiling.</param>
    /// <param name="effective">The ceiling it was given.</param>
    /// <param name="verdict">How the outcome was classified.</param>
    /// <param name="error">What it threw, if it threw.</param>
    /// <param name="value">What it returned, if it returned.</param>
    /// <param name="hasValue">Whether <paramref name="value" /> holds an answer from this attempt.</param>
    /// <param name="flags">Whether this attempt was a hedge, and whether it was discarded.</param>
    private void RecordAttempt<T>(
        ref AttemptSink log,
        ref bool recorded,
        long start,
        long startOffsetTicks,
        TimeSpan duration,
        bool timed,
        TimeSpan effective,
        Verdict verdict,
        Exception? error,
        in T value,
        bool hasValue,
        AttemptFlags flags)
    {
        log.Record(startOffsetTicks, duration.Ticks, verdict.Kind, verdict.SelfImposed, error, flags);

        // A leg nobody waited for is not an outcome. It raises its own event, is not classified, and is
        // not evidence: the breaker must not trip on cancellations this library issued, and the caller's
        // listener must not be told that a call failed when what happened is that a faster copy of it
        // succeeded.
        if ((flags & AttemptFlags.Discarded) != 0)
        {
            if (OnEvent is not null)
                Notify(CallEventKind.HedgeDiscarded, log.Count, Verdict.Ok, duration, null, null, null);

            return;
        }

        // Successes only, which is what makes the measured ceiling self-correcting: one tight enough to
        // cancel calls that would have succeeded starves its own estimator, the window falls back below
        // MinimumSamples, and the policy reverts to the configured AttemptTimeout until successes
        // accumulate again. Sampling the failures instead would let a wave of timeouts raise the ceiling
        // that produced them.
        if (verdict.Kind == VerdictKind.Ok && AttemptCeiling is not null)
            ExecutionState.AttemptCeilingFor(this)?.Record(duration);

        // Successes only again, and here it is what keeps backoff from collapsing: a dependency failing
        // fast has a very short latency distribution, and a base measured from it would turn the retry
        // curve into a tight loop at the moment the dependency could least afford one.
        if (verdict.Kind == VerdictKind.Ok && Backoff.Measured is not null)
            ExecutionState.BackoffBaseFor(this)?.Record(duration);

        if (OnEvent is not null)
        {
            var observed = ResultOf(value, hasValue);
            Notify(CallEventKind.Attempt, log.Count, verdict, duration, null, error, observed);

            // A callback that kept running well past the timeout that was supposed to stop it is the
            // ecosystem's most-hit footgun, and it is invisible from inside: the executor is blocked on
            // the very task that ignored its token. So it is reported retrospectively, the moment the
            // work finally does return, by comparing what the attempt was allowed against what it
            // actually took.
            if (timed && duration >= effective + OrphanGrace)
                Notify(CallEventKind.OrphanedWork, log.Count, verdict, duration, null, error, observed);
        }

        // The duration goes with it because the breaker trips on brownouts as well as errors: a
        // dependency returning 200s at 30x normal latency is the most common real degradation, and an
        // error-rate breaker sits closed through the entire incident.
        if (Breaker is { } sampled)
        {
            var outcome = sampled.Record(verdict.Kind, duration);
            recorded = true;

            if (outcome != BreakerTransition.None && OnEvent is not null)
                NotifyBreaker(outcome, log.Count, Time.GetElapsedTime(start));
        }
    }

    /// <summary>
    ///     The six questions, in the order they have to be asked: did it succeed, did the caller cancel,
    ///     is it permanent, are the attempts spent, is the deadline spent, will the budget fund another
    ///     one. Nothing here reads the log or the clock except to report; every input arrives as a
    ///     parameter.
    /// </summary>
    /// <typeparam name="T">What the callback returns, or <c>VoidResult</c>.</typeparam>
    /// <param name="attempts">How many attempts have been recorded, which is the number this one is.</param>
    /// <param name="start">Timestamp the whole call started at.</param>
    /// <param name="deadline">The effective deadline: <see cref="Deadline" />, clamped by an inbound one when <see cref="UseAmbientDeadline" /> is set.</param>
    /// <param name="deadlineSpent">Whether the ceiling that fired was the deadline rather than <see cref="AttemptTimeout" />.</param>
    /// <param name="verdict">How the outcome being judged was classified.</param>
    /// <param name="error">What it threw, if it threw.</param>
    /// <param name="value">What it returned, if it returned.</param>
    /// <param name="hasValue">Whether <paramref name="value" /> holds an answer.</param>
    /// <param name="budget">The budget this call charges, already resolved.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <param name="wait">The pause the loop serves before acting on the return value. See <see cref="AfterAttempt{T}" />.</param>
    /// <param name="reason">Why the call stopped. Only meaningful for <see cref="NextStep.Stop" />.</param>
    /// <returns>What the loop does next.</returns>
    private NextStep Decide<T>(
        int attempts,
        long start,
        TimeSpan deadline,
        bool deadlineSpent,
        Verdict verdict,
        Exception? error,
        in T value,
        bool hasValue,
        RetryBudget? budget,
        CancellationToken cancellationToken,
        out TimeSpan wait,
        out StopReason reason)
    {
        wait = TimeSpan.Zero;
        reason = StopReason.Succeeded;

        // Derived rather than passed: everything here is synchronous, so a comparison costs a register
        // and a parameter would cost a slot on a call the loop makes once per attempt.
        var bounded = deadline != Timeout.InfiniteTimeSpan;

        if (verdict.Kind == VerdictKind.Ok)
        {
            // A success is what funds future retries. Deposits and withdrawals are both on the budget
            // rather than split across it and the breaker, so the fraction the budget enforces is a
            // fraction of the traffic that actually reached the dependency.
            budget?.Deposit();

            // Deliberately checked *after* the success return rather than before it. The
            // post-attempt cancellation check exists to stop the loop starting another attempt; a
            // caller who cancelled while an attempt was already succeeding has waited for that attempt
            // either way, and throwing away work that is done and paid for helps nobody.
            if (OnEvent is not null)
                Notify(CallEventKind.Succeeded, attempts, verdict, Time.GetElapsedTime(start), null, null, ResultOf(value, hasValue),
                    StopReason.Succeeded);

            return NextStep.Succeeded;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (verdict.Kind == VerdictKind.Permanent)
        {
            reason = StopReason.Permanent;

            if (OnEvent is not null)
            {
                // The event that makes "Classifier.Default did not recognize your exception type"
                // visible rather than mysterious: the type is right there on it.
                Notify(CallEventKind.NotRetried, attempts, verdict, Time.GetElapsedTime(start), null, error, ResultOf(value, hasValue),
                    StopReason.Permanent);
            }

            return NextStep.Stop;
        }

        if (attempts >= Attempts)
        {
            reason = StopReason.AttemptsExhausted;

            if (OnEvent is not null)
                Notify(CallEventKind.Exhausted, attempts, verdict, Time.GetElapsedTime(start), null, error, ResultOf(value, hasValue),
                    StopReason.AttemptsExhausted);

            return NextStep.Stop;
        }

        var left = Remaining(Time, start, deadline);

        if (left == TimeSpan.Zero || deadlineSpent)
        {
            reason = StopReason.DeadlineExceeded;
            NotifyDeadline(attempts, verdict, Time.GetElapsedTime(start), error);
            return NextStep.Stop;
        }

        // Throttle, after the deadline check so a retry there is no time for is never charged for.
        // The per-attempt limit above cannot prevent a retry storm on its own, because every caller
        // independently believes it is being reasonable; only a budget expressed as a fraction of
        // traffic bounds the aggregate.
        //
        // A self-imposed refusal is exempt. The budget is a fraction of the traffic that actually
        // reached the dependency, and a retry of a call local admission control stopped costs the
        // dependency nothing - charging for it would let a burst of self-throttling quietly drain the
        // capacity real transient failures need.
        if (budget is not null && !verdict.SelfImposed && !budget.TrySpend())
        {
            reason = StopReason.BudgetExhausted;
            wait = GuardDelay(left);

            if (OnEvent is not null)
                Notify(CallEventKind.RejectedByBudget, attempts, verdict, Time.GetElapsedTime(start), wait, error, ResultOf(value, hasValue),
                    StopReason.BudgetExhausted);

            return NextStep.Stop;
        }

        var delay = Backoff.Compute(new NextAttempt(attempts + 1, verdict, error, left, cancellationToken), MeasuredBase(attempts + 1, verdict.Kind));

        // A delay that would consume the rest of the budget leaves nothing for the attempt after it,
        // so the deadline stops the operation here rather than sleeping through it - and neither does
        // a delay that leaves less than a call to this dependency has ever needed. See Viable().
        if (bounded && delay + Viable() >= left)
        {
            reason = StopReason.DeadlineExceeded;
            NotifyDeadline(attempts, verdict, Time.GetElapsedTime(start), error);
            return NextStep.Stop;
        }

        if (OnEvent is not null)
        {
            // Raised before the backoff is served rather than after it, so a listener sees the retry
            // coming and can report how long the call is about to sit idle.
            Notify(CallEventKind.Retrying, attempts + 1, verdict, Time.GetElapsedTime(start), delay, error, ResultOf(value, hasValue));
        }

        wait = delay;
        return NextStep.Retry;
    }

    /// <summary>
    ///     The least time a retry needs to have any chance of finishing: a low quantile of what a
    ///     successful call to this dependency recently took. <see cref="TimeSpan.Zero" /> when nothing
    ///     is measuring, which is what makes this invisible to a policy that has no estimate.
    ///     <para>
    ///         The retry decision already refuses an attempt when the backoff alone would outlast the
    ///         deadline. This asks the other half of the same question: with 6 ms left and a dependency
    ///         whose median call is 400 ms, starting an attempt sends a real request to a dependency that
    ///         is probably already struggling, holds a connection for 6 ms, and hands the caller an
    ///         <see cref="AttemptTimeoutException" /> where the <see cref="DeadlineExceededException" />
    ///         it was going to get anyway was both truer and available immediately.
    ///     </para>
    /// </summary>
    /// <returns>The estimate, or <see cref="TimeSpan.Zero" /> when there is none.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Deliberately a low quantile.</b> The question is "could this attempt plausibly
    ///         finish", and answering it from the tail would refuse attempts that had a good chance. So
    ///         the source is <see cref="NResilience.Breaker.NormalLatency" /> - the body of the
    ///         distribution, capped at the p50 by <see cref="SlowCalls.Quantile" /> - and never the
    ///         high-quantile windows <see cref="Hedge" /> and <see cref="AttemptCeiling" /> own.
    ///     </para>
    ///     <para>
    ///         <b>It can only ever refuse a retry.</b> The first attempt of every call runs whatever the
    ///         estimate says, because the first attempt is the one the caller asked for; and what this
    ///         changes is <i>when</i> the caller learns the deadline is spent, not <i>what</i> they
    ///         learn. The stop reason is the <see cref="StopReason.DeadlineExceeded" /> the call was
    ///         reaching a few milliseconds later anyway. Only the attempt count in the log differs.
    ///     </para>
    ///     <para>
    ///         Read on the retry decision rather than hoisted into a local, for the reason
    ///         <see cref="Internal.ExecutionState.AttemptCeilingFor" /> gives: a field held across the attempt
    ///         <c>await</c> would cost every caller's state-machine box whether or not anything was
    ///         measuring. The read itself is a memoized answer per window slice.
    ///     </para>
    /// </remarks>
    private TimeSpan Viable() => Breaker?.NormalLatency ?? TimeSpan.Zero;

    /// <summary>
    ///     What a normal call to this dependency recently took, for
    ///     <see cref="NResilience.Backoff.Measured" /> to derive its base from. Null when nothing is
    ///     measuring or the estimate is still cold, which is the case that leaves the curve exactly as
    ///     it is configured.
    /// </summary>
    /// <param name="attemptNumber">Which attempt is about to be delayed, for the event a changed base raises.</param>
    /// <param name="kind">What ended the previous attempt. A throttled one is never measured - see <see cref="BackoffBase" />.</param>
    /// <returns>The baseline, or null.</returns>
    /// <remarks>
    ///     Read on the retry decision rather than hoisted into a local, for the reason
    ///     <see cref="Internal.ExecutionState.AttemptCeilingFor" /> gives. A policy that configures no
    ///     measured base pays one field read off <c>this</c> per retry - and nothing at all per call,
    ///     because a call that does not retry never reaches here.
    /// </remarks>
    private TimeSpan? MeasuredBase(int attemptNumber, VerdictKind kind)
    {
        if (kind == VerdictKind.Throttled || Backoff.Measured is not { } measured)
            return null;

        if (ExecutionState.BackoffBaseFor(this)?.Threshold(measured.MinimumSamples) is not { } normal)
            return null;

        if (OnEvent is not null)
        {
            // The base the curve will actually use, which is the measurement after the clamp - the
            // number an operator comparing it against what they configured wants to see.
            var applied = measured.BaseFor(Backoff.TransientBase, normal);

            if (ExecutionState.BackoffBaseChanged(this, applied))
                Notify(CallEventKind.BackoffBaseAdapted, attemptNumber, Verdict.Ok, TimeSpan.Zero, applied, null, null);
        }

        return normal;
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
    internal static TimeSpan Remaining(TimeProvider time, long start, TimeSpan deadline)
    {
        if (deadline == Timeout.InfiniteTimeSpan)
            return Timeout.InfiniteTimeSpan;

        var left = deadline - time.GetElapsedTime(start);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    ///     This attempt's ceiling before the deadline is applied: <see cref="AttemptTimeout" />, lowered
    ///     by a multiple of recent latency when <see cref="AttemptCeiling" /> is configured and has an
    ///     estimate to offer.
    ///     <para>
    ///         Lowered, never raised. The clamp here is the whole safety argument for the feature - see
    ///         <see cref="AttemptCeiling" /> - and it is also what keeps this method total: every path
    ///         with no measured answer hands back the configured constant, which is exactly today's
    ///         behaviour.
    ///     </para>
    /// </summary>
    /// <param name="attemptNumber">Which attempt this is, for the event a changed ceiling raises.</param>
    /// <returns>The ceiling, or <see cref="Timeout.InfiniteTimeSpan" /> when there is none.</returns>
    /// <remarks>
    ///     Called once per attempt, and the cost to a policy that does not configure
    ///     <see cref="AttemptCeiling" /> is one branch inside <see cref="Measured" />: a field read off
    ///     <c>this</c>, which is already a field of the caller's state-machine box. Nothing here is held
    ///     across an <c>await</c>, which is the point - see <c>ExecutionState.AttemptCeilingFor</c>.
    /// </remarks>
    private TimeSpan Ceiling(int attemptNumber)
    {
        if (Measured() is not { } measured)
            return AttemptTimeout;

        // InfiniteTimeSpan is negative, so it cannot take part in the comparison - a policy that set no
        // constant ceiling and asked for a measured one gets the measured one, still bounded by the
        // deadline like any other.
        if (AttemptTimeout != Timeout.InfiniteTimeSpan && measured >= AttemptTimeout)
            return AttemptTimeout;

        if (OnEvent is not null && ExecutionState.CeilingChanged(this, measured))
            Notify(CallEventKind.AttemptTimeoutAdapted, attemptNumber, Verdict.Ok, TimeSpan.Zero, measured, null, null);

        return measured;
    }

    /// <summary>
    ///     The measured ceiling with its floors applied, before <see cref="AttemptTimeout" /> and the
    ///     deadline clamp it. Null when <see cref="AttemptCeiling" /> is not configured or the estimate is
    ///     still cold, which is the case that leaves the policy behaving exactly as it does today.
    /// </summary>
    /// <returns>The ceiling the measurement asks for, or null.</returns>
    private TimeSpan? Measured()
    {
        if (AttemptCeiling is not { } ceiling)
            return null;

        if (ExecutionState.AttemptCeilingFor(this)?.Threshold(ceiling.MinimumSamples) is not { } tail)
            return null;

        var measured = ceiling.CeilingFor(tail);

        if (measured < ceiling.Floor)
            measured = ceiling.Floor;

        // A hedge arms its second leg at the hedge threshold, so a ceiling at or below that would cancel
        // the first leg at the moment the second was due to start, and the caller would have bought a
        // feature that never fires. So when hedging is configured the ceiling is measured from at least
        // the hedge's own quantile: the same Multiple, applied to the larger of the two estimates. That
        // keeps the unit - the ceiling is always a multiple of a latency estimate - and it leaves the
        // first leg the room the second one needs rather than a tie nobody can rely on.
        //
        // A floor here rather than a refusal in Validate(), because whether the two collide depends on
        // the shape of the distribution and not on the configuration. Both quantiles are read from the
        // same traffic; only the traffic knows how far apart they are. It is unreachable whenever
        // AttemptCeiling.Quantile is at or above Hedge.Quantile, which is the common case.
        if (Hedge is { } hedge && ExecutionState.LatencyFor(this)?.Threshold(hedge.MinimumSamples) is { } armed)
        {
            var hedgeFloor = ceiling.CeilingFor(armed > hedge.MinimumDelay ? armed : hedge.MinimumDelay);

            if (measured < hedgeFloor)
                measured = hedgeFloor;
        }

        return measured;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TimeSpan Effective(TimeSpan attemptTimeout, TimeSpan remaining)
    {
        if (attemptTimeout == Timeout.InfiniteTimeSpan)
            return remaining;

        if (remaining == Timeout.InfiniteTimeSpan)
            return attemptTimeout;

        return attemptTimeout < remaining ? attemptTimeout : remaining;
    }
}
