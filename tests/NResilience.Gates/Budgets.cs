namespace NResilience.Gates;

/// <summary>
/// The checked-in allocation budgets. Every number here was measured on 2026-08-19, .NET 10.0.0
/// and .NET 8.0.22, arm64, Release, workstation non-concurrent GC, and is recorded with its
/// measured value beside it so a failure reads as "this moved" rather than "this is wrong".
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

    // ---- Suspending path. ----

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

    /// <summary>The realistic executor frame, no timeout source. Measured: 336 B.</summary>
    public const double RealLoopNoTimeoutOverhead = 390;

    /// <summary>The realistic executor frame with deadline, attempt timeout and budget. Measured: 401 B.</summary>
    public const double RealLoopDefaultOverhead = 465;

    /// <summary>Adding a breaker adds no state live across the await. Measured: 400 B.</summary>
    public const double RealLoopWithBreakerOverhead = 465;

    /// <summary>
    /// The inline attempt log, priced on its own: AttemptBuffer.Capacity (4) x sizeof(AttemptRecord)
    /// (24) = 96 bytes of state-machine box, paid by every suspending call whether or not anything
    /// fails. Measured: 96 B on both TFMs. Gated so that growing the record or the capacity is a
    /// decision rather than a drift.
    /// </summary>
    public const double InlineAttemptLogCost = 112;

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
    /// The trivial-policy comparison, gated only at parity. Measured: 1.27x
    /// (fused 240 B against Polly's empty-pipeline 305 B). The design predicted 2-3x here and
    /// that prediction does not survive measurement; see plans/phase-0a-results.md.
    /// </summary>
    public const double MinimumTrivialRatioVersusPolly = 1.0;

    // ---- Retry. ----

    /// <summary>
    /// Two transient failures then a success. Measured: 2112 B on .NET 10, 2848 B on .NET 8 —
    /// dominated by exception capture and rethrow, which both arms pay. Gated loosely on the
    /// absolute figure and strictly on the comparison, because the absolute number is mostly a
    /// property of the runtime's exception machinery rather than of this design.
    /// </summary>
    public const double RetryTwiceCeiling = 3_400;

    // ---- Cancellation primitives. ----

    public const double NewSource = 48;
    public const double NewSourceWithCancelAfter = 144;
    public const double LinkedFromNone = 48;
    public const double PooledSourceReuse = 0;
}
