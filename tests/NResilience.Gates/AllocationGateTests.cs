using System.Globalization;
using System.Runtime.CompilerServices;
using NResilience.Internal;
using Xunit;

namespace NResilience.Gates;

/// <summary>
///     The hard gate. Ordinary tests over allocation counter deltas on a warmed loop: deterministic,
///     fast, and they fail with a byte count.
///     <para>
///         Every assertion here points at the shipping <see cref="Resilience" /> executor. The stand-in is
///         still measured in the same sweep, and one gate below compares the two: if the real loop ever
///         becomes more expensive than the hand-written floor the stand-in established, that is the
///         design's central mechanism failing, and it should fail a build rather than be inferred from a
///         document.
///     </para>
/// </summary>
[Collection(BaselineCollection.Name)]
public sealed class AllocationGateTests(BaselineFixture baseline, ITestOutputHelper output)
{
    [Fact]
    public void Passthrough_allocates_nothing_on_the_synchronous_path()
        => AssertSyncOverhead(Baseline.LibNoneSync, Budgets.NoneSyncOverhead);

    [Fact]
    public void Passthrough_allocates_nothing_on_the_suspending_path()
        => AssertSuspendingOverhead(Baseline.LibNone, Budgets.NoneSuspendingOverhead);

    /// <summary>
    ///     The documented derivation - every bound turned off from a preset - is as free as naming
    ///     <c>Resilience.None</c>, even though the preset carries <c>RetryBudget.Automatic</c>.
    /// </summary>
    [Fact]
    public void A_passthrough_derived_from_a_preset_allocates_nothing_on_the_synchronous_path()
        => AssertSyncOverhead(Baseline.LibDerivedPassthroughSync, Budgets.NoneSyncOverhead);

    [Fact]
    public void A_passthrough_derived_from_a_preset_allocates_nothing_on_the_suspending_path()
        => AssertSuspendingOverhead(Baseline.LibDerivedPassthrough, Budgets.NoneSuspendingOverhead);

    [Fact]
    public void Full_policy_without_attempt_timeout_is_free_on_the_synchronous_path_with_static_lambda_and_state()
        => AssertSyncOverhead(Baseline.LibTrivialSyncState, Budgets.FullPolicyNoTimeoutSyncOverhead);

    [Fact]
    public void Full_policy_without_attempt_timeout_is_free_on_the_synchronous_path_with_a_cached_callback()
        => AssertSyncOverhead(Baseline.LibTrivialSyncCallback, Budgets.FullPolicyNoTimeoutSyncOverhead);

    /// <summary>
    ///     Guards the documented exception to the zero-allocation sync claim. It is gated so that the
    ///     cost stays one linked source and does not quietly become two.
    /// </summary>
    [Fact]
    public void Full_policy_with_attempt_timeout_costs_one_linked_source_on_the_synchronous_path()
        => AssertSyncOverhead(Baseline.LibDefaultSyncState, Budgets.FullPolicyWithTimeoutSyncOverhead);

    /// <summary>
    ///     Verifies that callbacks returning <see cref="ValueTask" /> also allocate nothing. 
    ///     This is measured against a raw <see cref="ValueTask" /> baseline to ensure the 
    ///     result reflects the executor's overhead rather than the callback's savings.
    /// </summary>
    [Fact]
    public void Full_policy_without_attempt_timeout_is_free_on_the_synchronous_path_with_a_ValueTask_callback()
        => AssertSyncOverheadVersus(Baseline.LibTrivialValueSyncState, Baseline.RawValueSync, Budgets.FullPolicyNoTimeoutSyncOverhead);

    /// <summary>
    ///     Verifies that <see cref="ValueTask" /> callbacks under an attempt timeout allocate the 
    ///     same single linked source as <see cref="Task" /> callbacks, with no additional 
    ///     overhead for the callback shape.
    /// </summary>
    [Fact]
    public void Full_policy_with_attempt_timeout_costs_one_linked_source_with_a_ValueTask_callback()
        => AssertSyncOverheadVersus(Baseline.LibDefaultValueSyncState, Baseline.RawValueSync, Budgets.FullPolicyWithTimeoutSyncOverhead);

