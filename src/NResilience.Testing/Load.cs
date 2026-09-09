namespace NResilience.Testing;

/// <summary>
///     The traffic a simulation offers: a rate, and how many other processes are offering the same
///     thing to the same dependency.
/// </summary>
/// <example>
///     <code>
/// var load = Load.Constant(perSecond: 500, peers: 50);
/// </code>
/// </example>
/// <seealso cref="Simulate" />
public sealed record Load
{
    private Load()
    {
    }

    /// <summary>
    ///     How many other processes offer the same rate to the same dependency. One, the default, is
    ///     this process alone.
    ///     <para>
    ///         The peers are a term in the dependency's arithmetic and nothing more: their offered load
    ///         counts against <see cref="Dependency.Concurrency" />, and their retries, breakers and
    ///         budgets are not simulated. That approximation is what makes it honest to ask "does my
    ///         retry budget hold when I am one of fifty" without claiming to have simulated fifty
    ///         policies.
    ///     </para>
    /// </summary>
    public int Peers { get; private init; } = 1;

    /// <summary>Calls this process starts per second, on average.</summary>
    public int PerSecond { get; private init; }

    /// <summary>
    ///     A steady offered rate for the whole run.
    ///     <para>
    ///         Arrivals are spread rather than evenly spaced, so the run contains the short bursts a
    ///         real second of traffic contains - which is what a concurrency bound and a hedge threshold
    ///         both react to. The gaps are drawn from the seed, so the bursts land in the same places on
    ///         every run.
    ///     </para>
    /// </summary>
    /// <param name="perSecond">Calls per second. Must be positive.</param>
    /// <param name="peers">How many other processes offer the same rate. Must be at least 1.</param>
    /// <returns>The load.</returns>
    public static Load Constant(int perSecond, int peers = 1) => new() { PerSecond = perSecond, Peers = peers };

    /// <summary>
    ///     Checks the load and throws <see cref="ResilienceConfigurationException" /> listing every
    ///     problem at once, the same way <see cref="Resilience.Validate" /> does.
    /// </summary>
    /// <exception cref="ResilienceConfigurationException">The load cannot be offered.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        if (PerSecond <= 0)
            problems.Add($"PerSecond must be positive; it is {PerSecond}.");

        if (Peers < 1)
            problems.Add($"Peers must be at least 1 - this process is one of them; it is {Peers}.");

        if (problems.Count > 0)
            throw new ResilienceConfigurationException(problems);
    }

    /// <summary>Runs <see cref="Validate" /> and returns this load, so a bad one throws where it is written.</summary>
    /// <returns>This load.</returns>
    /// <exception cref="ResilienceConfigurationException">The load cannot be offered.</exception>
    public Load Validated()
    {
        Validate();
        return this;
    }
}
