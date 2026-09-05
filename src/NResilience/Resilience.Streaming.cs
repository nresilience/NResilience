using System.Runtime.CompilerServices;
using NResilience.Internal;

namespace NResilience;

/// <content>
///     The streaming execution path: <c>IAsyncEnumerable&lt;T&gt;</c> sources, retried until the first
///     element is yielded and then handed to the caller untouched.
///     <para>
///         A fourth path rather than a flag on the existing three, for the same reason
///         <c>Admit</c> got its own: the classified region has to keep different things alive. A
///         call's attempt owns nothing that outlives it, so the executor can - and must - tear
///         everything down when the attempt ends. A streaming attempt that produces its first
///         element hands a <b>live enumerator and its token</b> to the caller, who finishes the
///         enumeration arbitrarily later and on another thread. The whole difference between this
///         file and <c>Resilience.Execution.cs</c> is which exit owns that teardown.
///     </para>
///     <para>
///         Everything else is shared: the admission, deadline and backoff shell, the
///         <see cref="AfterAttempt{T}" /> decision, the inline attempt log, the event vocabulary.
///         The two paths are meant to drift only where the comments in this file mark, and a
///         reader who knows the call loop already knows this one.
///     </para>
/// </content>
public sealed partial record Resilience
{
    /// <summary>
    ///     Why a hedged policy cannot start a stream, in one place because all four streaming entry
    ///     points refuse for the same reason.
    /// </summary>
    private const string HedgedStreamRefusal =
        "A policy with Hedge cannot run a streaming call: a hedge is a concurrent second copy of a value-returning attempt, and two interleaved enumerables is a buffering problem, not a hedge. Use a policy without Hedge for the IAsyncEnumerable<T> overloads of RunAsync and TryRunAsync; the same policy still runs calls.";

    /// <summary>
    ///     Runs a cold source, retrying until its first element is yielded, then handing the rest of
    ///     the enumeration to the caller untouched.
    ///     <para>
    ///         The first element is the success point, because once the caller has received one, a
    ///         retry would duplicate or drop work they have already acted on - and before it, the
    ///         stream is indistinguishable from a call: a connection reset, a deadline or a throttling
    ///         reply all arrive before anything is yielded. The first element is classified like any
    ///         result; elements after it pass through unclassified.
    ///     </para>
    ///     <para>
    ///         Cold: each enumeration runs a fresh attempt sequence, exactly as calling
    ///         <c>RunAsync</c> twice would. Each attempt re-invokes <paramref name="source" /> to build
    ///         a fresh stream, the way the HTTP handler builds a fresh request per attempt.
    ///     </para>
    /// </summary>
    /// <typeparam name="T">The element type of the source. Inferred; there is nothing to declare.</typeparam>
    /// <param name="source">
    ///     The cold source, taking the attempt's cancellation token: cancelled when that attempt
    ///     hits its <see cref="AttemptTimeout" /> - which bounds time to the first element only -
    ///     and when <paramref name="cancellationToken" /> is. Pass it into whatever you call.
    /// </param>
    /// <param name="cancellationToken">The caller's token. Cancelling it aborts the operation immediately and is never treated as a failure.</param>
    /// <returns>The first attempt's elements that the classifier accepted, then the rest of that attempt's enumeration, untouched.</returns>
    /// <exception cref="ResilienceConfigurationException">This policy has a <see cref="Hedge" /> configured.</exception>
    public IAsyncEnumerable<T> RunAsync<T>(Func<CancellationToken, IAsyncEnumerable<T>> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ExecutionState.EnsureValidated(this);

        // Refused here rather than in Validate: one policy legitimately serves both call sites and
        // stream sites, and refusing in Validate would break every hedged unary caller to protect
        // a streaming one. Refused here, eagerly, rather than at the first MoveNextAsync - an
        // error three frames later at the consumer's await foreach is a worse diagnostic than the
        // same error at the call site. That eagerness is one reason this method is a plain method
        // returning an iterator rather than an iterator itself.
        if (Hedge is not null)
        {
            throw new ResilienceConfigurationException(HedgedStreamRefusal);
        }

        // A policy that imposes nothing hands back the source's own enumerable. Whether that is
        // re-enumerable is then the source's business, and the enumeration-time token is not
        // merged with cancellationToken, because no iterator of ours sits in the middle to merge
        // them - a caller imposing nothing to begin with, which is the case where it does not
        // matter.
        if (IsPassthrough)
            return source(cancellationToken);

        return ExecuteStreamAsync<VoidResult, T, StatelessStreamStarter<VoidResult, T>>(
            new StatelessStreamStarter<VoidResult, T>(source), default, null, cancellationToken);
    }

