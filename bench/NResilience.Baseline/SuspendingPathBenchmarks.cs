using NResilience.Probes;
using NResilience.Probes.Polly;
using NBenchmark.Attributes;

namespace NResilience.Baseline;

/// <summary>
/// The suspending path — the path every real I/O call takes, and the only one where the
/// architectural claim can be tested. The raw un-wrapped callback is the baseline, so every
/// ratio the reporter prints is against an identical amount of real work.
///
/// Latency here is published, not gated. Shared CI runners are noisy enough that a latency gate
/// is either loose enough to catch nothing or tight enough to flake weekly, and a flaky gate
/// gets disabled within a month. The allocation numbers are what the build enforces; these are
/// for spotting trends.
/// </summary>
[BenchmarkCategory("suspending")]
public class SuspendingPathBenchmarks
{
    [Benchmark(Baseline = true, Description = "raw callback, no wrapper")]
    public ValueTask<int> Raw() => Scenarios.RawSuspending();

    [Benchmark(Description = "fused: None (passthrough)")]
    public ValueTask<int> FusedNone() => Scenarios.NoneSuspending();

    [Benchmark(Description = "fused: lean loop (toy)")]
    public ValueTask<int> FusedLean() => Scenarios.LeanSuspending();

    [Benchmark(Description = "fused: real loop, no attempt log")]
    public ValueTask<int> FusedNoLog() => Scenarios.FusedNoTimeoutNoLogSuspending();

    [Benchmark(Description = "fused: real loop, no timeout")]
    public ValueTask<int> FusedNoTimeout() => Scenarios.FusedNoTimeoutSuspending();

    [Benchmark(Description = "fused: real loop, Default")]
    public ValueTask<int> FusedDefault() => Scenarios.FusedDefaultSuspending();

    [Benchmark(Description = "fused: real loop, +breaker")]
    public ValueTask<int> FusedFull() => Scenarios.FusedFullSuspending();

    [Benchmark(Description = "polly: empty pipeline")]
    public ValueTask<int> PollyEmpty() => PollyScenarios.EmptySuspending();

    [Benchmark(Description = "polly: retry + timeout")]
    public ValueTask<int> PollyRetryTimeout() => PollyScenarios.RetryTimeoutSuspending();
}

/// <summary>
/// The synchronous fast path. Both libraries reach zero here, which is why it is table stakes
/// rather than a differentiator — but a regression to non-zero is worth seeing early.
/// </summary>
[BenchmarkCategory("sync")]
public class SynchronousPathBenchmarks
{
    [Benchmark(Baseline = true, Description = "raw callback, no wrapper")]
    public ValueTask<int> Raw() => Scenarios.RawSync();

    [Benchmark(Description = "fused: None (passthrough)")]
    public ValueTask<int> FusedNone() => Scenarios.NoneSync();

    [Benchmark(Description = "fused: no timeout, static+state")]
    public ValueTask<int> FusedStatic() => Scenarios.FusedNoTimeoutSyncState();

    [Benchmark(Description = "fused: Default, static+state")]
    public ValueTask<int> FusedDefaultStatic() => Scenarios.FusedDefaultSyncState();

    [Benchmark(Description = "polly: empty pipeline")]
    public ValueTask<int> PollyEmpty() => PollyScenarios.EmptySync();

    [Benchmark(Description = "polly: retry + timeout")]
    public ValueTask<int> PollyRetryTimeout() => PollyScenarios.RetryTimeoutSync();
}

/// <summary>
/// Retry, where the composed pipeline pays its per-layer cost once per attempt rather than once
/// per call. Two transient failures then a success, with backoff disabled on both sides.
/// </summary>
[BenchmarkCategory("retry")]
public class RetryBenchmarks
{
    private readonly Scenarios.RetryArm _fused = Scenarios.BuildFusedRetry();
    private readonly PollyScenarios.PollyRetryArm _polly = PollyScenarios.BuildRetryArm();

    [BenchmarkIterationSetup]
    public void Reset()
    {
        _fused.Reset();
        _polly.Reset();
    }

    [Benchmark(Baseline = true, Description = "fused: retry x2 -> success")]
    public ValueTask<int> Fused() => _fused.RunAsync();

    [Benchmark(Description = "polly: retry x2 -> success")]
    public ValueTask<int> Polly() => _polly.RunAsync();
}
