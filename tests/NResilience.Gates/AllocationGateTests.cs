using System.Globalization;
using System.Runtime.CompilerServices;
using NResilience.Internal;
using Xunit;

namespace NResilience.Gates;

/// <summary>
/// The hard gate. Ordinary tests over allocation counter deltas on a warmed loop: deterministic,
/// fast, and they fail with a byte count.
///
/// <para>
/// Phase 0b re-pointed every assertion here from the Phase 0a stand-in loop to the shipping
/// <see cref="Resilience"/> executor. The stand-in is still measured in the same sweep, and one
/// gate below compares the two: if the real loop ever becomes more expensive than the hand-written
/// floor Phase 0a established, that is the design's central mechanism failing, and it should fail
/// a build rather than be inferred from a document.
/// </para>
/// </summary>
[Collection(BaselineCollection.Name)]
public sealed class AllocationGateTests
{
    private readonly BaselineFixture _baseline;
    private readonly ITestOutputHelper _output;

    public AllocationGateTests(BaselineFixture baseline, ITestOutputHelper output)
    {
        _baseline = baseline;
        _output = output;
    }

    [Fact]
    public void Passthrough_allocates_nothing_on_the_synchronous_path()
        => AssertSyncOverhead(Baseline.LibNoneSync, Budgets.NoneSyncOverhead);

    [Fact]
    public void Passthrough_allocates_nothing_on_the_suspending_path()
        => AssertSuspendingOverhead(Baseline.LibNone, Budgets.NoneSuspendingOverhead);

    [Fact]
    public void Full_policy_without_attempt_timeout_is_free_on_the_synchronous_path_with_static_lambda_and_state()
        => AssertSyncOverhead(Baseline.LibTrivialSyncState, Budgets.FullPolicyNoTimeoutSyncOverhead);

    [Fact]
    public void Full_policy_without_attempt_timeout_is_free_on_the_synchronous_path_with_a_cached_callback()
        => AssertSyncOverhead(Baseline.LibTrivialSyncCallback, Budgets.FullPolicyNoTimeoutSyncOverhead);

    /// <summary>
    /// Guards the documented exception to the zero-allocation sync claim. It is gated so that the
    /// cost stays one linked source and does not quietly become two.
    /// </summary>
    [Fact]
    public void Full_policy_with_attempt_timeout_costs_one_linked_source_on_the_synchronous_path()
        => AssertSyncOverhead(Baseline.LibDefaultSyncState, Budgets.FullPolicyWithTimeoutSyncOverhead);

    [Fact]
    public void The_trivial_policy_stays_within_one_frame_budget()
        => AssertSuspendingOverhead(Baseline.LibTrivial, Budgets.TrivialOverhead);

    [Fact]
    public void The_default_policy_stays_within_budget_on_the_suspending_path()
        => AssertSuspendingOverhead(Baseline.LibDefault, Budgets.DefaultOverhead);

    /// <summary>
    /// A caller token that can be cancelled and never is: the production shape, and the one the
    /// yield gate systematically under-prices. See <see cref="SocketCrossCheckTests"/> for what the
    /// same arrangement costs once real I/O registers on the token.
    /// </summary>
    [Fact]
    public void A_cancellable_caller_token_stays_within_budget()
        => AssertSuspendingOverhead(Baseline.LibDefaultCancellable, Budgets.DefaultCancellableOverhead);

    /// <summary>
    /// <c>TryRunAsync</c> always materialises the attempt log. That is a deliberate difference from
    /// the throwing form rather than an oversight, so it is budgeted rather than left unpriced.
    /// </summary>
    [Fact]
    public void Reporting_the_outcome_instead_of_throwing_stays_within_budget()
        => AssertSuspendingOverhead(Baseline.LibTryRunDefault, Budgets.TryRunDefaultOverhead);