    /// <summary>
    ///     Runs a cold source with caller state, so the lambda can be <c>static</c> and allocate no
    ///     closure. Same semantics as the stateless form: retry until the first element, then hand
    ///     the rest of the enumeration to the caller untouched.
    /// </summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <typeparam name="T">The element type of the source.</typeparam>
    /// <param name="source">The cold source. See the stateless form for what its token bounds.</param>
    /// <param name="state">Handed to the source on every attempt.</param>
    /// <param name="cancellationToken">The caller's token. Cancelling it aborts the operation immediately and is never treated as a failure.</param>
    /// <returns>The first attempt's elements that the classifier accepted, then the rest of that attempt's enumeration, untouched.</returns>
    /// <exception cref="ResilienceConfigurationException">This policy has a <see cref="Hedge" /> configured.</exception>
    public IAsyncEnumerable<T> RunAsync<TState, T>(Func<TState, CancellationToken, IAsyncEnumerable<T>> source, TState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ExecutionState.EnsureValidated(this);

        if (Hedge is not null)
        {
            throw new ResilienceConfigurationException(HedgedStreamRefusal);
        }

        if (IsPassthrough)
            return source(state, cancellationToken);

        return ExecuteStreamAsync<TState, T, StatefulStreamStarter<TState, T>>(
            new StatefulStreamStarter<TState, T>(source), state, null, cancellationToken);
    }

    /// <summary>
    ///     Starts a cold source and reports the outcome instead of throwing. The streaming form of
    ///     <see cref="TryRunAsync{T}(Func{CancellationToken, Task{T}}, CancellationToken)" />, and what
    ///     replaces a fallback for a stream - a fallback is an <c>if</c>.
    ///     <para>
    ///         <b>The await ends at the first element</b>, which is the same success point the
    ///         throwing form uses: the returned <see cref="ValueTask{TResult}" /> completes once the
    ///         policy has started the stream, pulled one element and classified it - or given up. So
    ///         <see cref="CallResult{T}.IsSuccess" /> answers the question a stream can actually be
    ///         asked before it is consumed, and the retries, the backoff and the guards have all
    ///         already happened by the time you read it.
    ///     </para>
    ///     <para>
    ///         <b>Everything after the first element is still the consumer's.</b> A fault at element
    ///         nine throws from <c>MoveNextAsync</c> exactly as it does under <c>RunAsync</c>; it is
    ///         not folded into the result, because the result was decided before that element existed.
    ///         Only the outcomes a failed <c>RunAsync</c> would have thrown at the first
    ///         <c>MoveNextAsync</c> - a guard's refusal, a deadline, an exhausted attempt count, a
    ///         first element the classifier refused - arrive here as
    ///         <see cref="CallResult{T}.IsSuccess" /> being false.
    ///     </para>
    ///     <para>
    ///         <b>A successful result owns a live enumerator</b>, so enumerate it or dispose it: the
    ///         value is enumerable once, and it implements <see cref="IAsyncDisposable" /> for the
    ///         caller who reads <see cref="CallResult{T}.IsSuccess" /> and then decides not to
    ///         consume the stream. A failed result carries nothing to dispose.
    ///     </para>
    /// </summary>
    /// <typeparam name="T">The element type of the source. Inferred; there is nothing to declare.</typeparam>
    /// <param name="source">The cold source. See <see cref="RunAsync{T}(Func{CancellationToken, IAsyncEnumerable{T}}, CancellationToken)" /> for what its token bounds.</param>
    /// <param name="cancellationToken">The caller's token. Its cancellation is the one thing this method still throws.</param>
    /// <returns>The outcome, carrying the started stream when the policy started one.</returns>
    /// <exception cref="ResilienceConfigurationException">This policy has a <see cref="Hedge" /> configured.</exception>
    public ValueTask<CallResult<IAsyncEnumerable<T>>> TryRunAsync<T>(Func<CancellationToken, IAsyncEnumerable<T>> source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ExecutionState.EnsureValidated(this);

        // Refused at the call rather than at the first pull, and synchronously rather than on the
        // returned task, for the reason the throwing form refuses here: a configuration error
        // belongs at the call site. That is why this method is not itself async.
        if (Hedge is not null)
        {
            throw new ResilienceConfigurationException(HedgedStreamRefusal);
        }

        // No passthrough shortcut, for the same reason the buffered TryRunAsync has none: the
        // caller asked for a result object, and a result object needs an outcome and a log that
        // handing back the source's own enumerable cannot produce.
        return TryStartAsync<VoidResult, T, StatelessStreamStarter<VoidResult, T>>(
            new StatelessStreamStarter<VoidResult, T>(source), default, cancellationToken);
    }

