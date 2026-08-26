using System.Globalization;
using System.Text;
using NResilience.Probes;
using NResilience.Probes.Polly;
using Xunit;

namespace NResilience.Gates;

/// <summary>
///     Cross-checks the <c>Task.Yield</c> gate against real I/O.
///     The gate suspends on <c>Task.Yield</c> because it needs determinism. The design's argument is
///     about the path real I/O takes. This test runs the same arms over a loopback TCP round trip
///     and asserts only that the two agree about the <i>ordering and rough magnitude</i> of the
///     fused-versus-composed gap - not about the absolute bytes, which a socket cannot produce
///     repeatably enough to gate on.
///     If this ever disagrees with the gate, the gate is measuring an artefact and the design's
///     central number is not trustworthy. That is worth one slow test.
///     The yield gate structurally cannot see one thing - that giving the callback a <i>cancellable</i>
///     token costs 208 B over real I/O against 65 B over <c>Task.Yield</c> - and that finding is the
///     reason this test exists rather than being a formality.
///     The comparison is only valid while the round trip actually suspends, so the sweep asserts
///     that before it asserts the ratio. See <see cref="LoopbackEcho.RoundTripAsync" />.
/// </summary>
[Collection(BaselineCollection.Name)]
public sealed class SocketCrossCheckTests(ITestOutputHelper output)
{
    /// <summary>
    ///     Windows does not produce a usable reading for this comparison, and the reason is Polly's
    ///     arm rather than anything in this repository. Measured on one Windows runner, in one job,
    ///     against a Default arm that came back at its usual 657-672 B on every platform: Polly read
    ///     1,130 B/op on net10 and 523 B/op on net8, against 1,432 B/op on Linux and macOS. The net8
    ///     figure puts a retrying, timing-out composed pipeline *below* the single fused executor it
    ///     wraps, which is not a result a machine can have; a pooled resilience context that hits on
    ///     one thread pattern and misses on another would explain a swing of that size and sign, and
    ///     Windows I/O completion ports resume continuations on a different thread pattern.
    ///     So the ratio is asserted where it can be measured. This costs Windows nothing it was
    ///     relying on: the deterministic <c>Task.Yield</c> gate and every budget in
    ///     <see cref="Budgets" /> still run on all three operating systems, and what is skipped here
    ///     is the cross-check that those are not measuring an artefact - a question one platform
    ///     answers, because the artefact it looks for would be in this repository's code and not in
    ///     the host's socket stack.
    /// </summary>
    public static bool OnWindows => OperatingSystem.IsWindows();

