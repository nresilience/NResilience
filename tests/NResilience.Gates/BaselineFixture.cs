using NResilience.Probes;
using Xunit;

namespace NResilience.Gates;

/// <summary>
/// Measures every arm once per test run. Sharing one sweep across the gate classes keeps the
/// comparisons internally consistent - a ratio between two arms is only meaningful if both
/// were measured in the same process, under the same GC, in the same tier state.
/// </summary>
public sealed class BaselineFixture : IAsyncLifetime
{
    public Dictionary<string, AllocationMeasurement> Suspending { get; private set; } = [];

    public Dictionary<string, AllocationMeasurement> Sync { get; private set; } = [];

    public Dictionary<string, AllocationMeasurement> Cancellation { get; private set; } = [];

    public async ValueTask InitializeAsync()
    {
        Suspending = await Arms.MeasureAsync(Arms.Suspending());
        Sync = await Arms.MeasureAsync(Arms.SyncCompleting());
        Cancellation = await Arms.MeasureAsync(Arms.Cancellation());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public double SuspendingBytes(string arm) => Suspending[arm].BytesPerOperation;

    public double SyncBytes(string arm) => Sync[arm].BytesPerOperation;

    public double CancellationBytes(string arm) => Cancellation[arm].BytesPerOperation;

    /// <summary>Bytes above the un-wrapped callback, which is the only number a comparison may use.</summary>
    public double SuspendingOverhead(string arm) => SuspendingBytes(arm) - SuspendingBytes(Baseline.RawSuspending);

    public double SyncOverhead(string arm) => SyncBytes(arm) - SyncBytes(Baseline.RawSync);
}

/// <summary>Arm names, in one place, so a rename cannot silently turn a gate into a lookup failure.</summary>
public static class Baseline
{
    public const string RawSuspending = "raw callback (baseline)";

    // The shipping executor. Every gate asserts against these.
    public const string LibNone = "lib: None (passthrough)";
    public const string LibTrivial = "lib: trivial (no bounds)";
    public const string LibDefault = "lib: Default";
    public const string LibDefaultCancellable = "lib: Default, cancellable token";
    public const string LibTryRunDefault = "lib: TryRunAsync, Default";
    public const string LibDefaultListener = "lib: Default + listener";

    /// <summary>The same arm with the shipping log listener chained on, at a logger that carries nothing.</summary>
    public const string LibDefaultLogging = "lib: Default + listener + logging";
    public const string LibRetry = "lib: retry x2 -> success";

    public const string LibLimited = "lib: limited x2 -> success";

    // The stand-in, kept as reference rows.
    public const string NonePassthrough = "fused: None (passthrough)";
    public const string LeanLoop = "fused: lean loop";
    public const string RealNoLogNoTimeout = "fused: real loop, no log, no timeout";
    public const string RealNoTimeout = "fused: real loop, no timeout";
    public const string RealNoLogDefault = "fused: real loop, no log, Default";
    public const string RealDefault = "fused: real loop, Default";
    public const string RealBreaker = "fused: real loop, +breaker";
    public const string RealDefaultCancellable = "fused: Default, cancellable token";
    public const string PollyEmpty = "polly: empty pipeline";
    public const string PollyRetryTimeout = "polly: retry + timeout";
    public const string PollyRetryTimeoutCancellable = "polly: r+t, cancellable token";
    public const string FusedRetry = "fused: retry x2 -> success";
    public const string PollyRetry = "polly: retry x2 -> success";

    public const string RawSync = "raw callback (baseline)";

    public const string LibNoneSync = "lib: None (passthrough)";
    public const string LibTrivialSyncState = "lib: trivial, static+state";
    public const string LibTrivialSyncCallback = "lib: trivial, callback";
    public const string LibDefaultSyncState = "lib: Default, static+state";

    public const string NoneSync = "fused: None (passthrough)";
    public const string NoTimeoutSyncState = "fused: no timeout, static+state";
    public const string NoTimeoutSyncCallback = "fused: no timeout, callback";
    public const string DefaultSyncState = "fused: Default, static+state";
    public const string BreakerSyncState = "fused: +breaker, static+state";
    public const string PollyEmptySync = "polly: empty pipeline";
    public const string PollyRetryTimeoutSync = "polly: retry + timeout";

    public const string NewSource = "new CancellationTokenSource()";
    public const string NewSourceCancelAfter = "new CTS + CancelAfter";
    public const string LinkedCancellable = "CreateLinked(cancellable)";
    public const string LinkedNone = "CreateLinked(None)";
    public const string LinkedTwoCancellable = "CreateLinked(two cancellable)";
    public const string PooledSource = "pooled CTS: CancelAfter+TryReset";
    public const string DelayThenCancel = "Task.Delay(30s) then cancel";
}

[CollectionDefinition(Name)]
public sealed class BaselineCollection : ICollectionFixture<BaselineFixture>
{
    public const string Name = "baseline";
}