    /// <summary>
    ///     Starts a cold source with caller state and reports the outcome instead of throwing, so the
    ///     lambda can be <c>static</c> and allocate no closure. Same semantics as the stateless form.
    /// </summary>
    /// <typeparam name="TState">The state's type.</typeparam>
    /// <typeparam name="T">The element type of the source.</typeparam>
    /// <param name="source">The cold source. See the stateless form for what its token bounds.</param>
    /// <param name="state">Handed to the source on every attempt.</param>
    /// <param name="cancellationToken">The caller's token. Its cancellation is the one thing this method still throws.</param>
    /// <returns>The outcome, carrying the started stream when the policy started one.</returns>
    /// <exception cref="ResilienceConfigurationException">This policy has a <see cref="Hedge" /> configured.</exception>
    public ValueTask<CallResult<IAsyncEnumerable<T>>> TryRunAsync<TState, T>(Func<TState, CancellationToken, IAsyncEnumerable<T>> source,
        TState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ExecutionState.EnsureValidated(this);

        if (Hedge is not null)
        {
            throw new ResilienceConfigurationException(HedgedStreamRefusal);
        }

        return TryStartAsync<TState, T, StatefulStreamStarter<TState, T>>(
            new StatefulStreamStarter<TState, T>(source), state, cancellationToken);
    }

