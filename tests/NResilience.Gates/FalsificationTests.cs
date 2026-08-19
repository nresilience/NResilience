using System.Globalization;
using Xunit;

namespace NResilience.Gates;

/// <summary>
/// Phase 0a is not benchmarking infrastructure. It is the falsification test for the whole
/// design, and this class is where that test lives.
///
/// The claim under test: fusing the pipeline into one async frame substantially cuts allocation
/// on the suspending path — the path every real I/O call takes. Both sides are measured in this
/// process, on this harness, against the same un-wrapped callback, so no number here is obtained
/// by subtracting across two harnesses. That subtraction is the failure mode the design document
/// criticises, and it produced the 4-8x figure these tests exist to check.
/// </summary>
[Collection(BaselineCollection.Name)]
public sealed class FalsificationTests
{
    private readonly BaselineFixture _baseline;
    private readonly ITestOutputHelper _output;

    public FalsificationTests(BaselineFixture baseline, ITestOutputHelper output)
    {
        _baseline = baseline;
        _output = output;
    }

    /// <summary>
    /// The load-bearing comparison: a policy a real caller would actually configure — retry,
    /// attempt timeout, deadline, classification, budget — against Polly's equivalent.
    /// </summary>
    [Fact]
    public void Fusing_the_loop_substantially_beats_a_composed_pipeline_for_a_realistic_policy()
    {
        double fused = _baseline.SuspendingOverhead(Baseline.RealDefault);
        double polly = _baseline.SuspendingOverhead(Baseline.PollyRetryTimeout);
        double ratio = polly / fused;

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"realistic policy: fused {fused:0.0} B/op vs polly {polly:0.0} B/op = {ratio:0.00}x (gate: >= {Budgets.MinimumOverheadRatioVersusPolly:0.0}x)"));

        Assert.True(
            ratio >= Budgets.MinimumOverheadRatioVersusPolly,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 The fused-frame advantage has fallen to {ratio:0.00}x for a realistic policy
                 (fused {fused:0.0} B/op, Polly {polly:0.0} B/op above an identical raw callback),
                 against a gate of {Budgets.MinimumOverheadRatioVersusPolly:0.0}x.

                 This is the falsification condition described in plans/nresilience-design-v3.md:
                 below this ratio the argument for collapsing composition no longer pays for the
                 flexibility it gives up, and the architecture needs revisiting rather than the
                 gate needing relaxing.
                 """));
    }

    /// <summary>
    /// The trivial-policy comparison, which the design predicted at 2-3x and which measurement
    /// does not support. It is gated at parity so a regression that made the fused loop worse
    /// than a composed pipeline doing nothing would still fail the build.
    /// </summary>
    [Fact]
    public void Fusing_the_loop_is_no_worse_than_a_composed_pipeline_for_a_trivial_policy()
    {
        double fused = _baseline.SuspendingOverhead(Baseline.RealNoLogNoTimeout);
        double polly = _baseline.SuspendingOverhead(Baseline.PollyEmpty);
        double ratio = polly / fused;

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"trivial policy: fused {fused:0.0} B/op vs polly {polly:0.0} B/op = {ratio:0.00}x (gate: >= {Budgets.MinimumTrivialRatioVersusPolly:0.0}x)"));

        Assert.True(
            ratio >= Budgets.MinimumTrivialRatioVersusPolly,
            string.Create(
                CultureInfo.InvariantCulture,
                $"A trivial fused policy now allocates more ({fused:0.0} B/op) than Polly's empty pipeline ({polly:0.0} B/op)."));
    }

    [Fact]
    public void Fusing_the_loop_beats_a_composed_pipeline_when_retries_actually_happen()
    {
        double fused = _baseline.SuspendingBytes(Baseline.FusedRetry);
        double polly = _baseline.SuspendingBytes(Baseline.PollyRetry);

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"retry x2: fused {fused:0.0} B/op vs polly {polly:0.0} B/op = {polly / fused:0.00}x"));

        Assert.True(
            fused < polly,
            string.Create(CultureInfo.InvariantCulture, $"Retrying twice now costs {fused:0.0} B/op against Polly's {polly:0.0} B/op."));
    }

    /// <summary>
    /// Not a gate on this design — a check that the harness is credible. Appendix B of the design
    /// document reports Polly's suspending-path overhead as 312 B for an empty pipeline and
    /// 1328 B for retry+timeout, measured elsewhere. If this harness reproduces those figures,
    /// the numbers it produces for the fused loop can be trusted; if it does not, nothing else
    /// here means anything.
    /// </summary>
    [Fact]
    public void The_harness_reproduces_the_published_polly_baseline()
    {
        double empty = _baseline.SuspendingOverhead(Baseline.PollyEmpty);
        double retryTimeout = _baseline.SuspendingOverhead(Baseline.PollyRetryTimeout);

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"polly empty: {empty:0.0} B/op (Appendix B: 312 B); polly retry+timeout: {retryTimeout:0.0} B/op (Appendix B: 1328 B)"));

        Assert.InRange(empty, 250, 400);
        Assert.InRange(retryTimeout, 1_100, 1_600);
    }
}
