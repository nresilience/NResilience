namespace NResilience;

/// <summary>
///     How often a hedge has to actually win before this process keeps paying for hedging.
///     <para>
///         <c>WinRate.AtLeast(0.2)</c> means "keep hedging while at least one hedge in five produces the
///         answer". Whether that ever happens is the one thing no configuration can state: it depends on
///         whether latency is independent enough between two attempts that the second one wins. Against
///         a dependency with one slow shard it wins often, and hedging is the best feature in the
///         library. Against a dependency that is uniformly slow because it is overloaded, the second leg
///         is exactly as slow as the first, hedging wins nothing, and <see cref="Hedge.Quantile" />
///         quietly adds <c>1 - Quantile</c> of extra load to a service that is already struggling.
///     </para>
///     <para>
///         The loop is AIMD, on the load multiplier rather than on the threshold. A window in which the
///         win rate falls below <see cref="Minimum" /> <b>halves</b> the fraction of would-be hedges that
///         start; a window that clears it <b>adds</b> a quarter back. Multiplicative retreat and additive
///         return, the asymmetry <see cref="Recovery" /> and the adaptive limiter both use and for the
///         same reason: the cost of hedging too much is borne by the dependency, and the cost of hedging
///         too little is borne by this process.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var api = Resilience.Http with
/// {
///     Hedge = Hedge.At(0.95) with { WinRate = WinRate.AtLeast(0.2) },
/// };
/// </code>
/// </example>
/// <remarks>
///     <para>
///         <b>Why the load multiplier and not the quantile.</b> Raising the effective quantile would be
///         the obvious retreat, and it is the wrong one twice over. A quantile is fixed at construction
///         so that <see cref="Hedge" />'s estimate can be memoized per slice, so moving it would cost a
///         second latency window - and, worse, the relationship between a quantile and the resulting
///         hedge rate is a property of the dependency's distribution rather than of the configuration.
///         Admitting a fraction of the hedges that would have fired keeps the arithmetic exact: the load
///         hedging adds is <c>(1 - Quantile) x allowance</c>, whatever the distribution is doing. The
///         admission is deficit-accounted rather than sampled, for the reason
///         <see cref="Recovery" />'s ramp is: evenly spaced, and identical on every run.
///     </para>
///     <para>
///         <b>The failure mode.</b> A dependency whose tail is caused by something a second attempt
///         cannot route around - a saturated dependency, a slow downstream every replica shares - is
///         exactly the case this loop retreats from, and it is also a case where the tail is real and
///         the caller is feeling it. Hedging is not what fixes it, which is the argument for retreating,
///         but an operator watching <c>nresilience.hedges{outcome=suppressed}</c> climb should read it as
///         "hedging has stopped helping", not as "the dependency is fine now".
///     </para>
///     <para>
///         <b>Why the return is on the clock.</b> A retreat means fewer hedges start, which means less
///         evidence about whether hedging would work now, which means an evidence-driven return would
///         ratchet the allowance to <see cref="MinimumAllowance" /> and leave it there for the life of
///         the process. So a window that holds fewer than <see cref="MinimumSamples" /> hedges relaxes
///         the allowance instead of holding it, and the loop settles into a shallow cycle rather than a
///         floor. The amplitude of that cycle is the steady-state cost of the feature against a
///         dependency hedging cannot help, and it is bounded by the return step.
///     </para>
///     <para>
///         Every property but <see cref="Minimum" /> has a working default, so
///         <c>WinRate.AtLeast(0.2)</c> is a complete configuration and
///         <c>WinRate.AtLeast(0.2) with { Window = ... }</c> is the way to change one. The defaults are
///         supplied on read rather than by a constructor, for the reason <see cref="Hedge" /> gives: a
///         struct's default instance is the one thing a constructor cannot reach.
///     </para>
/// </remarks>
public readonly record struct WinRate
{
    /// <summary>
    ///     The win rate required when <see cref="AtLeast" /> is called without one. One hedge in five:
    ///     below that, hedging at <c>0.95</c> is spending 5% extra load to shorten under 1% of calls,
    ///     which is not the trade the feature was turned on for.
    /// </summary>
    internal const double DefaultMinimum = 0.2;

    /// <summary>How many hedges the estimate needs, when <see cref="MinimumSamples" /> was not set.</summary>
    private const int DefaultMinimumSamples = 10;

    /// <summary>The smallest fraction of would-be hedges still admitted, when <see cref="MinimumAllowance" /> was not set.</summary>
    private const double DefaultMinimumAllowance = 0.05;

    /// <summary>What a losing window multiplies the allowance by.</summary>
    private const double RetreatFactor = 0.5;

    /// <summary>What a window that is not losing adds back to the allowance.</summary>
    private const double ReturnStep = 0.25;

    private readonly double? _minimumAllowance;
    private readonly int? _minimumSamples;
    private readonly TimeSpan? _window;

    /// <summary>
    ///     The fraction of hedges that has to win for hedging to keep running at full rate. <c>0.2</c>
    ///     is one in five.
    ///     <para>
    ///         Must be in <c>(0, 1)</c>. This is the one number an operator supplies, and it is
    ///         dimensionless on purpose: "at least one hedge in five has to be worth it" is a judgment
    ///         that survives being copied to another dependency, and it is stated in the units the
    ///         existing <c>nresilience.hedges{outcome=won}</c> over <c>{outcome=started}</c> already
    ///         reports.
    ///     </para>
    /// </summary>
    public double Minimum { get; init; }

    // Two other properties here begin with "Minimum", and each is the minimum of a different thing:
    // this one is the minimum win *rate*, MinimumSamples is the minimum number of hedges the estimate
    // needs, and MinimumAllowance is the floor under the fraction of hedges still admitted. The bare
    // name belongs to the win rate because that is what the type is named after.

    /// <summary>
    ///     How much history the win rate covers, and - a quarter of it at a time - how often the loop
    ///     takes a decision. Default 1 minute.
    ///     <para>
    ///         The default retreats from full rate to <see cref="MinimumAllowance" /> over about a
    ///         minute and returns over the same, which is fast enough to matter inside an incident and
    ///         slow enough that a single unlucky quarter-minute does not turn hedging off.
    ///     </para>
    /// </summary>
    public TimeSpan Window
    {
        get => _window ?? TimeSpan.FromMinutes(1);
        init => _window = value;
    }

    /// <summary>
    ///     How many hedges the window needs before the loop has an opinion. Default 10.
    ///     <para>
    ///         Below it the allowance relaxes rather than holding, which is both the cold-start rule -
    ///         a process that has never hedged hedges at the configured rate - and what lets the loop
    ///         come back after a retreat has starved it of evidence.
    ///     </para>
    /// </summary>
    public int MinimumSamples
    {
        get => _minimumSamples ?? DefaultMinimumSamples;
        init => _minimumSamples = value;
    }

    /// <summary>
    ///     The smallest fraction of would-be hedges the loop will still admit. Default <c>0.05</c>.
    ///     <para>
    ///         Not zero, because a trickle is what tells the loop whether hedging has started working
    ///         again; the clock in <see cref="Window" /> is the other half of that answer. <c>0</c> is no
    ///         floor at all rather than an off switch - the retreat keeps halving, which suspends hedging
    ///         in all but name - and it must be less than 1, because at 1 the loop can never retreat and
    ///         is feedback that cannot act.
    ///     </para>
    /// </summary>
    public double MinimumAllowance
    {
        get => _minimumAllowance ?? DefaultMinimumAllowance;
        init => _minimumAllowance = value;
    }

    /// <summary>
    ///     Value equality over the <i>effective</i> configuration, so a value that names a default
    ///     explicitly equals one that left it alone.
    /// </summary>
    /// <param name="other">The other configuration.</param>
    /// <returns>True when both would behave identically.</returns>
    public bool Equals(WinRate other) =>
        Minimum.Equals(other.Minimum)
        && Window == other.Window
        && MinimumSamples == other.MinimumSamples
        && MinimumAllowance.Equals(other.MinimumAllowance);

    /// <summary>The way to configure win-rate feedback.</summary>
    /// <param name="minimum">The fraction of hedges that has to win. Must be in <c>(0, 1)</c>.</param>
    /// <returns>The configuration.</returns>
    public static WinRate AtLeast(double minimum = DefaultMinimum) => new() { Minimum = minimum };

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Minimum, Window, MinimumSamples, MinimumAllowance);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Minimum:0.##%} of hedges win over {Window.TotalSeconds:0.#}s " +
        $"(min {MinimumSamples} hedges, down to {MinimumAllowance:0.##%} of the hedge rate)";

    /// <summary>
    ///     Collects everything wrong with this configuration, in the shape
    ///     <see cref="Resilience.Validate" /> reports problems.
    /// </summary>
    /// <param name="problems">The list to add to.</param>
    internal void Validate(List<string> problems)
    {
        if (double.IsNaN(Minimum) || Minimum <= 0 || Minimum >= 1)
        {
            problems.Add(
                $"WinRate.Minimum must be in (0, 1); it is {Minimum}. " +
                "At 0 nothing can ever fall below it and the feedback never acts; at 1 every window is " +
                "losing, because a hedge that loses its race is the ordinary case. " +
                "Use WinRate.AtLeast(0.2) for one hedge in five.");
        }

        if (Window <= TimeSpan.Zero)
            problems.Add($"WinRate.Window must be positive; it is {Window}.");

        if (MinimumSamples < 1)
            problems.Add($"WinRate.MinimumSamples must be at least 1; it is {MinimumSamples}.");

        if (double.IsNaN(MinimumAllowance) || MinimumAllowance < 0 || MinimumAllowance >= 1)
        {
            problems.Add(
                $"WinRate.MinimumAllowance must be in [0, 1); it is {MinimumAllowance}. " +
                "It is the fraction of would-be hedges the loop can retreat to, and at 1 it cannot " +
                "retreat at all - which is feedback that can never act. Use 0 for no floor at all.");
        }
    }

    /// <summary>The allowance after a window whose win rate fell below <see cref="Minimum" />.</summary>
    /// <param name="allowance">The current allowance.</param>
    /// <returns>Half of it, floored at <see cref="MinimumAllowance" />.</returns>
    internal double Retreated(double allowance) => Math.Max(MinimumAllowance, allowance * RetreatFactor);

    /// <summary>
    ///     The allowance after <paramref name="slices" /> decision points that were not losing - either
    ///     because the win rate cleared <see cref="Minimum" />, or because there was not enough evidence
    ///     to say it had not.
    /// </summary>
    /// <param name="allowance">The current allowance.</param>
    /// <param name="slices">How many decision points passed. More than one only after an idle spell.</param>
    /// <returns>The relaxed allowance, capped at 1.</returns>
    internal double Relaxed(double allowance, long slices)
    {
        var relaxed = allowance + ReturnStep * slices;

        return relaxed >= 1 || double.IsNaN(relaxed) ? 1 : relaxed;
    }
}
