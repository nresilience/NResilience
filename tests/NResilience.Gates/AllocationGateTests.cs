using System.Globalization;
using Xunit;

namespace NResilience.Gates;

/// <summary>
/// The hard gate. Ordinary tests over allocation counter deltas on a warmed loop: deterministic,
/// fast, and they fail with a byte count.
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
        => AssertSyncOverhead(Baseline.NoneSync, Budgets.NoneSyncOverhead);

    [Fact]
    public void Passthrough_allocates_nothing_on_the_suspending_path()
        => AssertSuspendingOverhead(Baseline.NonePassthrough, Budgets.NoneSuspendingOverhead);

    [Fact]
    public void Full_policy_without_attempt_timeout_is_free_on_the_synchronous_path_with_static_lambda_and_state()
        => AssertSyncOverhead(Baseline.NoTimeoutSyncState, Budgets.FullPolicyNoTimeoutSyncOverhead);

    [Fact]
    public void Full_policy_without_attempt_timeout_is_free_on_the_synchronous_path_with_a_cached_callback()
        => AssertSyncOverhead(Baseline.NoTimeoutSyncCallback, Budgets.FullPolicyNoTimeoutSyncOverhead);

    /// <summary>
    /// Guards the documented exception to the zero-allocation sync claim. It is gated so that the
    /// cost stays one linked source and does not quietly become two.
    /// </summary>
    [Fact]
    public void Full_policy_with_attempt_timeout_costs_one_linked_source_on_the_synchronous_path()
        => AssertSyncOverhead(Baseline.DefaultSyncState, Budgets.FullPolicyWithTimeoutSyncOverhead);

    [Fact]
    public void Full_policy_with_a_breaker_costs_no_more_than_without_one_on_the_synchronous_path()
        => AssertSyncOverhead(Baseline.BreakerSyncState, Budgets.FullPolicyWithTimeoutSyncOverhead);

    [Fact]
    public void Real_loop_without_a_timeout_source_stays_within_one_frame_budget()
        => AssertSuspendingOverhead(Baseline.RealNoTimeout, Budgets.RealLoopNoTimeoutOverhead);

    [Fact]
    public void Real_loop_with_deadline_attempt_timeout_and_budget_stays_within_budget()
        => AssertSuspendingOverhead(Baseline.RealDefault, Budgets.RealLoopDefaultOverhead);

    [Fact]
    public void A_breaker_adds_no_state_across_the_await()
        => AssertSuspendingOverhead(Baseline.RealBreaker, Budgets.RealLoopWithBreakerOverhead);

    /// <summary>
    /// The inline attempt log is the largest single contributor to the state-machine box, and it
    /// is paid on the happy path by every suspending call. Its price is measured by running the
    /// identical loop with the log removed, so this asserts a difference rather than a total.
    /// </summary>
    [Fact]
    public void The_inline_attempt_log_costs_what_its_capacity_says_it_costs()
    {
        double withLog = _baseline.SuspendingOverhead(Baseline.RealNoTimeout);
        double withoutLog = _baseline.SuspendingOverhead(Baseline.RealNoLogNoTimeout);
        double cost = withLog - withoutLog;

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"inline attempt log: {cost:0.0} B/op (with {withLog:0.0}, without {withoutLog:0.0}); budget {Budgets.InlineAttemptLogCost:0} B"));

        Assert.True(
            cost <= Budgets.InlineAttemptLogCost + Budgets.SuspendingNoiseFloor,
            string.Create(CultureInfo.InvariantCulture, $"The inline attempt log now costs {cost:0.0} B/op against a budget of {Budgets.InlineAttemptLogCost:0} B/op."));
    }

    [Fact]
    public void Retrying_twice_stays_within_the_ceiling()
    {
        double actual = _baseline.SuspendingBytes(Baseline.FusedRetry);

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Baseline.FusedRetry}: {actual:0.0} B/op; ceiling {Budgets.RetryTwiceCeiling:0} B"));

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
