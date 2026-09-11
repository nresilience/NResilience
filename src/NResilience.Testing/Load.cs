using NResilience.Testing.Internal;

namespace NResilience.Testing;

/// <summary>
///     The traffic a simulation offers: a rate, how many other processes are offering the same thing
///     to the same dependency, and how much of it matters.
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

    /// <summary>The share of calls offered at <see cref="Criticality.Sheddable" />.</summary>
    private double SheddableShare { get; init; }

    /// <summary>The share of calls offered at <see cref="Criticality.SheddablePlus" />.</summary>
    private double SheddablePlusShare { get; init; }

    /// <summary>The share of calls offered at <see cref="Criticality.CriticalPlus" />.</summary>
    private double CriticalPlusShare { get; init; }

    /// <summary>
    ///     Whether any of the load is offered at something other than <see cref="Criticality.Critical" />.
    ///     <para>
    ///         A run that is entirely <see cref="Criticality.Critical" /> draws nothing from the seed
    ///         to decide a level, so an unmixed run and one mixed to all-critical are the same run.
    ///     </para>
    /// </summary>
    internal bool IsMixed => SheddableShare > 0 || SheddablePlusShare > 0 || CriticalPlusShare > 0;

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
    ///     Offers a share of the load at one <see cref="Criticality" />, so a run can ask what a
    ///     brownout costs the checkout while the backfill is still competing for the same dependency.
    /// </summary>
    /// <param name="criticality">The level. Not <see cref="Criticality.Critical" /> - see the remarks.</param>
    /// <param name="fraction">The share of calls offered at it, from 0 to 1.</param>
    /// <returns>A new load. The receiver is unchanged.</returns>
    /// <remarks>
    ///     <para>
    ///         <b><see cref="Criticality.Critical" /> is the remainder and cannot be set.</b> It is
    ///         what an unlabeled call already is, so a load that names shares for the other three has
    ///         said everything there is to say - and a fourth number that had to agree with the first
    ///         three would only ever be a way to disagree with them. Passing it is refused.
    ///     </para>
    ///     <para>
    ///         The level is published the way a service publishes one, through
    ///         <see cref="AmbientCriticality" />, and it is a property of the run rather than of the
    ///         policy: the traffic is what it is whether or not the policy sets
    ///         <see cref="Resilience.UseAmbientCriticality" /> to notice. That is the comparison worth
    ///         running, and it is the same reason <see cref="Simulation.WithPool" /> is accepted on a
    ///         policy with no <see cref="Resilience.Saturation" />.
    ///     </para>
    ///     <para>
    ///         The mix is this process's own traffic. <see cref="Peers" /> scales what the dependency is
    ///         offered without simulating the peers, and it does not label their calls either.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    /// // Seven calls in ten are a backfill nobody is waiting on.
    /// var load = Load.Constant(perSecond: 500).Mix(Criticality.Sheddable, fraction: 0.7);
    /// </code>
    /// </example>
    public Load Mix(Criticality criticality, double fraction) => criticality switch
    {
        Criticality.Sheddable => this with { SheddableShare = fraction },
        Criticality.SheddablePlus => this with { SheddablePlusShare = fraction },
        Criticality.CriticalPlus => this with { CriticalPlusShare = fraction },
        _ => throw new ArgumentOutOfRangeException(
            nameof(criticality),
            criticality,
            "Critical is the remainder of the mix and cannot be set; it is what a call with no level is."),
    };

    /// <summary>
    ///     The share of calls offered at one level. <see cref="Criticality.Critical" /> is whatever the
    ///     other three leave, so a load with no mix is entirely critical.
    /// </summary>
    /// <param name="criticality">The level.</param>
    /// <returns>The share, from 0 to 1.</returns>
    public double ShareOf(Criticality criticality) => criticality switch
    {
        Criticality.Sheddable => SheddableShare,
        Criticality.SheddablePlus => SheddablePlusShare,
        Criticality.CriticalPlus => CriticalPlusShare,
        Criticality.Critical => 1 - SheddableShare - SheddablePlusShare - CriticalPlusShare,
        _ => 0,
    };

    /// <summary>
    ///     Which level the next call is offered at. Walked in the order the levels are declared, so the
    ///     same seed labels the same calls.
    /// </summary>
    /// <param name="dice">The run's random stream.</param>
    /// <returns>The level.</returns>
    internal Criticality Draw(ChaosDice dice)
    {
        var roll = dice.Next();
        var cumulative = SheddableShare;

        if (roll < cumulative)
            return Criticality.Sheddable;

        cumulative += SheddablePlusShare;

        if (roll < cumulative)
            return Criticality.SheddablePlus;

        // Critical takes the remainder, and taking it here rather than adding a share keeps the last
        // bucket catching every roll the arithmetic leaves over.
        return roll < 1 - CriticalPlusShare ? Criticality.Critical : Criticality.CriticalPlus;
    }

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

        Share(problems, Criticality.Sheddable, SheddableShare);
        Share(problems, Criticality.SheddablePlus, SheddablePlusShare);
        Share(problems, Criticality.CriticalPlus, CriticalPlusShare);

        var mixed = SheddableShare + SheddablePlusShare + CriticalPlusShare;

        if (mixed > 1)
        {
            problems.Add(
                $"The criticality mix offers {mixed:F3} of the load before Critical, which takes the remainder; " +
                "the shares other than Critical must leave something over, so they cannot come to more than 1.");
        }

        if (problems.Count > 0)
            throw new ResilienceConfigurationException(problems);
    }

    private static void Share(List<string> problems, Criticality criticality, double fraction)
    {
        if (double.IsNaN(fraction) || fraction < 0 || fraction > 1)
            problems.Add($"The {criticality} share must be between 0 and 1; it is {fraction}.");
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
