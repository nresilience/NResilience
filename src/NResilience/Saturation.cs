using NResilience.Internal;

namespace NResilience;

/// <summary>
///     When this process is the bottleneck, so a latency measurement taken now describes the local
///     thread pool rather than the dependency - and the policy stops learning from it until it does not.
///     <para>
///         <c>Saturation.Above(5)</c> means "five times this process's own normal queue delay". That
///         number ports across pod sizes, host types and thread-pool settings; <c>200 ms</c> does not.
///     </para>
///     <para>
///         Opt-in, and off in every preset. It changes what the other measured terms learn, which is a
///         change worth turning on deliberately.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var api = Resilience.Http with
/// {
///     AttemptCeiling = AttemptCeiling.Above(3),
///     Saturation     = Saturation.Above(5),   // and stop measuring while the local queue is deep
/// };
/// </code>
/// </example>
/// <remarks>
///     <para>
///         <b>The defect this fixes.</b> Every estimator in the library measures wall-clock duration
///         around the callback and attributes all of it to the dependency. Six things read the result -
///         <see cref="Resilience.AttemptCeiling" />, <see cref="MeasuredBase" />,
///         <see cref="Resilience.Hedge" />, <see cref="SlowCalls" />, <c>Breaker.NormalLatency</c> and
///         the adaptive limiter - and a thread-pool queue delay of 400 ms is indistinguishable, from
///         inside the executor, from a dependency that got 400 ms slower. The library's response to the
///         second is correct; its response to the first is to relax every bound it has, at the moment a
///         local incident is under way. A process that cannot measure the dependency should not guess
///         about it.
///     </para>
///     <para>
///         <b>What it does, and what it does not.</b> While the process reads as saturated, this
///         policy's own estimates stop being fed: the attempt ceiling, the measured backoff base and
///         the hedge threshold all hold whatever they last learned, and resume the moment the queue
///         drains. Nothing is refused, no bound is changed, and no delay is added - so the worst this
///         can do is leave the policy behaving exactly as it does today.
///     </para>
///     <para>
///         It does not reach the <see cref="Resilience.Breaker" /> or an adaptive limiter, for the
///         reason <see cref="Resilience.Adaptive" /> does not: both are live objects that two policies
///         may share, and a switch on one policy may not silently re-configure a guard the other is
///         holding. The breaker's own latency baseline has a second reason - it is recorded and read in
///         one step, so declining to feed it while still judging against it would trip the breaker on a
///         local incident, and declining to judge would delay a trip. That half needs a decision of its
///         own rather than this flag.
///     </para>
///     <para>
///         <b>Tighten-only.</b> Saturation can only decline to record. It can never permit an attempt,
///         lengthen a bound, or delay a trip.
///     </para>
///     <para>
///         <b>The failure mode, stated outright.</b> A process that is <i>always</i> saturated learns a
///         high queue delay as normal and stops finding itself saturated. That is the same shape as the
///         adaptive limiter's "a process starting into an existing queue learns the queued latency as
///         normal", and it has the same answer: the baseline is relative, so it can only tell you that
///         this minute is unlike the last hour.
///     </para>
///     <para>
///         Every property but <see cref="Multiple" /> has a working default, so
///         <c>Saturation.Above(5)</c> is a complete configuration and
///         <c>Saturation.Above(5) with { Floor = ... }</c> is the way to change one. The defaults are
///         supplied on read rather than by a constructor, for the reason
///         <see cref="AttemptCeiling" /> gives: a struct's default instance is the one thing a
///         constructor cannot reach.
///     </para>
/// </remarks>
public readonly record struct Saturation
{
    /// <summary>How many samples the baseline needs, when <see cref="MinimumSamples" /> was not set.</summary>
    private const int DefaultMinimumSamples = 20;

    private readonly TimeSpan? _floor;
    private readonly int? _minimumSamples;

    /// <summary>
    ///     How many times this process's normal queue delay counts as saturated. <c>5</c> means the
    ///     policy stops recording once a work item waits five times as long to reach a thread as it
    ///     normally does.
    ///     <para>
    ///         Must be greater than 1. This is the one number to supply, and it is dimensionless on
    ///         purpose: "five times its own normal" survives being copied to a pod with a different
    ///         core count, and "200 ms" does not.
    ///     </para>
    /// </summary>
    public double Multiple { get; init; }

    /// <summary>
    ///     A floor under the delay that counts as saturated, whatever <see cref="Multiple" /> says.
    ///     Default 20 ms.
    ///     <para>
    ///         A healthy pool queues in microseconds, so five times normal is still microseconds - and
    ///         a policy that stopped measuring every time a GC pause pushed one probe to 50 µs would
    ///         never learn anything. This is the "do not bother" line, and it is what makes the
    ///         relative multiple safe to leave low.
    ///     </para>
    /// </summary>
    public TimeSpan Floor
    {
        get => _floor ?? TimeSpan.FromMilliseconds(20);
        init => _floor = value;
    }

    /// <summary>
    ///     How many probes the baseline needs before it is used at all. Default 20, matching
    ///     <see cref="AttemptCeiling.MinimumSamples" /> and <see cref="Hedge.MinimumSamples" />.
    ///     <para>
    ///         Probes are queued four times a second at most, so a cold process is not saturated for
    ///         its first few seconds however deep its queue is. That is the cold-start rule every
    ///         measured term in the library follows: no estimate means no opinion, not a guessed one.
    ///     </para>
    /// </summary>
    public int MinimumSamples
    {
        get => _minimumSamples ?? DefaultMinimumSamples;
        init => _minimumSamples = value;
    }

    /// <summary>
    ///     Value equality over the <i>effective</i> configuration, so a value that names a default
    ///     explicitly equals one that left it alone.
    /// </summary>
    /// <param name="other">The other configuration.</param>
    /// <returns>True when both would behave identically.</returns>
    public bool Equals(Saturation other) =>
        Multiple.Equals(other.Multiple) && Floor == other.Floor && MinimumSamples == other.MinimumSamples;

    /// <summary>The way to configure saturation awareness.</summary>
    /// <param name="multiple">How many times normal queue delay counts as saturated. Must be greater than 1.</param>
    /// <returns>The configuration.</returns>
    public static Saturation Above(double multiple = 5.0) => new() { Multiple = multiple };

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Multiple, Floor, MinimumSamples);

    /// <inheritdoc />
    public override string ToString() =>
        $"{Multiple:0.##}x normal queue delay (floor {Floor.TotalMilliseconds:0.#}ms, min {MinimumSamples} samples)";

    /// <summary>
    ///     Collects everything wrong with this configuration on its own, in the shape
    ///     <see cref="Resilience.Validate" /> reports problems. What is wrong with it only in
    ///     combination with the surrounding policy is checked there.
    /// </summary>
    /// <param name="problems">The list to add to.</param>
    internal void Validate(List<string> problems)
    {
        if (double.IsNaN(Multiple) || double.IsInfinity(Multiple) || Multiple <= 1)
        {
            problems.Add(
                $"Saturation.Multiple must be greater than 1; it is {Multiple}. " +
                "Use Saturation.Above(5) to stop measuring once the pool queues five times as long as it normally does.");
        }

        if (Floor <= TimeSpan.Zero)
            problems.Add($"Saturation.Floor must be positive; it is {Floor}.");

        if (MinimumSamples < 1)
            problems.Add($"Saturation.MinimumSamples must be at least 1; it is {MinimumSamples}.");
    }

    /// <summary>Whether this reading counts as saturated under this configuration.</summary>
    /// <param name="reading">What the probe currently reports.</param>
    /// <returns>True when a measurement taken now would describe the local queue.</returns>
    /// <remarks>
    ///     Both bars have to be cleared: the absolute <see cref="Floor" /> and the relative
    ///     <see cref="Multiple" />. A cold baseline is never saturated, whatever the delay - the
    ///     cold-start rule, and the reason a process that starts into a deep queue does not immediately
    ///     stop measuring.
    /// </remarks>
    internal bool IsSaturated(in PoolProbe.Reading reading)
    {
        if (reading.Normal is not { } normal)
            return false;

        if (reading.Delay < Floor)
            return false;

        // A double comparison rather than scaling the ticks into a long, so an absurd Multiple
        // saturates the comparison instead of overflowing it.
        return reading.Delay.Ticks >= normal.Ticks * Multiple;
    }
}
