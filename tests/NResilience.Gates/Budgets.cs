namespace NResilience.Gates;

/// <summary>
/// The checked-in allocation budgets. Every number here was measured on 2026-08-19, .NET 10.0.0
/// and .NET 8.0.22, arm64, Release, workstation non-concurrent GC, and is recorded with its
/// measured value beside it so a failure reads as "this moved" rather than "this is wrong".
///
/// Phase 0b re-pointed every budget at the <b>shipping</b> executor. Where a figure changed, the
/// Phase 0a stand-in value is kept in the comment, because the delta is the answer to the question
/// Phase 0 exists to ask.
///
/// Budgets carry roughly 15% headroom over the measured figure, because allocation is
/// deterministic but not identical across architectures. They are ceilings, not targets: a
/// change that comes in under budget still needs its number looked at.
/// </summary>
public static class Budgets
{
    // ---- Synchronous fast path. These are exact, and the only genuinely zero rows. ----

    /// <summary>Resilience.None is a single branch that returns the callback's own task.</summary>
    public const double NoneSyncOverhead = 0;

    /// <summary>Full policy minus the attempt timeout, static lambda + value-type state. Measured: 0 B.</summary>
    public const double FullPolicyNoTimeoutSyncOverhead = 0;

    /// <summary>
    /// The same call with an attempt timeout. Measured: 64 B — one linked source per attempt.
    ///
    /// This is a deliberate, documented departure from the CI gate table in
    /// plans/nresilience-design-v3.md, which budgets 0 bytes for "full policy, sync-completing".
    /// It is not achievable: the callback must receive a token the attempt timeout can cancel,
    /// the timeout source cannot be known to be unnecessary until after the callback returns,
    /// and the pooled source's own token must never be handed to user code because TryReset
    /// preserves token identity. Polly reaches 24 B here by handing out its pooled token, which
    /// is the hazard this design refuses. See plans/phase-0a-results.md.
    /// </summary>
    public const double FullPolicyWithTimeoutSyncOverhead = 72;

    // ---- Suspending path. Measured against the shipping executor in Phase 0b. ----

    /// <summary>
    /// The instrument's noise floor, applied to every suspending assertion.
    ///
    /// Suspending measurements read <c>GC.GetTotalAllocatedBytes(precise: true)</c>, because a
    /// continuation resumed on a thread-pool thread does not allocate against the thread that
    /// started the operation. A process-wide counter cannot return an exact zero: the process is
    /// quiesced but not empty, so a passthrough arm that genuinely allocates nothing still
    /// measures a fraction of a byte per operation. Observed drift is under 1 B/op over
    /// 2,000-iteration batches; 8 B is a floor an actual regression cannot hide beneath, since
    /// the smallest object the runtime can allocate is larger than that.
    ///
    /// The synchronous assertions use the thread-local counter and get no such allowance —
    /// their zeros are exact.
    /// </summary>
    public const double SuspendingNoiseFloor = 8;

    /// <summary>Passthrough allocates nothing on any path. Measured: 0 B.</summary>
    public const double NoneSuspendingOverhead = 0;

    /// <summary>
    /// The trivial shipping shape: retry and classification, no deadline and no attempt timeout.
    /// Measured: 320 B on .NET 10, 308 B on .NET 8, against the Phase 0a stand-in's 336 B.
    /// </summary>
    public const double TrivialOverhead = 368;

    /// <summary>
    /// The realistic policy — <c>Resilience.Default</c>: three attempts, a deadline, an attempt
    /// timeout, exponential backoff, classification and the inline attempt log.
    /// Measured: 384 B on .NET 10, 381 B on .NET 8, against the Phase 0a stand-in's 401 B.
    /// </summary>
    public const double DefaultOverhead = 448;

    /// <summary>
    /// The same call with a caller token that can be cancelled and never is — the production case.
    /// Measured: 400 B on .NET 10, 397 B on .NET 8, against the stand-in's 416 B. The extra 16 B
    /// over <see cref="DefaultOverhead"/> is the marginal cost of linking against a long-lived
    /// source whose registration storage already exists; see <see cref="MinimumSocketRatioVersusPolly"/>
    /// for what the same link costs when real I/O registers on the resulting token.
    /// </summary>
    public const double DefaultCancellableOverhead = 464;

    /// <summary>
    /// <c>TryRunAsync</c>, which always materialises the attempt log because its caller has
    /// explicitly asked for a result object. Measured: 553 B on .NET 10, 551 B on .NET 8 — so
    /// asking for the history costs about 170 B over the throwing form. Budgeted rather than
    /// left to be discovered by a caller who assumed the two were the same price.
    /// </summary>
    public const double TryRunDefaultOverhead = 640;

