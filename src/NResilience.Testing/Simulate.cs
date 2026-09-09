namespace NResilience.Testing;

/// <summary>
///     Runs a policy against a modeled dependency on a virtual clock and reports what it cost.
///     <para>
///         Chaos injection tells you what happens when you break something once. A simulation measures
///         the result: the load multiplier, the availability and the p99 of <i>your</i> configuration
///         over five simulated minutes of a brownout, in the time it takes to run a unit test.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var report = Simulate.Policy(Policies.Api)
///     .Against(Dependency
///         .Healthy(p50: TimeSpan.FromMilliseconds(20), p99: TimeSpan.FromMilliseconds(200))
///         .Brownout(after: TimeSpan.FromSeconds(30), slower: 8, lasting: TimeSpan.FromMinutes(1)))
///     .Under(Load.Constant(perSecond: 500))
///     .For(TimeSpan.FromMinutes(5))
///     .Run(seed: 42);
///
/// Assert.True(report.LoadMultiplier &lt;= 1.2);
/// </code>
/// </example>
/// <remarks>
///     <para>
///         <b>Three things are fake and no more:</b> the clock, the random source, and the dependency.
///         The policy is yours, and the executor, the breaker, the retry budget, the classifier and
///         every estimator are the shipping ones. Nothing about the decision logic is re-implemented
///         here - a simulator that modeled the library would be worse than no simulator, and this one
///         cannot, because it does not know how.
///     </para>
///     <para>
///         <b>One process.</b> It cannot model fifty pods sharing a dependency and does not pretend to:
///         <see cref="Load.Peers" /> scales the load the dependency is offered without simulating the
///         peers' own decisions.
///     </para>
///     <para>
///         <b>One thread.</b> Which is also why <see cref="Resilience.Saturation" /> reads nothing
///         useful in a simulation: the thread pool it measures is the real one, and the real one is
///         idle while a simulation runs.
///     </para>
/// </remarks>
/// <seealso cref="Dependency" />
/// <seealso cref="Load" />
/// <seealso cref="SimulationReport" />
public static class Simulate
{
    /// <summary>Starts a simulation of a policy. Chain <c>Against</c>, <c>Under</c>, <c>For</c> and <c>Run</c>.</summary>
    /// <param name="policy">The policy to simulate. Its <see cref="Resilience.Time" /> is replaced by the virtual clock.</param>
    /// <returns>The simulation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy" /> is null.</exception>
    public static Simulation Policy(Resilience policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return new Simulation(policy);
    }
}
