using NResilience.Probes;
using NResilience.Probes.Polly;

namespace NResilience.Gates;

/// <summary>
/// One measurable arm of the A/B.
/// <para>
/// The body is bound behind <see cref="Of{T}"/> rather than stored as a
/// <c>Func&lt;ValueTask&lt;int&gt;&gt;</c>, so an arm whose natural result is not the shared gate's
/// <c>int</c> — <c>TryRunAsync</c> returns a <c>CallResult&lt;T&gt;</c> — is measured in its own
/// shape. A conversion wrapper would suspend, and a wrapper that suspends allocates a
/// state-machine box the arm it is wrapping does not, which would charge that arm for the harness.
/// The closure is created once when the list is built, outside every measured region.
/// </para>
/// </summary>
public sealed class Arm
{
    private readonly Func<int, int, int, Task<AllocationMeasurement>> _measure;

    private Arm(string name, AllocationCounter counter, Func<int, int, int, Task<AllocationMeasurement>> measure)
    {
        Name = name;
        Counter = counter;
        _measure = measure;
    }

    public string Name { get; }

    public AllocationCounter Counter { get; }

    public static Arm Of<T>(string name, Func<ValueTask<T>> body, AllocationCounter counter, Action? betweenOperations = null) =>
        new(name, counter, (warmup, iterations, repeats) =>
            AllocationProbe.MeasureAsync(name, body, counter, warmup, iterations, repeats, betweenOperations));

    public Task<AllocationMeasurement> MeasureAsync(int warmup, int iterations, int repeats) =>
        _measure(warmup, iterations, repeats);
}

/// <summary>
/// Every arm, in one list, measured in one process. Appendix B's headline comparison was
/// inferred by subtracting a floor measured on one harness from a total measured on another,
/// which the design document itself calls out as the failure mode to avoid. This list is the
/// replacement.
///
/// <para>
/// Every gate points at the <b>shipping</b> executor. The stand-in arms are
/// kept and still measured, in the same sweep, because the only trustworthy stand-in-versus-shipping
/// delta is one taken in one process under one GC in one tier state — and because the stand-in carries two
/// things the shipping library does not yet have: a breaker and a budget, whose frame cost the
/// shipping breaker and budget will inherit. Reference rows, not gates.
/// </para>
/// </summary>
public static class Arms
{
    public static IReadOnlyList<Arm> Suspending()
    {
        Scenarios.RetryArm standInRetry = Scenarios.BuildFusedRetry();
        ShippingScenarios.RetryArm shippingRetry = ShippingScenarios.BuildRetry();
        PollyScenarios.PollyRetryArm pollyRetry = PollyScenarios.BuildRetryArm();
        ShippingScenarios.LimitArm shippingLimited = ShippingScenarios.BuildLimited();

        return
        [
            Arm.Of("raw callback (baseline)", Scenarios.RawSuspending, AllocationCounter.ProcessWide),

            // The shipping executor: what every gate below asserts against.
            Arm.Of("lib: None (passthrough)", ShippingScenarios.NoneSuspending, AllocationCounter.ProcessWide),
            Arm.Of("lib: trivial (no bounds)", ShippingScenarios.TrivialSuspending, AllocationCounter.ProcessWide),
            Arm.Of("lib: Default", ShippingScenarios.DefaultSuspending, AllocationCounter.ProcessWide),
            Arm.Of("lib: Default, cancellable token", ShippingScenarios.DefaultSuspendingCancellable, AllocationCounter.ProcessWide),
            Arm.Of("lib: TryRunAsync, Default", ShippingScenarios.TryRunDefaultSuspending, AllocationCounter.ProcessWide),
            Arm.Of("lib: Default + listener", ShippingScenarios.DefaultListenerSuspending, AllocationCounter.ProcessWide),
            Arm.Of("lib: Default + listener + logging", ShippingScenarios.DefaultLoggingSuspending, AllocationCounter.ProcessWide),
            Arm.Of("lib: retry x2 -> success", shippingRetry.RunAsync, AllocationCounter.ProcessWide, shippingRetry.Reset),
            Arm.Of("lib: limited x2 -> success", shippingLimited.RunAsync, AllocationCounter.ProcessWide, shippingLimited.Reset),

            // Stand-in: reference rows for the stand-in-versus-shipping delta.
            Arm.Of("fused: None (passthrough)", Scenarios.NoneSuspending, AllocationCounter.ProcessWide),
            Arm.Of("fused: lean loop", Scenarios.LeanSuspending, AllocationCounter.ProcessWide),
            Arm.Of("fused: real loop, no log, no timeout", Scenarios.FusedNoTimeoutNoLogSuspending, AllocationCounter.ProcessWide),
            Arm.Of("fused: real loop, no timeout", Scenarios.FusedNoTimeoutSuspending, AllocationCounter.ProcessWide),
            Arm.Of("fused: real loop, no log, Default", Scenarios.FusedDefaultNoLogSuspending, AllocationCounter.ProcessWide),
            Arm.Of("fused: real loop, Default", Scenarios.FusedDefaultSuspending, AllocationCounter.ProcessWide),
            Arm.Of("fused: real loop, +breaker", Scenarios.FusedFullSuspending, AllocationCounter.ProcessWide),
            Arm.Of("fused: Default, cancellable token", Scenarios.FusedDefaultSuspendingCancellable, AllocationCounter.ProcessWide),
            Arm.Of("polly: empty pipeline", PollyScenarios.EmptySuspending, AllocationCounter.ProcessWide),
            Arm.Of("polly: retry + timeout", PollyScenarios.RetryTimeoutSuspending, AllocationCounter.ProcessWide),
            Arm.Of("polly: r+t, cancellable token", PollyScenarios.RetryTimeoutSuspendingCancellable, AllocationCounter.ProcessWide),
            Arm.Of("fused: retry x2 -> success", standInRetry.RunAsync, AllocationCounter.ProcessWide, standInRetry.Reset),
            Arm.Of("polly: retry x2 -> success", pollyRetry.RunAsync, AllocationCounter.ProcessWide, pollyRetry.Reset),
        ];
    }

