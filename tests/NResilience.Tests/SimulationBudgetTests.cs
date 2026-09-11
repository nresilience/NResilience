using NResilience.Testing;
using NResilience.Testing.Internal;

namespace NResilience.Tests;

/// <summary>
///     A retry budget inside a simulation.
///     <para>
///         A budget refills against a clock, and a budget built outside a test was built against the
///         system clock. Dropped unchanged into a run on a virtual clock it would refill by however
///         many real milliseconds the run happened to take - so the same scenario would answer
///         differently on a fast machine, on a slow one, and on the same machine twice. Determinism is
///         the whole premise of simulating rather than measuring, so these are the tests that hold it.
///     </para>
/// </summary>
public sealed class SimulationBudgetTests
{
    private static readonly int[] Seeds = [1, 2, 3, 4, 5, 6];

    private static readonly Dependency Browned = Dependency
        .Healthy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200))
        .Brownout(TimeSpan.FromSeconds(5), slower: 20, TimeSpan.FromSeconds(20));

    private static Simulation Run(RetryBudget budget) =>
        Simulate.Policy(TestPolicy.Instant with
            {
                Attempts = 3,
                Deadline = TimeSpan.FromSeconds(10),
                AttemptTimeout = TimeSpan.FromSeconds(2),
                Backoff = Backoff.Exponential(TimeSpan.FromMilliseconds(50)),
                Budget = budget,
            })
            .Against(Browned)
            .Under(Load.Constant(perSecond: 300))
            .For(TimeSpan.FromSeconds(40));

    public static TheoryData<string, RetryBudget> Budgets => new()
    {
        { "automatic", RetryBudget.Automatic },
        { "none", RetryBudget.None },
        { "private", RetryBudget.Of(fraction: 0.1, minimumPerSecond: 3) },
        { "shared", RetryBudget.Shared("simulation-budget-tests", fraction: 0.1, minimumPerSecond: 3) },
    };

    [Theory]
    [MemberData(nameof(Budgets))]
    public void The_same_run_twice_reports_the_same_numbers(string label, RetryBudget budget)
    {
        Assert.NotNull(label);

        var first = Run(budget).Run(seed: 7);
        var second = Run(budget).Run(seed: 7);

        Assert.Equal(first.ToString(), second.ToString());
        Assert.Equal(first.Reached, second.Reached);
    }

    [Theory]
    [MemberData(nameof(Budgets))]
    public void The_same_band_twice_reports_the_same_numbers(string label, RetryBudget budget)
    {
        Assert.NotNull(label);

        var first = Run(budget).RunAll(Seeds);
        var second = Run(budget).RunAll(Seeds);

        Assert.Equal(
            first.Reports.Select(report => report.ToString()),
            second.Reports.Select(report => report.ToString()));
    }

    [Theory]
    [MemberData(nameof(Budgets))]
    public void A_seed_reports_the_same_numbers_alone_as_it_does_in_a_band(string label, RetryBudget budget)
    {
        Assert.NotNull(label);

        // What a band is for: one seed's numbers are a property of that seed, not of which other
        // seeds it was listed beside.
        var alone = Seeds.Select(seed => Run(budget).Run(seed).ToString()).ToArray();

        var band = Run(budget).RunAll(Seeds);

        Assert.Equal(alone, band.Reports.Select(report => report.ToString()));
    }

    [Fact]
    public void A_budget_carried_into_a_run_is_not_the_one_the_policy_was_configured_with()
    {
        var configured = RetryBudget.Of(fraction: 0.1, minimumPerSecond: 3);

        var rebased = new BudgetClock(TimeProvider.System).For(configured);

        Assert.NotSame(configured, rebased);
        Assert.Equal(configured.Name, rebased.Name);
    }

    [Fact]
    public void Two_policies_holding_one_budget_share_the_one_it_is_rebased_to()
    {
        // A graph where two services share a budget has to keep sharing it, or rebasing would quietly
        // turn a modelled shared budget into two separate ones and halve the thing being measured.
        var shared = RetryBudget.Shared("two-policies", fraction: 0.1, minimumPerSecond: 3);
        var run = new BudgetClock(TimeProvider.System);

        Assert.Same(run.For(shared), run.For(shared));
    }

    [Fact]
    public void Two_runs_do_not_share_a_rebased_budget()
    {
        var shared = RetryBudget.Shared("two-runs", fraction: 0.1, minimumPerSecond: 3);

        Assert.NotSame(
            new BudgetClock(TimeProvider.System).For(shared),
            new BudgetClock(TimeProvider.System).For(shared));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Automatic_and_none_are_carried_through_unchanged(bool automatic)
    {
        // Neither holds a bucket: Automatic is a marker the executor resolves per policy instance on
        // that policy's own clock, and None is nothing at all. Rebuilding either would be inventing a
        // budget where the caller asked for none.
        var budget = automatic ? RetryBudget.Automatic : RetryBudget.None;

        Assert.Same(budget, new BudgetClock(TimeProvider.System).For(budget));
    }
}
