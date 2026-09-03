namespace NResilience.Gates;

/// <summary>
///     The checked-in allocation budgets. Every number here was measured on 2026-08-19, .NET 10.0.0
///     and .NET 8.0.22, arm64, Release, workstation non-concurrent GC, and is recorded with its
///     measured value beside it so a failure reads as "this moved" rather than "this is wrong".
///     Deadline propagation increased every suspending figure by <b>16 bytes</b>. The effective
///     deadline is resolved once per call and stored in the state-machine box for all callers,
///     regardless of <c>UseAmbientDeadline</c>. This avoids re-reading an <c>AsyncLocal</c> per attempt.
///     The 8-byte value costs 16 bytes due to box alignment; dropping the <c>bounded</c> flag did
///     not recover space, indicating it resided in padding.
///     The breaker and the retry budget moved every suspending figure by exactly
///     <b>8 bytes</b> - one reference field in the state-machine box, for the budget the call will
///     charge. No budget was widened for it; the design's open question 1 allowed 64 B of headroom for
///     the breaker and budget and this used an eighth of it. The breaker costs nothing in the box at all, because the
///     policy holding it is already a field.
///     The adaptive attempt ceiling (<c>Resilience.AttemptCeiling</c>) does not increase allocations on any path.
///     <c>Ceiling()</c> resolves the latency window at each of the two points that need it rather than
///     hoisting it into a local, so the reference never joins the state-machine box. Additionally, the
///     four call sites compute <c>deadlineCeiling</c> as a <c>bool</c> beside the ceiling instead of keeping
///     the ceiling itself live across the attempt <c>await</c>.
///     Measured both ways: a hoisted <c>TimeSpan</c> cost the streaming path 8 bytes (848 -> 855
///     B/op) and the bool cost nothing. Suspending figures are unchanged to the byte - 344, 408, 424
///     and 584 - and the sequential loops now carry a fourth <c>bool</c> that lands in existing padding
///     (<c>recorded</c>, <c>hasValue</c> and <c>deadlineSpent</c>).
///     Every budget points at the <b>shipping</b> executor. Where a figure changed, the
///     stand-in value is kept in the comment, because the delta is the answer to the question
///     the stand-in exists to ask.
///     Budgets carry roughly 15% headroom over the measured figure, because allocation is
///     deterministic but not identical across architectures. They are ceilings, not targets: a
///     change that comes in under budget still needs its number looked at.
/// </summary>
public static class Budgets
{
    // ---- Synchronous fast path. These are exact, and the only genuinely zero rows. ----

    /// <summary>Resilience.None is a single branch that returns the callback's own task.</summary>
    public const double NoneSyncOverhead = 0;

    /// <summary>Full policy minus the attempt timeout, static lambda + value-type state. Measured: 0 B.</summary>
    public const double FullPolicyNoTimeoutSyncOverhead = 0;

    /// <summary>
    ///     The same call with an attempt timeout. Measured: 64 B - one linked source per attempt.
    ///     This is a deliberate, documented departure from the CI gate table, which budgets 0
    ///     bytes for "full policy, sync-completing". It is not achievable: the callback must
    ///     receive a token the attempt timeout can cancel, the timeout source cannot be known to
    ///     be unnecessary until after the callback returns, and the pooled source's own token
    ///     must never be handed to user code because TryReset preserves token identity. Polly
    ///     reaches 24 B here by handing out its pooled token, which is the hazard this design
    ///     refuses.
    /// </summary>
    public const double FullPolicyWithTimeoutSyncOverhead = 72;

    /// <summary>
    ///     What converting a synchronously-completing <see cref="ValueTask" /> callback into a
    ///     <see cref="Task" /> costs, and therefore what the <c>ValueTask</c> execution overloads remove:
    ///     one task built for an answer already in hand. Measured: 72 B, against an
    ///     <c>IValueTaskSource</c>-backed callback - the shape <c>Socket</c>, <c>Channel</c> and
    ///     <c>PipeReader</c> hand out.
    ///     A floor rather than a ceiling. The gate that uses it asserts the conversion costs at
    ///     <i>least</i> this much more than the native path, because a figure that collapsed to zero
    ///     would mean the overloads had stopped being reached - the extension methods losing to an
    ///     instance overload is a silent failure otherwise.
    ///     <para>
    ///         Measured in the same sweep: the raw <c>ValueTask</c> callback allocates 0 B where the raw
    ///         <c>Task</c> one allocates 72 B, so the trivial policy reaches 0 B/op <i>total</i> with a
    ///         <c>ValueTask</c> callback rather than 0 B above a callback that already paid. Under
    ///         <c>Resilience.Default</c> the same call measures 64 B against the <c>Task</c> form's
    ///         136 B - the same 64 B of executor overhead, with the callback's task no longer built.
    ///         The suspending path measures 496 B either way, to the byte.
    ///     </para>
    /// </summary>
    public const double ValueTaskConversionFloor = 64;

