using NResilience.Internal;

namespace NResilience.Testing;

/// <summary>
///     This process's own thread pool, as a simulation models it: a normal queue delay, and a schedule
///     of stretches during which work items wait far longer than that.
///     <para>
///         The fourth and last model in a simulation, alongside the clock, the random source and the
///         <see cref="Dependency" />. It exists because <see cref="NResilience.Saturation" /> is the one
///         term in the library that measures <i>this</i> process rather than the thing it calls, so a
///         simulation with a real idle pool underneath it can never exercise it. What reads the model
///         is the shipping code: the same saturation comparison and the same contamination check the
///         executor runs in production, unchanged.
///     </para>
///     <para>
///         Build one with <see cref="Healthy" /> and add stretches with <see cref="Stall" />. Every
///         method returns a new value, so a pool can be held in a <c>static readonly</c> field and
///         varied per simulation.
///     </para>
/// </summary>
/// <example>
///     <code>
/// var pool = Pool
///     .Healthy(delay: TimeSpan.FromMicroseconds(value: 80))
///     .Stall(after: TimeSpan.FromSeconds(value: 30), delay: TimeSpan.FromMilliseconds(value: 400),
///         lasting: TimeSpan.FromSeconds(value: 20));
/// </code>
/// </example>
/// <seealso cref="Simulate" />
public sealed record Pool
{
    /// <summary>How many probes fit in the baseline's window: four a second for a minute.</summary>
    private static readonly int Capacity = (int)(PoolProbe.Window.Ticks / PoolProbe.Interval.Ticks);

    private Episode[] _stalls = [];

    private Pool()
    {
    }

    /// <summary>
    ///     How long a work item waits for a thread while nothing is wrong. Tens of microseconds is what
    ///     a healthy pool actually looks like.
    ///     <para>
    ///         This is the number every stall is measured against, because
    ///         <see cref="NResilience.Saturation.Multiple" /> is relative: it is not enough for a stall
    ///         to be deep, it has to be deep relative to what this process normally does. That is why
    ///         there is no default - a baseline picked for you would silently decide whether your stalls
    ///         register.
    ///     </para>
    /// </summary>
    public TimeSpan Delay { get; private init; }

    /// <summary>A pool whose work items wait <paramref name="delay" /> for a thread, always.</summary>
    /// <param name="delay">The normal queue delay. Must be positive.</param>
    /// <returns>The pool.</returns>
    /// <remarks>
    ///     On its own this pool is never saturated, whatever <paramref name="delay" /> is: the
    ///     comparison is against the process's own recent median, and for an unstalled pool that median
    ///     is <paramref name="delay" />. Which is the shipping behaviour - a process that is
    ///     <i>always</i> slow has no saturation to detect - and the reason <see cref="Stall" /> is where
    ///     a simulation gets interesting.
    /// </remarks>
    public static Pool Healthy(TimeSpan delay) => new() { Delay = delay };

    /// <summary>
    ///     A stretch during which work items wait far longer than normal for a thread. What a burst of
    ///     synchronous blocking, a starved pool or a GC pause storm looks like from inside the executor:
    ///     every call appears to have got slower, and the dependency is fine.
    /// </summary>
    /// <param name="after">How far into the run the stall starts.</param>
    /// <param name="delay">How long a work item waits for a thread during it. Must be positive.</param>
    /// <param name="lasting">How long it lasts. Must be positive.</param>
    /// <returns>A new pool. The receiver is unchanged.</returns>
    /// <remarks>
    ///     Two things this deliberately reproduces rather than smoothing over, because both are
    ///     properties of the shipping probe and both are the kind of thing a simulation is for:
    ///     <list type="bullet">
    ///         <item>
    ///             A stall in the first <see cref="NResilience.Saturation.MinimumSamples" /> quarter
    ///             seconds of the run is not detected at all. The baseline is cold, and a cold baseline
    ///             is never saturated.
    ///         </item>
    ///         <item>
    ///             A stall that covers more than half of the baseline's window stops being detected
    ///             partway through, because the baseline is a rolling median over that window and
    ///             eventually the stall <i>is</i> the median. The onset is reported and then the
    ///             episode goes quiet while the queue is still deep. The window is a minute once the
    ///             run has been going that long, and everything so far before then - so a stall early
    ///             in a run is swallowed sooner than the same stall later in it.
    ///         </item>
    ///     </list>
    /// </remarks>
    public Pool Stall(TimeSpan after, TimeSpan delay, TimeSpan lasting) =>
        this with { _stalls = [.. _stalls, new Episode(after, lasting, delay)] };

    /// <summary>
    ///     Checks the model and throws <see cref="ResilienceConfigurationException" /> listing every
    ///     problem at once, the same way <see cref="Resilience.Validate" /> does.
    /// </summary>
    /// <exception cref="ResilienceConfigurationException">The pool cannot be simulated.</exception>
    public void Validate()
    {
        var problems = new List<string>();

        if (Delay <= TimeSpan.Zero)
            problems.Add($"Delay must be positive; it is {Delay}. A pool with no queue delay at all is one no multiple of it can exceed.");

        foreach (var stall in _stalls)
        {
            if (stall.After < TimeSpan.Zero)
                problems.Add($"Stall must not start before the run does; it starts at {stall.After}.");

            if (stall.Lasting <= TimeSpan.Zero)
                problems.Add($"Stall must last a positive time; it lasts {stall.Lasting}.");

            if (stall.Delay <= TimeSpan.Zero)
                problems.Add($"Stall must queue for a positive time; it queues for {stall.Delay}.");
        }

        if (problems.Count > 0)
            throw new ResilienceConfigurationException(problems);
    }

