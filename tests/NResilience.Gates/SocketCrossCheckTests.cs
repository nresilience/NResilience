using System.Globalization;
using System.Text;
using NResilience.Probes;
using NResilience.Probes.Polly;
using Polly;
using Xunit;

namespace NResilience.Gates;

/// <summary>
/// Cross-checks the <c>Task.Yield</c> gate against real I/O.
///
/// The gate suspends on <c>Task.Yield</c> because it needs determinism. The design's argument is
/// about the path real I/O takes. This test runs the same arms over a loopback TCP round trip
/// and asserts only that the two agree about the <i>ordering and rough magnitude</i> of the
/// fused-versus-composed gap — not about the absolute bytes, which a socket cannot produce
/// repeatably enough to gate on.
///
/// If this ever disagrees with the gate, the gate is measuring an artefact and the design's
/// central number is not trustworthy. That is worth one slow test.
///
/// The yield gate structurally cannot see one thing — that giving the callback a <i>cancellable</i>
/// token costs 208 B over real I/O against 65 B over <c>Task.Yield</c> — and that finding is the
/// reason this test exists rather than being a formality.
/// </summary>
[Collection(BaselineCollection.Name)]
public sealed class SocketCrossCheckTests
{
    private readonly ITestOutputHelper _output;

    public SocketCrossCheckTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Real_socket_io_agrees_with_the_yield_gate()
    {
        await using LoopbackEcho echo = await LoopbackEcho.StartAsync();

        Func<CancellationToken, Task<int>> callback = echo.RoundTripAsync;
        Func<CancellationToken, ValueTask<int>> pollyCallback = ct => new ValueTask<int>(echo.RoundTripAsync(ct));

        Resilience trivial = ShippingScenarios.Trivial;
        Resilience full = Resilience.Default;
        ResiliencePipeline pollyRetryTimeout = PollyScenarios.BuildRetryTimeout();

        // A socket round trip is slower and noisier than a yield, so fewer iterations and more
        // repeats: the estimator takes the minimum, and more repeats give it more chances at a
        // clean one.
        const int Warmup = 500;
        const int Iterations = 500;
        const int Repeats = 9;

        AllocationMeasurement raw = await AllocationProbe.MeasureAsync(
            "socket: raw round trip", () => new ValueTask<int>(echo.RoundTripAsync(CancellationToken.None)),
            AllocationCounter.ProcessWide, Warmup, Iterations, Repeats);

        // Decomposition. The no-timeout arm hands the callback the caller's own (non-cancellable)
        // token, so the socket registers on nothing; the Default arm hands it a linked token that
        // both socket calls must register on. The gap between them is a cost the Task.Yield gate
        // structurally cannot see, because Task.Yield ignores the token it is given, and it is the
        // price of never handing user code a pooled source's token - an arrangement that tried to
        // avoid it measured worse.
        AllocationMeasurement fusedNoTimeoutResult = await AllocationProbe.MeasureAsync(
            "socket: lib, no timeout", () => trivial.RunAsync(callback),
            AllocationCounter.ProcessWide, Warmup, Iterations, Repeats);

        AllocationMeasurement fused = await AllocationProbe.MeasureAsync(
            "socket: lib, Default", () => full.RunAsync(callback),
            AllocationCounter.ProcessWide, Warmup, Iterations, Repeats);

        AllocationMeasurement polly = await AllocationProbe.MeasureAsync(
            "socket: polly, retry + timeout", () => pollyRetryTimeout.ExecuteAsync(pollyCallback, CancellationToken.None),
            AllocationCounter.ProcessWide, Warmup, Iterations, Repeats);

        double fusedOverhead = fused.BytesPerOperation - raw.BytesPerOperation;
        double pollyOverhead = polly.BytesPerOperation - raw.BytesPerOperation;
        double ratio = pollyOverhead / fusedOverhead;

        var report = new StringBuilder();
        report.AppendLine("LOOPBACK SOCKET CROSS-CHECK (process-wide counter)");
        report.Append(CultureInfo.InvariantCulture, $"  {raw}").AppendLine();
        report.Append(CultureInfo.InvariantCulture, $"  {fusedNoTimeoutResult}  (+{fusedNoTimeoutResult.BytesPerOperation - raw.BytesPerOperation:0.0} B)").AppendLine();
        report.Append(CultureInfo.InvariantCulture, $"  {fused}  (+{fusedOverhead:0.0} B)").AppendLine();
        report.Append(CultureInfo.InvariantCulture, $"  cancellable-token cost at the I/O layer: {fused.BytesPerOperation - fusedNoTimeoutResult.BytesPerOperation:0.0} B/op").AppendLine();
        report.Append(CultureInfo.InvariantCulture, $"  {polly}  (+{pollyOverhead:0.0} B)").AppendLine();
        report.Append(CultureInfo.InvariantCulture, $"  ratio: {ratio:0.00}x").AppendLine();
        _output.WriteLine(report.ToString());

        Assert.True(fusedOverhead > 0, "The fused executor should cost something over a raw socket round trip.");
        Assert.True(
            ratio >= Budgets.MinimumSocketRatioVersusPolly,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Over a real socket the fused advantage is {ratio:0.00}x (fused +{fusedOverhead:0.0} B, Polly +{pollyOverhead:0.0} B), below the {Budgets.MinimumSocketRatioVersusPolly:0.0}x floor. The gate may be measuring an artefact."));
    }
}