    // ---- Suspending path. Measured against the shipping executor. ----

    /// <summary>
    ///     The instrument's noise floor, applied to every suspending assertion.
    ///     Suspending measurements read <c>GC.GetTotalAllocatedBytes(precise: true)</c>, because a
    ///     continuation resumed on a thread-pool thread does not allocate against the thread that
    ///     started the operation. A process-wide counter cannot return an exact zero: the process is
    ///     quiesced but not empty, so a passthrough arm that genuinely allocates nothing still
    ///     measures a fraction of a byte per operation. Observed drift is under 1 B/op over
    ///     2,000-iteration batches; 8 B is a floor an actual regression cannot hide beneath, since
    ///     the smallest object the runtime can allocate is larger than that.
    ///     The synchronous assertions use the thread-local counter and get no such allowance -
    ///     their zeros are exact.
    /// </summary>
    public const double SuspendingNoiseFloor = 8;

    /// <summary>Passthrough allocates nothing on any path. Measured: 0 B.</summary>
    public const double NoneSuspendingOverhead = 0;

    /// <summary>
    ///     The trivial shipping shape: retry and classification, no deadline and no attempt timeout.
    ///     Measured: 336 B on .NET 10, 326 B on .NET 8, against the stand-in's 336 B.
    ///     (344 B and 334 B before <c>Verdict</c> packed its pushback; 328 B and 313 B before deadline
    ///     propagation; 320 B and 308 B before the breaker and budget.)
    /// </summary>
    public const double TrivialOverhead = 368;

    /// <summary>
    ///     The realistic policy - <c>Resilience.Default</c>: three attempts, a deadline, an attempt
    ///     timeout, exponential backoff, classification and the inline attempt log.
    ///     Measured: 400 B on .NET 10, 399 B on .NET 8, against the stand-in's 399 B.
    ///     (408 B and 407 B before <c>Verdict</c> packed its pushback - a verdict is live across the
    ///     attempt <c>await</c>, so its eight bytes were box; 393 B and 390 B before deadline
    ///     propagation; 384 B and 381 B before the breaker and budget.)
    /// </summary>
    public const double DefaultOverhead = 448;

    /// <summary>
    ///     The same call with a caller token that can be cancelled and never is - the production case.
    ///     Measured: 416 B on .NET 10, 415 B on .NET 8, against the stand-in's 416 B (424 B and 423 B
    ///     before <c>Verdict</c> packed its pushback; 408 B and 407 B
    ///     before deadline propagation; 400 B and 397 B before the breaker and budget). The extra 16 B
    ///     over <see cref="DefaultOverhead" /> is the marginal cost of
    ///     linking against a long-lived source whose registration storage already exists; see
    ///     <see cref="MinimumSocketRatioVersusPolly" /> for what the same link costs when real I/O
    ///     registers on the resulting token.
    /// </summary>
    public const double DefaultCancellableOverhead = 464;

    /// <summary>
    ///     <c>TryRunAsync</c>, which always materializes the attempt log because its caller has
    ///     explicitly asked for a result object. Measured: 552 B on .NET 10 (584 B before
    ///     <see cref="AttemptSize" /> dropped to 48 - 8 B of box for the verdict and 24 B for the one
    ///     <c>Attempt</c> this call materializes; 568 B before deadline
    ///     propagation, 561 B before hedging, 553 B before the breaker and budget) - so asking for the history costs about 150 B over the throwing
    ///     form. Budgeted rather than left to be discovered by a caller who assumed the two were the same
    ///     price.
    ///     The 8 B hedging added is <c>Attempt.StartOffset</c>, one <see cref="TimeSpan" /> per
    ///     materialized attempt. It is what makes overlapping attempts readable, it cannot be derived
    ///     from the other fields, and it is charged only on the paths that materialize a log at all -
    ///     the suspending figures for the throwing entry points did not move.
    /// </summary>
    public const double TryRunDefaultOverhead = 640;

