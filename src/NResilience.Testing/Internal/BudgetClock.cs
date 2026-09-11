namespace NResilience.Testing.Internal;

/// <summary>
///     Puts the retry budgets of one run onto that run's clock, and keeps two policies that shared a
///     budget sharing the one it was rebased to.
///     <para>
///         One of these lives for one run. Within it, each distinct budget is rebased once and every
///         policy holding that budget gets the same rebased bucket - so a graph where two services
///         share a budget still models them sharing it. Across runs nothing is shared at all, which is
///         what stops one seed's retries from spending the next seed's tokens.
///     </para>
/// </summary>
/// <param name="time">The run's clock.</param>
internal sealed class BudgetClock(TimeProvider time)
{
    // Reference equality, deliberately: what makes two policies share a budget is holding the same
    // instance, which is exactly what RetryBudget.Shared hands out for one name.
    private readonly Dictionary<RetryBudget, RetryBudget> _rebased = new(ReferenceEqualityComparer.Instance);

    /// <summary>The budget this run should use in place of <paramref name="budget" />.</summary>
    /// <param name="budget">The budget the policy was configured with.</param>
    /// <returns>The rebased budget, the same one every time it is asked for within this run.</returns>
    public RetryBudget For(RetryBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);

        if (_rebased.TryGetValue(budget, out var already))
            return already;

        var rebased = budget.OnClock(time);
        _rebased[budget] = rebased;

        return rebased;
    }
}
