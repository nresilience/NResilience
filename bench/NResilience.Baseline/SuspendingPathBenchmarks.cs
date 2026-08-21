using NResilience.Probes;
using NResilience.Probes.Polly;
using NBenchmark.Attributes;

namespace NResilience.Baseline;

/// <summary>
/// The suspending path is the path every real I/O call takes and the only one where the
/// architectural claim can be tested. The raw unwrapped callback is the baseline, so every
/// ratio the reporter prints is against an identical amount of real work.
///
/// Latency is published rather than gated. Shared CI runners are noisy enough that a latency gate
/// is either too loose to catch issues or too tight and flakes weekly; flaky gates are usually
/// disabled within a month. The build enforces allocation numbers; these benchmarks are
/// for spotting trends.
/// </summary>
[BenchmarkCategory("suspending")]
public class SuspendingPathBenchmarks
{
    [Benchmark(Baseline = true, Description = "raw callback, no wrapper")]
    public ValueTask<int> Raw() => Scenarios.RawSuspending();

    [Benchmark(Description = "lib: None (passthrough)")]
    public ValueTask<int> LibNone() => ShippingScenarios.NoneSuspending();

    [Benchmark(Description = "lib: trivial (no bounds)")]
    public ValueTask<int> LibTrivial() => ShippingScenarios.TrivialSuspending();

    [Benchmark(Description = "lib: Default")]
    public ValueTask<int> LibDefault() => ShippingScenarios.DefaultSuspending();

    [Benchmark(Description = "lib: TryRunAsync, Default")]
    public ValueTask<CallResult<int>> LibTryRun() => ShippingScenarios.TryRunDefaultSuspending();

    [Benchmark(Description = "fused: lean loop (toy)")]
    public ValueTask<int> FusedLean() => Scenarios.LeanSuspending();

    [Benchmark(Description = "fused: real loop, Default")]
    public ValueTask<int> FusedDefault() => Scenarios.FusedDefaultSuspending();

    [Benchmark(Description = "polly: empty pipeline")]
    public ValueTask<int> PollyEmpty() => PollyScenarios.EmptySuspending();

    [Benchmark(Description = "polly: retry + timeout")]
    public ValueTask<int> PollyRetryTimeout() => PollyScenarios.RetryTimeoutSuspending();
}

/// <summary>
/// The synchronous fast path. Both libraries reach zero overhead, making this a baseline
/// requirement rather than a differentiator - but a regression to non-zero is worth seeing early.
/// </summary>
[BenchmarkCategory("sync")]
public class SynchronousPathBenchmarks
{
    [Benchmark(Baseline = true, Description = "raw callback, no wrapper")]
    public ValueTask<int> Raw() => Scenarios.RawSync();

    [Benchmark(Description = "lib: None (passthrough)")]
    public ValueTask<int> LibNone() => ShippingScenarios.NoneSync();

    [Benchmark(Description = "lib: trivial, static+state")]
    public ValueTask<int> LibTrivialStatic() => ShippingScenarios.TrivialSyncState();

    [Benchmark(Description = "lib: Default, static+state")]
    public ValueTask<int> LibDefaultStatic() => ShippingScenarios.DefaultSyncState();

    [Benchmark(Description = "polly: empty pipeline")]
    public ValueTask<int> PollyEmpty() => PollyScenarios.EmptySync();

    [Benchmark(Description = "polly: retry + timeout")]
    public ValueTask<int> PollyRetryTimeout() => PollyScenarios.RetryTimeoutSync();
}

/// <summary>
/// Benchmarks retry, where the composed pipeline incurs its per-layer cost once per attempt
/// rather than once per call. This scenario uses two transient failures followed by a success,
/// with backoff disabled on both sides.
/// </summary>
[BenchmarkCategory("retry")]
public class RetryBenchmarks
{
    private readonly ShippingScenarios.RetryArm _lib = ShippingScenarios.BuildRetry();
    private readonly PollyScenarios.PollyRetryArm _polly = PollyScenarios.BuildRetryArm();

    [BenchmarkIterationSetup]
    public void Reset()
    {
        _lib.Reset();
        _polly.Reset();
    }

    [Benchmark(Baseline = true, Description = "lib: retry x2 -> success")]
    public ValueTask<int> Lib() => _lib.RunAsync();

    [Benchmark(Description = "polly: retry x2 -> success")]
    public ValueTask<int> Polly() => _polly.RunAsync();
}