    /// <summary>
    ///     <c>Resilience.Default</c> with a listener attached. Measured: 456 B on .NET 10, 455 B on
    ///     .NET 8 - 48 B over <see cref="DefaultOverhead" />, which is two boxed <c>int</c> results,
    ///     one for the attempt and one for the success.
    ///     The whole claim is in that number: raising the events themselves is free, because
    ///     <c>CallEvent</c> is a struct passed by value to an <see cref="System.Action{T}" /> and the
    ///     delegate is a field on a policy the state-machine box already holds. What a listener costs
    ///     is the one thing it asked for that cannot be given away - the attempt's result, boxed,
    ///     because a cross-cutting listener has no <c>T</c> to be generic over.
    /// </summary>
    public const double DefaultWithListenerOverhead = 512;

    /// <summary>
    ///     <c>Resilience.Default</c> with <see cref="Resilience.Admit" /> configured to always admit,
    ///     against <c>ShippingScenarios.AdmitHook</c>, a cached delegate returning a cached completed
    ///     <c>Task&lt;Verdict&gt;</c> - so this prices only what the executor's second,
    ///     <c>ExecuteWithAdmitAsync</c> loop costs, not the hook's own allocation.
    ///     Measured: 440.0 B on .NET 10, 439.4 B on .NET 8 - 32.0 B and 31.9 B over
    ///     <see cref="DefaultOverhead" />'s measured figure, the one extra hoisted
    ///     <c>TaskAwaiter&lt;Verdict&gt;</c> field <see cref="Admit" /> costs a caller who configures it.
    ///     This is the Tier 2 spike from <c>plans/flat-executor-debate-review.md</c>: the number that
    ///     matters more than this one is <see cref="DefaultOverhead" /> itself holding still, which
    ///     <c>A_policy_with_no_Admit_hook_pays_nothing_for_the_second_execution_path</c> asserts directly.
    /// </summary>
    public const double AdmitConfiguredOverhead = 488;

    /// <summary>
    ///     <c>Resilience.Default</c> with <see cref="Resilience.Hedge" /> configured, on a call where no
    ///     hedge actually fires - the steady state, and therefore what turning hedging on costs on the
    ///     roughly <c>Quantile</c> of calls that never needed it. Measured: 1315 B on .NET 10 and 1435 B
    ///     on .NET 8 - the widest gap between the two runtimes on this ledger, and it is the cancellable
    ///     delay below that opens it. It moves by more than the 16 B deadline propagation cost the
    ///     sequential loops, because the hedged loop's local functions capture the effective deadline in
    ///     the closure they already share, on top of the box field.
    ///     This is the one number in this file that is large on purpose. The hedged loop holds a list of
    ///     legs, runs each in its own <c>async</c> local function, races them with
    ///     <see cref="Task.WhenAny(Task,Task)" /> - or, above a concurrency ceiling of two, over an
    ///     array built per wait - and arms a cancellable <see cref="Task.Delay(TimeSpan)" /> for the
    ///     threshold. There is no version of hedging that
    ///     does not allocate, and pretending otherwise would produce a worse design rather than a
    ///     cheaper one.
    ///     Three things moved it recently and they do not all point the same way: the array is gone from
    ///     the two-task race that <c>MaxConcurrent</c>'s default makes the common one (-40 B), the 128-B
    ///     <see cref="Resilience.Hedge" /> is no longer hoisted into a local the loop's closures capture
    ///     (-128 B), and the arming delay is now given a cancellation source of the loop's own so its
    ///     timer is released when the race ends rather than when its threshold elapses (+172 B: the
    ///     source, and the more expensive promise a cancellable delay returns). The last is a
    ///     deliberate trade of allocation this path already accepts for a standing population of dead
    ///     <c>TimerQueueTimer</c>s proportional to throughput times threshold, which nothing on this
    ///     ledger would have shown. It lands at roughly what a Polly retry-plus-timeout pipeline costs per call -
    ///     except that only callers who asked for hedging pay it.
    ///     The number that matters more than this one is <see cref="DefaultOverhead" /> holding still,
    ///     which <c>A_policy_with_no_Hedge_pays_nothing_for_the_third_execution_path</c> asserts
    ///     directly, in the same sweep.
    ///     The ceiling was raised from 1500 B when that trade was taken. At 1500 it left the .NET 8
    ///     measurement 4% of headroom where every other budget in this file carries 10-16% - a ceiling
    ///     that fails on hardware drift rather than on a regression.
    /// </summary>
    public const double HedgeConfiguredOverhead = 1650;

