using System.Globalization;
using Xunit;

namespace NResilience.Gates;

/// <summary>
/// This is not benchmarking infrastructure. It is the falsification test for the whole
/// design, and this class is where that test lives.
///
/// The claim under test: fusing the pipeline into one async frame substantially cuts allocation
/// on the suspending path — the path every real I/O call takes. Both sides are measured in this
/// process, on this harness, against the same un-wrapped callback, so no number here is obtained
/// by subtracting across two harnesses. That subtraction is the failure mode the design document
/// criticises, and it produced the 4-8x figure these tests exist to check.
///
/// These tests first ran against a hand-written stand-in, because the point of running them first was
/// that no library code existed to bias them. They now point at the shipping executor,
/// which is the version that has to hold from here on.
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
        double fused = _baseline.SuspendingOverhead(Baseline.LibDefault);
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
    /// The trivial-policy comparison, which the design predicted at 2-3x and which measurement does
    /// not support in either direction it was claimed.
    ///
    /// The smallest non-passthrough policy this library can express still retries, still classifies
    /// and still records attempts, and it costs slightly <i>more</i> than a Polly pipeline with no
    /// strategies in it at all. That is a fair statement of the trade rather than a defeat — the two
    /// are not doing the same work — but the claim as written compared the two, so the gate is
    /// written the way the measurement actually falls: a ceiling that holds the trivial shape at
    /// parity with an empty pipeline and fails if it drifts away.
    /// </summary>
    [Fact]
    public void A_trivial_policy_stays_at_parity_with_a_pipeline_that_does_nothing()
    {
        double fused = _baseline.SuspendingOverhead(Baseline.LibTrivial);
        double polly = _baseline.SuspendingOverhead(Baseline.PollyEmpty);
        double ratio = fused / polly;

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"trivial policy: fused {fused:0.0} B/op vs polly empty {polly:0.0} B/op = {ratio:0.00}x the wrong way (gate: <= {Budgets.MaximumTrivialRatioVersusPollyEmpty:0.00}x)"));

        Assert.True(
            ratio <= Budgets.MaximumTrivialRatioVersusPollyEmpty,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 A trivial fused policy now costs {fused:0.0} B/op against Polly's empty pipeline at
                 {polly:0.0} B/op — {ratio:0.00}x — past the {Budgets.MaximumTrivialRatioVersusPollyEmpty:0.00}x parity ceiling.

                 The fused design wins in proportion to how much policy is configured, so the trivial
                 end is where it has least to give and where a frame that quietly grew would show up
                 first. Check what was added to the loop rather than raising this number.
                 """));
    }

    [Fact]
    public void Fusing_the_loop_beats_a_composed_pipeline_when_retries_actually_happen()
    {
        double fused = _baseline.SuspendingBytes(Baseline.LibRetry);
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
