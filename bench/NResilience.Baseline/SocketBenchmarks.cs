using NBenchmark.Attributes;
using NResilience.Probes;
using NResilience.Probes.Polly;
using Polly;

namespace NResilience.Baseline;

/// <summary>
/// The same comparison over a real loopback TCP round trip. Tagged <c>socket</c> so it can be
/// filtered in or out; its value is as a cross-check on the <c>Task.Yield</c> arms rather than
/// as a trend line, because socket timings carry noise the yield arms do not.
/// </summary>
[BenchmarkCategory("socket")]
public class SocketBenchmarks
{
    private LoopbackEcho _echo = null!;
    private FusedExecutor _fused = null!;
    private ResiliencePipeline _polly = null!;
    private Func<CancellationToken, Task<int>> _callback = null!;
    private Func<CancellationToken, ValueTask<int>> _pollyCallback = null!;

    // Setup and teardown are synchronous because the harness binds them as void delegates.
    // Blocking here is outside the measured region, so it costs nothing that gets reported.
    [BenchmarkSetup]
    public void Setup()
    {
        _echo = LoopbackEcho.StartAsync().GetAwaiter().GetResult();
        _fused = new FusedExecutor(FusedPolicy.Default);
        _polly = PollyScenarios.BuildRetryTimeout();
        _callback = _echo.RoundTripAsync;
        _pollyCallback = ct => new ValueTask<int>(_echo.RoundTripAsync(ct));
    }

    [BenchmarkTeardown]
    public void Teardown() => _echo.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark(Baseline = true, Description = "socket: raw round trip")]
    public Task<int> Raw() => _echo.RoundTripAsync(CancellationToken.None);

    [Benchmark(Description = "socket: fused, no timeout")]
    public ValueTask<int> FusedNoTimeout() => Scenarios.NoTimeout.RunAsync(_callback);

    [Benchmark(Description = "socket: fused, Default")]
    public ValueTask<int> FusedDefault() => _fused.RunAsync(_callback);

    [Benchmark(Description = "socket: polly, retry + timeout")]
    public ValueTask<int> PollyRetryTimeout() => _polly.ExecuteAsync(_pollyCallback, CancellationToken.None);
}