    /// <summary>
    ///     The streaming path under <c>Resilience.Default</c>, measured over a full enumeration of a
    ///     suspending source and compared against the identical enumeration with no policy in the
    ///     middle. The budget is itemized rather than totalled, because every line is a thing the
    ///     design chose to buy and a reviewer is entitled to see which one moved:
    ///     <list type="bullet">
    ///         <item>
    ///             one iterator box - <c>ExecuteStreamAsync</c>'s own state machine, the analog of
    ///             the call paths' box;
    ///         </item>
    ///         <item>
    ///             one linked <c>attemptSource</c> - which a call pays too, for the same reason: the
    ///             surviving enumerator's token has to reach the source;
    ///         </item>
    ///         <item>
    ///             one pooled timer CTS, returned on every attempt except the winner, whose
    ///             <b>disposal</b> is the streaming-only delta - the one rule whose violation is silent
    ///             (a returned-while-linked source lets the next tenant's CancelAfter cancel a live
    ///             stream), so its cost is paid deliberately and itemized here;
    ///         </item>
    ///         <item>
    ///             the first-element pull and the per-element passthrough loop, which are the
    ///             source's own enumerator costs above the raw arm's identical pulls.
    ///         </item>
    ///     </list>
    ///     The <c>[EnumeratorCancellation]</c> merge allocates one further linked CTS only when the
    ///     caller supplies both a call-time and an enumeration-time token, which this arm does not -
    ///     the arm binds the caller's token at <c>RunAsync</c>, so the count here is the one-token
    ///     shape.
    ///     Measured: 848 B/op on .NET 10 and 849 B/op on .NET 8, against the raw enumeration's
    ///     1,216 B/op total - roughly twice the call path's Default overhead, which is the honest
    ///     reading: a stream pays for everything a call pays (the box, the linked source, the pooled
    ///     timer) plus the enumerator itself and the surviving sources a call tears down at attempt
    ///     end. Budgeted with roughly 15% headroom over the measured figure, as the call budgets are.
    /// </summary>
    public const double DefaultStreamingOverhead = 1000;

    /// <summary>
    ///     The pay-for-play gate, expressed as the thing it actually claims: what a listener adds must
    ///     be accounted for by the boxes it asked for, and nothing else.
    ///     Two events on a successful call carry a result - <c>Attempt</c> and <c>Succeeded</c> - and a
    ///     boxed <c>int</c> is 24 B on both target frameworks, so 48 B is the whole of it. The ceiling
    ///     allows one further box for measurement drift; anything beyond that means the executor grew
    ///     a per-event allocation, which is exactly the failure mode that makes telemetry something
    ///     people turn off in production.
    /// </summary>
    public const double ListenerAllowance = 72;

    /// <summary>
    ///     What chaining the log listener onto an already-listening policy may cost when every level is
    ///     disabled. The generated <c>[LoggerMessage]</c> guard returns before it formats anything, and
    ///     the listener's own path is one <c>switch</c> and one <c>IsEnabled</c> call per event - so the
    ///     honest budget is the noise floor and nothing more.
    /// </summary>
    public const double DisabledLoggingAllowance = SuspendingNoiseFloor;

    /// <summary>
    ///     The shipping executor must not be more expensive than the hand-written stand-in
    ///     used to establish the achievable floor. Measured: 408 B against 399 B on .NET 10, while
    ///     additionally capturing a per-attempt exception, classifying results, awaiting a pre-attempt
    ///     hook, carrying the retry budget the stand-in's own breaker-and-budget arm did not charge for,
    ///     and clamping against an inherited deadline the stand-in has no concept of.
    ///     It read 393 B against 401 B - the real loop <i>cheaper</i> than the floor - until deadline
    ///     propagation put 16 B in the box, and it now sits 9 B above the stand-in, inside an allowance
    ///     that was set for tier and GC drift. That is the honest reading: the shipping loop does strictly
    ///     more than the floor it is measured against, and the margin for the next feature that costs
    ///     every caller a field is now 7 B rather than 16.
    ///     This is the gate that would catch the design's central mechanism failing to survive
    ///     implementation. The allowance is
    ///     for tier and GC drift between two arms of the same sweep, not for regression headroom.
    /// </summary>
    public const double ShippingVersusStandInAllowance = 16;

