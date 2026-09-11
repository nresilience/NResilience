using NResilience.Extensions;
using NResilience.Testing;

namespace NResilience.Docs;

/// <summary>
///     The simulator: a policy run against a modeled dependency on a virtual clock.
/// </summary>
/// <remarks>
///     The sample report on the page is <c>simulation-report.txt</c>, and the test below asserts that
///     the run prints exactly it. So the page cannot claim numbers the simulator does not produce, and
///     a change that moves them fails here first.
/// </remarks>
public sealed class Simulating
{
    [Fact]
    public void A_policy_can_be_run_against_a_brownout()
    {
        // <snippet:simulation-run>
        var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10), Name = "api" };

        var report = Simulate.Policy(policy: api)
            .Against(dependency: Dependency
                .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 200))
                .Brownout(after: TimeSpan.FromSeconds(value: 30), slower: 8, lasting: TimeSpan.FromMinutes(value: 1)))
            .Under(load: Load.Constant(perSecond: 500))
            .For(duration: TimeSpan.FromMinutes(value: 5))
            .Run(seed: 42);

        // The number the dependency's owner asks for: attempts that reached it, per call you made.
        Assert.True(condition: report.LoadMultiplier <= 1.2);

        // And the one your own caller feels.
        Assert.True(condition: report.Latency(quantile: 0.99) < TimeSpan.FromSeconds(value: 1));

        // </snippet:simulation-run>

        Assert.Equal(expected: Published(), actual: report.ToString().TrimEnd('\n'));
    }

    [Fact]
    public void A_retry_budget_can_be_shown_to_earn_its_place()
    {
        // <snippet:simulation-compare>
        var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10), Name = "api" };

        var slowing = Dependency
            .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 200))
            .Brownout(after: TimeSpan.FromSeconds(value: 30), slower: 8, lasting: TimeSpan.FromMinutes(value: 1));

        // The same seeds and the same dependency, one setting different - so the difference between
        // the two bands is the setting rather than the run.
        var unbudgeted = Simulate.Policy(policy: api with { Budget = RetryBudget.None })
            .Against(dependency: slowing)
            .Under(load: Load.Constant(perSecond: 500))
            .For(duration: TimeSpan.FromMinutes(value: 5))
            .RunAll(1, 2, 3, 4, 5);

        var budgeted = Simulate.Policy(policy: api)
            .Against(dependency: slowing)
            .Under(load: Load.Constant(perSecond: 500))
            .For(duration: TimeSpan.FromMinutes(value: 5))
            .RunAll(1, 2, 3, 4, 5);

        // Not "it won on seed 42" - the two ranges do not overlap, so it wins on every seed.
        Assert.True(condition: budgeted.LoadMultiplier.Separates(other: unbudgeted.LoadMultiplier));
        Assert.True(condition: budgeted.LoadMultiplier.Maximum < unbudgeted.LoadMultiplier.Minimum);

        // </snippet:simulation-compare>
    }

    [Fact]
    public void One_pod_of_fifty_offers_a_different_question()
    {
        // <snippet:simulation-peers>
        var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10), Name = "api" };

        // Four hundred calls at once is what this dependency serves before it starts queueing, and
        // fifty pods offering five hundred a second each is what decides whether it gets there.
        var shared = Dependency
            .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 200))
            .Brownout(after: TimeSpan.FromSeconds(value: 30), slower: 8, lasting: TimeSpan.FromMinutes(value: 1))
            .Capacity(concurrent: 400);

        var report = Simulate.Policy(policy: api)
            .Against(dependency: shared)
            .Under(load: Load.Constant(perSecond: 500, peers: 50))
            .For(duration: TimeSpan.FromMinutes(value: 5))
            .Run(seed: 42);

        // The five-minute average hides the second that tipped the dependency over.
        Assert.True(condition: report.Amplification > report.LoadMultiplier);

        // </snippet:simulation-peers>
    }

    [Fact]
    public void A_backfill_can_be_made_to_hold_back_for_a_checkout()
    {
        // <snippet:simulation-criticality>
        var api = Resilience.Http with
        {
            Deadline = TimeSpan.FromSeconds(value: 10),
            UseAmbientCriticality = true,
            Name = "api",
        };

        // Seven calls in ten are a backfill nobody is waiting on. Critical is the remainder, which is
        // what a call with no level already is - so it is not set, it is what is left.
        var load = Load.Constant(perSecond: 500).Mix(criticality: Criticality.Sheddable, fraction: 0.7);

        var report = Simulate.Policy(policy: api)
            .Against(dependency: Dependency
                .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 200))
                .Brownout(after: TimeSpan.FromSeconds(value: 30), slower: 8, lasting: TimeSpan.FromMinutes(value: 1)))
            .Under(load: load)
            .For(duration: TimeSpan.FromMinutes(value: 5))
            .Run(seed: 42);

        // The measurement the setting exists to move. The aggregate averages the two together and can
        // hide the whole trade.
        Assert.True(condition: report.AvailabilityAt(criticality: Criticality.Critical)
                               > report.AvailabilityAt(criticality: Criticality.Sheddable));

        // </snippet:simulation-criticality>
    }

    [Fact]
    public void A_local_thread_pool_stall_can_be_told_apart_from_a_slow_dependency()
    {
        // <snippet:simulation-pool>
        var api = Resilience.Http with
        {
            Deadline = TimeSpan.FromSeconds(value: 10),
            AttemptCeiling = AttemptCeiling.Above(multiple: 3),
            Saturation = Saturation.Above(multiple: 5),
            Name = "api",
        };

        // A healthy pool queues for tens of microseconds. This one stops keeping up half a minute in:
        // work items wait 400 ms for a thread, and every call looks 400 ms slower from inside the
        // executor while the dependency is fine.
        var pool = Pool
            .Healthy(delay: TimeSpan.FromMicroseconds(value: 80))
            .Stall(after: TimeSpan.FromSeconds(value: 30), delay: TimeSpan.FromMilliseconds(value: 400),
                lasting: TimeSpan.FromSeconds(value: 20));

        var report = Simulate.Policy(policy: api)
            .Against(dependency: Dependency
                .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 200)))
            .Under(load: Load.Constant(perSecond: 200))
            .WithPool(pool: pool)
            .For(duration: TimeSpan.FromMinutes(value: 2))
            .Run(seed: 42);

        // One event for one episode, raised at its onset - so this is a count of local incidents.
        Assert.Equal(expected: 1, actual: report.CountOf(kind: CallEventKind.SaturationDetected));

        // </snippet:simulation-pool>
    }

    [Fact]
    public void A_bulkhead_costs_nothing_until_the_dependency_slows()
    {
        // <snippet:simulation-limiter>
        var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10), Name = "api" };

        // Four hundred calls a second at 50 ms apiece is about twenty in flight, so sixty permits is
        // headroom while the dependency is well - and a wall the moment calls start piling up.
        Simulation Bulkhead(Dependency dependency) =>
            Simulate.Policy(policy: api)
                .Against(dependency: dependency)
                .Under(load: Load.Constant(perSecond: 400))
                .For(duration: TimeSpan.FromSeconds(value: 20))
                .WithLimiter(limiter: _ => Limit.Concurrency(permits: 60));

        var well = Dependency.Healthy(p50: TimeSpan.FromMilliseconds(value: 50), p99: TimeSpan.FromMilliseconds(value: 300));

        var healthy = Bulkhead(dependency: well).Run(seed: 7);
        var brownout = Bulkhead(dependency: well
            .Brownout(after: TimeSpan.FromSeconds(value: 5), slower: 8, lasting: TimeSpan.FromSeconds(value: 10))).Run(seed: 7);

        // A guard that costs nothing while nothing is wrong is the whole argument for setting one.
        Assert.Equal(expected: 0, actual: healthy.RefusedByLimiter);

        // And one that bites the moment calls pile up is what stops the pile-up spreading. These
        // attempts never reached the dependency, so they are not in Reached either.
        Assert.True(condition: brownout.RefusedByLimiter > 0);
        Assert.True(condition: brownout.Reached < healthy.Reached);

        // </snippet:simulation-limiter>
    }

    [Fact]
    public void A_run_can_be_asked_to_record_what_it_did()
    {
        // <snippet:simulation-timeline>
        var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10), Name = "api" };

        var report = Simulate.Policy(policy: api)
            .Against(dependency: Dependency
                .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 200))
                .Failing(rate: 0.2))
            .Under(load: Load.Constant(perSecond: 50))
            .For(duration: TimeSpan.FromSeconds(value: 10))
            .Recording()
            .Run(seed: 42);

        // Every event the run raised, in order, each with the virtual time it was raised at. Null
        // unless the run was asked to record one, because a five-minute run raises a few hundred
        // thousand of them.
        var timeline = report.Timeline!;

        foreach (var entry in timeline.Take(count: 20))
        {
            Console.WriteLine(value: entry);
        }

        // Which makes a claim about ordering an assertion rather than an argument: the backoff a
        // retry served is on the event that scheduled it.
        var retry = timeline.First(entry => entry.Event.Kind == CallEventKind.Retrying);

        Assert.NotNull(@object: retry.Event.Delay);
        Assert.True(condition: retry.At > TimeSpan.Zero);

        // </snippet:simulation-timeline>
    }

    [Fact]
    public void A_retry_storm_compounds_down_a_call_graph()
    {
        // <snippet:simulation-topology>
        var api = Resilience.Http with { Deadline = TimeSpan.FromSeconds(value: 10) };

        var report = Simulate.Topology()
            .Calls(caller: "checkout", callee: "payments", policy: api with { Name = "payments" })
            .Calls(caller: "payments", callee: "bank", policy: api with { Name = "bank" })
            .Leaf(name: "bank", dependency: Dependency
                .Healthy(p50: TimeSpan.FromMilliseconds(value: 20), p99: TimeSpan.FromMilliseconds(value: 100))
                .Brownout(after: TimeSpan.FromSeconds(value: 10), slower: 10, lasting: TimeSpan.FromSeconds(value: 15)))
            .Under(load: Load.Constant(perSecond: 200), at: "checkout")
            .For(duration: TimeSpan.FromSeconds(value: 40))
            .Run(seed: 42);

        // Each policy retries about as much as it was told to.
        var upper = report.On(caller: "checkout", callee: "payments");
        var lower = report.On(caller: "payments", callee: "bank");

        // And the thing at the bottom feels the product of them - the number nobody configured, and
        // the one no single-dependency run can show you.
        Assert.True(condition: lower.Amplification > upper.Amplification);

        // The calls payments makes are the attempts checkout sent it.
        Assert.Equal(expected: upper.Reached, actual: lower.Calls);

        // </snippet:simulation-topology>
    }

    /// <summary>The published report, read off disk so the page and the assertion cannot disagree.</summary>
    private static string Published()
    {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "NResilience.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        var file = Path.Combine(directory.FullName, "tests", "NResilience.Docs", "simulation-report.txt");

        return File.ReadAllText(path: file).Replace(oldValue: "\r\n", newValue: "\n", comparisonType: StringComparison.Ordinal).TrimEnd('\n');
    }
}
