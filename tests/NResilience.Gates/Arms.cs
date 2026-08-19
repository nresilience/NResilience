using NResilience.Probes;
using NResilience.Probes.Polly;

namespace NResilience.Gates;

/// <summary>One measurable arm of the Phase 0a A/B.</summary>
public sealed record Arm(string Name, Func<ValueTask<int>> Body, AllocationCounter Counter, Action? BetweenOperations = null);

/// <summary>
/// Every arm, in one list, measured in one process. Appendix B's headline comparison was
/// inferred by subtracting a floor measured on one harness from a total measured on another,
/// which the design document itself calls out as the failure mode to avoid. This list is the
/// replacement.
/// </summary>
public static class Arms
{
    public static IReadOnlyList<Arm> Suspending()
    {
        Scenarios.RetryArm fusedRetry = Scenarios.BuildFusedRetry();
        PollyScenarios.PollyRetryArm pollyRetry = PollyScenarios.BuildRetryArm();

        return
        [
            new Arm("raw callback (baseline)", Scenarios.RawSuspending, AllocationCounter.ProcessWide),
            new Arm("fused: None (passthrough)", Scenarios.NoneSuspending, AllocationCounter.ProcessWide),
            new Arm("fused: lean loop", Scenarios.LeanSuspending, AllocationCounter.ProcessWide),
            new Arm("fused: real loop, no log, no timeout", Scenarios.FusedNoTimeoutNoLogSuspending, AllocationCounter.ProcessWide),
            new Arm("fused: real loop, no timeout", Scenarios.FusedNoTimeoutSuspending, AllocationCounter.ProcessWide),
            new Arm("fused: real loop, no log, Default", Scenarios.FusedDefaultNoLogSuspending, AllocationCounter.ProcessWide),
            new Arm("fused: real loop, Default", Scenarios.FusedDefaultSuspending, AllocationCounter.ProcessWide),
            new Arm("fused: real loop, +breaker", Scenarios.FusedFullSuspending, AllocationCounter.ProcessWide),
            new Arm("fused: Default, cancellable token", Scenarios.FusedDefaultSuspendingCancellable, AllocationCounter.ProcessWide),
            new Arm("polly: empty pipeline", PollyScenarios.EmptySuspending, AllocationCounter.ProcessWide),
            new Arm("polly: retry + timeout", PollyScenarios.RetryTimeoutSuspending, AllocationCounter.ProcessWide),
            new Arm("polly: r+t, cancellable token", PollyScenarios.RetryTimeoutSuspendingCancellable, AllocationCounter.ProcessWide),
            new Arm("fused: retry x2 -> success", fusedRetry.RunAsync, AllocationCounter.ProcessWide, fusedRetry.Reset),
            new Arm("polly: retry x2 -> success", pollyRetry.RunAsync, AllocationCounter.ProcessWide, pollyRetry.Reset),
        ];
    }

    public static IReadOnlyList<Arm> SyncCompleting() =>
    [
        new Arm("raw callback (baseline)", Scenarios.RawSync, AllocationCounter.ThreadLocal),
        new Arm("fused: None (passthrough)", Scenarios.NoneSync, AllocationCounter.ThreadLocal),
        new Arm("fused: no timeout, static+state", Scenarios.FusedNoTimeoutSyncState, AllocationCounter.ThreadLocal),
        new Arm("fused: no timeout, callback", Scenarios.FusedNoTimeoutSyncCallback, AllocationCounter.ThreadLocal),
        new Arm("fused: Default, static+state", Scenarios.FusedDefaultSyncState, AllocationCounter.ThreadLocal),
        new Arm("fused: +breaker, static+state", Scenarios.FusedFullSyncState, AllocationCounter.ThreadLocal),
        new Arm("polly: empty pipeline", PollyScenarios.EmptySync, AllocationCounter.ThreadLocal),
        new Arm("polly: retry + timeout", PollyScenarios.RetryTimeoutSync, AllocationCounter.ThreadLocal),
    ];

    public static IReadOnlyList<Arm> Cancellation() =>
    [
        new Arm("new CancellationTokenSource()", CtsFacts.NewSource, AllocationCounter.ThreadLocal),
        new Arm("new CTS + CancelAfter", CtsFacts.NewSourceWithCancelAfter, AllocationCounter.ThreadLocal),
        new Arm("CreateLinked(cancellable)", CtsFacts.LinkedFromCancellable, AllocationCounter.ThreadLocal),
        new Arm("CreateLinked(None)", CtsFacts.LinkedFromNone, AllocationCounter.ThreadLocal),
        new Arm("CreateLinked(two cancellable)", CtsFacts.LinkedFromTwoCancellable, AllocationCounter.ThreadLocal),
        new Arm("pooled CTS: CancelAfter+TryReset", CtsFacts.PooledSourceReused, AllocationCounter.ThreadLocal),
        new Arm("Task.Delay(30s) then cancel", CtsFacts.DelayCreatedThenCancelled, AllocationCounter.ThreadLocal),
    ];

    public static async Task<Dictionary<string, AllocationMeasurement>> MeasureAsync(
        IReadOnlyList<Arm> arms,
        int warmup = AllocationProbe.DefaultWarmup,
        int iterations = AllocationProbe.DefaultIterations,
        int repeats = AllocationProbe.DefaultRepeats)
    {
        var results = new Dictionary<string, AllocationMeasurement>(StringComparer.Ordinal);
        foreach (Arm arm in arms)
        {
            results[arm.Name] = await AllocationProbe.MeasureAsync(
                arm.Name, arm.Body, arm.Counter, warmup, iterations, repeats, arm.BetweenOperations);
        }

        return results;
    }
}