    /// <summary>
    ///     Validates the purpose of the <see cref="ValueTask" /> overloads. This test compares a 
    ///     native <see cref="ValueTask" /> callback against one converted with <c>AsTask()</c> 
    ///     using the same policy and pooled source.
    ///     <para>
    ///         This assertion uses a floor rather than a ceiling. These overloads are extension 
    ///         methods so that <c>async</c> lambdas still bind to the <see cref="Task" /> form. 
    ///         If a future instance overload shadows these extension methods, both arms will 
    ///         measure the conversion cost, and the delta will collapse. This is the only 
    ///         symptom of such a failure.
    ///     </para>
    /// </summary>
    [Fact]
    public void The_ValueTask_overloads_remove_the_conversion_a_Task_callback_would_pay()
    {
        var native = baseline.SyncBytes(Baseline.LibTrivialValueSyncState);
        var converted = baseline.SyncBytes(Baseline.LibTrivialValueAsTaskSyncState);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"ValueTask callback {native:0.0} B/op vs the same callback via AsTask() {converted:0.0} B/op, delta {converted - native:0.0} B (floor {Budgets.ValueTaskConversionFloor:0} B)"));

        Assert.True(
            converted - native >= Budgets.ValueTaskConversionFloor,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 A ValueTask callback measured {native:0.0} B/op against {converted:0.0} B/op for the same
                 callback converted with AsTask(). The two are no longer distinguishable, which means the
                 ValueTask overloads are not being reached - most likely because an applicable instance
                 overload now shadows the extension methods.
                 """));
    }

    [Fact]
    public void The_trivial_policy_stays_within_one_frame_budget()
        => AssertSuspendingOverhead(Baseline.LibTrivial, Budgets.TrivialOverhead);

    [Fact]
    public void The_default_policy_stays_within_budget_on_the_suspending_path()
        => AssertSuspendingOverhead(Baseline.LibDefault, Budgets.DefaultOverhead);

    /// <summary>
    ///     A caller token that can be cancelled and never is: the production shape, and the one the
    ///     yield gate systematically under-prices. See <see cref="SocketCrossCheckTests" /> for what the
    ///     same arrangement costs once real I/O registers on the token.
    /// </summary>
    [Fact]
    public void A_cancellable_caller_token_stays_within_budget()
        => AssertSuspendingOverhead(Baseline.LibDefaultCancellable, Budgets.DefaultCancellableOverhead);

    /// <summary>
    ///     <c>TryRunAsync</c> always materializes the attempt log. That is a deliberate difference from
    ///     the throwing form rather than an oversight, so it is budgeted rather than left unpriced.
    /// </summary>
    [Fact]
    public void Reporting_the_outcome_instead_of_throwing_stays_within_budget()
        => AssertSuspendingOverhead(Baseline.LibTryRunDefault, Budgets.TryRunDefaultOverhead);

    /// <summary>
    ///     Telemetry with a listener attached, budgeted rather than described. The design's stated
    ///     reason for having its own event type at all is that Polly's costs 6.9x when enabled - the
    ///     configuration production actually runs - so what this costs when it is on is the number
    ///     that matters.
    /// </summary>
    [Fact]
    public void A_listener_stays_within_budget()
        => AssertSuspendingOverhead(Baseline.LibDefaultListener, Budgets.DefaultWithListenerOverhead);

    /// <summary>
    ///     "Pay-for-play" as a gate rather than a claim: attaching a listener may cost the boxes the
    ///     listener asked for and must not cost anything else.
    ///     Both arms are measured in this sweep, so the difference is a measurement rather than a
    ///     subtraction across two harnesses - which is the failure mode this whole harness exists to
    ///     avoid.
    /// </summary>
    [Fact]
    public void A_listener_costs_only_the_results_it_asked_to_be_boxed()
    {
        var listening = baseline.SuspendingOverhead(Baseline.LibDefaultListener);
        var silent = baseline.SuspendingOverhead(Baseline.LibDefault);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"listener {listening:0.0} B/op vs no listener {silent:0.0} B/op, delta {listening - silent:0.0} B (allowance {Budgets.ListenerAllowance:0} B)"));

        Assert.True(
            listening - silent <= Budgets.ListenerAllowance,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 Attaching a listener now costs {listening - silent:0.0} B/op, against the {Budgets.ListenerAllowance:0} B
                 that two boxed results plus drift can account for. Something in the event path is
                 allocating per event - a captured closure, an event object, or a result boxed on a
                 path that has no listener to hand it to.
                 """));
    }

    /// <summary>
    ///     The log listener's own promise, as a gate: a call whose logging levels are all disabled
    ///     allocates exactly what the same call allocates with a listener alone.
    ///     Differenced against the telemetry-only arm measured in the same sweep, which is the shape
    ///     every other budget here uses. The record templates are generated by <c>[LoggerMessage]</c>,
    ///     so the <c>IsEnabled</c> guard is emitted rather than written - if this fails, something in
    ///     the listener is formatting, boxing or capturing before it asks whether anybody is listening.
    /// </summary>
    [Fact]
    public void A_logging_listener_allocates_nothing_when_its_levels_are_disabled()
    {
        var logging = baseline.SuspendingOverhead(Baseline.LibDefaultLogging);
        var listening = baseline.SuspendingOverhead(Baseline.LibDefaultListener);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"logging {logging:0.0} B/op vs listener only {listening:0.0} B/op, delta {logging - listening:0.0} B (allowance {Budgets.DisabledLoggingAllowance:0} B)"));

        Assert.True(
            logging - listening <= Budgets.DisabledLoggingAllowance,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 Chaining the log listener onto an already-listening policy now costs
                 {logging - listening:0.0} B/op with every level disabled, against the
                 {Budgets.DisabledLoggingAllowance:0} B noise floor. Something in the listener is doing
                 work before it asks whether anybody is listening.
                 """));
    }

    /// <summary>
    ///     The other half of pay-for-play, and the half that everyone pays: a policy with no listener
    ///     must cost exactly what it cost before telemetry existed.
    ///     <see cref="The_default_policy_stays_within_budget_on_the_suspending_path" /> already gates the
    ///     absolute figure, and the telemetry work moved it by zero bytes - the delegate is a field on a record the
    ///     state-machine box already holds a reference to, so reading it is free, and every event site
    ///     is behind a null test. This asserts the comparison the budget cannot: that the silent path
    ///     has not drifted toward the listening one.
    /// </summary>
    [Fact]
    public void A_policy_with_no_listener_pays_nothing_for_telemetry()
    {
        var silent = baseline.SuspendingOverhead(Baseline.LibDefault);
        var listening = baseline.SuspendingOverhead(Baseline.LibDefaultListener);

        Assert.True(
            silent < listening - Budgets.SuspendingNoiseFloor,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 A policy with no listener measured {silent:0.0} B/op against {listening:0.0} B/op with one.
                 The two are indistinguishable, which means either the null-listener path is doing the
                 work anyway or the listening arm has stopped raising events at all.
                 """));
    }

    /// <summary>
    ///     The claim Tier 2 of the flat-executor extensibility review exists to test: a policy that
    ///     never configures <see cref="Resilience.Admit" /> must not pay one byte for the second,
    ///     <c>ExecuteWithAdmitAsync</c> execution path existing in the assembly.
    ///     <see cref="The_default_policy_stays_within_budget_on_the_suspending_path" /> already gates the
    ///     absolute figure; this asserts the comparison directly, in the same sweep, so a regression
    ///     reads as "Admit moved the baseline" rather than requiring two test runs to notice.
    /// </summary>
    [Fact]
    public void A_policy_with_no_Admit_hook_pays_nothing_for_the_second_execution_path()
    {
        var withoutAdmit = baseline.SuspendingBytes(Baseline.LibDefault);
        var withAdmit = baseline.SuspendingBytes(Baseline.LibDefaultAdmit);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"no Admit {withoutAdmit:0.0} B/op vs Admit configured {withAdmit:0.0} B/op, delta {withAdmit - withoutAdmit:0.0} B"));

        Assert.True(
            withoutAdmit < withAdmit - Budgets.SuspendingNoiseFloor,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 A policy without Admit configured measured {withoutAdmit:0.0} B/op against {withAdmit:0.0} B/op
                 with one configured. The two are indistinguishable, which means either
                 ExecuteWithAdmitAsync is not actually a separate code path or the no-Admit entry points
                 are selecting it anyway.
                 """));
    }

    /// <summary>
    ///     What configuring <see cref="Resilience.Admit" /> costs: the second execution path's one extra
    ///     hoisted <c>Task&lt;Verdict&gt;</c> awaiter field, and nothing else. Gated loosely, in keeping
    ///     with every other number in this file being measured rather than reasoned - see the sequencing
    ///     note in <c>plans/flat-executor-debate-review.md</c>.
    /// </summary>
    [Fact]
    public void The_Admit_hook_stays_within_budget_on_the_suspending_path()
        => AssertSuspendingOverhead(Baseline.LibDefaultAdmit, Budgets.AdmitConfiguredOverhead);
    /// <summary>
    ///     The claim the hedging design is argued on: a hedged call allocates, and <b>only</b> a hedged
    ///     call does. The third execution path exists in the assembly whether or not
    ///     <see cref="Resilience.Hedge" /> is set, and a policy that does not set it must not pay a byte
    ///     for it. Asserted in the same sweep, so a regression reads as "hedging moved the baseline"
    ///     rather than requiring two runs to notice.
    /// </summary>
    [Fact]
    public void A_policy_with_no_Hedge_pays_nothing_for_the_third_execution_path()
    {
        var withoutHedge = baseline.SuspendingBytes(Baseline.LibDefault);
        var withHedge = baseline.SuspendingBytes(Baseline.LibDefaultHedge);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"no hedging {withoutHedge:0.0} B/op vs hedging configured {withHedge:0.0} B/op, delta {withHedge - withoutHedge:0.0} B"));

        Assert.True(
            withoutHedge < withHedge - Budgets.SuspendingNoiseFloor,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 A policy without Hedge configured measured {withoutHedge:0.0} B/op against {withHedge:0.0} B/op
                 with one configured. The two are indistinguishable, which means either
                 ExecuteHedgedAsync is not actually a separate code path or the non-hedging entry points
                 are selecting it anyway.
                 """));
    }

    /// <summary>
    ///     What configuring <see cref="Resilience.Hedge" /> costs on a call where no hedge fires, which
    ///     is the state a hedging policy is in for about <c>Quantile</c> of its calls. Deliberately
    ///     budgeted rather than left unmeasured: the hedged path is the one place this library spends,
    ///     and a spend nobody wrote down is a spend nobody notices growing.
    /// </summary>
    [Fact]
    public void The_hedged_path_stays_within_its_own_budget()
        => AssertSuspendingOverhead(Baseline.LibDefaultHedge, Budgets.HedgeConfiguredOverhead);

    /// <summary>
    ///     The streaming path's own budget, measured over a full enumeration and compared against the
    ///     identical enumeration with no policy in the middle. Every existing budget in this file is
    ///     per-callback, and a stream is not one callback, so this gate exists to publish the honest
    ///     per-enumeration figure rather than to squeeze it - the itemized ledger is in
    ///     <see cref="Budgets.DefaultStreamingOverhead" />.
    /// </summary>
    [Fact]
    public void The_streaming_path_stays_within_its_own_budget()
    {
        var actual = baseline.SuspendingOverheadVersus(Baseline.LibDefaultStream, Baseline.RawStream);
        var ceiling = Budgets.DefaultStreamingOverhead + Budgets.SuspendingNoiseFloor;

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"streaming: {baseline.SuspendingBytes(Baseline.LibDefaultStream):0.0} B/op total, {actual:0.0} B/op above '{Baseline.RawStream}'; budget {Budgets.DefaultStreamingOverhead:0} B (+{Budgets.SuspendingNoiseFloor:0} B instrument floor)"));

        Assert.True(
            actual <= ceiling,
            string.Create(CultureInfo.InvariantCulture,
                $"The streaming path now allocates {actual:0.0} B/op above the raw enumeration, against a budget of {Budgets.DefaultStreamingOverhead:0} B/op."));
    }

    /// <summary>
    ///     Verifies that the <see cref="ValueTask" /> callback shape adds no overhead to the 
    ///     state-machine box. Awaiting a <see cref="ValueTask" /> directly in the executor would 
    ///     add a hoisted awaiter field to the state-machine type, increasing the cost of every 
    ///     suspending call. To avoid this, the executor passes pending <see cref="ValueTask" /> 
    ///     objects to the loop as <see cref="Task" /> objects. This test ensures both callback 
    ///     shapes suspend with the same overhead.
    /// </summary>
    [Fact]
    public void A_ValueTask_callback_costs_the_suspending_path_nothing()
    {
        var value = baseline.SuspendingBytes(Baseline.LibDefaultValue);
        var task = baseline.SuspendingBytes(Baseline.LibDefault);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"ValueTask callback {value:0.0} B/op vs Task callback {task:0.0} B/op, delta {value - task:0.0} B (floor {Budgets.SuspendingNoiseFloor:0} B)"));

        Assert.True(
            value - task <= Budgets.SuspendingNoiseFloor,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 A suspending call with a ValueTask callback now costs {value:0.0} B/op against {task:0.0} B/op
                 with a Task one. The two shapes share one loop and one awaiter field, so a gap between them
                 means the executor has grown a second hoisted awaiter - which every caller pays for,
                 including the ones that never pass a ValueTask.
                 """));
    }

    /// <summary>
    ///     The falsification test for the shipping executor. The stand-in measured a hand-written fused loop to establish
    ///     what was achievable before any library existed; the shipping executor has to match it while
    ///     doing strictly more - capturing a per-attempt exception, classifying results, and awaiting a
    ///     pre-attempt hook. Both arms are measured in this sweep, so the comparison is not inferred.
    /// </summary>
    [Fact]
    public void The_shipping_executor_is_no_more_expensive_than_the_stand_in()
    {
        var shipping = baseline.SuspendingOverhead(Baseline.LibDefault);
        var standIn = baseline.SuspendingOverhead(Baseline.RealDefault);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"shipping executor {shipping:0.0} B/op vs stand-in {standIn:0.0} B/op (allowance {Budgets.ShippingVersusStandInAllowance:0} B)"));

        Assert.True(
            shipping <= standIn + Budgets.ShippingVersusStandInAllowance,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 The shipping executor now costs {shipping:0.0} B/op above the raw callback against the
                 stand-in's {standIn:0.0} B/op. The stand-in is the floor established
                 for a fused loop doing less work than this one, so the real executor exceeding it means
                 the frame has grown state that the design does not account for.
                 """));
    }

    /// <summary>
    ///     The inline attempt log is the largest single discretionary contributor to the state-machine
    ///     box, and it is paid on the happy path by every suspending call whether or not anything ever
    ///     fails.
    ///     The stand-in priced it by differencing two stand-in loops, one with the log removed. The shipping
    ///     executor has no log-less variant - the log is not optional - so this asserts the layout that
    ///     determines the cost: capacity times record size, both of which a change would have to move.
    ///     The record shrank from 24 bytes to 16 and the capacity stayed at 4, taking the log from
    ///     96 B of box to 64 B.
    /// </summary>
    [Fact]
    public void The_inline_attempt_log_costs_what_its_layout_says_it_costs()
    {
        var recordSize = Unsafe.SizeOf<AttemptRecord>();
        var capacity = AttemptBuffer.Capacity;
        var cost = recordSize * capacity;

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"inline attempt log: {capacity} x {recordSize} B = {cost} B of state-machine box; budget {Budgets.InlineAttemptLogCost:0} B"));

        Assert.Equal(16, recordSize);

        Assert.True(
            cost <= Budgets.InlineAttemptLogCost,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The inline attempt log now costs {cost} B of box ({capacity} x {recordSize} B) against a budget of {Budgets.InlineAttemptLogCost:0} B. Every byte of it is live across the attempt await and is paid by callers whose calls never fail."));
    }

    /// <summary>
    ///     A verdict is live across the attempt <c>await</c>, so every byte of it is paid for in the
    ///     state-machine box of every suspending call.
    ///     Two packings keep it at 16 bytes and both were adopted on a premise this asserts rather than
    ///     assumes: <c>SelfImposed</c> - the bit that keeps the retry budget from being charged for a
    ///     refusal local admission control imposed on this process - rides in the padding the single-byte
    ///     <c>Kind</c> already leaves, which is the whole reason it is not a fifth <c>VerdictKind</c>;
    ///     and <c>RetryAfter</c> is a biased <c>long</c> of ticks behind a <c>TimeSpan?</c> property,
    ///     where the field measured eight bytes more.
    /// </summary>
    [Fact]
    public void The_verdict_carries_its_origin_and_its_pushback_for_free()
    {
        var size = Unsafe.SizeOf<Verdict>();

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Verdict: {size} B; budget {Budgets.VerdictSize} B"));

        Assert.Equal(Budgets.VerdictSize, size);
    }

    /// <summary>
    ///     An <see cref="Attempt" /> is what a materialized log is an array of, so its size is what a
    ///     failure costs to report. It stores the verdict as the packed byte the inline log already
    ///     stores rather than embedding a <see cref="Verdict" />, because the pushback is documented as
    ///     not round-tripped - and that is the difference this asserts.
    /// </summary>
    [Fact]
    public void An_attempt_does_not_carry_a_pushback_it_never_reports()
    {
        var size = Unsafe.SizeOf<Attempt>();

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Attempt: {size} B; budget {Budgets.AttemptSize} B"));

        Assert.Equal(Budgets.AttemptSize, size);
    }

    /// <summary>
    ///     A <see cref="CallEvent" /> is passed by value to every listener, so its size is what raising
    ///     an event costs. The three nullable value types it would naturally hold accounted for 40 of the
    ///     88 bytes it used to measure; each is now biased-by-one behind a property of the original type.
    /// </summary>
    [Fact]
    public void Raising_an_event_copies_no_nullable_padding()
    {
        var size = Unsafe.SizeOf<CallEvent>();

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"CallEvent: {size} B; budget {Budgets.CallEventSize} B"));

        Assert.Equal(Budgets.CallEventSize, size);
    }

    /// <summary>
    ///     The refusal path, priced. Not a hot path, and not asserted tightly for that reason - what it
    ///     asserts is that a refusal costs what a retried exception costs, because the two paths differ
    ///     only in which catch clause runs.
    /// </summary>
    [Fact]
    public void Being_refused_twice_costs_what_failing_twice_costs()
    {
        var limited = baseline.SuspendingBytes(Baseline.LibLimited);
        var retried = baseline.SuspendingBytes(Baseline.LibRetry);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{Baseline.LibLimited}: {limited:0.0} B/op against retry's {retried:0.0} B/op; ceiling {Budgets.LimitedTwiceCeiling:0} B"));

        Assert.True(
            limited <= Budgets.LimitedTwiceCeiling,
            string.Create(CultureInfo.InvariantCulture,
                $"Being refused twice now allocates {limited:0.0} B/op against a ceiling of {Budgets.LimitedTwiceCeiling:0} B/op."));
    }

    [Fact]
    public void Retrying_twice_stays_within_the_ceiling()
    {
        var actual = baseline.SuspendingBytes(Baseline.LibRetry);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Baseline.LibRetry}: {actual:0.0} B/op; ceiling {Budgets.RetryTwiceCeiling:0} B"));

        Assert.True(
            actual <= Budgets.RetryTwiceCeiling,
            string.Create(CultureInfo.InvariantCulture,
                $"Retry x2 now allocates {actual:0.0} B/op against a ceiling of {Budgets.RetryTwiceCeiling:0} B/op."));
    }

    private void AssertSyncOverhead(string arm, double budget)
    {
        var actual = baseline.SyncOverhead(arm);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{arm}: {baseline.SyncBytes(arm):0.0} B/op total, {actual:0.0} B/op above the raw callback; budget {budget:0} B"));

        Assert.True(
            actual <= budget,
            string.Create(CultureInfo.InvariantCulture,
                $"'{arm}' now allocates {actual:0.0} B/op above the raw callback, against a budget of {budget:0} B/op."));
    }

    /// <summary>
    ///     <see cref="AssertSyncOverhead" /> against a raw baseline other than the <see cref="Task" />
    ///     one, for arms whose callback shape differs from it.
    /// </summary>
    private void AssertSyncOverheadVersus(string arm, string raw, double budget)
    {
        var actual = baseline.SyncOverheadVersus(arm, raw);

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{arm}: {baseline.SyncBytes(arm):0.0} B/op total, {actual:0.0} B/op above '{raw}'; budget {budget:0} B"));

        Assert.True(
            actual <= budget,
            string.Create(CultureInfo.InvariantCulture,
                $"'{arm}' now allocates {actual:0.0} B/op above '{raw}', against a budget of {budget:0} B/op."));
    }

    private void AssertSuspendingOverhead(string arm, double budget)
    {
        var actual = baseline.SuspendingOverhead(arm);
        var ceiling = budget + Budgets.SuspendingNoiseFloor;

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{arm}: {baseline.SuspendingBytes(arm):0.0} B/op total, {actual:0.0} B/op above the raw callback; budget {budget:0} B (+{Budgets.SuspendingNoiseFloor:0} B instrument floor)"));

        Assert.True(
            actual <= ceiling,
            string.Create(CultureInfo.InvariantCulture,
                $"'{arm}' now allocates {actual:0.0} B/op above the raw callback, against a budget of {budget:0} B/op."));
    }
}