    /// <summary>
    /// The shipping executor must not be more expensive than the hand-written stand-in Phase 0a
    /// used to establish the achievable floor. Measured: 384 B against 401 B on .NET 10 — the real
    /// loop is <i>cheaper</i>, while additionally capturing a per-attempt exception, classifying
    /// results and awaiting a pre-attempt hook.
    ///
    /// This is the gate that would catch the design's central mechanism failing to survive
    /// implementation, which is the whole reason Phase 0 was split into 0a and 0b. The allowance is
    /// for tier and GC drift between two arms of the same sweep, not for regression headroom.
    /// </summary>
    public const double ShippingVersusStandInAllowance = 16;

    /// <summary>
    /// The inline attempt log, priced by its own layout rather than by differencing two loops:
    /// <c>AttemptBuffer.Capacity</c> (4) x <c>sizeof(AttemptRecord)</c> (16) = 64 bytes of
    /// state-machine box, paid by every suspending call whether or not anything ever fails.
    ///
    /// Phase 0a measured this by running the identical stand-in loop with the log removed, and got
    /// 96 B for a 24-byte record. The shipping executor has no log-less variant to difference
    /// against — the log is not optional — so the gate asserts the layout instead, which is the
    /// thing a change would actually move.
    /// </summary>
    public const double InlineAttemptLogCost = 64;

    // ---- The falsification test. ----

    /// <summary>
    /// The design's central claim, reduced to a number that can fail a build: the fused executor
    /// under a realistic policy must allocate substantially less than Polly's equivalent pipeline
    /// on the suspending path.
    ///
    /// Measured: 3.2x. The design document predicted 4-8x for a realistic policy, so this gate is
    /// set at the level below which the architectural argument stops paying for itself rather than
    /// at the measured value. If this fails, collapsing composition is no longer worth what it
    /// costs in flexibility, and that is a Phase 0 answer, not a Phase 6 discovery.
    /// </summary>
    public const double MinimumOverheadRatioVersusPolly = 2.5;

    /// <summary>
    /// The same claim measured over a real loopback TCP round trip rather than over
    /// <c>Task.Yield</c>. Measured: 2.38x, against 3.22x on the yield gate.
    ///
    /// The two disagree for a reason worth knowing: <c>Task.Yield</c> ignores the token it is
    /// handed, so the yield gate cannot see what it costs to give a callback a <i>cancellable</i>
    /// token. Real I/O registers on that token, and the executor's per-attempt linked source
    /// makes every attempt token cancellable even when the caller's was not. The socket figure
    /// is therefore the more honest headline, and the yield gate is the deterministic proxy that
    /// CI can actually enforce.
    /// </summary>
    public const double MinimumSocketRatioVersusPolly = 2.0;

    /// <summary>
    /// The trivial-policy comparison, which is not a win and is gated as such.
    ///
    /// The design predicted 2-3x here. Phase 0a falsified it (1.27x for a stripped stand-in), and
    /// Phase 0b settles it against the shipping executor: the smallest non-passthrough policy the
    /// library can express costs 320 B against Polly's empty-pipeline 304 B, a ratio of
    /// <b>1.05x the wrong way</b>. The two are not doing the same work — Polly's empty pipeline does
    /// nothing at all, while the fused loop is classifying, retrying and recording attempts — but the
    /// claim as written compared the two, and as written it was false.
    ///
    /// So this gate is a ceiling on the fused loop rather than a floor under a ratio: the trivial
    /// shape may sit at parity with a pipeline that does nothing, and must not drift away from it.
    /// The honest headline is that the fused design wins in proportion to how much policy is
    /// configured.
    ///
    /// Measured: 1.05x on .NET 10 and 1.10x on .NET 8. The ceiling sits at 1.25x rather than nearer
    /// the measurement because the <i>denominator</i> is the unstable half — Polly's empty pipeline
    /// measures between 290 B and 304 B across runs, while the fused trivial shape holds at 319-320 B
    /// to the byte. <see cref="TrivialOverhead"/> is the strict gate on this arm; this one exists to
    /// catch the loop drifting away from parity, and 1.25x still catches about 30 B of growth.
    /// </summary>
    public const double MaximumTrivialRatioVersusPollyEmpty = 1.25;

    // ---- Retry. ----

    /// <summary>
    /// Two transient failures then a success, against the shipping executor. Measured: 2,049 B on
    /// .NET 10 and 2,336 B on .NET 8, against the Phase 0a stand-in's 2,113 B and 2,848 B — so the
    /// real retry path is cheaper than the stand-in on both, and markedly so on .NET 8.
    ///
    /// Dominated by exception capture and rethrow, which both arms pay. Gated loosely on the
    /// absolute figure and strictly on the comparison, because the absolute number is mostly a
    /// property of the runtime's exception machinery rather than of this design.
    /// </summary>
    public const double RetryTwiceCeiling = 2_900;

    // ---- Cancellation primitives. ----

    public const double NewSource = 48;
    public const double NewSourceWithCancelAfter = 144;
    public const double LinkedFromNone = 48;
    public const double PooledSourceReuse = 0;
}
