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

        // The same seed and the same dependency, one setting different - so the difference between
        // the two reports is the setting rather than the run.
        var unbudgeted = Simulate.Policy(policy: api with { Budget = RetryBudget.None })
            .Against(dependency: slowing)
            .Under(load: Load.Constant(perSecond: 500))
            .For(duration: TimeSpan.FromMinutes(value: 5))
            .Run(seed: 42);

        var budgeted = Simulate.Policy(policy: api)
            .Against(dependency: slowing)
            .Under(load: Load.Constant(perSecond: 500))
            .For(duration: TimeSpan.FromMinutes(value: 5))
            .Run(seed: 42);

        Assert.True(condition: budgeted.LoadMultiplier < unbudgeted.LoadMultiplier);
        Assert.True(condition: budgeted.CountOf(kind: CallEventKind.RejectedByBudget) > 0);

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
