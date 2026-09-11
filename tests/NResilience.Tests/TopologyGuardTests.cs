using NResilience.Extensions;
using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     The two guards that belong to a process rather than to a call graph: the thread pool a
///     service's own attempts wait in, and the limiter that bounds what one of its calls sends out.
///     <para>
///         Both exist in a single-dependency run already. What a graph adds is the part that matters:
///         a stall is local, and its consequences are not.
///     </para>
/// </summary>
public sealed class TopologyGuardTests
{
    private static readonly Dependency Bank =
        Dependency.Healthy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));

    private static readonly Resilience Api = TestPolicy.Instant with
    {
        Attempts = 3,
        Deadline = TimeSpan.FromSeconds(10),
        AttemptTimeout = TimeSpan.FromSeconds(2),
        Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(20)),
        AttemptCeiling = AttemptCeiling.Above(multiple: 3),
    };

    private static readonly Resilience Aware = Api with { Saturation = Saturation.Above(multiple: 5) };

    /// <summary>A pool that stops keeping up half a minute in.</summary>
    private static readonly Pool Stalling = Pool
        .Healthy(TimeSpan.FromMicroseconds(80))
        .Stall(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(20));

    private static Topology Chain(Resilience? checkout = null, Resilience? payments = null, int seconds = 120) =>
        Simulate.Topology()
            .Calls("checkout", "payments", (checkout ?? Api) with { Name = "checkout" })
            .Calls("payments", "bank", (payments ?? Api) with { Name = "payments" })
            .Leaf("bank", Bank)
            .Under(Load.Constant(perSecond: 200), at: "checkout")
            .For(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void A_stalled_service_looks_slow_to_everyone_calling_it()
    {
        var healthy = Chain().Run(seed: 42);
        var stalled = Chain().WithPool("payments", Stalling).Run(seed: 42);

        // The bank is perfectly well in both runs. The only thing that changed is a queue inside
        // payments, and it is the checkout that pays for it.
        Assert.True(
            stalled.On("checkout", "payments").Latency(0.99) > healthy.On("checkout", "payments").Latency(0.99) * 5,
            $"a stall inside payments moved the checkout's p99 from {healthy.On("checkout", "payments").Latency(0.99)} "
            + $"to {stalled.On("checkout", "payments").Latency(0.99)}");

        // And the bank was answering in its usual time throughout - the slowness the checkout felt
        // exists nowhere except inside payments.
        Assert.True(
            stalled.At("bank").Latency(0.99) < TimeSpan.FromMilliseconds(200),
            $"the bank's own p99 was {stalled.At("bank").Latency(0.99)}");
    }

    [Fact]
    public void A_stalled_service_knows_its_own_pool_from_its_dependency_and_its_callers_cannot()
    {
        // Both policies are configured to notice saturation. Only payments has a pool to notice.
        var report = Chain(checkout: Aware, payments: Aware).WithPool("payments", Stalling).Run(seed: 42);

        Assert.Equal(1, report.On("payments", "bank").CountOf(CallEventKind.SaturationDetected));

        // The checkout sees a slow payments and has no way to tell that apart from a slow bank. That
        // is the shape of the problem at graph scale, and the reason a local guard is not a whole
        // answer.
        Assert.Equal(0, report.On("checkout", "payments").CountOf(CallEventKind.SaturationDetected));
    }

    [Fact]
    public void A_service_with_no_pool_reads_one_that_never_queues()
    {
        var report = Chain(checkout: Aware, payments: Aware).Run(seed: 42);

        foreach (var edge in report.Edges)
            Assert.Equal(0, report.On(edge.Caller, edge.Callee).CountOf(CallEventKind.SaturationDetected));

        // Never the real host pool, which would put a machine-dependent term in a report that is
        // otherwise reproducible to the last byte.
        Assert.Equal(report.ToString(), Chain(checkout: Aware, payments: Aware).Run(seed: 42).ToString());
    }

    [Fact]
    public void A_pool_slows_every_call_the_service_makes()
    {
        // No attempt ceiling, so nothing cuts an attempt short and the delay is all that is measured.
        var healthy = FanOut(Api with { AttemptCeiling = null }).Run(seed: 42);
        var stalled = FanOut(Api with { AttemptCeiling = null }).WithPool("checkout", Stalling).Run(seed: 42);

        // One process, one pool, and every attempt it makes waits in it - both calls move together.
        foreach (var edge in stalled.Edges)
        {
            Assert.True(
                stalled.On(edge.Caller, edge.Callee).Latency(0.99) > healthy.On(edge.Caller, edge.Callee).Latency(0.99) * 3,
                $"{edge} was not slowed by its caller's pool: "
                + $"{healthy.On(edge.Caller, edge.Callee).Latency(0.99)} to {stalled.On(edge.Caller, edge.Callee).Latency(0.99)}");
        }
    }

    [Fact]
    public void A_ceiling_learned_from_a_healthy_dependency_turns_a_local_stall_into_work_never_done()
    {
        // The same stall, against a policy that bounds an attempt by what this dependency normally
        // costs, and one that does not.
        var bounded = FanOut(Api).WithPool("checkout", Stalling).Run(seed: 42);
        var unbounded = FanOut(Api with { AttemptCeiling = null }).WithPool("checkout", Stalling).Run(seed: 42);

        // The measured ceiling was learned while the pool was healthy, so once it stalls every attempt
        // overruns a bound that describes a dependency which never changed. The first call fails, and
        // because a service makes its calls one after another the second is never made at all.
        Assert.True(
            bounded.On("checkout", "catalog").Calls < unbounded.On("checkout", "catalog").Calls * 0.95,
            $"bounded made {bounded.On("checkout", "catalog").Calls} catalog calls and unbounded made "
            + $"{unbounded.On("checkout", "catalog").Calls}");

        // Which is the misattribution Saturation exists to prevent, costing a whole downstream call
        // rather than a retry - and it is only visible where there is a downstream call to lose.
        Assert.True(bounded.On("checkout", "payments").Availability < unbounded.On("checkout", "payments").Availability);
    }

    private static Topology FanOut(Resilience policy) =>
        Simulate.Topology()
            .Calls("checkout", "payments", policy with { Name = "payments" })
            .Calls("checkout", "catalog", policy with { Name = "catalog" })
            .Leaf("payments", Bank)
            .Leaf("catalog", Bank)
            .Under(Load.Constant(perSecond: 100), at: "checkout")
            .For(TimeSpan.FromSeconds(120));

    [Fact]
    public void The_same_seed_produces_a_byte_identical_report_with_a_pool()
    {
        Assert.Equal(
            Chain().WithPool("payments", Stalling).Run(seed: 42).ToString(),
            Chain().WithPool("payments", Stalling).Run(seed: 42).ToString());
    }

    [Fact]
    public void A_bulkhead_on_one_call_is_invisible_until_that_callee_slows()
    {
        static Topology Graph(Dependency bank) =>
            Simulate.Topology()
                .Calls("checkout", "payments", Api with { Name = "checkout" })
                .Calls("payments", "bank", Api with { Name = "payments" })
                .Leaf("bank", bank)
                .WithLimiter("payments", "bank", _ => Limit.Concurrency(permits: 60))
                .Under(Load.Constant(perSecond: 200), at: "checkout")
                .For(TimeSpan.FromSeconds(40));

        var well = Dependency.Healthy(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));

        var healthy = Graph(well).Run(seed: 42);
        var brownout = Graph(well.Brownout(TimeSpan.FromSeconds(10), slower: 8, TimeSpan.FromSeconds(15))).Run(seed: 42);

        Assert.Equal(0, healthy.On("payments", "bank").RefusedByLimiter);
        Assert.True(brownout.On("payments", "bank").RefusedByLimiter > 0);
    }

    [Fact]
    public void A_limiter_guards_the_one_call_it_was_put_on()
    {
        var report = Simulate.Topology()
            .Calls("checkout", "payments", Api with { Name = "checkout" })
            .Calls("payments", "bank", Api with { Name = "payments" })
            .Leaf("bank", Bank)
            .WithLimiter("payments", "bank", _ => Limit.Concurrency(permits: 3))
            .Under(Load.Constant(perSecond: 200), at: "checkout")
            .For(TimeSpan.FromSeconds(30))
            .Run(seed: 42);

        var guarded = report.On("payments", "bank");

        Assert.True(guarded.RefusedByLimiter > 0);

        // A refused attempt never left payments, so it is not in Reached and the multiplier falls
        // below one.
        Assert.True(guarded.LoadMultiplier < 1, $"the guarded call still sent {guarded.LoadMultiplier} attempts per call");

        // And the call above it has no limiter of its own.
        Assert.Equal(0, report.On("checkout", "payments").RefusedByLimiter);
    }

    [Fact]
    public void A_replenishing_limiter_is_refused_and_says_which_call()
    {
        var graph = Chain(seconds: 20).WithLimiter("payments", "bank", _ => Limit.PerSecond(permits: 100));

        var thrown = Assert.Throws<InvalidOperationException>(() => graph.Run(seed: 42));

        Assert.Contains("refills against the wall clock", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("'payments' calling 'bank'", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_queueing_limiter_is_refused_and_says_which_call()
    {
        var graph = Chain(seconds: 20).WithLimiter("payments", "bank", _ => Limit.Concurrency(permits: 3, queueLimit: 50));

        var thrown = Assert.Throws<InvalidOperationException>(() => graph.Run(seed: 42));

        Assert.Contains("queueLimit 0", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("'payments' calling 'bank'", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_services_own_limiter_bounds_the_sum_of_what_it_has_in_flight()
    {
        var well = Dependency.Healthy(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));

        // The same number of permits spent two ways: twenty across everything the checkout calls, or
        // twenty for each call it makes. Both dependencies are perfectly well, so the only thing being
        // measured is the shape of the bound.
        var shared = TwoCallees(well, well).WithLimiter("checkout", _ => Limit.Concurrency(permits: 20)).Run(seed: 42);

        var apiece = TwoCallees(well, well)
            .WithLimiter("checkout", "slow", _ => Limit.Concurrency(permits: 20))
            .WithLimiter("checkout", "fast", _ => Limit.Concurrency(permits: 20))
            .Run(seed: 42);

        var sharedRefusals = shared.On("checkout", "slow").RefusedByLimiter + shared.On("checkout", "fast").RefusedByLimiter;
        var apieceRefusals = apiece.On("checkout", "slow").RefusedByLimiter + apiece.On("checkout", "fast").RefusedByLimiter;

        // One pool for the whole service is a much tighter bound than the same number per call, because
        // it is a bound on the total rather than on each. That is the difference between the two, and
        // it is the reason a process-wide bulkhead is a different guard rather than a shorter way to
        // write several.
        Assert.True(
            sharedRefusals > apieceRefusals * 4,
            $"shared refused {sharedRefusals} and per-call refused {apieceRefusals}");

        Assert.True(apiece.On("checkout", "fast").Availability > 0.99);
    }

    [Fact]
    public void A_service_limiter_and_a_call_limiter_both_apply()
    {
        var well = Dependency.Healthy(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));

        // A generous bound on the process and a tight one on the one call: the two-level bulkhead.
        var report = TwoCallees(well, well)
            .WithLimiter("checkout", _ => Limit.Concurrency(permits: 200))
            .WithLimiter("checkout", "slow", _ => Limit.Concurrency(permits: 3))
            .Run(seed: 42);

        Assert.True(report.On("checkout", "slow").RefusedByLimiter > 0);

        // The narrower bound is doing the work, and the other call is untouched by it.
        Assert.Equal(0, report.On("checkout", "fast").RefusedByLimiter);
    }

    [Fact]
    public void A_refusal_is_counted_against_the_call_that_was_attempting()
    {
        var well = Dependency.Healthy(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));

        var report = TwoCallees(well, well).WithLimiter("checkout", _ => Limit.Concurrency(permits: 5)).Run(seed: 42);

        // One gate, shared, so it could not say which of its calls was turned away - the count belongs
        // to the call rather than to the limiter.
        Assert.True(report.On("checkout", "slow").RefusedByLimiter > 0);
        Assert.True(report.On("checkout", "fast").RefusedByLimiter > 0);

        foreach (var edge in report.Edges)
        {
            var measured = report.On(edge.Caller, edge.Callee);

            // Refused attempts never left, so they are not in Reached.
            Assert.True(measured.Reached + measured.RefusedByLimiter >= measured.Calls);
        }
    }

    [Fact]
    public void The_same_seed_produces_a_byte_identical_report_with_a_service_limiter()
    {
        var well = Dependency.Healthy(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));

        Assert.Equal(
            TwoCallees(well, well).WithLimiter("checkout", _ => Limit.Concurrency(permits: 20)).Run(seed: 42).ToString(),
            TwoCallees(well, well).WithLimiter("checkout", _ => Limit.Concurrency(permits: 20)).Run(seed: 42).ToString());
    }

    [Fact]
    public void A_service_limiter_that_cannot_run_on_a_virtual_clock_says_which_service()
    {
        var replenishing = Assert.Throws<InvalidOperationException>(
            () => Chain(seconds: 20).WithLimiter("payments", _ => Limit.PerSecond(permits: 100)).Run(seed: 42));

        Assert.Contains("refills against the wall clock", replenishing.Message, StringComparison.Ordinal);
        Assert.Contains("'payments'", replenishing.Message, StringComparison.Ordinal);

        var queueing = Assert.Throws<InvalidOperationException>(
            () => Chain(seconds: 20).WithLimiter("payments", _ => Limit.Concurrency(permits: 3, queueLimit: 50)).Run(seed: 42));

        Assert.Contains("queueLimit 0", queueing.Message, StringComparison.Ordinal);
    }

    /// <summary>A checkout calling one callee that will brown out and one that will not.</summary>
    private static Topology TwoCallees(Dependency slow, Dependency fast) =>
        Simulate.Topology()
            .Calls("checkout", "slow", Api with { Name = "slow" })
            .Calls("checkout", "fast", Api with { Name = "fast" })
            .Leaf("slow", slow)
            .Leaf("fast", fast)
            .Under(Load.Constant(perSecond: 300), at: "checkout")
            .For(TimeSpan.FromSeconds(40));

    [Theory]
    [MemberData(nameof(BadGuards))]
    public void A_guard_the_graph_cannot_place_is_refused(Topology topology, string expected)
    {
        var thrown = Assert.Throws<InvalidOperationException>(topology.Validate);

        Assert.Contains(expected, thrown.Message, StringComparison.Ordinal);
    }

    public static TheoryData<Topology, string> BadGuards()
    {
        var load = Load.Constant(perSecond: 100);
        var duration = TimeSpan.FromSeconds(10);

        Topology Base() =>
            Simulate.Topology()
                .Calls("checkout", "payments", Api)
                .Calls("payments", "bank", Api)
                .Leaf("bank", Bank)
                .Under(load, at: "checkout")
                .For(duration);

        return new TheoryData<Topology, string>
        {
            { Base().WithPool("bank", Stalling), "is a leaf and cannot have a pool" },
            { Base().WithPool("nobody", Stalling), "which makes no calls" },
            { Base().WithPool("payments", Stalling).WithPool("payments", Stalling), "One service, one process, one pool" },
            { Base().WithLimiter("checkout", "bank", _ => Limit.Concurrency(10)), "not a call this topology makes" },
            { Base().WithLimiter("bank", _ => Limit.Concurrency(10)), "is a leaf and cannot have a limiter" },
            { Base().WithLimiter("nobody", _ => Limit.Concurrency(10)), "which makes no calls" },
            {
                Base().WithLimiter("payments", _ => Limit.Concurrency(10)).WithLimiter("payments", _ => Limit.Concurrency(20)),
                "One service, one process-wide limiter"
            },
            {
                Base().WithLimiter("payments", "bank", _ => Limit.Concurrency(10))
                    .WithLimiter("payments", "bank", _ => Limit.Concurrency(20)),
                "One call, one limiter"
            },
        };
    }
}
