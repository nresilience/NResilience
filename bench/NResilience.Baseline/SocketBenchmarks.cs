using NBenchmark.Attributes;
using NResilience.Probes;
using NResilience.Probes.Polly;
using Polly;

namespace NResilience.Baseline;

/// <summary>
/// Performs the same comparison over a real loopback TCP round trip. This benchmark is
/// tagged <c>socket</c> for filtering; it serves as a cross-check for the <c>Task.Yield</c>
/// arms rather than a trend line, because socket timings are noisier than yield arms.
/// </summary>
[BenchmarkCategory("socket")]
public class SocketBenchmarks
{
    private LoopbackEcho _echo = null!;
    private ResiliencePipeline _polly = null!;
    private Func<CancellationToken, Task<int>> _callback = null!;
    private Func<CancellationToken, ValueTask<int>> _pollyCallback = null!;

    // Setup and teardown are synchronous because the harness binds them as void delegates.
    // Blocking here is outside the measured region, so it costs nothing that gets reported.
    [BenchmarkSetup]
    public void Setup()
    {
        _echo = LoopbackEcho.StartAsync().GetAwaiter().GetResult();
        _polly = PollyScenarios.BuildRetryTimeout();
        _callback = _echo.RoundTripAsync;
        _pollyCallback = ct => new ValueTask<int>(_echo.RoundTripAsync(ct));
    }

    [BenchmarkTeardown]
    public void Teardown() => _echo.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark(Baseline = true, Description = "socket: raw round trip")]
    public Task<int> Raw() => _echo.RoundTripAsync(CancellationToken.None);

    [Benchmark(Description = "socket: lib, no timeout")]
    public ValueTask<int> LibNoTimeout() => ShippingScenarios.Trivial.RunAsync(_callback);

    [Benchmark(Description = "socket: lib, Default")]
    public ValueTask<int> LibDefault() => Resilience.Default.RunAsync(_callback);

    [Benchmark(Description = "socket: polly, retry + timeout")]
    public ValueTask<int> PollyRetryTimeout() => _polly.ExecuteAsync(_pollyCallback, CancellationToken.None);
}