    public static IReadOnlyList<Arm> SyncCompleting() =>
    [
        Arm.Of("raw callback (baseline)", Scenarios.RawSync, AllocationCounter.ThreadLocal),

        Arm.Of("lib: None (passthrough)", ShippingScenarios.NoneSync, AllocationCounter.ThreadLocal),
        Arm.Of("lib: trivial, static+state", ShippingScenarios.TrivialSyncState, AllocationCounter.ThreadLocal),
        Arm.Of("lib: trivial, callback", ShippingScenarios.TrivialSyncCallback, AllocationCounter.ThreadLocal),
        Arm.Of("lib: Default, static+state", ShippingScenarios.DefaultSyncState, AllocationCounter.ThreadLocal),

        Arm.Of("fused: None (passthrough)", Scenarios.NoneSync, AllocationCounter.ThreadLocal),
        Arm.Of("fused: no timeout, static+state", Scenarios.FusedNoTimeoutSyncState, AllocationCounter.ThreadLocal),
        Arm.Of("fused: no timeout, callback", Scenarios.FusedNoTimeoutSyncCallback, AllocationCounter.ThreadLocal),
        Arm.Of("fused: Default, static+state", Scenarios.FusedDefaultSyncState, AllocationCounter.ThreadLocal),
        Arm.Of("fused: +breaker, static+state", Scenarios.FusedFullSyncState, AllocationCounter.ThreadLocal),
        Arm.Of("polly: empty pipeline", PollyScenarios.EmptySync, AllocationCounter.ThreadLocal),
        Arm.Of("polly: retry + timeout", PollyScenarios.RetryTimeoutSync, AllocationCounter.ThreadLocal),
    ];

    public static IReadOnlyList<Arm> Cancellation() =>
    [
        Arm.Of("new CancellationTokenSource()", CtsFacts.NewSource, AllocationCounter.ThreadLocal),
        Arm.Of("new CTS + CancelAfter", CtsFacts.NewSourceWithCancelAfter, AllocationCounter.ThreadLocal),
        Arm.Of("CreateLinked(cancellable)", CtsFacts.LinkedFromCancellable, AllocationCounter.ThreadLocal),
        Arm.Of("CreateLinked(None)", CtsFacts.LinkedFromNone, AllocationCounter.ThreadLocal),
        Arm.Of("CreateLinked(two cancellable)", CtsFacts.LinkedFromTwoCancellable, AllocationCounter.ThreadLocal),
        Arm.Of("pooled CTS: CancelAfter+TryReset", CtsFacts.PooledSourceReused, AllocationCounter.ThreadLocal),
        Arm.Of("Task.Delay(30s) then cancel", CtsFacts.DelayCreatedThenCancelled, AllocationCounter.ThreadLocal),
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
            results[arm.Name] = await arm.MeasureAsync(warmup, iterations, repeats);
        }

        return results;
    }
}
