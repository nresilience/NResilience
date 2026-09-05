namespace NResilience.Extensions;

/// <summary>
///     The bounds and the gains of an <see cref="AdaptiveLimiter" />: a concurrency limit the process
///     discovers from latency rather than one an operator divides out by hand.
///     <para>
///         <c>Limit.Concurrency(50)</c> is correct on one pod and wrong on a hundred, and the arithmetic
///         that makes it right - the dependency's ceiling divided by the expected pod count - goes stale
///         on every scaling change and nobody revisits it. These options describe the <i>range</i> the
///         answer may take and how fast it may move, and leave the answer itself to be measured.
///     </para>
///     <para>
///         Every property has a working default, so <c>new AdaptiveLimitOptions()</c> is a complete
///         configuration and the two worth setting per dependency are <see cref="Minimum" /> and
///         <see cref="Maximum" />. They are the guardrails: the control loop cannot leave them, so they
///         are what bounds the damage when the signal is wrong.
///     </para>
/// </summary>
/// <example>
///     <code language="json">
/// {
///   "RateLimit": {
///     "Adaptive": {
///       "Minimum": 4,
///       "Maximum": 200
///     }
///   }
/// }
/// </code>
/// </example>
/// <remarks>
///     <para>
///         <b>The signal.</b> Latency under load reveals queueing, and queueing is the only observable
///         difference between a dependency that is keeping up and one that is not. The limiter compares
///         the fastest call of a recent round against a baseline of what fast has recently meant; when
///         even the fastest call of the round is <see cref="Multiple" /> times the baseline, there is a
///         queue somewhere downstream and the limit shrinks by <see cref="DecreaseFactor" />. When there
///         is not, and the limit is what is actually constraining the caller, it grows by one.
///     </para>
///     <para>
///         <b>Multiplicative decrease and additive increase</b>, in that pairing, for the reason TCP uses
///         it: the cost of being too high is borne by the dependency and the cost of being too low is
///         borne by this process, so the two directions must not move at the same speed. Backing off is
///         geometric and probing is one permit per round.
///     </para>
/// </remarks>
public sealed class AdaptiveLimitOptions
{
    /// <summary>
    ///     Where the limit starts, before enough calls have been seen to measure anything. Default 20.
    ///     <para>
    ///         It is a starting point rather than a target: the loop leaves it within a few rounds in
    ///         whichever direction the latency says. Set it low if the process starts under load, because
    ///         a limit that starts above the dependency's capacity spends its first rounds contributing
    ///         to the queue it is trying to measure.
    ///     </para>
    /// </summary>
    public int Initial { get; set; } = 20;

    /// <summary>
    ///     The floor the limit may never go below. Default 4.
    ///     <para>
    ///         This is not a tuning knob so much as a liveness guarantee. A dependency that is slow for a
    ///         reason unrelated to this caller's concurrency - a bad deploy, a cold cache - drives the loop
    ///         down every round, and without a floor the limiter would converge on refusing everything and
    ///         never sample the recovery. Below about four, one slow call is most of the round.
    ///     </para>
    /// </summary>
    public int Minimum { get; set; } = 4;

    /// <summary>
    ///     The ceiling the limit may never go above. Default 200.
    ///     <para>
    ///         The one number worth setting per dependency, and it is a bound rather than an estimate: it
    ///         is what the loop cannot exceed when the baseline is wrong. The baseline can be wrong -
    ///         a process that starts under a queue measures the queued latency as normal - so
    ///         <see cref="Maximum" /> should be a number the dependency can survive, not a number that
    ///         will never be reached.
    ///     </para>
    /// </summary>
    public int Maximum { get; set; } = 200;

    /// <summary>
    ///     How many times the baseline a round's fastest call has to be before the limit shrinks.
    ///     Default 2.0, and it must be greater than 1.
    ///     <para>
    ///         Dimensionless on purpose, for the reason <see cref="SlowCalls.Multiple" /> is: "twice as
    ///         slow as this dependency normally is" ports to another dependency and "80 ms" does not.
    ///         Lower is more cautious - it backs off on less evidence and settles on a smaller limit;
    ///         higher tolerates more queueing before reacting.
    ///     </para>
    /// </summary>
    public double Multiple { get; set; } = 2.0;

    /// <summary>
    ///     What the limit is multiplied by when a round says there is queueing. Default 0.9, and it must
    ///     be strictly between 0 and 1.
    ///     <para>
    ///         Gentler than TCP's halving because the round that triggers it is one round rather than a
    ///         dropped packet, and because the floor and the ceiling already bound the outcome. At 0.9 the
    ///         limit halves in about seven consecutive congested rounds, which is fast enough to matter and
    ///         slow enough not to react to one unlucky sample.
    ///     </para>
    /// </summary>
    public double DecreaseFactor { get; set; } = 0.9;

    /// <summary>
    ///     Checks the options and throws <see cref="ResilienceConfigurationException" /> listing every
    ///     problem at once, in the same shape <see cref="Resilience.Validate" /> uses.
    /// </summary>
    /// <exception cref="ResilienceConfigurationException">The options do not describe a limit the loop can move within.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        if (Minimum < 1)
            problems.Add($"Minimum must be at least 1; it is {Minimum}.");

        if (Maximum < Minimum)
            problems.Add($"Maximum must be at least Minimum; Maximum is {Maximum} and Minimum is {Minimum}.");

        if (Initial < Minimum || Initial > Maximum)
            problems.Add($"Initial must be between Minimum and Maximum; it is {Initial}, and the range is {Minimum} to {Maximum}.");

        if (double.IsNaN(Multiple) || Multiple <= 1)
            problems.Add($"Multiple must be greater than 1; it is {Multiple}. A multiple of 1 or less makes every round look congested.");

        if (double.IsNaN(DecreaseFactor) || DecreaseFactor <= 0 || DecreaseFactor >= 1)
            problems.Add($"DecreaseFactor must be strictly between 0 and 1; it is {DecreaseFactor}. At 1 the limit never shrinks.");

        if (problems.Count > 0)
            throw new ResilienceConfigurationException(problems);
    }
}
