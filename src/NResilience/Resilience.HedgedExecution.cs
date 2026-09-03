using NResilience.Internal;

namespace NResilience;

/// <content>
///     The hedged execution loop: the third and last of them, selected only when <see cref="Hedge" /> is
///     configured.
///     <para>
///         The other two loops run one attempt at a time, and everything about them is shaped by the cost
///         of a single <c>async</c> frame. This one deliberately is not. A hedged call runs a task per
///         in-flight leg, races them with <see cref="Task.WhenAny(Task[])" />, and holds a list of them -
///         so it allocates, and the point of it being a separate method is that <i>only</i> callers who
///         configured hedging pay any of that. The non-hedged budgets in <c>NResilience.Gates</c> do not
///         move by a byte, and that is a gate rather than an intention.
///     </para>
///     <para>
///         It shares the two halves of the post-attempt decision -
///         <see cref="RecordAttempt{T}" /> and <see cref="Decide{T}" /> - with the sequential loops, and
///         that sharing is the whole reason this file is short enough to read. What is different here is
///         only the shape of a round: several legs may be in flight, the first <see cref="VerdictKind.Ok" />
///         wins, and the rest are cancelled and thrown away without being counted as evidence about
///         anything.
///     </para>
/// </content>
public sealed partial record Resilience
{
    /// <summary>
    ///     Runs one call, hedging attempts against the live latency estimate.
    /// </summary>
    /// <typeparam name="TState">Caller state threaded to the callback, or <c>VoidResult</c>.</typeparam>
    /// <typeparam name="T">What the callback returns, or <c>VoidResult</c>.</typeparam>
    /// <typeparam name="TInvoker">How the callback is called.</typeparam>
    /// <typeparam name="TOut">What the entry point returns.</typeparam>
    /// <typeparam name="TShaper">How the outcome is shaped into <typeparamref name="TOut" />.</typeparam>
    /// <param name="invoker">The callback, wrapped.</param>
    /// <param name="state">The caller's state.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>Whatever the entry point promised.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The callback is invoked concurrently.</b> That is what hedging is, and it is the one
    ///         thing a caller has to know before configuring it: two copies of the work may be running at
    ///         the same time, so anything the callback mutates has to tolerate that. The HTTP handler does
    ///         (each leg builds its own request from a buffered body), and it will not hedge a request it
    ///         would not retry.
    ///     </para>
    ///     <para>
    ///         <b>Hedging owns disposal of what it discards.</b> A race produces answers nobody asked
    ///         for, so this loop disposes every value it drops - the losers of a race, and the results of
    ///         a round that went on to be retried - via a runtime type test for
    ///         <see cref="IAsyncDisposable" /> and <see cref="IDisposable" />. That test is the only
    ///         reflection-shaped thing in the executor and it is confined to this path, which is what
    ///         keeps <see cref="Resilience" /> non-generic: a disposal hook typed in <c>T</c> could not
    ///         live on the policy at all.
    ///     </para>
    /// </remarks>
    private async ValueTask<TOut> ExecuteHedgedAsync<TState, T, TInvoker, TOut, TShaper>(
        TInvoker invoker,
        TState state,
        CancellationToken cancellationToken)
        where TInvoker : struct, IInvoker<TState, T>
        where TShaper : struct, IOutcomeShaper<T, TOut>
    {
        var hedge = Hedge!.Value;

        // Non-null by construction: ExecutionState builds one for every policy whose Hedge is set, and
        // this method is only reached for those.
        var latency = ExecutionState.LatencyFor(this)!;

        // Null unless this policy configured WinRate. Read once, like the budget and the estimate above.
        var wins = ExecutionState.WinRateFor(this);

        // The effective deadline, resolved once per call. See the sequential loop for why the ambient
        // read happens here and not per attempt; the local functions below close over it.
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

        // The legs currently in flight. Capacity is the concurrency ceiling, so the list never grows.
        var legs = new List<HedgeLeg<T>>(hedge.MaxConcurrent);

        // Wire calls started. Distinct from log.Count, which also counts discarded legs, and it is this
        // one that Attempts bounds - Attempts is the number of calls the dependency sees.
        var started = 0;

        // Set when something refuses a hedge whose threshold has already fired, so one refusal does not
        // turn into a hedge decision every threshold for the rest of the round. Cleared when a new round
        // begins.
        var hedgeRefused = false;

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            while (true)
            {
                if (legs.Count == 0)
                {
                    // A new round. The first leg of one is an ordinary attempt in every respect: the
                    // deadline is checked, the breaker admits it, and if the breaker refuses, the call
                    // stops exactly as it would without hedging configured.
                    hedgeRefused = false;

                    var remaining = Remaining(Time, start, deadline);

                    if (remaining == TimeSpan.Zero)
                    {
                        reason = StopReason.DeadlineExceeded;
                        NotifyDeadline(log.Count + 1, verdict, Time.GetElapsedTime(start), error);
                        break;
                    }

                    if (Breaker is { } breaker)
                    {
                        var admitted = breaker.TryEnter(out var admission);

                        if (admission != BreakerTransition.None && OnEvent is not null)
                            NotifyBreaker(admission, log.Count + 1, Time.GetElapsedTime(start));

                        if (!admitted)
                        {
                            reason = StopReason.DependencyUnavailable;
                            var pause = GuardDelay(remaining);

                            if (OnEvent is not null)
                                Notify(CallEventKind.RejectedByBreaker, log.Count + 1, verdict, Time.GetElapsedTime(start), pause, error, null,
                                    StopReason.DependencyUnavailable);

                            await Delay(Time, pause, cancellationToken).ConfigureAwait(false);
                            break;
                        }
                    }

                    legs.Add(StartLeg(++started, hedged: false));
                }

                var armed = ArmHedge();

                // One array per wait. A hedged call is already allocating a task per leg, and building
                // the array here rather than keeping a parallel one is what keeps the list of legs the
                // single source of truth about what is in flight.
                var pending = new Task[legs.Count + (armed is null ? 0 : 1)];

                for (var i = 0; i < legs.Count; i++)
                    pending[i] = legs[i].Work!;

                if (armed is { } arming)
                    pending[legs.Count] = arming.Delay;

                var done = await Task.WhenAny(pending).ConfigureAwait(false);

                if (armed is { } fired && ReferenceEquals(done, fired.Delay))
                {
                    // The leg has been running longer than the configured quantile of recent calls to
                    // this dependency, so a copy of it is worth starting - and both of the questions
                    // about whether it is worth its load are asked here rather than when the timer was
                    // armed, so that they are asked only about a call that really did get slow.
                    if (Suppressed(fired.Threshold))
                        continue;

                    // Charged here rather than when the timer was armed, so a call that came back on its
                    // own is never charged for a hedge it did not need - and after the gates above, so a
                    // hedge nobody is going to start does not spend a token that funds a retry this call
                    // may still need.
                    //
                    // Hedges and retries draw on the same bucket on purpose: both are amplification, and
                    // what the budget exists to bound is the total. A policy already retrying at its
                    // limit therefore stops hedging, which is the right precedence - a retry is evidence
                    // that something failed, and a hedge is a guess that something is slow.
                    if (budget is not null && !budget.TrySpend())
                    {
                        hedgeRefused = true;
                        continue;
                    }

                    var hedged = StartLeg(++started, hedged: true);
                    legs.Add(hedged);

                    // Counted here rather than in Admits(), so the denominator of the win rate is the
                    // hedges that actually reached the dependency.
                    wins?.Started();

                    if (OnEvent is not null)
                    {
                        Notify(CallEventKind.HedgeStarted, hedged.Number, verdict, Time.GetElapsedTime(start), fired.Threshold, null, null);
                    }

                    continue;
                }

                var leg = Leg(legs, done);
                legs.Remove(leg);

                LegOutcome<T> outcome;

                try
                {
                    outcome = await leg.Work!.ConfigureAwait(false);
                }
                finally
                {
                    // The leg is out of the list, so nothing will cancel it now and its sources can go
                    // back. On the throwing path the enclosing finally handles the legs still in flight.
                    ReleaseLeg(leg, Time);
                }

                // A refusal this process imposed on itself never reached the dependency, so it is not a
                // sample of how long the dependency takes and must not move the threshold that decides
                // when to hedge it.
                if (!outcome.Verdict.SelfImposed)
                    latency.Record(outcome.Duration);

                verdict = outcome.Verdict;
                error = outcome.Error;

                if (outcome.HasValue)
                {
                    // Whatever we were holding is now unreachable: this loop asked for a value nobody
                    // requested, so this loop disposes the one it replaces.
                    if (hasValue)
                        await DropAsync(value).ConfigureAwait(false);

                    value = outcome.Value;
                    hasValue = true;
                }

                var recorded = false;

                RecordAttempt(
                    ref log, ref recorded, start, Time.GetElapsedTime(start, leg.StartTimestamp).Ticks, outcome.Duration,
                    leg.Timed, leg.Effective, verdict, error, in value, hasValue,
                    leg.Hedged ? AttemptFlags.Hedged : AttemptFlags.None);

                if (verdict.Kind == VerdictKind.Ok)
                {
                    var winner = log.Count;

                    if (leg.Hedged)
                    {
                        wins?.Won();

                        if (OnEvent is not null)
                            Notify(CallEventKind.HedgeWon, winner, verdict, Time.GetElapsedTime(start), null, null, null);
                    }

                    DiscardLegs(ref log, legs, start);

                    // Ok is Decide's first branch and always comes back Succeeded, so the return value
                    // has nothing to say. Going through it anyway is what keeps the budget deposit and
                    // the terminal event in one place for all three loops.
                    _ = Decide(winner, start, deadline, outcome.DeadlineSpent, verdict, error, in value, hasValue, budget, cancellationToken,
                        out _, out _);

                    var succeeded = shaper.WantsLogOnSuccess
                        ? log.Materialize(Time.GetElapsedTime(start), deadline)
                        : AttemptLog.Empty;

                    return shaper.Success(value, succeeded);
                }

                // A sibling is still running, so there is nothing to decide: this leg failed, but the
                // round has not.
                if (legs.Count > 0)
                    continue;

                var next = Decide(
                    log.Count, start, deadline, outcome.DeadlineSpent, verdict, error, in value, hasValue, budget, cancellationToken,
                    out var wait, out var stopped);

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
        }
        catch (Exception)
        {
            // Nothing is going to hand this to a caller now, and hedging is what asked for it. The
            // ordinary failure paths leave through `break` instead, and the value they are holding is
            // the one the caller is about to be given.
            if (hasValue)
                await DropAsync(value).ConfigureAwait(false);

            throw;
        }
        finally
        {
            // Reached only when something threw: the caller cancelled, or a BeforeAttempt hook failed.
            // Every ordinary exit leaves the list empty. Nothing is logged here - there is no log left
            // to hand anybody - but the legs are still cancelled and still cleaned up, because a leg
            // holding a socket does not care why we left.
            for (var i = 0; i < legs.Count; i++)
                Abandon(legs[i], Breaker, Time);

            legs.Clear();
        }

        var attempts = log.Materialize(Time.GetElapsedTime(start), deadline);

        var retryAfter = reason switch
        {
            StopReason.DependencyUnavailable => Breaker?.RetryAfterHint(),
            StopReason.BudgetExhausted => budget?.RetryAfterHint(),
            _ => null,
        };

        return shaper.Failure(value, hasValue, error, reason, deadline, attempts, retryAfter);

        // ---------------------------------------------------------------------------------------
        // Local functions. They capture the loop's state rather than taking twelve parameters each;
        // the closure they share is one object on a path that already allocates several.
        // ---------------------------------------------------------------------------------------

        // Whether this hedge is worth what it costs, asked at the moment its threshold fires rather than
        // when the timer was armed - so the two questions are asked only about calls that really did get
        // slow, and so a listener can compare what was suppressed against what started.
        bool Suppressed(TimeSpan threshold)
        {
            // Closed is not the same as healthy: a breaker's default trip is five consecutive failures,
            // so a dependency erroring on 40% of calls sits closed while this process hedges every slow
            // one. Once the error rate has climbed to SuppressAt of the rate that would open the breaker,
            // hedging stops rather than adding load to a dependency already in trouble.
            var elevated = Breaker is { } gate && gate.IsErrorRateElevated(hedge.SuppressAt);

            // And a dependency that is not failing can still be one hedging cannot help. Asked second
            // because it is the question with the weaker evidence behind it, and because Admits() takes
            // the loop's decision as a side effect - which should not happen for a hedge already refused.
            if (!elevated && (wins is null || wins.Admits()))
                return false;

            hedgeRefused = true;

            if (OnEvent is not null)
                Notify(CallEventKind.HedgeSuppressed, started + 1, verdict, Time.GetElapsedTime(start), threshold, null, null);

            return true;
        }

        // Whether a hedge is worth arming, and the threshold that would trigger it. The gates about the
        // dependency are at the firing point instead - see Suppressed().
        (Task Delay, TimeSpan Threshold)? ArmHedge()
        {
            if (hedgeRefused || legs.Count >= hedge.MaxConcurrent || started >= Attempts)
                return null;

            // Half-open counts as not closed: those attempts are probes, and a probe that is raced is not
            // a probe. The two gates that ask whether a hedge is *worth* starting - the error rate, and
            // the win rate - are not here but at the firing point, so that the hedge they refuse is one
            // a call actually got slow enough to want. See Suppressed().
            if (Breaker is { State: not BreakerState.Closed })
                return null;

            if (latency.Threshold(hedge.MinimumSamples) is not { } threshold)
                return null;

            // A dependency whose p95 is a few hundred microseconds would otherwise have every call
            // hedged, which spends the extra traffic on calls nobody would describe as slow.
            if (threshold < hedge.MinimumDelay)
                threshold = hedge.MinimumDelay;

            var left = Remaining(Time, start, deadline);

            if (left != Timeout.InfiniteTimeSpan && threshold >= left)
                return null;

            // Deliberately not given the caller's token. A cancelled caller is observed through the
            // legs, which are cancelled with it, and a delay that can fault is a second way for this
            // loop to unwind for no gain.
            return (Task.Delay(threshold, Time), threshold);
        }

        // Creates a leg and starts it. The sources exist before the body runs, because the body is what
        // a discard has to be able to interrupt.
        HedgeLeg<T> StartLeg(int number, bool hedged)
        {
            var leg = new HedgeLeg<T>
            {
                Number = number,
                Hedged = hedged,
                StartTimestamp = Time.GetTimestamp(),
            };

            if (deadline != Timeout.InfiniteTimeSpan || AttemptTimeout != Timeout.InfiniteTimeSpan)
            {
                // A pooled source drives the ceiling and is never handed out; the leg's own source links
                // it with the caller's token. Same arrangement as the sequential loops, and for the same
                // reason - see the comment there. CancelAfter is left until the body has run its
                // BeforeAttempt hook, so the ceiling covers the attempt rather than the setup.
                leg.Timer = CtsPool.Rent(Time);
            }

            leg.Source = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leg.Timer?.Token ?? default)
                : leg.Timer is { } own
                    ? CancellationTokenSource.CreateLinkedTokenSource(own.Token)
                    : new CancellationTokenSource();

            leg.Work = RunLegAsync(leg);
            return leg;
        }