    /// <summary>
    ///     The inline attempt log, priced by its own layout rather than by differencing two loops:
    ///     <c>AttemptBuffer.Capacity</c> (4) x <c>sizeof(AttemptRecord)</c> (16) = 64 bytes of
    ///     state-machine box, paid by every suspending call whether or not anything ever fails.
    ///     The stand-in measured this by running the identical stand-in loop with the log removed, and got
    ///     96 B for a 24-byte record. The shipping executor has no log-less variant to difference
    ///     against - the log is not optional - so the gate asserts the layout instead, which is the
    ///     thing a change would actually move.
    /// </summary>
    public const double InlineAttemptLogCost = 64;

    /// <summary>
    ///     <c>sizeof(Verdict)</c>: a <c>long</c> of biased <c>RetryAfter</c> ticks and one packed byte
    ///     carrying both <c>VerdictKind</c> and <c>SelfImposed</c>, which the runtime lays out in 16
    ///     bytes.
    ///     Both packings were added on the premise that they are free: the flag rides in the padding the
    ///     single-byte <c>Kind</c> already leaves, and the pushback rides in a <c>long</c> with zero as
    ///     its sentinel rather than in the <c>TimeSpan?</c> the public property still exposes. A verdict
    ///     is live across the attempt <c>await</c> and so is paid for in the state-machine box of every
    ///     suspending call, which makes that premise worth asserting rather than assuming. The obvious
    ///     shape - a <c>bool</c> field beside a <c>TimeSpan?</c> - measured 32 bytes.
    /// </summary>
    public const int VerdictSize = 16;

    /// <summary>
    ///     <c>sizeof(Attempt)</c>. Every materialized attempt log is an array of these - which is every
    ///     <c>TryRunAsync</c> call and every failing <c>RunAsync</c> call - so a byte here is a byte per
    ///     attempt per failure.
    ///     <c>Attempt.Verdict</c> is documented as not round-tripping the pushback, so the struct stores
    ///     the same packed byte the inline log stores and rebuilds the verdict in the property rather
    ///     than embedding one. Embedding it measured 72 bytes.
    /// </summary>
    public const int AttemptSize = 48;

    /// <summary>
    ///     <c>sizeof(CallEvent)</c>: what raising one event costs to copy into a listener, on a struct
    ///     whose whole point is that a listener left attached in production allocates nothing.
    ///     Three fields that would naturally be nullable value types - <c>Delay</c>, <c>Reason</c> and
    ///     the verdict's own <c>RetryAfter</c> - are stored biased-by-one instead, and <c>Kind</c> is
    ///     stored as a byte. The natural shape measured 88 bytes.
    /// </summary>
    public const int CallEventSize = 64;

    // ---- The falsification test. ----

    /// <summary>
    ///     The design's central claim, reduced to a number that can fail a build: the fused executor
    ///     under a realistic policy must allocate substantially less than Polly's equivalent pipeline
    ///     on the suspending path.
    ///     Measured: 3.2x. The original argument predicted 4-8x for a realistic policy, so this gate is
    ///     set at the level below which the architectural argument stops paying for itself rather than
    ///     at the measured value. If this fails, collapsing composition is no longer worth what it
    ///     costs in flexibility, and that is an answer the falsification test exists to give.
    /// </summary>
    public const double MinimumOverheadRatioVersusPolly = 2.5;