    /// <summary>
    ///     Drives the streaming loop as far as its first element and shapes what it reports into a
    ///     <see cref="CallResult{T}" />.
    ///     <para>
    ///         The whole non-throwing path is this method: the loop is the same iterator the
    ///         throwing form returns, driven one <c>MoveNextAsync</c> by this method rather than by
    ///         the consumer's <c>await foreach</c>. That first pull runs every attempt, every
    ///         backoff and every guard, because the iterator's first yield is the handover. There is
    ///         no second copy of the loop, and no shaper interface either - a stream has one shape
    ///         of outcome, not the two the buffered paths have to keep apart.
    ///     </para>
    /// </summary>
    private async ValueTask<CallResult<IAsyncEnumerable<T>>> TryStartAsync<TState, T, TStarter>(
        TStarter starter,
        TState state,
        CancellationToken cancellationToken)
        where TStarter : struct, IStreamStarter<TState, T>
    {
        var outcome = new StreamOutcome();

        var enumerator = ExecuteStreamAsync<TState, T, TStarter>(starter, state, outcome, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        bool started;

        try
        {
            // The loop reports its failures through outcome and yield-breaks, so what escapes here
            // is what escapes the throwing form for the same reasons: the caller's cancellation,
            // and whatever a BeforeAttempt hook threw. Neither is an outcome of the call.
            started = await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (outcome.Error is not null)
        {
            // The loop stopped without handing anything over: nothing is live, and the iterator has
            // already run its own teardown. Disposed anyway, because a completed iterator's
            // DisposeAsync is the cheap no-op and "the failure path leaks" is not a thing to have
            // to reason about.
            await enumerator.DisposeAsync().ConfigureAwait(false);

            return new CallResult<IAsyncEnumerable<T>>(false, null, false, outcome.Error, outcome.Reason, outcome.Attempts);
        }

        // Success, with or without an element: a source that completed empty is a success with
        // nothing to hand over, and StartedStream re-yields nothing for it - the consumer's
        // enumeration simply completes. Either way the value is the live enumeration, and the
        // caller now owns it.
        return new CallResult<IAsyncEnumerable<T>>(
            true, new StartedStream<T>(enumerator, started), true, null, StopReason.Succeeded, outcome.Attempts);
    }

    /// <summary>
    ///     The streaming loop. The same shell as <see cref="ExecuteAsync{TState,T,TInvoker,TOut,TShaper}" />
    ///     - admission, deadline, <see cref="BeforeAttempt" />, the timeout-source arrangement, the
    ///     <see cref="AfterAttempt{T}" /> decision - with a classified region that ends at the first
    ///     element and a handover that outlives it.
    ///     <para>
    ///         <see cref="Admit" /> is awaited inside this one path rather than forking a fifth, for a
    ///         reason that is a measurement and not doctrine: the "one bit, zero bytes" argument that
    ///         split the call paths is about fields every caller pays for. This path's floor is
    ///         already an iterator box plus a surviving token source, so one more hoisted awaiter
    ///         field is below its own noise.
    ///     </para>
    ///     <para>
    ///         Two C# rules shape the body and are doing useful work. <c>yield return</c> cannot
    ///         appear inside a <c>try</c> that has a <c>catch</c>, so the classified region - the only
    ///         part of the loop that judges outcomes - contains no yields, and the yields contain no
    ///         catch: the compiler enforces the "post-start faults belong to the consumer" rule. And a
    ///         lambda cannot be an iterator, which is why the public entry points take a source
    ///         <i>factory</i> and this method is the only iterator in the file.
    ///     </para>
    ///     <para>
    ///         Every <c>yield</c> is after the loop. The loop body is the call loop with its attempt
    ///         replaced by "start the source and pull one element", and the only exit that holds live
    ///         resources is <see cref="NextStep.Succeeded" /> with an element in hand - so the
    ///         handover, the passthrough and the failure shape are three short blocks past the loop
    ///         rather than a fork inside it.
    ///     </para>
    /// </summary>
    private async IAsyncEnumerable<T> ExecuteStreamAsync<TState, T, TStarter>(
        TStarter starter,
        TState state,
        StreamOutcome? outcome,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TStarter : struct, IStreamStarter<TState, T>
    {
        // The effective deadline, resolved once, for the same reasons as the call paths.
        var deadline = UseAmbientDeadline ? ResilienceDeadline.Clamp(Deadline) : Deadline;

        // The budget this call charges, resolved once, likewise.
        var budget = ExecutionState.BudgetFor(this);
        var start = Time.GetTimestamp();
        AttemptSink log = default;

        var verdict = Verdict.Ok;
        Exception? error = null;
        T value = default!;
        var hasValue = false;

        // The three resources whose lifetime is this path's whole difference from a call. Method-scoped
        // because the winning attempt's copies must still be reachable in the handover, after the loop:
        //
        //   timer         the pooled ceiling source. Disarmed at the first element so it cannot fire
        //                 mid-enumeration, and - on the attempt that survives - disposed rather than
        //                 returned, because a surviving enumerator's linked source holds a registration
        //                 on its token for as long as the consumer enumerates, and a pooled source
        //                 re-armed by its next tenant would cancel that registration arbitrarily later
        //                 and on another thread.
        //   attemptSource the linked source the surviving enumerator's token came from. A call disposes
        //                 it when the attempt ends; a surviving stream cannot, because the consumer
        //                 holds its token and registrations added mid-enumeration would throw
        //                 ObjectDisposedException. Disposed in the epilogue, when the consumer is done.
        //   enumerator    the surviving attempt's enumerator. Losing attempts dispose theirs in the
        //                 classified region's finally; the winning attempt's is disposed by the
        //                 epilogue - or by the consumer breaking the await foreach, which the
        //                 try/finally around the yields turns into the same epilogue.
        //
        // They are re-initialized at the top of every iteration, before anything can read them, so a
        // non-timed attempt never tears down the previous iteration's already-returned timer twice -
        // the one pool-corruption hazard hoisting these creates, closed by one assignment each.
        CancellationTokenSource? timer = null;
        CancellationTokenSource? attemptSource = null;
        IAsyncEnumerator<T>? enumerator = null;

        var succeeded = false;
        var reason = StopReason.Succeeded;

        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            // Whether this iteration's admission took one of the breaker's probe slots. See the
            // sequential loop for why the release needs this rather than the breaker's own state.
            var probe = false;

            if (Breaker is { } breaker)
            {
                var admitted = breaker.TryEnter(out var admission, out probe);

                if (admission != BreakerTransition.None && OnEvent is not null)
                    NotifyBreaker(admission, log.Count + 1, Time.GetElapsedTime(start));

                if (!admitted)
                {
                    reason = StopReason.DependencyUnavailable;
                    var pause = GuardDelay(Remaining(Time, start, deadline));

                    if (OnEvent is not null)
                    {
                        Notify(CallEventKind.RejectedByBreaker, log.Count + 1, verdict, Time.GetElapsedTime(start), pause, error, null,
                            StopReason.DependencyUnavailable);
                    }

                    await Delay(Time, pause, cancellationToken).ConfigureAwait(false);
                    break;
                }
            }

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

                // The enumerator is re-initialized here, before Arm() can hand back this iteration's
                // sources - see the field comments above for why all three must be.
                enumerator = null;

                // The ceiling, where it came from, and the sources that enforce it. The same two-source
                // arrangement as a call, with one difference in ownership, not construction: on the
                // attempt that survives, neither source is released here. See Arm() and the field
                // comments above.
                Arm(log.Count + 1, remaining, deadline, cancellationToken,
                    out timer, out attemptSource, out var attemptToken, out var effective, out var deadlineCeiling);

                var attemptStart = Time.GetTimestamp();
                error = null;
                hasValue = false;

                // The classified region: start the source, pull one element, judge it. No yields in
                // here - the compiler forbids them in a try with a catch, and that restriction is the
                // post-start semantic being enforced rather than worked around.
                var surviving = false;

                try
                {
                    var decision = Verdict.Ok;

                    if (Admit is { } admit)
                    {
                        // Awaited under attemptToken and classified exactly as for calls: a verdict
                        // other than Ok skips the source entirely and is processed as if the source
                        // had produced it.
                        decision = await admit(new NextAttempt(log.Count + 1, verdict, error, remaining, attemptToken)).ConfigureAwait(false);
                    }

                    if (decision.Kind == VerdictKind.Ok)
                    {
                        enumerator = starter.Start(state, attemptToken).GetAsyncEnumerator(attemptToken);
                        var moved = await enumerator.MoveNextAsync().ConfigureAwait(false);

                        if (moved)
                        {
                            // The ceiling is disarmed the moment an element is in hand, and then
                            // tested, because disarming alone does not remove the race: the timer
                            // can fire in the window between MoveNextAsync returning true and the
                            // disarm landing, and the consumer would lose the enumeration to a
                            // token they do not own and cannot see. If it fired, the attempt
                            // overran its ceiling before the element was in hand - the element is
                            // dropped, the attempt is judged a timeout like any other, and the
                            // one bool read has bought a semantic that holds under load rather
                            // than usually.
                            var raced = false;

                            if (timer is not null)
                            {
                                timer.CancelAfter(Timeout.InfiniteTimeSpan);
                                raced = timer.IsCancellationRequested;
                            }

                            if (raced)
                            {
                                verdict = Verdict.Transient;
                                error = new AttemptTimeoutException(effective);
                                deadlineSpent = deadlineCeiling;
                            }
                            else
                            {
                                // The one verdict point. A non-Ok verdict here is retryable, and the
                                // consumer never sees this element.
                                value = enumerator.Current;
                                hasValue = true;
                                verdict = Classifier.ClassifyResult(value);
                                surviving = verdict.Kind == VerdictKind.Ok;
                            }
                        }
                        else
                        {
                            // An empty source that completes is a success: no element, no verdict
                            // point, and the attempt returned normally. A "stream that completed
                            // empty" and a "stream that worked" are the same outcome. Nothing
                            // survives, so the finally below tears the attempt down like any
                            // non-surviving one.
                            verdict = Verdict.Ok;
                        }
                    }
                    else
                        verdict = decision;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Never retried, never counted, never converted into a timeout, and no classifier
                    // can override it.
                    throw;
                }
                catch (OperationCanceledException canceled) when (attemptSource is not null && attemptSource.IsCancellationRequested)
                {
                    // Our own attempt timeout, arriving as it does for a call.
                    verdict = Verdict.Transient;
                    error = new AttemptTimeoutException(effective, canceled);
                    deadlineSpent = deadlineCeiling;
                }
                catch (RateLimitedException limited)
                {
                    verdict = Verdict.Refused(limited.RetryAfter);
                    error = limited;
                }
                catch (Exception exception)
                {
                    verdict = Classifier.ClassifyException(exception);

                    if (verdict.Kind == VerdictKind.Ok)
                        verdict = Verdict.Permanent;

                    error = exception;
                }
                finally
                {
                    // The losing half of the ownership split: an attempt that produced no surviving
                    // element - it failed, it was judged non-Ok, it raced its ceiling, or it came
                    // back empty - has nothing that outlives it, so everything it holds is released
                    // here. The enumerator goes first, because its disposal runs the source's own
                    // finally blocks and those may still read the token; the linked source goes
                    // before the timer returns to the pool for the same reason - disposing the link
                    // removes its registration from the timer's token, which is what makes the
                    // return safe.
                    if (!surviving)
                    {
                        if (enumerator is not null)
                        {
                            try
                            {
                                await enumerator.DisposeAsync().ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // Cleanup of an attempt this loop is discarding anyway. Letting it
                                // replace the verdict would make a source whose disposal faults
                                // un-retryable, and there is no listener to tell: the events for
                                // this attempt have not been raised yet. The consumer-owned
                                // disposal in the epilogue propagates, as a plain await foreach
                                // would; this one is ours.
                            }
                        }

                        attemptSource?.Dispose();

                        if (timer is not null)
                            CtsPool.Return(timer, Time);
                    }
                }

                var next = AfterAttempt(
                    ref log, ref probe, start, attemptStart, deadline, attemptSource is not null, effective, deadlineSpent,
                    verdict, error, in value, hasValue, budget, cancellationToken, out var wait, out var stopped);

                if (next == NextStep.Succeeded)
                {
                    // The only exit holding live resources: an element the classifier called Ok is
                    // in hand, its enumerator is intact, and its sources must outlive the loop. The
                    // handover below owns them from here.
                    succeeded = true;
                    break;
                }

                if (next == NextStep.Stop)
                {
                    reason = stopped;

                    // Zero for every stop but a guarded one, and Delay() hands back a completed
                    // task for zero - so the reasons that stop without pausing do not suspend here.
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
                if (probe)
                    Breaker!.ReleaseProbe();
            }
        }

        if (succeeded && outcome is not null)
        {
            // The one thing a non-throwing caller needs that the handover does not already carry.
            // Materialized here, before either success exit, because TryRunAsync's contract is the
            // buffered one - a caller who asked for a result object gets the history on success
            // too, and "it succeeded on the third attempt" has to be assertable.
            outcome.Attempts = log.Materialize(Time.GetElapsedTime(start), deadline);
        }

        if (succeeded && !hasValue)
        {
            // The empty source: a success with nothing to hand over. The attempt was torn down in
            // the classified region's finally like any non-surviving one, so there is nothing to
            // dispose here - the consumer's enumeration simply completes with zero elements.
            yield break;
        }

        if (succeeded)
        {
            // The handover. The yields sit inside a try with a finally and no catch - the one
            // shape the compiler allows - so a consumer who breaks the await foreach at any point
            // runs the epilogue through the iterator's DisposeAsync, exactly as a consumer who
            // finishes it does. The first element has already been pulled and judged; everything
            // after it passes through unclassified, because the call already succeeded and
            // re-judging mid-stream data would be a second policy nobody configured.
            try
            {
                yield return value;

                while (await enumerator!.MoveNextAsync().ConfigureAwait(false))
                {
                    yield return enumerator.Current;
                }
            }
            finally
            {
                // The winning half of the ownership split. Everything a surviving attempt holds is
                // disposed here rather than returned: the linked source still has the consumer's
                // registrations on it and is simply dead after this, and the timer's token still
                // has the linked source's registration, so a pooled re-arm could cancel it - the
                // one rule in this file whose violation is silent, intermittent, and blamed on
                // the wrong call. The enumerator's disposal is nested in its own try so a source
                // whose cleanup faults still releases the sources, and its exception propagates
                // to the consumer, whose enumeration it belongs to.
                try
                {
                    await enumerator!.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    attemptSource?.Dispose();
                    timer?.Dispose();
                }
            }

            yield break;
        }

        // The failure tail, and it is the call path's - reached from the same three breaks (breaker
        // refusal, deadline, AfterAttempt's stop) that reach the call paths' tail, shaped by the
        // same FailureException.Build, so the consumer's first MoveNextAsync throws the exception a
        // failed call would have thrown, with the same log attached. That is unconditional, including when
        // an attempt produced an element the classifier refused: a call may return its final failed
        // value because a caller holding a failed response can see that it failed - a status code
        // self-describes. An element does not. A stream's only failure channel is completion versus
        // exception, so spending completion on a value the classifier rejected makes a truncated
        // stream indistinguishable from a one-element success. The consumer never receives an
        // element the policy judged unacceptable; the verdict, the reason and the attempt log all
        // travel on the exception instead.
        var attempts = log.Materialize(Time.GetElapsedTime(start), deadline);

        // Read after the guarded delay rather than before it, for the same reasons as the call
        // paths: the hint is that much shorter by the time the caller sees it.
        var retryAfter = reason switch
        {
            StopReason.DependencyUnavailable => Breaker?.RetryAfterHint(),
            StopReason.BudgetExhausted => budget?.RetryAfterHint(),
            _ => null,
        };

        var failure = FailureException.Build(reason, error, deadline, attempts, retryAfter);

        if (outcome is not null)
        {
            // The non-throwing caller's exit. The failure is built rather than described, so both
            // forms report the same exception object with the same log attached - one of them
            // through CallResult<T>.Exception, and the other by being thrown.
            outcome.Reason = reason;
            outcome.Attempts = attempts;
            outcome.Error = failure;

            yield break;
        }

        throw failure;
    }
}
