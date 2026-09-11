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
///         <b>Four things are fake and no more:</b> the clock, the random source, the dependency, and
///         this process's own thread pool. The policy is yours, and the executor, the breaker, the
///         retry budget, the classifier and every estimator are the shipping ones. Nothing about the
///         decision logic is re-implemented here - a simulator that modeled the library would be worse
///         than no simulator, and this one cannot, because it does not know how.
///     </para>
///     <para>
///         <b>One process.</b> It cannot model fifty pods sharing a dependency and does not pretend to:
///         <see cref="Load.Peers" /> scales the load the dependency is offered without simulating the
///         peers' own decisions.
///     </para>
///     <para>
///         <b>One thread.</b> Which is why the pool <see cref="Resilience.Saturation" /> measures is
///         modeled rather than observed: the real one is idle while a simulation runs, so a run reads
///         what <see cref="Simulation.WithPool" /> describes, and a policy that configures
///         <see cref="Resilience.Saturation" /> without one reads a pool that never queues.
///     </para>
///     <para>
///         <b>One seed is one sample.</b> <see cref="Simulation.Run" /> is exact about the run it did;
///         it is <see cref="Simulation.RunAll" /> that is honest about the configuration, because the
///         numbers worth tuning on move from seed to seed even when the averages do not.
///     </para>
/// </remarks>
/// <seealso cref="Dependency" />
/// <seealso cref="Load" />
/// <seealso cref="SimulationReport" />
public static class Simulate
{
    /// <summary>
    ///     Starts a simulation of a graph of services calling each other. Chain <c>Calls</c>,
    ///     <c>Leaf</c>, <c>Under</c>, <c>For</c> and <c>Run</c>.
    ///     <para>
    ///         <see cref="Policy" /> answers what one policy costs one dependency. This answers the
    ///         question a team has instead: when the thing at the bottom browns out, what happens at the
    ///         top. A retry storm is a property of a call graph, and no arrangement of a
    ///         single-dependency run can show one.
    ///     </para>
    /// </summary>
    /// <returns>The empty topology.</returns>
    public static Topology Topology() => new();

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
