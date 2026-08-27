using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NResilience.Extensions;

/// <summary>
///     What the resilience health check looks at, and what it reports when it does not like what it
///     sees.
/// </summary>
/// <remarks>
///     The defaults encode one opinion worth stating outright: an open breaker is
///     <see cref="HealthStatus.Degraded" />, not <see cref="HealthStatus.Unhealthy" />. A breaker opens
///     because a <i>dependency</i> is down, and this process is then doing exactly what it was
///     configured to do - shedding load and protecting the dependency. Reporting yourself unhealthy for
///     that invites an orchestrator to restart a pod that is working correctly, remove it from a load
///     balancer that should keep sending it traffic, or fail a deployment over someone else's outage.
///     Both statuses are configurable, because a process that cannot do anything useful while its one
///     dependency is down is a real shape - it is just not the default one.
/// </remarks>
public sealed class ResilienceHealthOptions
{
    private readonly List<(string Name, Breaker? Breaker, RetryBudget? Budget)> _watched = [];

    /// <summary>
    ///     What an open or isolated breaker reports. <see cref="HealthStatus.Degraded" /> by default; see
    ///     the remarks on this class for why.
    /// </summary>
    public HealthStatus BreakerOpenStatus { get; set; } = HealthStatus.Degraded;

    /// <summary>What a retry budget at or above <see cref="BudgetThreshold" /> reports.</summary>
    public HealthStatus BudgetExhaustedStatus { get; set; } = HealthStatus.Degraded;

    /// <summary>
    ///     The utilization at which a retry budget counts as exhausted, from 0 to 1.
    ///     <para>
    ///         Not 1.0, on purpose. A budget sitting at 0.9 is already refusing retries in bursts, and by
    ///         the time it reads exactly 1.0 the thing worth alerting on has been happening for a while.
    ///     </para>
    /// </summary>
    public double BudgetThreshold { get; set; } = 0.9;

    /// <summary>
    ///     Whether the per-host breakers and budgets held by clients registered with
    ///     <c>AddResilience()</c> are included. On by default, because for most processes that is where
    ///     every breaker actually is.
    ///     <para>
    ///         What is reported is the handler generation currently serving the client.
    ///         <c>IHttpClientFactory</c> rebuilds the chain when the handler lifetime expires - two
    ///         minutes by default - and the per-host guards belong to the handler, so a breaker that
    ///         opened is reported until that rotation and not after it.
    ///     </para>
    /// </summary>
    public bool IncludeHttpClients { get; set; } = true;

    /// <summary>Everything added with <see cref="Watch(string, Breaker)" /> or <see cref="Watch(string, RetryBudget)" />.</summary>
    internal IReadOnlyList<(string Name, Breaker? Breaker, RetryBudget? Budget)> Watched => _watched;

    /// <summary>
    ///     Also report this breaker. For a policy held in a <c>static readonly</c> field rather than
    ///     registered in the container, which the health check has no other way to find.
    /// </summary>
    /// <param name="name">How it is reported. Usually the dependency's name.</param>
    /// <param name="breaker">The breaker.</param>
    /// <returns>These options, so calls chain.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> or <paramref name="breaker" /> is null.</exception>
    public ResilienceHealthOptions Watch(string name, Breaker breaker)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(breaker);

        _watched.Add((name, breaker, null));
        return this;
    }

    /// <summary>Also report this retry budget.</summary>
    /// <param name="name">How it is reported.</param>
    /// <param name="budget">The budget.</param>
    /// <returns>These options, so calls chain.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name" /> or <paramref name="budget" /> is null.</exception>
    public ResilienceHealthOptions Watch(string name, RetryBudget budget)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(budget);

        _watched.Add((name, null, budget));
        return this;
    }

    /// <summary>Checks the options and throws <see cref="ResilienceConfigurationException" /> listing every problem at once.</summary>
    /// <exception cref="ResilienceConfigurationException">The options cannot be used.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        if (double.IsNaN(BudgetThreshold) || BudgetThreshold <= 0 || BudgetThreshold > 1)
            problems.Add($"BudgetThreshold must be greater than 0 and at most 1; it is {BudgetThreshold}.");

        if (problems.Count > 0)
            throw new ResilienceConfigurationException(problems);
    }
}
