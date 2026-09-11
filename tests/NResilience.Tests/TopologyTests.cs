using NResilience.Testing;

namespace NResilience.Tests;

/// <summary>
///     A graph of services calling each other.
///     <para>
///         The first two tests are the ones worth having. Determinism is what makes any number here an
///         assertion rather than an anecdote, and compounding is the thing a single-dependency run
///         cannot show: three policies each behaving exactly as configured, and a dependency three hops
///         down feeling several times the load anybody asked for.
///     </para>
/// </summary>
public sealed class TopologyTests
{
    private static readonly Dependency Quick =
        Dependency.Healthy(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(30));

    private static readonly Dependency Sick = Dependency
        .Healthy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100))
        .Brownout(TimeSpan.FromSeconds(10), slower: 10, TimeSpan.FromSeconds(15))
        .Failing(0.2);

    private static readonly Resilience Retrying = TestPolicy.Instant with
    {
        Attempts = 3,
        Deadline = TimeSpan.FromSeconds(10),
        AttemptTimeout = TimeSpan.FromSeconds(2),
        Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(20)),
        Budget = RetryBudget.None,
    };

    private static readonly Resilience Once = Retrying with { Attempts = 1 };

    /// <summary>checkout -> payments -> bank, with the bank the thing that breaks.</summary>
    private static Topology Chain(Resilience? upper = null, Resilience? lower = null, int seconds = 40) =>
        Simulate.Topology()
            .Calls("checkout", "payments", upper ?? Retrying)
            .Calls("payments", "bank", lower ?? Retrying)
            .Leaf("bank", Sick)
            .Under(Load.Constant(perSecond: 200), at: "checkout")
            .For(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void The_same_seed_produces_a_byte_identical_report()
    {
        Assert.Equal(Chain().Run(seed: 42).ToString(), Chain().Run(seed: 42).ToString());
    }

    [Fact]
    public void A_different_seed_produces_a_different_run()
    {
        Assert.NotEqual(Chain().Run(seed: 1).ToString(), Chain().Run(seed: 2).ToString());
    }

    [Fact]
    public void Retries_compound_down_the_graph()
    {
        var report = Chain().Run(seed: 42);

        var upper = report.On("checkout", "payments");
        var lower = report.On("payments", "bank");

        // Each policy is doing exactly what it was configured to do.
        Assert.True(upper.LoadMultiplier > 1, $"checkout retried {upper.LoadMultiplier} times per call");
        Assert.True(lower.LoadMultiplier > 1, $"payments retried {lower.LoadMultiplier} times per call");

        // And the thing at the bottom feels the product of them, which is the number nobody configured
        // and nobody can read off a single-dependency run.
        Assert.True(
            lower.Amplification > upper.Amplification,
            $"the bank felt {lower.Amplification} in its worst second and payments felt {upper.Amplification}");

        // The calls payments makes are the attempts checkout sent it.
        Assert.Equal(upper.Reached, lower.Calls);
    }

    [Fact]
    public void A_retry_budget_in_the_middle_stops_the_storm_reaching_the_bottom()
    {
        var unbudgeted = Chain().Run(seed: 42);
        var budgeted = Chain(lower: Retrying with { Budget = RetryBudget.Automatic }).Run(seed: 42);

        Assert.True(
            budgeted.On("payments", "bank").Amplification < unbudgeted.On("payments", "bank").Amplification,
            $"budgeted {budgeted.On("payments", "bank").Amplification}, unbudgeted {unbudgeted.On("payments", "bank").Amplification}");

        Assert.True(budgeted.On("payments", "bank").CountOf(CallEventKind.RejectedByBudget) > 0);
    }

    [Fact]
    public void A_policy_that_never_retries_costs_the_bottom_exactly_what_was_offered()
    {
        var report = Chain(upper: Once, lower: Once).Run(seed: 42);

        Assert.Equal(1.0, report.On("checkout", "payments").LoadMultiplier);
        Assert.Equal(1.0, report.On("payments", "bank").LoadMultiplier);
    }

    [Fact]
    public void A_graph_can_be_deeper_than_two_hops()
    {
        var report = Simulate.Topology()
            .Calls("edge", "checkout", Retrying)
            .Calls("checkout", "payments", Retrying)
            .Calls("payments", "ledger", Retrying)
            .Calls("ledger", "bank", Retrying)
            .Leaf("bank", Sick)
            .Under(Load.Constant(perSecond: 100), at: "edge")
            .For(TimeSpan.FromSeconds(40))
            .Run(seed: 42);

        // Four policies between the load and the thing that broke, each multiplying the last.
        Assert.Equal(report.On("edge", "checkout").Reached, report.On("checkout", "payments").Calls);
        Assert.Equal(report.On("checkout", "payments").Reached, report.On("payments", "ledger").Calls);
        Assert.Equal(report.On("payments", "ledger").Reached, report.On("ledger", "bank").Calls);

        Assert.True(
            report.On("ledger", "bank").Amplification > report.On("edge", "checkout").Amplification,
            "four hops of retries did not compound");
    }

    [Fact]
    public void Two_services_can_share_a_dependency()
    {
        var report = Simulate.Topology()
            .Calls("checkout", "payments", Retrying)
            .Calls("checkout", "catalog", Retrying)
            .Calls("payments", "shared", Retrying)
            .Calls("catalog", "shared", Retrying)
            .Leaf("shared", Sick)
            .Under(Load.Constant(perSecond: 100), at: "checkout")
            .For(TimeSpan.FromSeconds(30))
            .Run(seed: 42);

        // The leaf serves both callers, so what it felt is the sum of what each of them sent.
        Assert.Equal(
            report.On("payments", "shared").Reached + report.On("catalog", "shared").Reached,
            report.At("shared").Calls);
    }

    [Fact]
    public void A_service_makes_its_calls_in_the_order_they_were_declared()
    {
        var report = Simulate.Topology()
            .Calls("checkout", "payments", Once)
            .Calls("checkout", "catalog", Once)
            .Leaf("payments", Sick)
            .Leaf("catalog", Quick)
            .Under(Load.Constant(perSecond: 100), at: "checkout")
            .For(TimeSpan.FromSeconds(30))
            .Run(seed: 42);

        // One after another, so a request that cannot pay never looks anything up - which is a real
        // shape, and the one to be aware of when a page really fans out in parallel.
        Assert.True(
            report.On("checkout", "catalog").Calls < report.On("checkout", "payments").Calls,
            "the second call was made even where the first had failed");

        Assert.Equal(report.On("checkout", "payments").Succeeded, report.On("checkout", "catalog").Calls);
    }

    [Fact]
    public void A_service_reports_what_it_costs_the_rest_of_the_graph()
    {
        var report = Chain().Run(seed: 42);

        var payments = report.At("payments");

        // Requests it served, which is the attempts that reached it.
        Assert.Equal(report.On("checkout", "payments").Reached, payments.Calls);

        // And attempts it sent on, across every call it makes.
        Assert.Equal(report.On("payments", "bank").Reached, payments.Reached);

        // A leaf sends nothing on.
        Assert.Equal(1.0, report.At("bank").LoadMultiplier);
    }

    [Fact]
    public void Load_can_arrive_at_more_than_one_entry()
    {
        var report = Simulate.Topology()
            .Calls("checkout", "bank", Retrying)
            .Calls("backfill", "bank", Retrying)
            .Leaf("bank", Sick)
            .Under(Load.Constant(perSecond: 100), at: "checkout")
            .Under(Load.Constant(perSecond: 300), at: "backfill")
            .For(TimeSpan.FromSeconds(30))
            .Run(seed: 42);

        Assert.Equal(["checkout", "backfill"], report.Entries);

        // Three times the rate, so about three times the requests.
        Assert.Equal(3.0, (double)report.At("backfill").Calls / report.At("checkout").Calls, tolerance: 0.15);
    }

    [Fact]
    public void Criticality_offered_at_an_entry_reaches_every_call_below_it()
    {
        var report = Simulate.Topology()
            .Calls("checkout", "payments", Retrying)
            .Calls("payments", "bank", Retrying)
            .Leaf("bank", Sick)
            .Under(Load.Constant(perSecond: 200).Mix(Criticality.Sheddable, fraction: 0.5), at: "checkout")
            .For(TimeSpan.FromSeconds(30))
            .Run(seed: 42);

        // The level is published once, at the entry, and read by every policy underneath - which is the
        // whole argument for propagating one.
        foreach (var edge in report.Edges)
        {
            var measured = report.On(edge.Caller, edge.Callee);

            Assert.True(measured.CallsAt(Criticality.Sheddable) > 0, $"{edge} saw no sheddable calls");
            Assert.True(measured.CallsAt(Criticality.Critical) > 0, $"{edge} saw no critical calls");
        }
    }

    [Fact]
    public void Every_edge_can_record_its_own_timeline()
    {
        var report = Chain(seconds: 20).Recording().Run(seed: 42);

        foreach (var edge in report.Edges)
        {
            var timeline = report.On(edge.Caller, edge.Callee).Timeline;

            Assert.NotNull(timeline);
            Assert.NotEmpty(timeline);
        }

        // A graph's events are attributable, which is what one merged stream would lose.
        Assert.NotEqual(
            report.On("checkout", "payments").Timeline!.Count,
            report.On("payments", "bank").Timeline!.Count);
    }

    [Fact]
    public void A_band_runs_the_graph_once_per_seed()
    {
        var band = Chain(seconds: 20).RunAll(1, 2, 3);

        Assert.Equal([1, 2, 3], band.Seeds);
        Assert.Equal(3, band.Reports.Count);
        Assert.True(band.On("payments", "bank").Amplification.Spread > 0, "amplification did not move across three seeds");
        Assert.True(band.At("checkout").Availability.Median > 0);
    }

    [Theory]
    [MemberData(nameof(BadGraphs))]
    public void A_graph_the_simulator_cannot_run_is_refused(Topology topology, string expected)
    {
        var thrown = Assert.Throws<InvalidOperationException>(topology.Validate);

        Assert.Contains(expected, thrown.Message, StringComparison.Ordinal);
    }

    public static TheoryData<Topology, string> BadGraphs()
    {
        var load = Load.Constant(perSecond: 100);
        var duration = TimeSpan.FromSeconds(10);

        return new TheoryData<Topology, string>
        {
            {
                Simulate.Topology()
                    .Calls("a", "b", Retrying).Calls("b", "a", Retrying)
                    .Under(load, at: "a").For(duration),
                "form a cycle"
            },
            {
                Simulate.Topology()
                    .Calls("a", "a", Retrying).Leaf("a", Quick)
                    .Under(load, at: "a").For(duration),
                "calls itself"
            },
            {
                Simulate.Topology()
                    .Calls("a", "b", Retrying)
                    .Under(load, at: "a").For(duration),
                "called but never declared"
            },
            {
                Simulate.Topology()
                    .Calls("a", "b", Retrying).Calls("b", "c", Retrying).Leaf("b", Quick).Leaf("c", Quick)
                    .Under(load, at: "a").For(duration),
                "is declared as a leaf and also makes calls"
            },
            {
                Simulate.Topology()
                    .Calls("a", "b", Retrying).Calls("a", "b", Once).Leaf("b", Quick)
                    .Under(load, at: "a").For(duration),
                "more than once"
            },
            {
                Simulate.Topology()
                    .Calls("a", "b", Retrying).Leaf("b", Quick)
                    .Under(load, at: "b").For(duration),
                "which makes no calls"
            },
            {
                Simulate.Topology()
                    .Calls("a", "b", Retrying).Leaf("b", Quick)
                    .Under(Load.Constant(perSecond: 100, peers: 50), at: "a").For(duration),
                "Peers approximate processes that have no policies"
            },
            {
                Simulate.Topology().Under(load, at: "a").For(duration),
                "has no calls"
            },
            {
                Simulate.Topology().Calls("a", "b", Retrying).Leaf("b", Quick).For(duration),
                "has no load"
            },
            {
                Simulate.Topology().Calls("a", "b", Retrying).Leaf("b", Quick).Under(load, at: "a"),
                "has no duration"
            },
        };
    }
}