    /// <summary>
    /// Phase 0b's own falsification test. Phase 0a measured a hand-written fused loop to establish
    /// what was achievable before any library existed; the shipping executor has to match it while
    /// doing strictly more — capturing a per-attempt exception, classifying results, and awaiting a
    /// pre-attempt hook. Both arms are measured in this sweep, so the comparison is not inferred.
    /// </summary>
    [Fact]
    public void The_shipping_executor_is_no_more_expensive_than_the_phase_0a_stand_in()
    {
        double shipping = _baseline.SuspendingOverhead(Baseline.LibDefault);
        double standIn = _baseline.SuspendingOverhead(Baseline.RealDefault);

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"shipping executor {shipping:0.0} B/op vs Phase 0a stand-in {standIn:0.0} B/op (allowance {Budgets.ShippingVersusStandInAllowance:0} B)"));

        Assert.True(
            shipping <= standIn + Budgets.ShippingVersusStandInAllowance,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 The shipping executor now costs {shipping:0.0} B/op above the raw callback against the
                 Phase 0a stand-in's {standIn:0.0} B/op. The stand-in is the floor Phase 0a established
                 for a fused loop doing less work than this one, so the real executor exceeding it means
                 the frame has grown state that the design does not account for.
                 """));
    }

    /// <summary>
    /// The inline attempt log is the largest single discretionary contributor to the state-machine
    /// box, and it is paid on the happy path by every suspending call whether or not anything ever
    /// fails.
    ///
    /// Phase 0a priced it by differencing two stand-in loops, one with the log removed. The shipping
    /// executor has no log-less variant — the log is not optional — so this asserts the layout that
    /// determines the cost: capacity times record size, both of which a change would have to move.
    /// Phase 1 shrank the record from 24 bytes to 16 and kept the capacity at 4, taking the log from
    /// 96 B of box to 64 B.
    /// </summary>
    [Fact]
    public void The_inline_attempt_log_costs_what_its_layout_says_it_costs()
    {
        int recordSize = Unsafe.SizeOf<AttemptRecord>();
        int capacity = AttemptBuffer.Capacity;
        int cost = recordSize * capacity;

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"inline attempt log: {capacity} x {recordSize} B = {cost} B of state-machine box; budget {Budgets.InlineAttemptLogCost:0} B"));

        Assert.Equal(16, recordSize);
        Assert.True(
            cost <= Budgets.InlineAttemptLogCost,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The inline attempt log now costs {cost} B of box ({capacity} x {recordSize} B) against a budget of {Budgets.InlineAttemptLogCost:0} B. Every byte of it is live across the attempt await and is paid by callers whose calls never fail."));
    }

    [Fact]
    public void Retrying_twice_stays_within_the_ceiling()
    {
        double actual = _baseline.SuspendingBytes(Baseline.LibRetry);

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Baseline.LibRetry}: {actual:0.0} B/op; ceiling {Budgets.RetryTwiceCeiling:0} B"));

        Assert.True(
            actual <= Budgets.RetryTwiceCeiling,
            string.Create(CultureInfo.InvariantCulture, $"Retry x2 now allocates {actual:0.0} B/op against a ceiling of {Budgets.RetryTwiceCeiling:0} B/op."));
    }

    private void AssertSyncOverhead(string arm, double budget)
    {
        double actual = _baseline.SyncOverhead(arm);

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{arm}: {_baseline.SyncBytes(arm):0.0} B/op total, {actual:0.0} B/op above the raw callback; budget {budget:0} B"));

        Assert.True(
            actual <= budget,
            string.Create(CultureInfo.InvariantCulture, $"'{arm}' now allocates {actual:0.0} B/op above the raw callback, against a budget of {budget:0} B/op."));
    }

    private void AssertSuspendingOverhead(string arm, double budget)
    {
        double actual = _baseline.SuspendingOverhead(arm);
        double ceiling = budget + Budgets.SuspendingNoiseFloor;

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{arm}: {_baseline.SuspendingBytes(arm):0.0} B/op total, {actual:0.0} B/op above the raw callback; budget {budget:0} B (+{Budgets.SuspendingNoiseFloor:0} B instrument floor)"));

        Assert.True(
            actual <= ceiling,
            string.Create(CultureInfo.InvariantCulture, $"'{arm}' now allocates {actual:0.0} B/op above the raw callback, against a budget of {budget:0} B/op."));
    }
}