        // One attempt, start to finish, with nothing decided: the loop decides. Classification happens
        // here because it is the last thing that is genuinely about this attempt alone.
        async Task<LegOutcome<T>> RunLegAsync(HedgeLeg<T> leg)
        {
            if (BeforeAttempt is { } beforeAttempt)
            {
                // Outside the try below, exactly as in the sequential loops: this hook is documented as
                // running outside the classification region, so what it throws propagates out of the
                // call unchanged rather than being retried.
                await beforeAttempt(new NextAttempt(leg.Number, verdict, error, Remaining(Time, start, deadline), cancellationToken))
                    .ConfigureAwait(false);
            }

            // Measured from here, so a slow hook is not charged to the dependency, and the ceiling below
            // bounds the attempt rather than the setup.
            leg.StartTimestamp = Time.GetTimestamp();

            var remaining = Remaining(Time, start, deadline);
            var ceiling = Ceiling(leg.Number);
            var effective = Effective(ceiling, remaining);

            // See the sequential loop: computed here, beside the ceiling, rather than in the catch
            // below - so the ceiling is not live across this leg's awaits.
            var deadlineCeiling = deadline != Timeout.InfiniteTimeSpan && effective != ceiling;

            leg.Effective = effective;
            leg.Timed = leg.Timer is not null;

            if (leg.Timer is { } timer && effective != Timeout.InfiniteTimeSpan)
                timer.CancelAfter(effective);

            var token = leg.Source!.Token;

            T legValue = default!;
            var legHas = false;
            var legVerdict = Verdict.Ok;
            Exception? legError = null;
            var deadlineSpent = false;

            try
            {
                if (Admit is { } admit)
                {
                    // Awaited here rather than in the loop, which is the one place hedging is cheaper
                    // than the design it inherits: the hoisted awaiter field this costs belongs to the
                    // leg's state machine, so it is charged per leg of a hedged call instead of to every
                    // caller of a shared loop.
                    var decision = await admit(new NextAttempt(leg.Number, verdict, error, remaining, token)).ConfigureAwait(false);

                    if (decision.Kind != VerdictKind.Ok)
                        return new LegOutcome<T>(decision, null, legValue, false, Time.GetElapsedTime(leg.StartTimestamp), false);
                }

                var attempt = invoker.Invoke(state, token, ref legValue);

                if (attempt is not null)
                {
                    await attempt.ConfigureAwait(false);
                    legValue = invoker.Result(attempt);
                }

                legHas = true;
                legVerdict = Classify.ClassifyResult(legValue);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller's cancellation. Never retried, never counted, never classified, and it
                // leaves through the loop's finally, which cancels this leg's siblings on the way.
                throw;
            }
            catch (OperationCanceledException) when (leg.Discarded)
            {
                // We cancelled it, because a sibling answered first. There is nothing to classify: the
                // loop logs this leg as discarded and disposes anything it managed to produce.
                return new LegOutcome<T>(Verdict.Ok, null, legValue, legHas, Time.GetElapsedTime(leg.StartTimestamp), false);
            }
            catch (OperationCanceledException canceled) when (leg.Source is { IsCancellationRequested: true })
            {
                legVerdict = Verdict.Transient;
                legError = new AttemptTimeoutException(effective, canceled);

                // See the sequential loop: when the deadline supplied the ceiling, the ceiling that
                // fired *was* the deadline, and that is the fact to stop on rather than what the clock
                // says afterwards.
                deadlineSpent = deadlineCeiling;
            }
            catch (RateLimitedException limited)
            {
                legVerdict = Verdict.Limited(limited.RetryAfter);
                legError = limited;
            }
            catch (Exception exception)
            {
                legVerdict = Classify.ClassifyException(exception);

                if (legVerdict.Kind == VerdictKind.Ok)
                    legVerdict = Verdict.Permanent;

                legError = exception;
            }

            return new LegOutcome<T>(legVerdict, legError, legValue, legHas, Time.GetElapsedTime(leg.StartTimestamp), deadlineSpent);
        }
    }

    /// <summary>The leg whose body is this task. Reference identity; the list is at most a handful long.</summary>
    private static HedgeLeg<T> Leg<T>(List<HedgeLeg<T>> legs, Task completed)
    {
        for (var i = 0; i < legs.Count; i++)
        {
            if (ReferenceEquals(legs[i].Work, completed))
                return legs[i];
        }

        // Unreachable: the task came out of a WhenAny over exactly these legs plus the hedge timer, and
        // the timer is matched before this is called.
        throw new InvalidOperationException("A completed hedge leg was not in the list of legs in flight.");
    }

    /// <summary>
    ///     Throws every leg still in flight away because one of them answered: each is logged as
    ///     discarded, cancelled, and then cleaned up in the background.
    ///     <para>
    ///         The log entry is written <i>now</i>, at the moment of cancellation, rather than when the
    ///         leg finally returns. Waiting would hand the caller's success back only once every loser
    ///         had stopped, and a leg that ignores its cancellation token could then hold up the very
    ///         call hedging exists to make faster. So the entry records how long the leg had been
    ///         running when it was discarded, which is the honest number and the one worth tuning
    ///         against.
    ///     </para>
    /// </summary>
    /// <typeparam name="T">What the callback returns, or <c>VoidResult</c>.</typeparam>
    /// <param name="log">The inline attempt log.</param>
    /// <param name="legs">The legs still in flight. Emptied.</param>
    /// <param name="start">Timestamp the whole call started at.</param>
    private void DiscardLegs<T>(ref AttemptSink log, List<HedgeLeg<T>> legs, long start)
    {
        T none = default!;

        for (var i = 0; i < legs.Count; i++)
        {
            var leg = legs[i];
            var recorded = false;

            RecordAttempt(
                ref log, ref recorded, start, Time.GetElapsedTime(start, leg.StartTimestamp).Ticks, Time.GetElapsedTime(leg.StartTimestamp),
                leg.Timed, leg.Effective, Verdict.Ok, null, in none, false,
                (leg.Hedged ? AttemptFlags.Hedged : AttemptFlags.None) | AttemptFlags.Discarded);

            Abandon(leg, Breaker, Time);
        }

        legs.Clear();
    }

    /// <summary>
    ///     Cancels a leg and cleans up after it without waiting: the probe slot it took from the breaker
    ///     goes back, whatever it produced is disposed, and its cancellation sources are released.
    /// </summary>
    /// <typeparam name="T">What the callback returns, or <c>VoidResult</c>.</typeparam>
    /// <param name="leg">The leg.</param>
    /// <param name="breaker">The breaker that admitted it, if any.</param>
    /// <param name="time">The clock, for returning the pooled timeout source.</param>
    private static void Abandon<T>(HedgeLeg<T> leg, Breaker? breaker, TimeProvider time)
    {
        // Set before the cancel, so the leg's own handler can tell this from its attempt timeout.
        leg.Discarded = true;

        // No ObjectDisposedException guard, and none needed: a leg is released only after it has been
        // taken out of the list of legs in flight, and only legs in that list are cancelled.
        leg.Source?.Cancel();

        _ = CleanUpAsync(leg, breaker, time);
    }

    /// <summary>
    ///     Waits for a discarded leg in the background, purely to dispose what it produced.
    /// </summary>
    /// <typeparam name="T">What the callback returns, or <c>VoidResult</c>.</typeparam>
    /// <param name="leg">The leg.</param>
    /// <param name="breaker">The breaker that admitted it, if any.</param>
    /// <param name="time">The clock.</param>
    /// <returns>A task nobody awaits.</returns>
    /// <remarks>
    ///     Fire-and-forget, and safely so: everything it can throw is caught here, so it can neither
    ///     fail the call it is cleaning up after nor surface later as an unobserved task exception. A
    ///     cancelled leg usually completes immediately; one that ignores its token completes whenever it
    ///     feels like it, and this is what makes that the dependency's problem rather than the caller's.
    /// </remarks>
    private static async Task CleanUpAsync<T>(HedgeLeg<T> leg, Breaker? breaker, TimeProvider time)
    {
        try
        {
            var outcome = await leg.Work!.ConfigureAwait(false);

            if (outcome.HasValue)
                await DropAsync(outcome.Value).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nobody is waiting for this leg, so there is nobody to report to. It was never classified
            // and never logged as an outcome; an exception from it is not evidence about anything.
        }
        finally
        {
            // The leg consumed a probe slot on the way in and never recorded an outcome, so the slot has
            // to go back or a half-open breaker wedges forever.
            breaker?.ReleaseProbe();
            ReleaseLeg(leg, time);
        }
    }

    /// <summary>Returns a finished leg's cancellation sources. The pooled one goes back to the pool.</summary>
    /// <typeparam name="T">What the callback returns, or <c>VoidResult</c>.</typeparam>
    /// <param name="leg">The leg.</param>
    /// <param name="time">The clock, which decides whether the pool is usable at all.</param>
    private static void ReleaseLeg<T>(HedgeLeg<T> leg, TimeProvider time)
    {
        if (leg.Source is { } source)
        {
            leg.Source = null;
            source.Dispose();
        }

        if (leg.Timer is { } timer)
        {
            leg.Timer = null;
            CtsPool.Return(timer, time);
        }
    }

    /// <summary>
    ///     Disposes a value the loop is throwing away, if it is disposable.
    ///     <para>
    ///         The one runtime type test in the executor, and it is what lets hedging work at all without
    ///         making <see cref="Resilience" /> generic: <see cref="HttpResponseMessage" /> is
    ///         <see cref="IDisposable" />, so a hedged HTTP call leaks no sockets and the core carries no
    ///         HTTP-specific code to achieve it. A value that is neither kind of disposable is simply
    ///         dropped.
    ///     </para>
    /// </summary>
    /// <typeparam name="T">What the callback returns, or <c>VoidResult</c>.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>A task that completes when it has been disposed.</returns>
    private static ValueTask DropAsync<T>(T value)
    {
        if (value is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();

        if (value is IDisposable disposable)
            disposable.Dispose();

        return default;
    }
}