    /// <summary>
    ///     The same claim measured over a real loopback TCP round trip rather than over
    ///     <c>Task.Yield</c>. Measured: 2.41x on both TFMs, against 3.22x on the yield gate.
    ///     The two disagree for a reason worth knowing: <c>Task.Yield</c> ignores the token it is
    ///     handed, so the yield gate cannot see what it costs to give a callback a <i>cancellable</i>
    ///     token. Real I/O registers on that token, and the executor's per-attempt linked source
    ///     makes every attempt token cancellable even when the caller's was not. The socket figure
    ///     is therefore the more honest headline, and the yield gate is the deterministic proxy that
    ///     CI can actually enforce.
    ///     The 2.38x recorded earlier was taken before the probe's round trip was made to suspend
    ///     deterministically, and this floor is the reason the change was needed: with the receive
    ///     issued after the send, the fraction of round trips completing synchronously tracked the
    ///     platform's loopback timing, and that fraction moves the ratio. A Windows runner measuring
    ///     roughly half its receives synchronously reported 1.72x - not because anything regressed,
    ///     but because a synchronous callback erases a composed pipeline's per-attempt boxes while
    ///     the executor still pays for its linked source. The probe now asserts that no receive
    ///     completed synchronously before this ratio is allowed to mean anything.
    ///     Enforced on Linux and macOS. Windows is excluded because Polly's arm does not measure
    ///     repeatably there - on one runner it read below the fused executor it wraps - and the
    ///     reason is in that arm, not in this library, whose own figure is within 15 B of its
    ///     Linux and macOS value on the same runs. <c>SocketCrossCheckTests.OnWindows</c> carries
    ///     the numbers. The yield gate and every budget above still run everywhere.
    /// </summary>
    public const double MinimumSocketRatioVersusPolly = 2.0;

    /// <summary>
    ///     The trivial-policy comparison, which is not a win and is gated as such.
    ///     The design predicted 2-3x here. The stand-in falsified it (1.27x for a stripped stand-in), and
    ///     the shipping executor settles it: the smallest non-passthrough policy the
    ///     library can express costs 320 B against Polly's empty-pipeline 304 B, a ratio of
    ///     <b>1.05x the wrong way</b>. The two are not doing the same work - Polly's empty pipeline does
    ///     nothing at all, while the fused loop is classifying, retrying and recording attempts - but the
    ///     claim as written compared the two, and as written it was false.
    ///     So this gate is a ceiling on the fused loop rather than a floor under a ratio: the trivial
    ///     shape may sit at parity with a pipeline that does nothing, and must not drift away from it.
    ///     The honest headline is that the fused design wins in proportion to how much policy is
    ///     configured.
    ///     Measured: 1.05x on .NET 10 and 1.10x on .NET 8. The ceiling sits at 1.25x rather than nearer
    ///     the measurement because the <i>denominator</i> is the unstable half - Polly's empty pipeline
    ///     measures between 290 B and 304 B across runs, while the fused trivial shape holds at 319-320 B
    ///     to the byte. <see cref="TrivialOverhead" /> is the strict gate on this arm; this one exists to
    ///     catch the loop drifting away from parity, and 1.25x still catches about 30 B of growth.
    /// </summary>
    public const double MaximumTrivialRatioVersusPollyEmpty = 1.25;

    // ---- Retry. ----

    /// <summary>
    ///     Two transient failures then a success, against the shipping executor, with the retry budget
    ///     turned off - see ShippingScenarios.RetryArm for why an arm that retries thousands of times a
    ///     second is precisely what the budget exists to refuse. Measured: 2,056 B on .NET 10 and
    ///     2,344 B on .NET 8, against the stand-in's 2,113 B and 2,848 B - so the
    ///     real retry path is cheaper than the stand-in on both, and markedly so on .NET 8.
    ///     Dominated by exception capture and rethrow, which both arms pay. Gated loosely on the
    ///     absolute figure and strictly on the comparison, because the absolute number is mostly a
    ///     property of the runtime's exception machinery rather than of this design.
    /// </summary>
    public const double RetryTwiceCeiling = 2_900;

    /// <summary>
    ///     Two refusals from local admission control then a success. Measured: 2,056.8 B on .NET 10 and
    ///     2,344.0 B on .NET 8, against the retry arm's 2,056.8 B and 2,344.6 B in the same sweep - the
    ///     two paths are indistinguishable, which is exactly the claim. Dominated by the same exception
    ///     capture and rethrow the retry arm pays, so it sits at the same ceiling.
    ///     The refusal path is by definition not the hot one, and this number is recorded rather than
    ///     minimized. What the gate is for is the shape of the claim: a refusal must cost what a retried
    ///     exception costs and no more - if it ever costs materially more, something on the refusal path
    ///     has started allocating that the retry path does not.
    /// </summary>
    public const double LimitedTwiceCeiling = 2_900;

    // ---- Cancellation primitives. ----

    public const double NewSource = 48;
    public const double NewSourceWithCancelAfter = 144;
    public const double LinkedFromNone = 48;
    public const double PooledSourceReuse = 0;
}