    /// <summary>Runs <see cref="Validate" /> and returns this pool, so a bad one throws where it is written.</summary>
    /// <returns>This pool.</returns>
    /// <exception cref="ResilienceConfigurationException">The pool cannot be simulated.</exception>
    public Pool Validated()
    {
        Validate();
        return this;
    }

    /// <summary>
    ///     What the probe would report at <paramref name="at" />: the queue delay now, and the rolling
    ///     median that <see cref="NResilience.Saturation.Multiple" /> is applied to.
    /// </summary>
    /// <param name="at">How far into the run the reading is taken.</param>
    /// <param name="minimumSamples">How many probes the baseline needs before it reports one.</param>
    /// <returns>The reading, with a null baseline while it is still cold.</returns>
    internal PoolProbe.Reading At(TimeSpan at, int minimumSamples)
    {
        // Capped at what the window holds, so a MinimumSamples larger than that is never satisfied -
        // which is the shipping behaviour too: samples that age out of the window are gone, so asking
        // for more than fit is asking for a baseline that never warms.
        var samples = at.Ticks / PoolProbe.Interval.Ticks;

        if (samples > Capacity)
            samples = Capacity;

        return new PoolProbe.Reading(DelayAt(at), samples < minimumSamples ? null : Median(at));
    }

    /// <summary>
    ///     The queue delay at one instant: the deepest stall covering it, or the healthy baseline. Both
    ///     what the probe reports and what a simulated attempt waits before it runs.
    /// </summary>
    /// <param name="at">How far into the run.</param>
    /// <returns>The delay.</returns>
    /// <remarks>
    ///     Overlapping stalls take the deepest rather than compounding, which is where this parts
    ///     company with <see cref="Dependency" />'s brownouts. A brownout is a multiplier on a
    ///     dependency's own work and two of them genuinely stack; a queue delay is one number about one
    ///     queue, and two descriptions of the same queue cannot be multiplied together.
    /// </remarks>
    internal TimeSpan DelayAt(TimeSpan at)
    {
        var delay = Delay;

        foreach (var stall in _stalls)
        {
            if (at < stall.After || at >= stall.After + stall.Lasting)
                continue;

            if (stall.Delay > delay)
                delay = stall.Delay;
        }

        return delay;
    }

    /// <summary>
    ///     The baseline: the median queue delay over the minute ending at <paramref name="at" />.
    /// </summary>
    /// <param name="at">How far into the run the reading is taken.</param>
    /// <returns>The median.</returns>
    /// <remarks>
    ///     <para>
    ///         Weighted by how long each delay held rather than by counting simulated probes, which is
    ///         the same answer four evenly-spaced samples a second converge on and costs one pass over
    ///         the stalls instead of two hundred and forty evaluations per read.
    ///     </para>
    ///     <para>
    ///         One deliberate difference from the shipping baseline, in the direction that matters:
    ///         the shipping baseline answers with a histogram bucket's upper bound, so the median
    ///         comes out up to about 12% high, and a high baseline makes saturation marginally harder to
    ///         declare. This model returns the exact median instead. A simulation is therefore very
    ///         slightly more willing to call a stall saturated than the process is, and a stall that
    ///         only just clears the multiple here is one to leave margin on.
    ///     </para>
    /// </remarks>
    private TimeSpan Median(TimeSpan at)
    {
        var to = at.Ticks;
        var from = to - PoolProbe.Window.Ticks;

        if (from < 0)
            from = 0;

        // Every instant in one elementary interval has the same delay, so the boundaries are the only
        // places the answer can change: the window's ends plus each stall's edges, clipped into it.
        var edges = new List<long> { from, to };

        foreach (var stall in _stalls)
        {
            AddEdge(edges, stall.After.Ticks, from, to);
            AddEdge(edges, (stall.After + stall.Lasting).Ticks, from, to);
        }

        edges.Sort();

        var spans = new List<(long Delay, long Ticks)>();

        for (var i = 1; i < edges.Count; i++)
        {
            var width = edges[i] - edges[i - 1];

            if (width <= 0)
                continue;

            // The midpoint rather than either edge, so a half-open stall boundary lands on the side
            // the interval is actually on.
            var delay = DelayAt(TimeSpan.FromTicks(edges[i - 1] + (width / 2))).Ticks;
            spans.Add((delay, width));
        }

        var total = 0L;

        foreach (var span in spans)
            total += span.Ticks;

        // The lower median: the smallest delay that at least half the window sits at or below. Integer
        // arithmetic throughout, so the answer is the same on every machine.
        spans.Sort(static (left, right) => left.Delay.CompareTo(right.Delay));

        var target = (total + 1) / 2;
        var covered = 0L;

        foreach (var span in spans)
        {
            covered += span.Ticks;

            if (covered >= target)
                return TimeSpan.FromTicks(span.Delay);
        }

        return Delay;
    }

    /// <summary>Adds one stall boundary to the edge list, if it falls strictly inside the window.</summary>
    private static void AddEdge(List<long> edges, long edge, long from, long to)
    {
        if (edge > from && edge < to)
            edges.Add(edge);
    }

    /// <summary>One stretch of the run during which the pool is not keeping up.</summary>
    private readonly record struct Episode(TimeSpan After, TimeSpan Lasting, TimeSpan Delay);
}
