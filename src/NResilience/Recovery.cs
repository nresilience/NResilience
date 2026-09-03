namespace NResilience;

/// <summary>
///     How a breaker gives a dependency its traffic back: a growing fraction of calls over a ramp
///     whose length is derived from the break the breaker just served, rather than the whole offered
///     load in the millisecond the last probe succeeded.
///     <para>
///         <c>Recovery.Over(0.25)</c> means "ramp back over a quarter of however long you were open".
///         That number ports, because it is a statement about how much recovering a dependency needs
///         relative to how badly it was broken, and the breaker already knows the second half. A
///         fifteen-second break ramps over four seconds; a two-minute break ramps over thirty.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var breaker = new Breaker(new BreakerSettings
/// {
///     Recovery = Recovery.Over(0.25),   // give the traffic back over a quarter of the break
/// });
/// </code>
/// </example>
/// <remarks>
///     <para>
///         <b>Why a cliff is the wrong recovery.</b> A dependency that failed for a capacity reason -
///         which is most of them, because one that failed for a reason unrelated to load usually fails
///         the probe too - has been receiving one call every fifteen seconds. It has a cold cache, an
///         empty connection pool, and its own upstream work to catch up on.
///         <see cref="BreakerSettings.ProbeSuccesses" /> successful probes prove it can serve two
///         calls, and a cliffed close reads that as proof it can serve two thousand. It cannot, it
///         fails, the breaker re-opens with a doubled break, and the dependency spends more of each
///         period cold than the last.
///     </para>
///     <para>
///         <b>What makes it adaptive rather than merely gradual.</b> Two things, and neither is a
///         number anybody has to pick. The ramp's <i>length</i> comes from how long the breaker was
///         open, which is the only honest estimate of how much recovering there is to do. The ramp's
///         <i>growth</i> is evidence-driven: the admitted fraction only doubles behind calls that
///         succeeded and were not slow, and <see cref="BreakerSettings.SlowCalls" /> is already the
///         thing that separates "it answered" from "it recovered". A ramp that stalls at 12% because
///         the admitted traffic is three times slower than normal is the feature working - the
///         dependency is up and is not ready, and there is no other way for a breaker to say that.
///     </para>
///     <para>
///         <b>What it costs.</b> Callers refused during the ramp would have been served by a cliffed
///         breaker. That is a real availability cost, and it is why this is opt-in. The case for it is
///         that the alternative is not "all of them served" - it is "all of them served once, then all
///         of them refused again for twice as long", which is the oscillation the ramp exists to stop.
///         A refused caller gets the <see cref="CallRejectedException" /> an open breaker already
///         raises, through the rejection pause the executor already applies.
///     </para>
///     <para>
///         Every property but <see cref="Fraction" /> has a working default, so
///         <c>Recovery.Over(0.25)</c> is a complete configuration and
///         <c>Recovery.Over(0.25) with { Maximum = ... }</c> is the way to change one. The defaults are
///         supplied on read rather than by a constructor, for the reason <see cref="Hedge" /> gives: a
///         struct's default instance is the one thing a constructor cannot reach.
///     </para>
/// </remarks>
public readonly record struct Recovery
{
    /// <summary>The fraction of calls the ramp starts by admitting, when <see cref="Initial" /> was not set.</summary>
    private const double DefaultInitial = 0.05;

    private readonly double? _initial;
    private readonly TimeSpan? _maximum;
    private readonly TimeSpan? _minimum;

    /// <summary>
    ///     How much of the break just served the ramp lasts. <c>0.25</c> means a fifteen-second break
    ///     is handed back over roughly four seconds.
    ///     <para>
    ///         Must be greater than 0. This is the one number an operator supplies, and it is
    ///         dimensionless on purpose: "a quarter of however long it was down" is a judgment that
    ///         survives being copied to another dependency, and "five seconds" is not.
    ///     </para>
    /// </summary>
    public double Fraction { get; init; }

    /// <summary>
    ///     The shortest ramp, however brief the break was. Default 1 second.
    ///     <para>
    ///         A floor rather than a formality: <see cref="BreakerSettings.BreakDuration" /> can be set
    ///         to a second in a test or a tight inner loop, and a ramp measured in milliseconds is a
    ///         cliff with extra state.
    ///     </para>
    /// </summary>
    public TimeSpan Minimum
    {
        get => _minimum ?? TimeSpan.FromSeconds(1);
        init => _minimum = value;
    }

    /// <summary>
    ///     The longest ramp, however long the break was. Default 30 seconds.
    ///     <para>
    ///         The bound on what this feature can cost. <see cref="BreakerSettings.MaxBreakDuration" />
    ///         is two minutes by default and grows to it after four consecutive opens, and half a minute
    ///         of partial refusal is already more warm-up than any dependency the library can see needs.
    ///     </para>
    /// </summary>
    public TimeSpan Maximum
    {
        get => _maximum ?? TimeSpan.FromSeconds(30);
        init => _maximum = value;
    }

    /// <summary>
    ///     The fraction of calls the ramp admits when it starts. Default <c>0.05</c>.
    ///     <para>
    ///         Small on purpose. The point of the ramp is that the first thing a just-recovered
    ///         dependency sees is a trickle rather than everything, and the clock and the evidence both
    ///         raise this from below rather than lowering it from above.
    ///     </para>
    /// </summary>
    public double Initial
    {
        get => _initial ?? DefaultInitial;
        init => _initial = value;
    }

    /// <summary>The way to configure a ramped recovery.</summary>
    /// <param name="fraction">How much of the break just served the ramp lasts. Must be greater than 0.</param>
    /// <returns>The configuration.</returns>
    public static Recovery Over(double fraction = 0.25) => new() { Fraction = fraction };

    /// <summary>
    ///     Value equality over the <i>effective</i> configuration, so a value that names a default
    ///     explicitly equals one that left it alone.
    /// </summary>
    /// <param name="other">The other configuration.</param>
    /// <returns>True when both would behave identically.</returns>
    public bool Equals(Recovery other) =>
        Fraction.Equals(other.Fraction)
        && Minimum == other.Minimum
        && Maximum == other.Maximum
        && Initial.Equals(other.Initial);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Fraction, Minimum, Maximum, Initial);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Fraction:0.##}x the break, from {Initial:0.##%} " +
        $"(min {Minimum.TotalSeconds:0.#}s, max {Maximum.TotalSeconds:0.#}s)";

    /// <summary>
    ///     Collects everything wrong with this configuration on its own, in the shape
    ///     <see cref="BreakerSettings.Validate" /> reports problems.
    /// </summary>
    /// <param name="problems">The list to add to.</param>
    internal void Validate(List<string> problems)
    {
        if (double.IsNaN(Fraction) || double.IsInfinity(Fraction) || Fraction <= 0)
        {
            problems.Add(
                $"Recovery.Fraction must be greater than 0; it is {Fraction}. " +
                "Use Recovery.Over(0.25) to hand the traffic back over a quarter of the break just served.");
        }

        if (Minimum <= TimeSpan.Zero)
            problems.Add($"Recovery.Minimum must be positive; it is {Minimum}.");

        if (Maximum < Minimum)
            problems.Add($"Recovery.Maximum must be at least Recovery.Minimum; they are {Maximum} and {Minimum}.");

        if (double.IsNaN(Initial) || Initial <= 0 || Initial >= 1)
        {
            problems.Add(
                $"Recovery.Initial must be in (0, 1); it is {Initial}. " +
                "At 1 the ramp is the cliff it exists to replace, and at 0 it never admits a first call.");
        }
    }

    /// <summary>
    ///     How long the ramp runs after a break of <paramref name="served" />, clamped both ends so a
    ///     one-second break does not produce a ramp measured in milliseconds and a two-minute one does
    ///     not refuse callers for half of it.
    /// </summary>
    /// <param name="served">The break the breaker just finished serving.</param>
    /// <returns>The ramp length.</returns>
    internal TimeSpan RampFor(TimeSpan served)
    {
        var ticks = served.Ticks * Fraction;

        if (double.IsNaN(ticks) || ticks >= Maximum.Ticks)
            return Maximum;

        return ticks <= Minimum.Ticks ? Minimum : TimeSpan.FromTicks((long)ticks);
    }
}