    [Fact(
        Skip = "Polly's arm does not measure repeatably on Windows - it read below the fused executor it wraps, which is not a result a machine can have. See OnWindows.",
        SkipWhen = nameof(OnWindows))]
    public async Task Real_socket_io_agrees_with_the_yield_gate()
    {
        await using var echo = await LoopbackEcho.StartAsync();
        echo.ResetCounters();

        var callback = echo.RoundTripAsync;
        Func<CancellationToken, ValueTask<int>> pollyCallback = ct => new ValueTask<int>(echo.RoundTripAsync(ct));

        var trivial = ShippingScenarios.Trivial;
        var full = Resilience.Default;
        var pollyRetryTimeout = PollyScenarios.BuildRetryTimeout();

        // A socket round trip is slower and noisier than a yield, so fewer iterations and more
        // repeats: the estimator takes the minimum, and more repeats give it more chances at a
        // clean one.
        const int Warmup = 500;
        const int Iterations = 500;
        const int Repeats = 9;

        var raw = await AllocationProbe.MeasureAsync(
            "socket: raw round trip", () => new ValueTask<int>(echo.RoundTripAsync(CancellationToken.None)),
            AllocationCounter.ProcessWide, Warmup, Iterations, Repeats);

        // Decomposition. The no-timeout arm hands the callback the caller's own (non-cancellable)
        // token, so the socket registers on nothing; the Default arm hands it a linked token that
        // both socket calls must register on. The gap between them is a cost the Task.Yield gate
        // structurally cannot see, because Task.Yield ignores the token it is given, and it is the
        // price of never handing user code a pooled source's token - an arrangement that tried to
        // avoid it measured worse.
        var fusedNoTimeoutResult = await AllocationProbe.MeasureAsync(
            "socket: lib, no timeout", () => trivial.RunAsync(callback),
            AllocationCounter.ProcessWide, Warmup, Iterations, Repeats);

        var fused = await AllocationProbe.MeasureAsync(
            "socket: lib, Default", () => full.RunAsync(callback),
            AllocationCounter.ProcessWide, Warmup, Iterations, Repeats);

        var polly = await AllocationProbe.MeasureAsync(
            "socket: polly, retry + timeout", () => pollyRetryTimeout.ExecuteAsync(pollyCallback, CancellationToken.None),
            AllocationCounter.ProcessWide, Warmup, Iterations, Repeats);

        var fusedOverhead = fused.BytesPerOperation - raw.BytesPerOperation;
        var pollyOverhead = polly.BytesPerOperation - raw.BytesPerOperation;
        var ratio = pollyOverhead / fusedOverhead;

        var report = new StringBuilder();
        report.AppendLine("LOOPBACK SOCKET CROSS-CHECK (process-wide counter)");
        report.Append(CultureInfo.InvariantCulture, $"  {raw}").AppendLine();

        report.Append(CultureInfo.InvariantCulture,
            $"  {fusedNoTimeoutResult}  (+{fusedNoTimeoutResult.BytesPerOperation - raw.BytesPerOperation:0.0} B)").AppendLine();

        report.Append(CultureInfo.InvariantCulture, $"  {fused}  (+{fusedOverhead:0.0} B)").AppendLine();

        report.Append(CultureInfo.InvariantCulture,
            $"  cancellable-token cost at the I/O layer: {fused.BytesPerOperation - fusedNoTimeoutResult.BytesPerOperation:0.0} B/op").AppendLine();

        report.Append(CultureInfo.InvariantCulture, $"  {polly}  (+{pollyOverhead:0.0} B)").AppendLine();
        report.Append(CultureInfo.InvariantCulture, $"  ratio: {ratio:0.00}x").AppendLine();

        report.Append(CultureInfo.InvariantCulture,
            $"  round trips: {echo.RoundTrips:N0}, of which suspended: {echo.RoundTrips - echo.SynchronousReceives:N0}").AppendLine();

        output.WriteLine(report.ToString());

        // Every arm wraps the same round trip, each in strictly more machinery than the last, so the
        // four readings have a known order regardless of platform: raw below the library without a
        // timeout, that below the library with one, and Polly's composed pipeline above all three.
        // An out-of-order sweep is not a slow arm or a noisy one, it is an instrument that has
        // stopped measuring what it names, and a ratio computed from it is arithmetic on garbage -
        // a Windows runner once put Polly 134 B *below* the library's own Default arm and the gate
        // dutifully reported it as a 0.77x design regression. Checked before the ratio so the
        // failure names the instrument, and printed with each arm's spread, because on a host this
        // cannot be reproduced on by hand the spread is the only evidence of how it happened.
        var ordered =
            raw.BytesPerOperation < fusedNoTimeoutResult.BytesPerOperation
            && fusedNoTimeoutResult.BytesPerOperation < fused.BytesPerOperation
            && fused.BytesPerOperation < polly.BytesPerOperation;

        Assert.True(
            ordered,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The arms came back out of order (raw {raw.BytesPerOperation:0.0} < no-timeout {fusedNoTimeoutResult.BytesPerOperation:0.0} < Default {fused.BytesPerOperation:0.0} < Polly {polly.BytesPerOperation:0.0} does not hold), so this host did not produce a usable measurement and its {ratio:0.00}x means nothing. Per-repeat spreads: raw {raw.Spread:0.0} B, no-timeout {fusedNoTimeoutResult.Spread:0.0} B, Default {fused.Spread:0.0} B, Polly {polly.Spread:0.0} B."));

        // Asserted before the ratio, because it decides whether the ratio means anything. A round
        // trip whose receive completed synchronously never allocated a state machine, and the arms
        // stop being comparable: the executor pays for its per-attempt linked source either way,
        // while a composed pipeline's per-attempt boxes simply vanish. A sweep with synchronous
        // receives in it measures the platform's completion timing, not the design, so it must
        // report that rather than a ratio verdict.
        Assert.True(
            echo.SynchronousReceives == 0,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{echo.SynchronousReceives:N0} of {echo.RoundTrips:N0} round trips completed their receive synchronously, so this sweep did not measure the suspending path and its {ratio:0.00}x is not comparable to the yield gate. The probe, not the executor, is what regressed."));

        Assert.True(fusedOverhead > 0, "The fused executor should cost something over a raw socket round trip.");

        Assert.True(
            ratio >= Budgets.MinimumSocketRatioVersusPolly,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Over a real socket the fused advantage is {ratio:0.00}x (fused +{fusedOverhead:0.0} B, Polly +{pollyOverhead:0.0} B), below the {Budgets.MinimumSocketRatioVersusPolly:0.0}x floor. The gate may be measuring an artefact."));
    }
}
